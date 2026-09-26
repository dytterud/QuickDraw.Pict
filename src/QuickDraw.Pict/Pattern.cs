using System.IO;

namespace QuickDraw.Pict
{
    // A QuickDraw pattern: the classic 8x8 1-bit Pattern (MSB = leftmost pixel, one byte per row; a 1 bit draws the
    // foreground color), optionally replaced by a color PixPat: type 1 is a full PixMap pattern, type 2 (ditherPat)
    // a solid RGB color. Operand layout: Inside Macintosh: Imaging With QuickDraw, Appendix A, Listing A-1.
    internal sealed class Pattern
    {
        public byte[] Mono = new byte[8];
        public PixMap? Pixels;          // PixPat type 1
        public PictColor? Rgb;          // PixPat type 2 (ditherPat)

        public static Pattern Black => FromMono(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        public static Pattern White => FromMono(new byte[8]);

        public static Pattern FromMono(byte[] rows) => new Pattern { Mono = rows };

        // BkPixPat / PnPixPat / FillPixPat operands: patType, the 1-bit fallback pattern, then an RGBColor (type 2)
        // or PixMap + ColorTable + PixData (any other type, as Executor's eatPixPat).
        public static Pattern Read(BinaryReader b)
        {
            int patType = b.ReadU16BE();
            var pattern = FromMono(b.ReadExactly(8));
            if (patType == 2)
                pattern.Rgb = PictReader.ReadRgb(b);
            else
                pattern.Pixels = PixMap.ReadPatternPixMap(b);
            return pattern;
        }
    }
}
