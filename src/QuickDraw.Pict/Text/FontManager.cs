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
    }

    // The Macintosh ROM's Font Manager (FMSwapFont, System 7 bitmap path; screen device, 80 dpi, FScaleDisable off).
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
    // Not modelled: TrueType ('sfnt') families (text in them goes to the outline fallback), FScaleDisable,
    // synthetic color strikes and color NFNTs (1-bit strikes only), non-Roman scripts.
    internal static class FontManager
    {
        private const int Bold = 1, Italic = 2, Underline = 4, Outline = 8, Shadow = 16, Condense = 32, Extend = 64;
        private static readonly int[] VariantScore = { 4, 8, 1, 3, 3, 2, 1, 0 };      // ROM $FFCBF5F0
        private static readonly int[] WidthTableScore = { 1, 3, 5, 4, 4, 2, 2, 0 };   // ROM $FFCBF6EA
        private const int Geneva = 3;

        private readonly record struct Found(BitmapFont Font, int ActualSize, int Remaining, FontFamilyRecord? Fond);

        public static FontSelection? Swap(PictFontLibrary library, int family, int size, int face,
            (int h, int v) numer, (int h, int v) denom, int spaceExtra, bool fractEnable)
        {
            if (size == 0) size = 12;
            if (size < 0) return null;
            face &= 0xFF;
            int searchSize = FixedMath.FixRound(FixedMath.FixMul(
                FixedMath.FixRatio((short)numer.h, (short)denom.h), size << 16));

            foreach (int candidate in Families(library, family))
            {
                var fond = library.Family(candidate);
                if (fond != null && fond.Associations.Length > 0)
                {
                    var found = FromFamily(library, fond, searchSize, face, out bool trueType);
                    if (found != null) return Build(found.Value, size, face, numer, denom, spaceExtra, fractEnable);
                    if (trueType) return null;
                }
                if (candidate < 0x200 && FromOldFonts(library, candidate, searchSize, face) is { } old)
                    return Build(old, size, face, numer, denom, spaceExtra, fractEnable);
            }
            return null;
        }

        // The family (0 = system font, 1 = application font), then the fallbacks: for script families (0x4000 and
        // up) the system font, else the application font, Geneva and the system font.
        private static IEnumerable<int> Families(PictFontLibrary library, int family)
        {
            int mapped = family == 0 ? library.SystemFontId : family == 1 ? library.ApplicationFontId : family;
            var seen = new HashSet<int>();
            var list = family >= 0x4000
                ? new[] { mapped, library.SystemFontId, library.ApplicationFontId }
                : new[] { mapped, library.ApplicationFontId, Geneva, library.SystemFontId };
            foreach (int f in list)
                if (seen.Add(f)) yield return f;
        }

        private static Found? FromFamily(PictFontLibrary library, FontFamilyRecord fond, int searchSize, int face,
            out bool trueType)
        {
            trueType = false;
            var entries = new List<FontFamilyRecord.Association>();
            foreach (var a in fond.Associations)
                if ((a.Style & 0xFF00) == 0) entries.Add(a);
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
                    if (style == face) { chosen = a; break; }
                    int score = (style & ~face) != 0 ? -1 : Score(style, VariantScore);
                    if (score > bestScore) (bestScore, chosen) = (score, a);
                }
                if (chosen is not { } entry || library.Strike(entry.FontId) is not { } strike) return null;
                return new Found(strike, entry.Size, face & ~(entry.Style & 0xFF), fond);
            }

            if (Has(searchSize) && Load(searchSize) is { } exact) return exact;
            if (entries.Exists(a => a.Size == 0)) { trueType = true; return null; }
            if (Has(searchSize * 2) && Load(searchSize * 2) is { } doubled) return doubled;
            if ((searchSize & 1) == 0 && Has(searchSize / 2) && Load(searchSize / 2) is { } half) return half;
            int nearest = 0, best = int.MaxValue;
            foreach (var a in entries)
            {
                if (a.Size <= 0) continue;
                int d = Math.Abs(a.Size - searchSize);
                if (d < best || (d == best && a.Size > nearest)) (best, nearest) = (d, a.Size);
            }
            return nearest == 0 ? null : Load(nearest);
        }

        // Old-style FONTs (resource id family * 128 + size); nothing of the style is intrinsic.
        private static Found? FromOldFonts(PictFontLibrary library, int family, int searchSize, int face)
        {
            int size = (searchSize == 0 ? 1 : searchSize) & 0x7F;
            var order = new List<int> { size };
            if (size < 64) order.Add(size * 2);
            if ((size & 1) == 0) order.Add(size / 2);
            for (int s = size + 1; s <= 127; s++) order.Add(s);
            for (int s = size - 1; s >= 1; s--) order.Add(s);
            foreach (int s in order)
                if (library.OldStyleStrike(family, s) is { } strike)
                    return new Found(strike, s, face, null);
            return null;
        }

        private static FontSelection Build(Found found, int size, int face, (int h, int v) numer, (int h, int v) denom,
            int spaceExtra, bool fractEnable)
        {
            var f = found.Font;
            int remaining = found.Remaining;
            var s = new FontSelection { Font = f, CurStyle = remaining, Size = size, InNumer = numer, InDenom = denom };

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
            s.Numer = (Out(numer.h, denom.h), Out(numer.v, denom.v));
            s.Denom = (0x100, 0x100);

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
            int missing = f.MissingIndex;
            int Width(int index)
            {
                if (fondWidths != null)
                {
                    int word = fond!.WidthWord(fondWidths, index);
                    if (word == 0xFFFF && index != missing) word = fond.WidthWord(fondWidths, missing);
                    return unchecked((int)((uint)word * (uint)actual << 4));
                }
                if (nfntWidths) return f.FractionalWidths![index] << 8;
                return (f.OffsetWidths[index] & 0xFF) << 16;
            }
            for (int c = 0; c < 256; c++)
            {
                bool inRange = c >= f.FirstChar && c <= f.LastChar;
                int index = inRange && (fondWidths != null || f.OffsetWidths[c - f.FirstChar] != -1) ? c - f.FirstChar : missing;
                int w = Width(index);
                s.Widths[c] = w != 0 ? w + widthExtra : 0;
            }
            if (spaceExtra != 0)
                s.Widths[' '] += FixedMath.FixMul(FixedMath.FixMul(FixedMath.FixRatio((short)numer.h, (short)denom.h),
                    FixedMath.FixRatio((short)s.Denom.h, (short)s.Numer.h)), spaceExtra);
            s.Widths['\r'] = 0;
            return s;
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
