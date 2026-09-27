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
        public (ushort r, ushort g, ushort b) Rgb16;   // its exact 16-bit components

        public static Pattern Black => FromMono(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        public static Pattern White => FromMono(new byte[8]);

        public static Pattern FromMono(byte[] rows) => new Pattern { Mono = rows };

        // BkPixPat / PnPixPat / FillPixPat operands: patType, the 1-bit fallback pattern, then a PixMap + ColorTable +
        // PixData or an RGBColor. The ROM reads the RGBColor for type 2 only; Mac OS 9 reads a PixMap for types 1 and 3
        // only.
        public static Pattern Read(BinaryReader b, bool macOS9)
        {
            int patType = b.ReadU16BE();
            var pattern = FromMono(b.ReadExactly(8));
            if (macOS9 ? patType != 1 && patType != 3 : patType == 2)
            {
                int r = b.ReadU16BE(), g = b.ReadU16BE(), bl = b.ReadU16BE();
                pattern.Rgb = new PictColor((byte)(r >> 8), (byte)(g >> 8), (byte)(bl >> 8));
                pattern.Rgb16 = ((ushort)r, (ushort)g, (ushort)bl);
            }
            else
                pattern.Pixels = PixMap.ReadPatternPixMap(b, macOS9);
            return pattern;
        }
    }
}
