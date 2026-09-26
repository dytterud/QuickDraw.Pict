using System;
using System.Linq;

namespace QuickDraw.Pict
{
    // What FMSwapFont hands QuickDraw's text drawing: the strike, the style effects to synthesize (bold overstrikes,
    // italic slant in 1/16 pixels per row, underline, shadow/outline thickness), the extra width per character, the
    // stretch factor from the strike's size to the requested one, and the width of every character (16.16).
    internal sealed class FontSelection
    {
        public BitmapFont Font = null!;
        public int Bold, Italic, UlOffset, UlShadow, UlThick, Shadow, Extra;
        public (int h, int v) Numer, Denom;
        public int[] Widths = new int[256];
    }

    // Font Manager font selection, UNVERIFIED pending the ROM's FMSwapFont: follows Executor's FMSwapFont and
    // Inside Macintosh I-224's screen font characterization table (bold 1 pixel + 1 width, italic 8/16 pixel per
    // row, outline 1 + 1, shadow 2 + 2, condense -1, extend +1, underline offset/shadow/thickness 1). Integer widths
    // (FractEnable off). Styles the chosen strike already has are not synthesized.
    internal static class FontManager
    {
        private const int Bold = 1, Italic = 2, Underline = 4, Outline = 8, Shadow = 16, Condense = 32, Extend = 64;

        public static FontSelection? Swap(PictFontLibrary library, int family, int size, int face,
            (int h, int v) numer, (int h, int v) denom, int spaceExtra)
        {
            if (size <= 0) size = 12;
            if (numer.h == denom.h) numer.h = denom.h = 256;
            if (numer.v == denom.v) numer.v = denom.v = 256;
            if (family == 1) family = library.ApplicationFontId;
            else if (family == 0) family = library.SystemFontId;
            if (!library.HasFamily(family))
            {
                if (library.HasFamily(library.ApplicationFontId)) family = library.ApplicationFontId;
                else if (library.HasFamily(library.SystemFontId)) family = library.SystemFontId;
                else return null;
            }

            int strikeFace = 0, fontSize;
            BitmapFont? strike = null;
            var fond = library.Family(family);
            if (fond != null && fond.Associations.Length > 0)
            {
                fontSize = ClosestSize(fond.Associations.Select(a => a.Size), size, preferPowerOfTwo: true);
                FontFamilyRecord.Association? best = null;
                int matched = -1;
                foreach (var a in fond.Associations)
                {
                    if (a.Size > fontSize) break;
                    best ??= a;
                    if (a.Size == fontSize && (a.Style & ~face) == 0 && CountBits(a.Style & face) >= matched)
                    {
                        matched = CountBits(a.Style & face);
                        best = a;
                    }
                }
                if (best is { } b)
                {
                    strikeFace = b.Style;
                    strike = library.Strike(b.FontId);
                }
                strike ??= library.OldStyleStrike(family, fontSize);
            }
            else
            {
                var sizes = library.OldStyleSizes(family).ToArray();
                if (Array.IndexOf(sizes, size) >= 0) fontSize = size;
                else if (Array.IndexOf(sizes, size * 2) >= 0) fontSize = size * 2;
                else if (size / 2 > 0 && Array.IndexOf(sizes, size / 2) >= 0) fontSize = size / 2;
                else fontSize = ClosestSize(sizes, size, preferPowerOfTwo: false);
                strike = library.OldStyleStrike(family, fontSize);
            }
            if (strike == null || fontSize <= 0) return null;

            var s = new FontSelection { Font = strike };
            int style = face & ~strikeFace;
            if ((style & Bold) != 0) { s.Bold += 1; s.Extra += 1; }
            if ((style & Italic) != 0) s.Italic += 8;
            if ((style & Outline) != 0) { s.Shadow += 1; s.Extra += 1; }
            if ((style & Shadow) != 0) { s.Shadow += 2; s.Extra += 2; }
            if ((style & Condense) != 0) s.Extra -= 1;
            if ((style & Extend) != 0) s.Extra += 1;
            if ((style & Underline) != 0) { s.UlOffset = 1; s.UlShadow = 1; s.UlThick = 1; }

            s.Numer = ((int)((long)numer.h * 256 * size / denom.h / fontSize), (int)((long)numer.v * 256 * size / denom.v / fontSize));
            s.Denom = (256, 256);

            var f = strike;
            int missing = ((f.OffsetWidths[f.MissingIndex] & 0xFF) + s.Extra) << 16;
            for (int c = 0; c < 256; c++)
            {
                int ow = c >= f.FirstChar && c <= f.LastChar ? f.OffsetWidths[c - f.FirstChar] : -1;
                s.Widths[c] = ow == -1 ? missing : ((ow & 0xFF) + s.Extra) << 16;
            }
            s.Widths[' '] += spaceExtra;
            return s;
        }

        // Exact size; else (sizes from a family record) half or double the size; else the nearest, the smaller on
        // a tie.
        private static int ClosestSize(System.Collections.Generic.IEnumerable<int> sizes, int size, bool preferPowerOfTwo)
        {
            int powerOfTwo = 0, lesser = 0, greater = int.MaxValue;
            foreach (int s in sizes)
            {
                if (s < size)
                {
                    if (s == size / 2) powerOfTwo = s;
                    if (s > lesser) lesser = s;
                }
                else if (s == size) return s;
                else
                {
                    if (s == size * 2) powerOfTwo = s;
                    if (s < greater) greater = s;
                }
            }
            if (preferPowerOfTwo && powerOfTwo != 0) return powerOfTwo;
            if (greater == int.MaxValue) return lesser;
            return lesser != 0 && size - lesser <= greater - size ? lesser : greater;
        }

        private static int CountBits(int v)
        {
            int n = 0;
            for (; v != 0; v &= v - 1) n++;
            return n;
        }
    }
}
