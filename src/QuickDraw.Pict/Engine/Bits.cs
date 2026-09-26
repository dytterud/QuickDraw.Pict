using System;
using System.Collections.Generic;

namespace QuickDraw.Pict
{
    // CopyBits (StdBits → StretchBits) onto the canvas: srcRect of a picture's BitMap/PixMap to dstRect, clipped to the
    // canvas and the mask (the picture's clip ∩ its mask region), through a source transfer mode.
    //
    // Scaling follows the ROM's StretchBits: a vertical DDA that merges source rows into each destination row (error
    // term starting at -(srcH - 1) for exact integer shrinks, else -(srcH / 2)), then a horizontal row scaler. 1-bit
    // sources use the 1984 row stretchers (exact fast paths for x1.5, x2, x3, x4, x6, x8, x16 and multiples of 8, and
    // for x1/2, x1/4 and x3/4, else a 16-bit fraction stepper) and OR merged pixels (black wins). Deeper sources step
    // the fraction for every ratio: stretching replicates pixels; shrinking merges each group, rows first then
    // columns, into the largest index (2-8 bits) or the truncated per-component average (16 and 32 bits). Merging
    // and scaling happen at the source depth, before color conversion.
    internal static class Bits
    {
        public static void CopyBits(PictBitmap canvas, PixMap src, PictRect srcRect, PictRect dstRect, int mode,
            Region? mask, bool hilitePending, in PortColors colors, bool preserveAlpha)
        {
            if (srcRect.IsEmpty || dstRect.IsEmpty) return;
            var area = Region.FromRect(dstRect).Intersect(Region.FromRect(new PictRect(0, 0, canvas.Height, canvas.Width)));
            if (mask != null) area = area.Intersect(mask);
            if (area.IsEmpty) return;

            int srcW = srcRect.Width, srcH = srcRect.Height, dstW = dstRect.Width, dstH = dstRect.Height;
            int srcTop = srcRect.Top - src.Bounds.Top, srcLeft = srcRect.Left - src.Bounds.Left;
            bool scaled = srcW != dstW || srcH != dstH;
            var rows = scaled ? RowGroups(srcTop, srcH, dstH, src.Height) : null;
            var cols = scaled ? (src.PixelSize == 1 ? ColumnGroups(srcW, dstW) : DeepColumnGroups(srcW, dstW)) : null;
            bool averagedRows = dstH < srcH;

            mode &= ~TransferModes.DitherCopy;
            bool bilevel = src.PixelSize == 1 && IsBlackAndWhite(src.Palette);
            bool keepAlpha = preserveAlpha && src.PixelSize == 32 && src.CmpCount == 4 && mode == TransferModes.SrcCopy;
            int bitCap = 32 * ((srcW - 1) / 32 + 1);          // StretchBits reads whole longs of each source row

            foreach (var r in area.Rectangles())
                for (int y = r.Top; y < r.Bottom; y++)
                {
                    int dy = y - dstRect.Top;
                    int[]? group = rows == null ? new[] { srcTop + dy } : rows[dy];
                    if (group == null) continue;              // the source ran out before this row
                    for (int x = r.Left; x < r.Right; x++)
                    {
                        int dx = x - dstRect.Left;
                        var (first, end) = cols == null ? (dx, dx + 1) : cols[dx];
                        var dst = Painter.ReadPixel(canvas, x, y);
                        bool write;
                        PictColor result;
                        byte alpha = 255;
                        if (src.PixelSize == 1)
                        {
                            if (!TryBit(src, group, srcLeft, first, end, scaled ? bitCap : srcW, scaled, out bool bit)) continue;
                            if (bilevel)
                                write = TransferModes.ApplyBit(TransferModes.Normalize(mode, hilitePending), bit, dst, colors, out result);
                            else
                                write = ApplyColorSource(mode, hilitePending, src.Palette[bit ? 1 : 0], dst, colors, out result);
                        }
                        else if (!scaled)
                        {
                            int sy = group[0], sx = srcLeft + first;
                            if (sy < 0 || sy >= src.Height || sx < 0 || sx >= src.Width) continue;
                            write = ApplyColorSource(mode, hilitePending, src.GetPixel(sx, sy), dst, colors, out result);
                            if (keepAlpha) alpha = src.GetAlpha(sx, sy);
                        }
                        else
                        {
                            if (!TryDeep(src, group, srcLeft, first, end, out var color, out bool averaged, out byte a)) continue;
                            write = ApplyColorSource(mode, hilitePending, color, dst, colors, out result);
                            if (keepAlpha) alpha = averaged || averagedRows ? (byte)0 : a;
                        }
                        if (write) Painter.WritePixel(canvas, x, y, result, alpha);
                    }
                }
        }

        private static bool IsBlackAndWhite(PictColor[] palette) =>
            palette.Length >= 2 && TransferModes.SameRgb(palette[0], new PictColor(255, 255, 255)) &&
            TransferModes.SameRgb(palette[1], new PictColor(0, 0, 0));

        // OR of the 1-bit source over the merged rows and columns [first, end) (relative to srcRect's left). Scaled
        // copies read the source's memory linearly the way StretchBits' row buffer does, zero past `cap` columns;
        // unscaled copies only take the bitmap's own pixels. False when no merged pixel was readable.
        private static bool TryBit(PixMap src, int[] rows, int srcLeft, int first, int end, int cap, bool scaled, out bool bit)
        {
            bit = false;
            bool any = false;
            long rowBits = src.RowBytes * 8L, totalBits = src.Data.Length * 8L;
            foreach (int row in rows)
                for (int i = first; i < end && i < cap; i++)
                {
                    long address = row * rowBits + srcLeft + i;
                    if (!scaled && (srcLeft + i < 0 || srcLeft + i >= src.Width || row < 0 || row >= src.Height)) continue;
                    if (address < 0 || address >= totalBits) continue;
                    any = true;
                    if (((src.Data[address >> 3] >> (7 - (int)(address & 7))) & 1) != 0) bit = true;
                }
            return any;
        }

        // A scaled deep pixel: the rows then columns of its group merged at the source depth (largest index for 2-8
        // bits; truncated per-component average of 5-bit or 8-bit components for 16 and 32 bits), then converted.
        private static bool TryDeep(PixMap src, int[] rows, int srcLeft, int first, int end, out PictColor color,
            out bool averaged, out byte alpha)
        {
            color = default;
            averaged = false;
            alpha = 255;
            int maxIndex = -1, columns = 0;
            int sumR = 0, sumG = 0, sumB = 0;
            for (int i = first; i < end; i++)
            {
                int x = srcLeft + i;
                if (x < 0 || x >= src.Width) continue;
                int n = 0, r = 0, g = 0, b = 0;
                foreach (int row in rows)
                {
                    if (row < 0 || row >= src.Height) continue;
                    if (src.PixelSize <= 8)
                    {
                        maxIndex = Math.Max(maxIndex, src.GetIndex(x, row));
                        n++;
                        continue;
                    }
                    var (cr, cg, cb) = src.GetComponents(x, row);
                    r += cr; g += cg; b += cb;
                    n++;
                    alpha = src.GetAlpha(x, row);
                }
                if (n == 0) continue;
                if (n > 1) averaged = true;
                columns++;
                if (src.PixelSize > 8)
                {
                    sumR += r / n; sumG += g / n; sumB += b / n;
                }
            }
            if (columns == 0) return false;
            if (src.PixelSize <= 8)
            {
                color = maxIndex < src.Palette.Length ? src.Palette[maxIndex] : new PictColor(0, 0, 0);
                return true;
            }
            if (columns > 1) averaged = true;
            int R = columns == 2 ? sumR >> 1 : sumR / columns, G = columns == 2 ? sumG >> 1 : sumG / columns,
                B = columns == 2 ? sumB >> 1 : sumB / columns;
            color = src.PixelSize == 16
                ? new PictColor(Expand5(R), Expand5(G), Expand5(B))
                : new PictColor((byte)R, (byte)G, (byte)B);
            return true;
        }

        private static byte Expand5(int c) => (byte)((c << 3) | (c >> 2));

        // A full-color source pixel through the mode (TransferModes.ApplyBoolean / ApplyColor).
        private static bool ApplyColorSource(int mode, bool hilitePending, PictColor s, PictColor d, in PortColors c,
            out PictColor result)
        {
            int m = TransferModes.Normalize(mode, hilitePending);
            if (m >= TransferModes.Blend)
                return TransferModes.ApplyColor(m, s, d, c, out result);
            result = TransferModes.ApplyBoolean(m, s, d, c);
            return true;
        }

        // ---- StretchBits geometry ----

        // For each destination row: the source rows (relative to the bitmap's top) StretchBits merges into it, or null
        // when the source ran out first. Rows are consumed while the error term (starting at -srcHeight/2, + dstHeight
        // per source row, - srcHeight per destination row) stays <= 0.
        internal static int[]?[] RowGroups(int srcTop, int srcHeight, int dstHeight, int bitmapHeight)
        {
            var result = new int[]?[dstHeight];
            int start = srcHeight > dstHeight && srcHeight % dstHeight == 0 ? srcHeight - 1 : (ushort)srcHeight >> 1;
            int error = -start;
            int row = srcTop, k = 0;
            var group = new List<int>();
            while (row < bitmapHeight)
            {
                group.Clear();
                group.Add(row++);
                error += dstHeight;
                while (error <= 0 && row < bitmapHeight)
                {
                    group.Add(row++);
                    error += dstHeight;
                }
                var rows = group.ToArray();
                do
                {
                    result[k++] = rows;
                    if (k == dstHeight) return result;
                    error -= srcHeight;
                } while (error >= 0);
            }
            return result;
        }

        // Deep pixels: stretching replicates src[((f >> 1) + j * f) >> 16] with f = FixRatio(srcW, dstW); shrinking groups
        // source column i into destination ((f >> 1) + i * f) >> 16 with f = FixRatio(dstW, srcW) (16-bit fractions).
        internal static (int first, int end)[] DeepColumnGroups(int srcWidth, int dstWidth)
        {
            var result = new (int first, int end)[dstWidth];
            if (dstWidth == srcWidth)
            {
                for (int j = 0; j < dstWidth; j++) result[j] = (j, j + 1);
                return result;
            }
            if (dstWidth > srcWidth)
            {
                int f = FixedMath.FixRatio((short)srcWidth, (short)dstWidth) & 0xFFFF;
                for (int j = 0; j < dstWidth; j++)
                {
                    int i = (int)(((f >> 1) + (long)j * f) >> 16);
                    result[j] = (i, i + 1);
                }
                return result;
            }
            int fraction = FixedMath.FixRatio((short)dstWidth, (short)srcWidth) & 0xFFFF;
            var seen = new bool[dstWidth];
            for (int i = 0; i < srcWidth; i++)
            {
                int d = (int)(((fraction >> 1) + (long)i * fraction) >> 16);
                if (d >= dstWidth) break;
                result[d] = seen[d] ? (result[d].first, i + 1) : (i, i + 1);
                seen[d] = true;
            }
            return result;
        }

        // For each destination column: the source columns [first, end) (relative to srcRect's left) that land on it.
        internal static (int first, int end)[] ColumnGroups(int srcWidth, int dstWidth)
        {
            var result = new (int first, int end)[dstWidth];
            if (dstWidth == srcWidth)
            {
                for (int j = 0; j < dstWidth; j++) result[j] = (j, j + 1);
                return result;
            }
            if (dstWidth > srcWidth)
            {
                int ratio = FixedMath.FixRatio((short)srcWidth, (short)dstWidth) & 0xFFFF;
                Func<int, int> source = ratio switch
                {
                    0x8000 => j => j / 2,
                    0x4000 => j => j / 4,
                    0x2000 => j => j / 8,
                    0x1000 => j => j / 16,
                    0xAAAA => j => 2 * (j / 3) + (j % 3 == 0 ? 0 : 1),        // x1.5: a b b per source pair
                    0x5555 => j => j / 3,
                    0x2AAA => j => j / 6,
                    _ when dstWidth % srcWidth == 0 && dstWidth / srcWidth % 8 == 0 => j => j / (dstWidth / srcWidth),
                    _ => j => (int)(((ratio >> 1) + (long)j * ratio) >> 16),
                };
                for (int j = 0; j < dstWidth; j++)
                {
                    int i = source(j);
                    result[j] = (i, i + 1);
                }
                return result;
            }

            int fraction = FixedMath.FixRatio((short)dstWidth, (short)srcWidth) & 0xFFFF;
            switch (fraction)
            {
                case 0x8000:
                    for (int j = 0; j < dstWidth; j++) result[j] = (2 * j, 2 * j + 2);
                    return result;
                case 0x4000:
                    for (int j = 0; j < dstWidth; j++) result[j] = (4 * j, 4 * j + 4);
                    return result;
                case 0xC000:                                   // x3/4: a, b|c, d per 4 source columns
                    for (int j = 0; j < dstWidth; j++)
                    {
                        int k = 4 * (j / 3);
                        result[j] = (j % 3) switch { 0 => (k, k + 1), 1 => (k + 1, k + 3), _ => (k + 3, k + 4) };
                    }
                    return result;
            }
            // General shrink: source column i lands on destination column ((fraction / 2) + i * fraction) >> 16.
            int current = 0, start = 0;
            for (int i = 0; current < dstWidth; i++)
            {
                int d = (int)(((fraction >> 1) + (long)i * fraction) >> 16);
                if (d == current) continue;
                result[current] = (start, i);
                current = d;
                start = i;
            }
            return result;
        }
    }
}
