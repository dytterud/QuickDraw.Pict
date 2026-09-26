using System;

namespace QuickDraw.Pict
{
    // QuickDraw's character generator (DrText): characters are OR-ed into an off-screen 1-bit buffer at 16.16
    // character locations, the buffer is then emboldened, slanted and underlined, and transferred to the port with
    // the text mode through StretchBits (scaled when the font was stretched), after an optional shadow/outline pass.
    //
    // Geometry: textRect spans the pen location to pen + width (plus 32 pixels of slop for the Boolean or/xor/bic
    // modes), from pen.v - ascent for the strike's height. The buffer starts 32 pixels left of the 16-pixel word
    // holding the pen and is whole longs wide, at least one long wider than textRect.
    internal static class TextDrawer
    {
        public static void Draw(PictBitmap canvas, FontSelection s, ReadOnlySpan<byte> text, int penH, int penV,
            int textMode, Region? clip, bool hilitePending, in PortColors colors)
        {
            if (text.Length == 0) return;
            var f = s.Font;
            int fixedWidth = 0;
            foreach (byte c in text) fixedWidth += s.Widths[c];
            int width = (short)(fixedWidth >> 16);

            int mode3 = textMode & 7;
            var textRect = new PictRect(penV - f.Ascent, penH, penV - f.Ascent + f.RectHeight,
                penH + width + (mode3 != 0 && mode3 <= 3 ? 32 : 0));
            var dstRect = textRect;
            bool stretch = s.Numer != s.Denom;
            PictRect fromRect = default, toRect = default;
            if (stretch)
            {
                toRect = new PictRect(penV, penH, penV + s.Numer.v, penH + s.Numer.h);
                fromRect = new PictRect(penV, penH, penV + s.Denom.v, penH + s.Denom.h);
                dstRect = PictureMapping.MapRect(textRect, fromRect, toRect);
            }

            // Off-screen buffer: whole longs, word-aligned 32 pixels left of the text.
            int bufLeft = (textRect.Left & ~15) - 32;
            int rowLongs = ((ushort)(textRect.Right - bufLeft) >> 5) + 2;
            int bufWidth = rowLongs * 32, height = f.RectHeight;
            if (height <= 0) return;
            var buffer = new bool[bufWidth * height];

            // Characters. Spaces only advance; a missing character draws the missing symbol (skipped if the font
            // has none), a character with an empty image only advances.
            long charLoc = ((long)(penH + f.KernMax - bufLeft) << 16) | 0x8000;
            foreach (byte c in text)
            {
                if (c == ' ')
                {
                    charLoc += s.Widths[' '];
                    continue;
                }
                int index = c - f.FirstChar;
                int ow = index >= 0 && c <= f.LastChar ? f.OffsetWidths[index] : -1;
                if (ow < 0)
                {
                    index = f.MissingIndex;
                    ow = f.OffsetWidths[index];
                    if (ow < 0) continue;
                }
                int dstLeft = (ow >> 8) + (short)(charLoc >> 16);
                charLoc += s.Widths[c];
                int srcLeft = f.Locations[index], bits = f.Locations[index + 1] - srcLeft;
                if (bits <= 0) continue;
                int top = 0, rows = height;
                if (f.Heights != null)
                {
                    top = f.Heights[index] >> 8;
                    rows = f.Heights[index] & 0xFF;
                }
                for (int y = top; y < top + rows && y < height; y++)
                    for (int x = 0; x < bits; x++)
                        if (f.StrikeBit(y, srcLeft + x))
                        {
                            int bx = dstLeft + x;
                            if (bx >= 0 && bx < bufWidth) buffer[y * bufWidth + bx] = true;
                        }
            }
            int lastRight = (short)(charLoc >> 16) - f.KernMax;

            for (int i = 0; i < s.Bold; i++) SmearRight(buffer);
            if (s.Italic != 0) Slant(buffer, bufWidth, height, s.Italic);
            if (s.UlThick != 0) Underline(buffer, bufWidth, f.Ascent, f.Descent, lastRight);

            var bitmapBounds = new PictRect(textRect.Top, bufLeft, textRect.Bottom, textRect.Right);
            if (s.Shadow != 0)
            {
                // Shadow buffer: the text 4 rows taller, emboldened right and down (shadow & 3) + 1 times, drawn
                // with the text mode one pixel up-left; the text itself is then XOR-ed over it.
                var shadow = new bool[bufWidth * (height + 4)];
                Array.Copy(buffer, shadow, buffer.Length);
                int passes = (s.Shadow & 3) + 1;
                for (int i = 0; i < passes; i++) SmearRight(shadow);
                for (int i = 0; i < passes; i++) SmearDown(shadow, bufWidth, height + 4);
                var srcRect = new PictRect(textRect.Top, textRect.Left, textRect.Bottom + 4, textRect.Right);
                var shadowDst = new PictRect(srcRect.Top - 1, srcRect.Left - 1, srcRect.Bottom - 1, srcRect.Right - 1);
                if (stretch) shadowDst = PictureMapping.MapRect(shadowDst, fromRect, toRect);
                Bits.CopyBits(canvas, ToPixMap(shadow, bufWidth, height + 4,
                        new PictRect(bitmapBounds.Top, bitmapBounds.Left, bitmapBounds.Bottom + 4, bitmapBounds.Right)),
                    srcRect, shadowDst, mode3, clip, hilitePending, colors, false);
                Bits.CopyBits(canvas, ToPixMap(buffer, bufWidth, height, bitmapBounds), textRect, dstRect,
                    TransferModes.SrcXor, clip, false, colors, false);
                return;
            }
            Bits.CopyBits(canvas, ToPixMap(buffer, bufWidth, height, bitmapBounds), textRect, dstRect, textMode, clip,
                hilitePending, colors, false);
        }

        // Bold: the buffer as one bit stream (rows back to back) OR-ed with itself shifted right one pixel.
        private static void SmearRight(bool[] b)
        {
            for (int i = b.Length - 1; i > 0; i--)
                if (b[i - 1]) b[i] = true;
        }

        private static void SmearDown(bool[] b, int width, int height)
        {
            for (int y = height - 1; y > 0; y--)
                for (int x = 0; x < width; x++)
                    if (b[(y - 1) * width + x]) b[y * width + x] = true;
        }

        // Italic: working up from the bottom row, row k (0 = bottom) moves right by (k * italic) / 16 pixels,
        // pulling in the (still unslanted) stream bits to its left.
        private static void Slant(bool[] b, int width, int height, int italic)
        {
            int offset = 0;
            for (int y = height - 2; y >= 0; y--)
            {
                offset = (ushort)(offset + italic);
                int delta = offset >> 4;
                int start = y * width;
                for (int x = width - 1; x >= 0; x--)
                {
                    int from = start + x - delta;
                    b[start + x] = from >= 0 && b[from];
                }
            }
        }

        // Underline one row below the baseline, broken one pixel around any ink in the baseline row and the two below
        // it, and ending at the final pen position. Needs a descent of at least 2.
        private static void Underline(bool[] b, int width, int ascent, int descent, int lastRight)
        {
            if (descent < 2) return;
            int r0 = ascent, r1 = ascent + 1, r2 = descent == 2 ? r1 : ascent + 2;
            int height = b.Length / width;
            if (r2 >= height) return;
            var ink = new bool[width];
            for (int x = 0; x < width; x++)
                ink[x] = b[r0 * width + x] || b[r1 * width + x] || b[r2 * width + x];
            var spread = new bool[width];
            for (int x = 0; x < width; x++)
                spread[x] = ink[x] || (x > 0 && ink[x - 1]) || (x + 1 < width && ink[x + 1]);
            for (int x = 0; x < width && x < lastRight; x++)
                if (!spread[x]) b[r1 * width + x] = true;
        }

        private static PixMap ToPixMap(bool[] bits, int width, int height, PictRect bounds)
        {
            int rowBytes = width / 8;
            var data = new byte[rowBytes * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (bits[y * width + x]) data[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            return new PixMap
            {
                Bounds = bounds,
                RowBytes = rowBytes,
                PixelSize = 1,
                Palette = new[] { new PictColor(255, 255, 255), new PictColor(0, 0, 0) },
                Data = data,
            };
        }
    }
}
