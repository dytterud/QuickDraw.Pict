using System;

namespace QuickDraw.Pict
{
    // Transfers a pattern, or a 1-bit mask, through a region onto the canvas. Patterns are aligned to the canvas
    // origin shifted by the picture's pattern alignment (align: the Origin opcodes' accumulated dh, dv), so pixel
    // (x, y) takes pattern cell ((x + align.h) & 7, (y + align.v) & 7), as DrawPicture aligns them. A pixel the picture never drew (alpha 0) reads as
    // white, the erased background of a fresh port; pixels a mode leaves alone keep their alpha.
    internal static class Painter
    {
        private static readonly PictColor White = new PictColor(255, 255, 255);

        public static void FillRegion(PictBitmap canvas, Region region, Region? clip, Pattern pattern, (int h, int v) align,
            int mode, bool hilitePending, in PortColors colors)
        {
            var area = Visible(canvas, region, clip);
            if (area.IsEmpty) return;
            int m = TransferModes.Normalize(mode, hilitePending);
            bool colorPattern = pattern.Pixels != null || pattern.Rgb != null;

            foreach (var r in area.Rectangles())
                for (int y = r.Top; y < r.Bottom; y++)
                    for (int x = r.Left; x < r.Right; x++)
                    {
                        var dst = Read(canvas, x, y);
                        bool write;
                        PictColor result;
                        if (colorPattern)
                        {
                            // Color QuickDraw copies pixel patterns directly, ignoring Boolean pattern modes.
                            var src = pattern.Rgb ?? PatternPixel(pattern.Pixels!, x + align.h, y + align.v);
                            if (TransferModes.IsArithmetic(m) || m == TransferModes.Hilite)
                                write = TransferModes.ApplyColor(m, src, dst, colors, out result);
                            else
                            {
                                result = src;
                                write = true;
                            }
                        }
                        else
                        {
                            bool bit = ((pattern.Mono[(y + align.v) & 7] >> (7 - ((x + align.h) & 7))) & 1) != 0;
                            write = TransferModes.ApplyBit(m, bit, dst, colors, out result);
                        }
                        if (write) Write(canvas, x, y, result);
                    }
        }

        // A 1-bit mask (text) placed with its top-left at (left, top), transferred with a source mode.
        public static void FillMask(PictBitmap canvas, int left, int top, int width, int height, byte[] bits,
            Region? clip, int mode, bool hilitePending, in PortColors colors)
        {
            var area = Visible(canvas, Region.FromRect(new PictRect(top, left, top + height, left + width)), clip);
            if (area.IsEmpty) return;
            int m = TransferModes.Normalize(mode, hilitePending);
            foreach (var r in area.Rectangles())
                for (int y = r.Top; y < r.Bottom; y++)
                    for (int x = r.Left; x < r.Right; x++)
                    {
                        bool bit = bits[(y - top) * width + (x - left)] != 0;
                        if (TransferModes.ApplyBit(m, bit, Read(canvas, x, y), colors, out var result))
                            Write(canvas, x, y, result);
                    }
        }

        private static Region Visible(PictBitmap canvas, Region region, Region? clip)
        {
            var area = region.Intersect(Region.FromRect(new PictRect(0, 0, canvas.Height, canvas.Width)));
            return clip == null ? area : area.Intersect(clip);
        }

        private static PictColor PatternPixel(PixMap pm, int x, int y)
        {
            int w = Math.Max(1, pm.Width), h = Math.Max(1, pm.Height);
            return pm.GetPixel(((x % w) + w) % w, ((y % h) + h) % h);
        }

        private static PictColor Read(PictBitmap canvas, int x, int y)
        {
            int i = (y * canvas.Width + x) * 4;
            var p = canvas.Pixels;
            return p[i + 3] == 0 ? White : new PictColor(p[i], p[i + 1], p[i + 2], p[i + 3]);
        }

        private static void Write(PictBitmap canvas, int x, int y, PictColor c)
        {
            int i = (y * canvas.Width + x) * 4;
            var p = canvas.Pixels;
            p[i] = c.R; p[i + 1] = c.G; p[i + 2] = c.B; p[i + 3] = 255;
        }
    }
}
