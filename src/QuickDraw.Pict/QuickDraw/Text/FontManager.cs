using System;
using System.Collections.Generic;

namespace QuickDraw.Pict
{
    // What FMSwapFont hands QuickDraw's text drawing (FMOutput plus the width table): the strike, the style effects to
    // synthesize (bold smears, italic slant in 1/16 pixels per row, underline, shadow/outline thickness), the extra
    // width per character, the stretch (FOutNumer / FOutDenom, 8.8) from the strike to the requested size, the style
    // left to synthesize, the width of every character (Fixed), and the input it was chosen for.
    internal sealed class FontSelection
    {
        public BitmapFont Font = null!;
        public int Bold, Italic, UlOffset, UlShadow, UlThick, Shadow, Extra, CurStyle;
        public (int h, int v) Numer, Denom;
        public int[] Widths = new int[256];
        public int Size;                                   // the requested size (the width table's fSize)
        public (int h, int v) InNumer, InDenom;            // the text scale it was asked for
        public int Ascent, Descent;                        // FMOutput's metrics (bytes; scaled with FScaleDisable)
        public bool MacOS9;                                // drawn by Mac OS 9's text code
        public PictColor[]? Palette;                       // a color font's colors (fctb or the standard table)
    }

    // The Macintosh ROM's Font Manager (FMSwapFont, System 7 bitmap path; screen device, 80 dpi).
    //
    // The size searched for is the requested size scaled horizontally by numer.h / denom.h. A family with a 'FOND'
    // takes, from its association table, the exact size; else, with an outline (size 0) entry, TrueType; else double,
    // else half (even sizes), else the nearest listed size (ties to the larger) - and within a size the exact style,
    // else the best-scoring subset of the requested style (bold 4, italic 8, underline 1, outline 3, shadow 3,
    // condense 2, extend 1; a style with extra bits scores -1). Sizes are chosen from the table whether or not their
    // font resource exists: a missing one moves an exact/double/half choice on to the next step, and ends the search
    // at the nearest size. Families numbered under 0x200 then try old-style 'FONT' resources: size & 0x7F exact,
    // double (under 64), half (even), then the sizes above up to 127, then below. A family found nowhere falls back to
    // the application font, Geneva and the system font. Whatever the chosen strike lacks of the style is synthesized per the ROM's style
    // table (bold +1 smear +1 width, italic 8/16, outline 1 + 1 width, shadow 2 + 2 width, condense -1, extend +1,
    // underline 1/1/1); the remaining stretch to the requested size, times the text scale, is FOutNumer (8.8,
    // rounded). A family asking for it (FOND flags bit 12, or fractional widths on, and bit 13 clear) takes its extra
    // width from its own style-extra table instead. Widths are the strike's integer widths, or with fractional widths
    // on the NFNT's width table or the family's; the style extra goes on every non-zero width; the space extra, scaled
    // back to the strike, on the space; carriage return has width 0.
    //
    // With FScaleDisable (glyphState's scaling-disabled byte) a family skips the double/half sizes and takes the
    // nearest smaller size (a larger one only when there is none smaller); old-style fonts scan downward first. The
    // stretch is then cut to a power of two or three quarters of one (FOutNumer), and the leftover factor (1..2) scales
    // every width after its style extra, and the ascent, descent and leading (ROM $FFCBEE98, $FFCBEBAA, $FFCBE588).
    //
    // Mac OS 9's Font Manager differs in: the fallback order (application font, the lowest-numbered family, system
    // font, Geneva); style variants matched against face & $9B; and family width tables walked with the family's own
    // character range.
    //
    // Not modelled: TrueType ('sfnt') families (text in them goes to the outline fallback), synthetic color strikes
    // and color NFNTs (1-bit strikes only), non-Roman scripts.
    internal static class FontManager
    {
        private const int Bold = 1, Italic = 2, Underline = 4, Outline = 8, Shadow = 16, Condense = 32, Extend = 64;
        private static readonly int[] VariantScore = { 4, 8, 1, 3, 3, 2, 1, 0 };      // ROM $FFCBF5F0
        private static readonly int[] WidthTableScore = { 1, 3, 5, 4, 4, 2, 2, 0 };   // ROM $FFCBF6EA
        private const int Geneva = 3;

        private readonly record struct Found(BitmapFont Font, int ActualSize, int Remaining, FontFamilyRecord? Fond)
        {
            public int FontId { get; init; }
        }

        public static FontSelection? Swap(PictFontLibrary library, int family, int size, int face,
            (int h, int v) numer, (int h, int v) denom, int spaceExtra, bool fractEnable, bool fScaleDisable, bool macOS9)
        {
            if (size == 0) size = 12;
            if (size < 0) return null;
            face &= 0xFF;
            // The ROM searches for the size scaled by the horizontal ratio; Mac OS 9 folds the ratio into the size first.
            int searchSize;
            var fold = (size, numer, denom);
            if (macOS9)
            {
                fold = Fold(size, numer, denom);
                searchSize = fold.size;
            }
            else
                searchSize = FixedMath.FixRound(FixedMath.FixMul(FixedMath.FixRatio((short)numer.h, (short)denom.h), size << 16));

            foreach (int candidate in Families(library, family, macOS9))
            {
                var fond = library.Family(candidate);
                if (fond != null && fond.Associations.Length > 0)
                {
                    var found = FromFamily(library, fond, searchSize, face, fScaleDisable, macOS9, out bool trueType);
                    if (found != null)
                    {
                        var selection = Build(found.Value, size, face, numer, denom, spaceExtra, fractEnable, fScaleDisable, macOS9, fold);
                        if (found.Value.Font.Depth > 1)
                            selection.Palette = library.ColorFontPalette(found.Value.FontId, found.Value.Font.Depth);
                        return selection;
                    }
                    if (trueType) return null;
                }
                if (candidate < 0x200 && FromOldFonts(library, candidate, searchSize, face, fScaleDisable) is { } old)
                    return Build(old, size, face, numer, denom, spaceExtra, fractEnable, fScaleDisable, macOS9, fold);
            }
            return null;
        }

        // The family (0 = system font, 1 = application font), then the fallbacks: for script families (0x4000 and
        // up) the system font, else the application font, Geneva and the system font.
        private static IEnumerable<int> Families(PictFontLibrary library, int family, bool macOS9)
        {
            int mapped = family == 0 ? library.SystemFontId : family == 1 ? library.ApplicationFontId : family;
            var seen = new HashSet<int>();
            var list = macOS9
                ? new[] { mapped, library.ApplicationFontId, library.LowestFamily() ?? mapped, library.SystemFontId, Geneva }
                : family >= 0x4000
                ? new[] { mapped, library.SystemFontId, library.ApplicationFontId }
                : new[] { mapped, library.ApplicationFontId, Geneva, library.SystemFontId };
            foreach (int f in list)
                if (seen.Add(f)) yield return f;
        }

        private static Found? FromFamily(PictFontLibrary library, FontFamilyRecord fond, int searchSize, int face,
            bool fScaleDisable, bool macOS9, out bool trueType)
        {
            trueType = false;
            int match = macOS9 ? face & 0x9B : face;
            // Depth variants (style high byte = log2 depth) take part too: at a chosen size and style the last
            // variant of depth 2-16 bits is used on a 32-bit screen ($FFCBE6EA), else the plain one.
            var entries = new List<FontFamilyRecord.Association>(fond.Associations);
            bool Has(int s) => s > 0 && entries.Exists(a => a.Size == s);

            // The style variant of a size, loaded (null when its resource is missing).
            Found? Load(int size)
            {
                FontFamilyRecord.Association? chosen = null;
                int bestScore = int.MinValue;
                foreach (var a in entries)
                {
                    if (a.Size != size) continue;
                    int style = a.Style & 0xFF;
                    if (style == match) { chosen = a; break; }
                    int score = (style & ~match) != 0 ? -1 : Score(style, VariantScore);
                    if (score > bestScore) (bestScore, chosen) = (score, a);
                }
                if (chosen is not { } entry) return null;
                foreach (var a in entries)
                    if (a.Size == size && (a.Style & 0xFF) == (entry.Style & 0xFF) && (a.Style >> 8) is >= 1 and <= 4)
                        entry = a;
                if (library.Strike(entry.FontId) is not { } strike) return null;
                return new Found(strike, entry.Size, face & ~(entry.Style & 0xFF), fond) { FontId = entry.FontId };
            }

            if (Has(searchSize) && Load(searchSize) is { } exact) return exact;
            if (entries.Exists(a => a.Size == 0)) { trueType = true; return null; }
            if (!fScaleDisable)
            {
                if (Has(searchSize * 2) && Load(searchSize * 2) is { } doubled) return doubled;
                if ((searchSize & 1) == 0 && Has(searchSize / 2) && Load(searchSize / 2) is { } half) return half;
            }
            // The nearest size in table order (a leading size-0 entry skipped): the closest, later entries winning
            // ties; with FScaleDisable a larger size ends the scan once a smaller one was found ($FFCBF57A).
            int nearest = 0, best = 0x7FFF;
            for (int i = 0; i < entries.Count; i++)
            {
                if (i == 0 && entries[0].Size == 0) continue;
                int d = (short)(searchSize - entries[i].Size);
                if (d < 0)
                {
                    if (fScaleDisable && nearest != 0) break;
                    d = -d;
                }
                if (d <= best) (best, nearest) = (d, entries[i].Size);
            }
            return nearest == 0 ? null : Load(nearest);
        }

        // Old-style FONTs (resource id family * 128 + size); nothing of the style is intrinsic.
        private static Found? FromOldFonts(PictFontLibrary library, int family, int searchSize, int face, bool fScaleDisable)
        {
            int size = (searchSize == 0 ? 1 : searchSize) & 0x7F;
            var order = new List<int> { size };
            if (size < 64) order.Add(size * 2);
            if ((size & 1) == 0) order.Add(size / 2);
            var up = new List<int>();
            var down = new List<int>();
            for (int s = size + 1; s <= 127; s++) up.Add(s);
            for (int s = size - 1; s >= 1; s--) down.Add(s);
            order.AddRange(fScaleDisable ? down : up);
            order.AddRange(fScaleDisable ? up : down);
            foreach (int s in order)
                if (library.OldStyleStrike(family, s) is { } strike)
                    return new Found(strike, s, face, null);
            return null;
        }

        private static FontSelection Build(Found found, int size, int face, (int h, int v) numer, (int h, int v) denom,
            int spaceExtra, bool fractEnable, bool fScaleDisable, bool macOS9,
            (int size, (int h, int v) numer, (int h, int v) denom) fold)
        {
            var f = found.Font;
            int remaining = found.Remaining;
            var s = new FontSelection { Font = f, CurStyle = remaining, Size = size, InNumer = numer, InDenom = denom, MacOS9 = macOS9 };

            // The style table's byte additions.
            int extra = 0;
            if ((remaining & Bold) != 0) { s.Bold += 1; extra += 1; }
            if ((remaining & Italic) != 0) s.Italic += 8;
            if ((remaining & Outline) != 0) { s.Shadow += 1; extra += 1; }
            if ((remaining & Shadow) != 0) { s.Shadow += 2; extra += 2; }
            if ((remaining & Condense) != 0) extra -= 1;
            if ((remaining & Extend) != 0) extra += 1;
            if ((remaining & Underline) != 0) (s.UlOffset, s.UlShadow, s.UlThick) = (1, 1, 1);

            // The remaining stretch: text scale x requested / actual size, as 8.8 rounded.
            int actual = found.ActualSize & 0x7F;
            int sizeRatio = FixedMath.FixRatio((short)size, (short)actual);
            int Out(int n, int d) => (FixedMath.FixMul(FixedMath.FixRatio((short)n, (short)d), sizeRatio) + 0x80) >> 8;
            // Mac OS 9: the folded ratio times (folded size << 16) / strike size, multiplied half up, capped at $7FFF.
            long foldedRatio = actual == 0 ? 0x10000 : ((long)fold.size << 16) / actual;
            int OutMacOS9(int n, int d)
            {
                long ratio = d == 0 ? 0x10000 : ((long)n << 16) / d;
                long h = foldedRatio == 0x10000 ? ratio : ((ratio * foldedRatio) + 0x8000) >> 16;
                return h <= 0x7FFF7F ? (int)((h + 0x80) >> 8) : 0x7FFF;
            }
            s.Numer = macOS9
                ? (OutMacOS9(fold.numer.h, fold.denom.h), OutMacOS9(fold.numer.v, fold.denom.v))
                : (Out(numer.h, denom.h), Out(numer.v, denom.v));
            s.Denom = (0x100, 0x100);

            // FScaleDisable: the stretch cut to a power of two (or 3/4 of one), the rest as a Fixed factor.
            int hFactor = 0x10000, vFactor = 0x10000;
            if (fScaleDisable)
            {
                (int nh, hFactor) = ReduceStretch(s.Numer.h);
                (int nv, vFactor) = ReduceStretch(s.Numer.v);
                s.Numer = (nh, nv);
            }
            int Metric(int b, int factor) =>
                fScaleDisable ? (byte)FixedMath.FixRound(FixedMath.FixMul((sbyte)b << 16, factor)) : (byte)b;
            s.Ascent = Metric(f.Ascent & 0xFF, vFactor);
            s.Descent = Metric(f.Descent & 0xFF, vFactor);

            // Width source: with fractional widths, the NFNT's width table, else the family's (flags bit 14 clear).
            var fond = found.Fond;
            FontFamilyRecord.WidthTable? fondWidths = null;
            bool nfntWidths = fractEnable && f.FractionalWidths != null;
            if (fractEnable && !nfntWidths && fond != null && (fond.Flags & 0x4000) == 0)
                fondWidths = MatchWidthTable(fond, face);

            // Style extra: the style table's, or the family's own style-extra table.
            int widthExtra = (sbyte)extra << 16;
            s.Extra = (sbyte)extra;
            if (fond != null && (fond.Flags & 0x2000) == 0 && ((fond.Flags & 0x1000) != 0 || fractEnable))
            {
                int covered = fondWidths?.Style ?? 0;
                int sum = SignMagnitude(fond.Property[0]);
                for (int bit = 0; bit < 7; bit++)
                    if ((remaining & ~covered & (1 << bit)) != 0) sum += SignMagnitude(fond.Property[bit + 1]);
                int fixedExtra = FixedMath.FixMul(sum << 4, actual << 16);
                if (fractEnable) widthExtra = fixedExtra;
                else
                {
                    s.Extra = FixedMath.FixRound(fixedExtra);
                    widthExtra = s.Extra << 16;
                }
            }

            // The family's width table is read from its start for the strike's first..last char (so shifted when
            // the two ranges start differently), the missing symbol's width after them; 0xFFFF means missing.
            // The strike's own width table likewise marks missing characters with 0xFFFF.
            int missing = f.MissingIndex;
            int Width(int index)
            {
                if (fondWidths != null)
                {
                    // Mac OS 9 indexes the table by the family's own range.
                    int fondMissing = macOS9 ? fond!.LastChar - fond.FirstChar + 1 : missing;
                    int i = !macOS9 ? index : index == missing ? fondMissing : index + f.FirstChar - fond!.FirstChar;
                    int word = fond!.WidthWord(fondWidths, i);
                    if (word == 0xFFFF && i != fondMissing) word = fond.WidthWord(fondWidths, fondMissing);
                    return unchecked((int)((uint)word * (uint)actual << 4));
                }
                if (nfntWidths)
                {
                    int word = f.FractionalWidths![index];
                    if (word == 0xFFFF && index != missing) word = f.FractionalWidths[missing];
                    return word << 8;
                }
                return (f.OffsetWidths[index] & 0xFF) << 16;
            }
            bool tableWidths = fondWidths != null || nfntWidths;
            bool scaleWidths = fScaleDisable && hFactor != 0x10000;
            for (int c = 0; c < 256; c++)
            {
                bool inRange = c >= f.FirstChar && c <= f.LastChar;
                int index = inRange && (tableWidths || f.OffsetWidths[c - f.FirstChar] != -1) ? c - f.FirstChar : missing;
                int w = Width(index);
                if (w != 0)
                {
                    w = unchecked(w + widthExtra);
                    if (scaleWidths) w = FixedMath.FixMul(w, hFactor);
                }
                s.Widths[c] = w;
            }
            if (spaceExtra != 0)
                s.Widths[' '] += FixedMath.FixMul(FixedMath.FixMul(FixedMath.FixRatio((short)numer.h, (short)denom.h),
                    FixedMath.FixRatio((short)s.Denom.h, (short)s.Numer.h)), spaceExtra);
            s.Widths['\r'] = 0;
            return s;
        }

        // FScaleDisable's cut ($FFCBEE98): with the stretch n (8.8) and a numerator starting at $100, halve n (doubling
        // the numerator) while n >= $200, double it (halving the numerator) while n < $C0, and below $100 take 3/4 of
        // the numerator and n * 4 / 3. n is left as the factor (1..2, returned as Fixed). The ROM never returns for a
        // stretch of 0 or of $8000 and up; those are left uncut here.
        private static (int numer, int factor) ReduceStretch(int stretch)
        {
            if (stretch <= 0 || stretch >= 0x8000) return (stretch, 0x10000);
            int n = stretch, numer = 0x100;
            while (n >= 0x200) { numer = (short)(numer << 1); n >>= 1; }
            while (n < 0xC0) { numer >>= 1; n <<= 1; }
            if (n < 0x100)
            {
                numer = (short)(numer * 3) >> 2;
                n = (n << 2) / 3;
            }
            return (numer & 0xFFFF, n << 8);
        }

        // Mac OS 9's scale folding (FM_NormalizeScale / FM_CalcScale): a horizontal ratio r = numer.h / denom.h is
        // folded into the size, newSize = round(r x size), leaving numer = (r, v ratio) x size / newSize as 8.8 over
        // 256. No fold when the horizontal ratio is 1 or the scaled size is under 4 points; a zero component resets
        // the ratio to 1.
        internal static (int size, (int h, int v) numer, (int h, int v) denom) Fold(int size, (int h, int v) numer, (int h, int v) denom)
        {
            if (numer.h == 0 || numer.v == 0 || denom.h == 0 || denom.v == 0) return (size, (1, 1), (1, 1));
            numer = (Math.Abs(numer.h), Math.Abs(numer.v));
            denom = (Math.Abs(denom.h), Math.Abs(denom.v));
            if (numer.v == denom.v) (numer.v, denom.v) = (1, 1);
            if (numer.h == denom.h) return (size, (1, numer.v), (1, denom.v));
            int r = (int)(((long)numer.h << 16) / denom.h);
            int p = unchecked(r * size);
            if (p >> 16 < 4) return (size, numer, denom);
            int newSize = (p + 0x8000) >> 16;
            int f = FixedMath.FixRatio((short)size, (short)newSize);
            int hF = FixedMath.FixMul(f, r), vF = FixedMath.FixMul(f, (int)(((long)numer.v << 16) / denom.v));
            return (newSize, ((hF + 0x80) >> 8, (vF + 0x80) >> 8), (256, 256));
        }

        // The family width table for a style: the exact one, else the best-scoring subset.
        private static FontFamilyRecord.WidthTable? MatchWidthTable(FontFamilyRecord fond, int face)
        {
            FontFamilyRecord.WidthTable? best = null;
            int bestScore = -1;
            foreach (var t in fond.WidthTables)
            {
                int style = t.Style & 0xFF;
                if (style == face) return t;
                if ((style & ~face) != 0) continue;
                int score = Score(style, WidthTableScore);
                if (score > bestScore) (bestScore, best) = (score, t);
            }
            return best;
        }

        private static int Score(int style, int[] weights)
        {
            int score = 0;
            for (int bit = 0; bit < 8; bit++)
                if ((style & (1 << bit)) != 0) score += weights[bit];
            return score;
        }

        // Style-extra words 0x8000-0x8FFF are sign-magnitude negatives.
        private static int SignMagnitude(int word) => (word & 0xF000) == 0x8000 ? -(word & 0x0FFF) : (short)word;
    }
}
