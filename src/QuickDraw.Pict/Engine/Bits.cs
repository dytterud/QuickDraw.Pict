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
    //
    // Mac OS 9's rewrite differs: one DDA for rows and columns at every depth takes, for an enlargement,
    // destination k from source ceil(s (2k + 1) / 2d) - 1, and for a reduction merges sources between boundaries
    // floor((k + 1) s / d + 1/2) (so a 1-bit x1.5 stretch is aabccd); a clip starting where that error is exactly 0
    // shifts its first row/column by one; 16/32-bit merges promote to 8-bit components and average rounded; and
    // colorizing through srcOr/srcBic (or any colorizing copy of a direct source) blends each channel linearly.
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
            bool macOS9 = colors.MacOS9;
            var rows = scaled ? (macOS9 ? RowGroupsMacOS9(srcTop, srcH, dstH, src.Height) : RowGroups(srcTop, srcH, dstH, src.Height)) : null;
            var cols = scaled ? (macOS9 ? ColumnGroupsMacOS9(srcW, dstW) : src.PixelSize == 1 ? ColumnGroups(srcW, dstW) : DeepColumnGroups(srcW, dstW)) : null;
            bool averagedRows = dstH < srcH;
            if (macOS9 && scaled)
            {
                // DDAInit jumps straight to the first visible row / column; where its error is exactly 0 there, that
                // row / column is off by one.
                var visible = area.Bounds;
                int n = visible.Top - dstRect.Top;
                if (rows != null && n > 0 && n < rows.Length && ClippedStart(srcH, dstH, n) is int rowShift)
                {
                    rows = (int[]?[])rows.Clone();
                    var shifted = ShiftGroup(rows[n], rowShift, srcTop, src.Height);
                    rows[n] = shifted;
                }
                n = visible.Left - dstRect.Left;
                if (cols != null && n > 0 && n < cols.Length && ClippedStart(srcW, dstW, n) is int colShift)
                {
                    cols = ((int first, int end)[])cols.Clone();
                    cols[n] = colShift > 0 ? (cols[n].first + 1, cols[n].end + 1) : (cols[n].first - 1, cols[n].end);
                }
            }

            bool ditherCopy = (mode & TransferModes.DitherCopy) != 0;
            mode &= ~TransferModes.DitherCopy;
            bool bilevel = src.PixelSize == 1 && IsBlackAndWhite(src.Palette);
            bool keepAlpha = preserveAlpha && src.PixelSize == 32 && src.CmpCount == 4 && mode == TransferModes.SrcCopy;
            int bitCap = 32 * ((srcW - 1) / 32 + 1);          // StretchBits reads whole longs of each source row

            var device = colors.Device;
            // ditherCopy of a direct source onto an indexed or 16-bit screen: error diffusion over the visible bounds.
            if (device != null && ditherCopy && src.IsDirect &&
                TransferModes.Normalize(mode, hilitePending) == TransferModes.SrcCopy)
            {
                var visible = area;
                var rowsCopy = rows;
                var colsCopy = cols;
                var copyColors = colors;
                var b = area.Bounds;
                // The ROM converts the destination rect's full width; Mac OS 9 the visible bounds.
                int left = device.MacOS9 ? b.Left : dstRect.Left, right = device.MacOS9 ? b.Right : dstRect.Right;
                DeviceModes.Dither(canvas, device, b.Top, b.Bottom, left, right, (x, y) =>
                {
                    int dy = y - dstRect.Top, dx = x - dstRect.Left;
                    if (dy < 0 || dy >= dstH || dx < 0 || dx >= dstW) return null;
                    int[]? group = rowsCopy == null ? new[] { srcTop + dy } : rowsCopy[dy];
                    if (group == null) return null;
                    var (first, end) = colsCopy == null ? (dx, dx + 1) : colsCopy[dx];
                    PictColor color;
                    if (!scaled)
                    {
                        int sy = group[0], sx = srcLeft + first;
                        if (sy < 0 || sy >= src.Height || sx < 0 || sx >= src.Width) return null;
                        color = src.GetPixel(sx, sy);
                    }
                    else if (!TryDeep(src, group, srcLeft, first, end, macOS9, out color, out _, out _)) return null;
                    return ApplyColorSource(TransferModes.SrcCopy, false, color, new PictColor(255, 255, 255), copyColors, true,
                        out var copied) ? copied : null;
                }, (x, y) => visible.Contains(x, y));
                return;
            }

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
                        if (device != null)
                        {
                            CopyPixelOnDevice(canvas, device, src, group, srcLeft, first, end, scaled, bitCap, bilevel, x, y,
                                mode, hilitePending, colors);
                            continue;
                        }
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
                                write = ApplyColorSource(mode, hilitePending, src.Palette[bit ? 1 : 0], dst, colors, false, out result);
                        }
                        else if (!scaled)
                        {
                            int sy = group[0], sx = srcLeft + first;
                            if (sy < 0 || sy >= src.Height || sx < 0 || sx >= src.Width) continue;
                            write = ApplyColorSource(mode, hilitePending, src.GetPixel(sx, sy), dst, colors, src.IsDirect, out result);
                            if (keepAlpha) alpha = src.GetAlpha(sx, sy);
                        }
                        else
                        {
                            if (!TryDeep(src, group, srcLeft, first, end, macOS9, out var color, out bool averaged, out byte a)) continue;
                            write = ApplyColorSource(mode, hilitePending, color, dst, colors, src.IsDirect, out result);
                            if (keepAlpha) alpha = averaged || averagedRows ? (byte)0 : a;
                        }
                        if (write) Painter.WritePixel(canvas, x, y, result, alpha);
                    }
                }
        }

        // One destination pixel on an indexed or 16-bit screen.
        private static void CopyPixelOnDevice(PictBitmap canvas, ScreenDevice device, PixMap src, int[] group, int srcLeft,
            int first, int end, bool scaled, int bitCap, bool bilevel, int x, int y, int mode, bool hilitePending,
            in PortColors colors)
        {
            int m = TransferModes.Normalize(mode, hilitePending);
            int dst = device.Read(canvas, x, y), value;
            bool write;
            if (src.PixelSize == 1)
            {
                if (!TryBit(src, group, srcLeft, first, end, scaled ? bitCap : src.Width, scaled, out bool bit)) return;
                write = bilevel
                    ? DeviceModes.Bit(m, bit, dst, colors, out value)
                    : DeviceModes.Source(m, src.Palette[bit ? 1 : 0], false, dst, colors, out value, src.Exact(bit ? 1 : 0));
            }
            else
            {
                PictColor color;
                int index = -1;
                if (!scaled)
                {
                    int sy = group[0], sx = srcLeft + first;
                    if (sy < 0 || sy >= src.Height || sx < 0 || sx >= src.Width) return;
                    color = src.GetPixel(sx, sy);
                    if (src.PixelSize <= 8) index = src.GetIndex(sx, sy);
                }
                else if (!TryDeep(src, group, srcLeft, first, end, colors.MacOS9, out color, out _, out _, out index)) return;
                write = DeviceModes.Source(m, color, src.IsDirect, dst, colors, out value,
                    index >= 0 ? src.Exact(index) : null);
            }
            if (write) device.Write(canvas, x, y, value);
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
        private static bool TryDeep(PixMap src, int[] rows, int srcLeft, int first, int end, bool macOS9, out PictColor color,
            out bool averaged, out byte alpha) => TryDeep(src, rows, srcLeft, first, end, macOS9, out color, out averaged, out alpha, out _);

        private static bool TryDeep(PixMap src, int[] rows, int srcLeft, int first, int end, bool macOS9, out PictColor color,
            out bool averaged, out byte alpha, out int index)
        {
            index = -1;
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
                    if (macOS9 && src.PixelSize == 16) (cr, cg, cb) = (Expand5(cr), Expand5(cg), Expand5(cb));
                    r += cr; g += cg; b += cb;
                    n++;
                    alpha = src.GetAlpha(x, row);
                }
                if (n == 0) continue;
                if (n > 1) averaged = true;
                columns++;
                if (src.PixelSize > 8)
                {
                    int half = macOS9 ? n / 2 : 0;
                    sumR += (r + half) / n; sumG += (g + half) / n; sumB += (b + half) / n;
                }
            }
            if (columns == 0) return false;
            if (src.PixelSize <= 8)
            {
                index = maxIndex;
                color = maxIndex < src.Palette.Length ? src.Palette[maxIndex] : new PictColor(0, 0, 0);
                return true;
            }
            if (columns > 1) averaged = true;
            if (macOS9)
            {
                int half = columns / 2;
                color = new PictColor((byte)((sumR + half) / columns), (byte)((sumG + half) / columns), (byte)((sumB + half) / columns));
                return true;
            }
            int R = columns == 2 ? sumR >> 1 : sumR / columns, G = columns == 2 ? sumG >> 1 : sumG / columns,
                B = columns == 2 ? sumB >> 1 : sumB / columns;
            color = src.PixelSize == 16
                ? new PictColor(Expand5(R), Expand5(G), Expand5(B))
                : new PictColor((byte)R, (byte)G, (byte)B);
            return true;
        }

        private static byte Expand5(int c) => (byte)((c << 3) | (c >> 2));

        // A full-color source pixel through the mode (TransferModes.ApplyBoolean / ApplyColor). Mac OS 9 colorizes
        // srcOr / srcBic / notSrcOr / notSrcBic sources, and copies of direct sources, by a per-channel blend.
        private static bool ApplyColorSource(int mode, bool hilitePending, PictColor s, PictColor d, in PortColors c,
            bool direct, out PictColor result)
        {
            int m = TransferModes.Normalize(mode, hilitePending);
            if (m >= TransferModes.Blend)
                return TransferModes.ApplyColor(m, s, d, c, out result);
            if (c.MacOS9 && TransferModes.ColorizeBlend(m, s, d, c, direct) is PictColor blended)
            {
                result = blended;
                return true;
            }
            result = TransferModes.ApplyBoolean(m, s, d, c);
            return true;
        }

        // Mac OS 9's clipped-DDA quirk at the first visible index n: +1 when enlarging and s (2n + 1) is a multiple of
        // 2d (the enlarged row takes the next source); -1 when reducing and 2ns is an odd multiple of d (the group starts
        // one source early). Null otherwise.
        private static int? ClippedStart(int s, int d, int n)
        {
            if (d > s) return (long)s * (2 * n + 1) % (2L * d) == 0 ? 1 : null;
            if (d < s)
            {
                long t = 2L * n * s;
                return t % d == 0 && (t / d & 1) == 1 ? -1 : null;
            }
            return null;
        }

        private static int[]? ShiftGroup(int[]? group, int shift, int srcTop, int bitmapHeight)
        {
            if (group == null || group.Length == 0) return group;
            if (shift > 0)
            {
                int row = group[0] + 1;
                return row < bitmapHeight ? new[] { row } : group;
            }
            var list = new List<int> { group[0] - 1 };
            list.AddRange(group);
            return list.ToArray();
        }

        // Mac OS 9's column DDA (the same as its row DDA, for every depth).
        internal static (int first, int end)[] ColumnGroupsMacOS9(int srcWidth, int dstWidth)
        {
            var result = new (int first, int end)[dstWidth];
            long s = srcWidth, d = dstWidth;
            int previous = 0;
            for (int k = 0; k < dstWidth; k++)
            {
                if (dstWidth == srcWidth) result[k] = (k, k + 1);
                else if (dstWidth > srcWidth)
                {
                    int i = (int)((s * (2 * k + 1) + 2 * d - 1) / (2 * d)) - 1;
                    result[k] = (i, i + 1);
                }
                else
                {
                    int next = (int)((2 * (k + 1) * s + d) / (2 * d));
                    result[k] = (previous, next);
                    previous = next;
                }
            }
            return result;
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

        // Mac OS 9's vertical DDA: enlarging, destination row k takes source row ceil(s (2k + 1) / 2d) - 1; reducing,
        // destination row k merges source rows [b(k - 1), b(k)) with b(k) = floor((k + 1) s / d + 1/2), b(-1) = 0. Rows
        // past the bitmap are dropped (a row left with none is not drawn).
        internal static int[]?[] RowGroupsMacOS9(int srcTop, int srcHeight, int dstHeight, int bitmapHeight)
        {
            var result = new int[]?[dstHeight];
            long s = srcHeight, d = dstHeight;
            int previous = 0;
            for (int k = 0; k < dstHeight; k++)
            {
                var rows = new List<int>();
                if (dstHeight == srcHeight) rows.Add(k);
                else if (dstHeight > srcHeight) rows.Add((int)((s * (2 * k + 1) + 2 * d - 1) / (2 * d)) - 1);
                else
                {
                    int next = (int)((2 * (k + 1) * s + d) / (2 * d));
                    for (int i = previous; i < next; i++) rows.Add(i);
                    previous = next;
                }
                rows.RemoveAll(i => srcTop + i >= bitmapHeight);
                result[k] = rows.Count == 0 ? null : rows.ConvertAll(i => srcTop + i).ToArray();
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
        internal static (int first, int end)[] ColumnGroups(int srcWidth, int dstWidth, bool macOS9 = false)
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
                    0xAAAA when macOS9 => j => 2 * (j / 3) + (j % 3 == 2 ? 1 : 0),   // x1.5 (Mac OS 9): a a b
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
