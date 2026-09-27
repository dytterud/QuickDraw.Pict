using System;

namespace QuickDraw.Pict
{
    // The QuickTime decompressors built into the core, by compressor type. Each returns an RGBA image of the
    // description's size, or null for data it cannot decode. Formats: 'raw ' (uncompressed), 'rle ' (Animation),
    // 'rpza' (Road Pizza / Video), 'smc ' (Graphics), 'cvid' (Cinepak), '8BPS' (Planar RGB), 'yuv2' (Component
    // Video), 'YVU9' (Intel Raw), 'tga ' (Targa) and 'PNTG' (MacPaint).
    internal static class QuickTimeCodecs
    {
        public static PictBitmap? Decode(PictImageDescription d, byte[] data)
        {
            if (d.Width <= 0 || d.Height <= 0) return null;
            try
            {
                return d.CodecType switch
                {
                    "raw " => Raw(d, data),
                    "rle " => AnimationCodec.Decode(d, data),
                    "rpza" => RoadPizzaCodec.Decode(d, data),
                    "smc " => GraphicsCodec.Decode(d, data),
                    "cvid" => CinepakCodec.Decode(d, data),
                    "8BPS" => Planar(d, data),
                    "yuv2" => Yuv2(d, data),
                    "YVU9" => Yvu9(d, data),
                    "tga " => TargaCodec.Decode(data),
                    "PNTG" => MacPaintFile.DecodeRows(data),
                    _ => null,
                };
            }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidOperationException)
            {
                return null;                                            // corrupt data: treat as undecodable
            }
        }

        // ---- shared helpers ----

        internal static PictColor[] Palette(PictImageDescription d) =>
            d.ColorTable ?? StandardColorTables.ForDepth(d.Depth) ?? StandardColorTables.ForId(8)!;

        internal static void Set(PictBitmap img, int x, int y, PictColor c)
        {
            if ((uint)x >= (uint)img.Width || (uint)y >= (uint)img.Height) return;
            int i = (y * img.Width + x) * 4;
            img.Pixels[i] = c.R; img.Pixels[i + 1] = c.G; img.Pixels[i + 2] = c.B; img.Pixels[i + 3] = c.A;
        }

        internal static PictColor Rgb555(int v) =>
            new PictColor(Expand5((v >> 10) & 31), Expand5((v >> 5) & 31), Expand5(v & 31));

        private static byte Expand5(int c) => (byte)((c << 3) | (c >> 2));

        internal static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

        // Full-range YCbCr (JFIF) with centered chroma.
        internal static PictColor YuvToRgb(int y, int u, int v) =>
            new PictColor(Clamp(y + 1.402 * v), Clamp(y - 0.344136 * u - 0.714136 * v), Clamp(y + 1.772 * u));

        // Pixels of a row of depth-bit indexed or direct data.
        internal static PictColor Pixel(ReadOnlySpan<byte> row, int x, int depth, PictColor[] palette)
        {
            switch (depth)
            {
                case 16: return Rgb555((row[2 * x] << 8) | row[2 * x + 1]);
                case 24: return new PictColor(row[3 * x], row[3 * x + 1], row[3 * x + 2]);
                case 32: return new PictColor(row[4 * x + 1], row[4 * x + 2], row[4 * x + 3]);
            }
            int bits = depth > 32 ? depth - 32 : depth;
            int bit = x * bits;
            int value = (row[bit >> 3] >> (8 - bits - (bit & 7))) & ((1 << bits) - 1);
            return value < palette.Length ? palette[value] : new PictColor(0, 0, 0);
        }

        // ---- simple codecs ----

        // 'raw ': rows of the description's depth; the row length is taken from the data (rows are usually padded
        // to an even number of bytes).
        private static PictBitmap? Raw(PictImageDescription d, byte[] data)
        {
            int depth = d.Depth;
            int bits = depth > 32 ? depth - 32 : depth;
            int minRow = depth switch { 16 => d.Width * 2, 24 => d.Width * 3, 32 => d.Width * 4, _ => (d.Width * bits + 7) / 8 };
            int perRow = data.Length / d.Height;
            int rowBytes = perRow >= minRow && perRow <= minRow + 3 ? perRow : (minRow + 1) & ~1;
            var palette = Palette(d);
            var img = new PictBitmap(d.Width, d.Height);
            for (int y = 0; y < d.Height && (long)y * rowBytes + minRow <= data.Length; y++)
            {
                var row = data.AsSpan(y * rowBytes, minRow);
                for (int x = 0; x < d.Width; x++) Set(img, x, y, Pixel(row, x, depth, palette));
            }
            return img;
        }

        // '8BPS': a u16 PackBits byte count per row of each plane, then the planes (red, green, blue, then alpha;
        // one plane of palette indices for 8-bit images).
        private static PictBitmap? Planar(PictImageDescription d, byte[] data)
        {
            int channels = d.Depth switch { 8 => 1, 24 => 3, 32 => 4, _ => 0 };
            if (channels == 0) return null;
            int lines = d.Height * channels;
            if (data.Length < 2 * lines) return null;
            var planes = new byte[channels * d.Width * d.Height];
            int p = 2 * lines;
            for (int l = 0; l < lines; l++)
            {
                int count = (data[2 * l] << 8) | data[2 * l + 1];
                if (p + count > data.Length) return null;
                PackBits.Unpack(data.AsSpan(p, count), planes.AsSpan(l * d.Width, d.Width));
                p += count;
            }
            var palette = Palette(d);
            var img = new PictBitmap(d.Width, d.Height);
            int plane = d.Width * d.Height;
            for (int y = 0; y < d.Height; y++)
                for (int x = 0; x < d.Width; x++)
                {
                    int i = y * d.Width + x;
                    var c = channels == 1 ? palette[planes[i] % palette.Length]
                        : new PictColor(planes[i], planes[plane + i], planes[2 * plane + i], channels == 4 ? planes[3 * plane + i] : (byte)255);
                    Set(img, x, y, channels == 4 ? new PictColor(c.R, c.G, c.B) : c);
                }
            return img;
        }

        // 'yuv2': Y0 U Y1 V per pixel pair, chroma as signed bytes.
        private static PictBitmap? Yuv2(PictImageDescription d, byte[] data)
        {
            int pairs = (d.Width + 1) / 2;
            var img = new PictBitmap(d.Width, d.Height);
            for (int y = 0; y < d.Height; y++)
                for (int k = 0; k < pairs; k++)
                {
                    int o = (y * pairs + k) * 4;
                    if (o + 3 >= data.Length) return img;
                    int u = (sbyte)data[o + 1], v = (sbyte)data[o + 3];
                    Set(img, 2 * k, y, YuvToRgb(data[o], u, v));
                    Set(img, 2 * k + 1, y, YuvToRgb(data[o + 2], u, v));
                }
            return img;
        }

        // 'YVU9': a full Y plane, then V and U planes subsampled 4x4 (unsigned, centered on 128).
        private static PictBitmap? Yvu9(PictImageDescription d, byte[] data)
        {
            int w = d.Width, h = d.Height, cw = (w + 3) / 4, ch = (h + 3) / 4;
            if (data.Length < w * h + 2 * cw * ch) return null;
            var img = new PictBitmap(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int c = (y / 4) * cw + x / 4;
                    int v = data[w * h + c] - 128, u = data[w * h + cw * ch + c] - 128;
                    Set(img, x, y, YuvToRgb(data[y * w + x], u, v));
                }
            return img;
        }

    }
}
