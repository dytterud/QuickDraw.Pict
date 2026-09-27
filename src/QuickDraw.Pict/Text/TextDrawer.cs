using System;
using System.Collections.Generic;

namespace QuickDraw.Pict
{
    // The Macintosh ROM's character generator (DrText, 1-bit strikes into a color port): characters are OR-ed into an
    // off-screen 1-bit buffer at 16.16 character locations, the buffer is emboldened, slanted, underlined and (for
    // shadow and outline) turned into its shadow, then transferred to the port with the text mode by one StretchBits
    // (scaled when the Font Manager stretches the strike).
    //
    // Geometry: textRect runs from pen.v - ascent for the strike's height, and from the pen (less any negative
    // first-character kerning: the ROM looks the first character up by its raw code, not code - firstChar) to the
    // integer part of the fixed pen plus the text width - no slop, so ink past the final pen position is clipped -
    // widened for styles by the italic lean and bold/shadow thickness (summed in a byte, as the ROM does). The buffer
    // starts at the long-aligned pixel at or left of textRect and is two longs wider than it. Characters start at the
    // pen's own fraction; spaces advance by the space width only, other characters by their width plus the
    // character extra.
    //
    // Styles, as the ROM: bold smears the buffer (one bit stream, rows back to back) right one pixel per bold pixel;
    // italic shifts each row right by (rows below * italic) / 16; underline fills the row below the baseline across
    // the whole buffer except one pixel around ink in the baseline row and the two below it (descent 2 or more);
    // shadow/outline smears a 4-row-taller copy right and down (shadow & 3) + 1 times, XORs the glyphs out of it one
    // pixel right and down, and draws only that ring, one pixel up-left - a color port leaves the letters' inside
    // untouched.
    //
    // txMode: the pattern bit (8) is cleared; bit 6 (mask) makes the glyph bits a mask for the transfer.
    internal static class TextDrawer
    {
        // Draws the text with the pen at (penH, penV) + penFrac / 65536; returns the pen's new fraction.
        public static int Draw(PictBitmap canvas, FontSelection s, ReadOnlySpan<byte> text, int penH, int penV,
            int penFrac, int charExtra, int textMode, Region? clip, bool hilitePending, in PortColors colors)
        {
            penFrac &= 0xFFFF;
            if (text.Length == 0) return penFrac;
            if (s.MacOS9 && text.Length == 1 && text[0] == '\r') return penFrac;   // Mac OS 9: a lone CR draws nothing
            if (s.Palette != null) return DrawColorFont(canvas, s, text, penH, penV, penFrac, charExtra, clip, hilitePending, colors);
            var f = s.Font;

            int cx = CharExtra(s, charExtra);
            int width = Measure(s, text, charExtra);

            int mode = textMode & ~8 & 0xFFFF;
            bool masked = (mode & 0x40) != 0;
            mode &= ~0x40;

            // textRect.
            int top = (short)(penV - f.Ascent);
            PictRect textRect;
            if (s.MacOS9)
            {
                // Mac OS 9: the union of the glyph images (and spaces' origins), widened to the rounded advance for
                // copy-like modes or underline, plus bold, italic lean less the extra, and shadow; the left edge gives
                // room for the italic lean below the baseline.
                var (inkLeft, inkRight, inkTop, inkBottom) = InkExtent(s, text, cx);
                int rounded = (width + 0x8000) >> 16, it = (sbyte)s.Italic;
                int right = inkRight;
                bool extended = mode == 0 || mode > 3 || s.UlThick != 0;
                if (extended) right = Math.Max(right, rounded);
                right += s.Bold + (it != 0 ? it * (f.Ascent - 1) / 16 - s.Extra : 0) + (s.Shadow != 0 ? Math.Min(s.Shadow + 1, 4) : 0);
                if (s.Extra > 0) right = Math.Max(right - s.Extra, rounded);
                int left = Math.Min(inkLeft - (it != 0 ? it * (f.Descent - 1) / 16 : 0), 0);
                // Vertically the ink rows, from the baseline up at least, for srcOr / srcXor / srcBic; the whole font
                // rect for the other modes, underline and shadow.
                int rectTop = top, rectBottom = top + f.RectHeight;
                if (!extended && s.Shadow == 0)
                    (rectTop, rectBottom) = (penV + Math.Min(inkTop, 0), penV + Math.Max(inkBottom, Math.Min(inkTop, 0)));
                textRect = new PictRect((short)rectTop, (short)(penH + left), (short)rectBottom, (short)(penH + right));
            }
            else
            {
                int kern = 0;
                if (f.KernMax < 0)
                {
                    int ow = f.RawOffsetWidth(text[0]);
                    if (ow == -1) ow = f.RawOffsetWidth(0);
                    int offset = ((ow >> 8) & 0xFF) + f.KernMax;
                    if (offset <= 0) kern = offset;
                }
                int penFixed = unchecked((penH << 16) | penFrac);
                int right = (short)((uint)unchecked(penFixed + width) >> 16);
                if (s.CurStyle != 0)
                {
                    int lean = (short)(ushort)((uint)(ushort)(short)(sbyte)s.Italic * (ushort)(f.Ascent - 1)) >> 4;
                    int slop = (sbyte)(lean + s.Bold - s.Extra);
                    int shadow = (sbyte)s.Shadow;
                    right = (short)(right + slop + (shadow >= 4 ? 4 : shadow == 0 ? 0 : shadow + 1));
                    kern = (short)(kern - ((short)(ushort)((uint)(s.Italic & 0xFF) * (ushort)f.Descent) >> 4));
                }
                textRect = new PictRect(top, (short)(penH + kern), (short)(top + f.RectHeight), right);
            }

            // Scaling: the whole buffer is stretched once from (pen + denom) to (pen + numer).
            bool stretch = s.Numer != s.Denom;
            PictRect fromRect = default, toRect = default, dstRect = textRect;
            int advance = width;
            if (stretch)
            {
                advance = s.MacOS9
                    ? FixedMath.FixMulHalfUp(width, FixedMath.FixRatio((short)s.Numer.h, (short)s.Denom.h))
                    : (int)((ulong)(uint)width * (ushort)s.Numer.h / (ushort)s.Denom.h);
                toRect = new PictRect(penV, penH, penV + s.Numer.v, penH + s.Numer.h);
                fromRect = new PictRect(penV, penH, penV + s.Denom.v, penH + s.Denom.h);
                dstRect = PictureMapping.MapRect(textRect, fromRect, toRect);
            }
            int newFrac = (penFrac + advance) & 0xFFFF;

            // The buffer: whole longs from the long-aligned pixel at or left of textRect, plus one long of slop after
            // it (which bold smears into and the shadow reads back).
            int bufLeft = (short)(textRect.Left & ~31);
            int rowLongs = ((ushort)(textRect.Right - bufLeft) >> 5) + 2;
            int bufWidth = rowLongs * 32, height = f.RectHeight;
            if (height <= 0) return newFrac;
            int length = bufWidth * height;
            var buffer = new bool[length + 32];

            // Characters start at the pen's own fraction (the ROM) or at 1/2, the pen's fraction ignored (Mac OS 9).
            int charLoc = unchecked(((penH + f.KernMax - bufLeft) << 16) | (s.MacOS9 ? 0x8000 : penFrac));
            int span = f.LastChar - f.FirstChar;
            foreach (byte c in text)
            {
                if (c == ' ')
                {
                    charLoc = unchecked(charLoc + s.Widths[' ']);
                    continue;
                }
                int step = unchecked(s.Widths[c] + (s.MacOS9 && s.Widths[c] <= 0 ? 0 : cx));
                int index = (ushort)(c - f.FirstChar);
                int ow = index <= (ushort)span ? f.OffsetWidths[index] : -1;
                if (ow == -1)
                {
                    index = f.MissingIndex;
                    ow = f.OffsetWidths[index];
                    if (ow == -1) continue;                    // no missing symbol: skipped without advancing
                }
                int dstLeft = (short)(((ow >> 8) & 0xFF) + (short)(charLoc >> 16));
                charLoc = unchecked(charLoc + step);
                int srcLeft = f.Locations[index], bits = (short)(f.Locations[index + 1] - srcLeft);
                if (bits <= 0) continue;
                int rowTop = 0, rows = height & 0xFF;
                if (f.Heights != null)
                {
                    rowTop = (f.Heights[index] >> 8) & 0xFF;
                    rows = f.Heights[index] & 0xFF;
                }
                for (int y = rowTop; y < rowTop + rows && y < height; y++)
                    for (int x = 0; x < bits; x++)
                        if (f.StrikeBit(y, srcLeft + x))
                        {
                            long at = (long)y * bufWidth + dstLeft + x;
                            if (at >= 0 && at < buffer.Length) buffer[at] = true;
                        }
            }

            for (int i = 0; i < (s.Bold & 0xFF); i++) SmearRight(buffer, buffer.Length);
            if (s.MacOS9 && (sbyte)s.Italic != 0) SlantMacOS9(buffer, bufWidth, height, f.Ascent, (sbyte)s.Italic);
            else if (!s.MacOS9 && (s.Italic & 0xFF) != 0) Slant(buffer, bufWidth, height, s.Italic & 0xFF);
            if (s.UlThick != 0) Underline(buffer, bufWidth, height, f.Ascent, f.Descent);

            if (s.Shadow != 0)
            {
                int passes = (s.Shadow & 3) + 1;
                int shadowHeight = height + 4;
                var shadow = new bool[bufWidth * shadowHeight];
                Array.Copy(buffer, shadow, length);
                for (int i = 0; i < passes; i++) SmearRight(shadow, length + 32);
                for (int i = 0; i < passes; i++) SmearDown(shadow, bufWidth, shadowHeight);
                for (int j = length + 31; j >= 1; j--)       // XOR the glyphs out, one pixel right and down
                    if (buffer[j - 1]) shadow[bufWidth + j] = !shadow[bufWidth + j];
                var srcRect = new PictRect(textRect.Top, textRect.Left, textRect.Bottom + 4, textRect.Right);
                var shadowDst = new PictRect(srcRect.Top - 1, srcRect.Left - 1, srcRect.Bottom - 1, srcRect.Right - 1);
                if (stretch) shadowDst = PictureMapping.MapRect(shadowDst, fromRect, toRect);
                var pix = ToPixMap(shadow, bufWidth, shadowHeight, new PictRect(textRect.Top, bufLeft, textRect.Bottom + 4, textRect.Right));
                Blit(canvas, pix, srcRect, shadowDst, mode, masked, clip, hilitePending, colors);
                return newFrac;
            }
            Blit(canvas, ToPixMap(buffer, bufWidth, height, new PictRect(top, bufLeft, top + height, textRect.Right)),
                textRect, dstRect, mode, masked, clip, hilitePending, colors);
            return newFrac;
        }

        // Color fonts (2-8-bit strikes), as Mac OS 9 draws them: each glyph's whole box - its strike columns over the
        // strike's full height - copied opaque with srcCopy through the font's colors (and the port's colorizing), at
        // the pen (with its fraction) + the advance so far + kernMax + the glyph's offset. Stretched, only the advance
        // and the box width scale (width rounded half up; the offsets stay unscaled; verified against Mac OS 9 at
        // 3/2, 4/3 and 1/3). The text mode, styles and missing-glyph handling are ignored.
        private static int DrawColorFont(PictBitmap canvas, FontSelection s, ReadOnlySpan<byte> text, int penH, int penV,
            int penFrac, int charExtra, Region? clip, bool hilitePending, in PortColors colors)
        {
            var f = s.Font;
            var strike = f.StrikeMap(s.Palette!);
            int cx = CharExtra(s, charExtra), width = Measure(s, text, charExtra);
            bool stretch = s.Numer != s.Denom;
            var toRect = new PictRect(penV, penH, penV + s.Numer.v, penH + s.Numer.h);
            var fromRect = new PictRect(penV, penH, penV + s.Denom.v, penH + s.Denom.h);
            int top = penV - f.Ascent, span = f.LastChar - f.FirstChar;
            long penFixed = ((long)penH << 16) | (uint)penFrac;
            int advanced = 0;                                 // unscaled Fixed advance so far
            foreach (byte c in text)
            {
                int step = c == ' ' ? s.Widths[' '] : unchecked(s.Widths[c] + (s.MacOS9 && s.Widths[c] <= 0 ? 0 : cx));
                int index = c - f.FirstChar;
                if (index >= 0 && index <= span)
                {
                    int ow = f.OffsetWidths[index], srcLeft = f.Locations[index];
                    int bits = (short)(f.Locations[index + 1] - srcLeft);
                    if (bits > 0)
                    {
                        long scaled = stretch ? (long)advanced * (ushort)s.Numer.h / (ushort)s.Denom.h : advanced;
                        int x = (short)((penFixed + scaled) >> 16) + f.KernMax + ((ow >> 8) & 0xFF);
                        int w = stretch ? (int)((2L * bits * (ushort)s.Numer.h + (ushort)s.Denom.h) / (2L * (ushort)s.Denom.h)) : bits;
                        var box = new PictRect(top, x, top + f.RectHeight, x + w);
                        if (stretch)
                        {
                            var v = PictureMapping.MapRect(box, fromRect, toRect);
                            box = new PictRect(v.Top, x, v.Bottom, x + w);
                        }
                        if (w > 0)
                            Bits.CopyBits(canvas, strike, new PictRect(0, srcLeft, f.RectHeight, srcLeft + bits), box,
                                TransferModes.SrcCopy, clip, hilitePending, colors, false);
                    }
                }
                advanced = unchecked(advanced + step);
            }
            int advance = !stretch ? width : s.MacOS9
                ? FixedMath.FixMulHalfUp(width, FixedMath.FixRatio((short)s.Numer.h, (short)s.Denom.h))
                : (int)((ulong)(uint)width * (ushort)s.Numer.h / (ushort)s.Denom.h);
            return (penFrac + advance) & 0xFFFF;
        }

        // Character extra (Fixed per point) in strike pixels: x size x text scale x FOutDenom / FOutNumer (Mac OS 9's
        // multiplies round half up).
        private static int CharExtra(FontSelection s, int charExtra)
        {
            if (charExtra == 0) return 0;
            Func<int, int, int> mul = s.MacOS9 ? FixedMath.FixMulHalfUp : FixedMath.FixMul;
            return mul(mul(charExtra, s.Size << 16), mul(
                FixedMath.FixRatio((short)s.InNumer.h, (short)s.InDenom.h), FixedMath.FixRatio((short)s.Denom.h, (short)s.Numer.h)));
        }

        // StdTxMeas's FixTxWid: the widths plus the character extra on everything but spaces (unscaled, Fixed); Mac
        // OS 9 adds the extra only to characters with a width.
        public static int Measure(FontSelection s, ReadOnlySpan<byte> text, int charExtra)
        {
            int cx = CharExtra(s, charExtra), width = 0;
            foreach (byte c in text)
                width = unchecked(width + s.Widths[c] + (c == ' ' || (s.MacOS9 && s.Widths[c] <= 0) ? 0 : cx));
            return width;
        }

        // Mac OS 9's ink union, relative to the pen: each glyph image at ((advance so far + 1/2) >> 16) + its offset +
        // kernMax, for its strike width, and each space's origin.
        private static (int left, int right, int top, int bottom) InkExtent(FontSelection s, ReadOnlySpan<byte> text, int cx)
        {
            var f = s.Font;
            int left = int.MaxValue, right = int.MinValue, advance = 0, span = f.LastChar - f.FirstChar;
            int top = int.MaxValue, bottom = int.MinValue;              // baseline-relative ink rows [top, bottom)
            foreach (byte c in text)
            {
                int origin = (advance + 0x8000) >> 16;
                if (c == ' ')
                {
                    (left, right) = (Math.Min(left, origin), Math.Max(right, origin));
                    (top, bottom) = (Math.Min(top, 0), Math.Max(bottom, 0));
                    advance = unchecked(advance + s.Widths[' ']);
                    continue;
                }
                int index = (ushort)(c - f.FirstChar);
                int ow = index <= (ushort)span ? f.OffsetWidths[index] : -1;
                if (ow == -1)
                {
                    index = f.MissingIndex;
                    ow = f.OffsetWidths[index];
                    if (ow == -1) continue;
                }
                advance = unchecked(advance + s.Widths[c] + (s.Widths[c] <= 0 ? 0 : cx));
                int bits = (short)(f.Locations[index + 1] - f.Locations[index]);
                if (bits <= 0) continue;
                int x = origin + ((ow >> 8) & 0xFF) + f.KernMax;
                (left, right) = (Math.Min(left, x), Math.Max(right, x + bits));
                int srcLeft = f.Locations[index];
                for (int y = 0; y < f.RectHeight; y++)
                    for (int b = 0; b < bits; b++)
                        if (f.StrikeBit(y, srcLeft + b))
                        {
                            (top, bottom) = (Math.Min(top, y - f.Ascent), Math.Max(bottom, y - f.Ascent + 1));
                            break;
                        }
            }
            if (top > bottom) (top, bottom) = (0, 0);
            return left > right ? (0, 0, top, bottom) : (left, right, top, bottom);
        }

        // Mac OS 9's italic: rows above the baseline's first row below (row `ascent`) shift right by
        // (rows above it x italic) / 16, that row and the ones below shift left by ((rows below + 1) x italic) / 16
        // (truncating divides), each row on its own.
        private static void SlantMacOS9(bool[] b, int width, int height, int ascent, int italic)
        {
            var row = new bool[width];
            for (int y = 0; y < height; y++)
            {
                int shift = y < ascent ? (ascent - y) * italic / 16 : -((y - ascent + 1) * italic / 16);
                if (shift == 0) continue;
                int start = y * width;
                Array.Copy(b, start, row, 0, width);
                for (int x = 0; x < width; x++)
                {
                    int from = x - shift;
                    b[start + x] = from >= 0 && from < width && row[from];
                }
            }
        }

        // StretchBits with the text mode; a masked mode uses the bits themselves as the mask (so only the glyphs'
        // pixels are touched).
        private static void Blit(PictBitmap canvas, PixMap bits, PictRect srcRect, PictRect dstRect, int mode, bool masked,
            Region? clip, bool hilitePending, in PortColors colors)
        {
            if (masked)
            {
                var mask = MaskRegion(bits, srcRect, dstRect, colors.MacOS9);
                clip = clip == null ? mask : clip.Intersect(mask);
            }
            Bits.CopyBits(canvas, bits, srcRect, dstRect, mode, clip, hilitePending, colors, false);
        }

        private static Region MaskRegion(PixMap bits, PictRect srcRect, PictRect dstRect, bool macOS9)
        {
            if (dstRect.IsEmpty) return Region.Empty;
            var scratch = new PictBitmap(dstRect.Width, dstRect.Height);
            var black = new PictColor(0, 0, 0);
            Bits.CopyBits(scratch, bits, srcRect, new PictRect(0, 0, dstRect.Height, dstRect.Width), TransferModes.SrcCopy,
                null, false, new PortColors(black, new PictColor(255, 255, 255), default, default, macOS9), false);
            var rows = new SortedDictionary<int, List<int>>();
            for (int y = 0; y < scratch.Height; y++)
                for (int x = 0; x < scratch.Width; x++)
                    if (scratch[x, y] == black)
                    {
                        if (!rows.TryGetValue(y + dstRect.Top, out var runs)) rows[y + dstRect.Top] = runs = new List<int>();
                        runs.Add(x + dstRect.Left);
                        runs.Add(x + dstRect.Left + 1);
                    }
            return Region.FromScanlines(rows);
        }

        // ROXR.L #1 / OR over the first `bits` of the stream: each set bit also sets the one after it.
        private static void SmearRight(bool[] b, int bits)
        {
            for (int i = Math.Min(bits, b.Length) - 1; i > 0; i--)
                if (b[i - 1]) b[i] = true;
        }

        private static void SmearDown(bool[] b, int width, int height)
        {
            for (int y = height - 1; y > 0; y--)
                for (int x = 0; x < width; x++)
                    if (b[(y - 1) * width + x]) b[y * width + x] = true;
        }

        // Italic: working up from the second-to-last row, row k (0 = bottom) takes the stream bits (k * italic) / 16
        // to its left (bits left of the buffer read as 0).
        private static void Slant(bool[] b, int width, int height, int italic)
        {
            int offset = 0;
            for (int y = height - 2; y >= 0; y--)
            {
                offset += italic;
                int delta = offset >> 4;
                int start = y * width;
                for (int x = width - 1; x >= 0; x--)
                {
                    int from = start + x - delta;
                    b[start + x] = from >= 0 && b[from];
                }
            }
        }

        // Underline the row below the baseline row, except one pixel around ink in the baseline row and the two below.
        private static void Underline(bool[] b, int width, int height, int ascent, int descent)
        {
            if (descent < 2) return;
            int r0 = ascent, r1 = ascent + 1, r2 = descent == 2 ? ascent : ascent + 2;
            if (r0 < 0 || r1 >= height) return;
            bool Ink(int row, int x) => row < height && b[row * width + x];
            var ink = new bool[width];
            for (int x = 0; x < width; x++) ink[x] = Ink(r0, x) || Ink(r1, x) || Ink(r2, x);
            for (int x = 0; x < width; x++)
                if (!(ink[x] || (x > 0 && ink[x - 1]) || (x + 1 < width && ink[x + 1]))) b[r1 * width + x] = true;
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
