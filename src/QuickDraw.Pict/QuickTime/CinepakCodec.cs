using System;

namespace QuickDraw.Pict
{
    // 'cvid' (Cinepak): a frame header (flags, 24-bit length, width, height, strip count), then strips (id, size,
    // rect) of chunks. Codebook chunks (0x20xx) fill the V4 (bit 0x0200 clear) or V1 codebook with 2x2 entries:
    // four luma values plus signed U and V (6 bytes), or four values only (bit 0x0400: grayscale / palette indices);
    // bit 0x0100 marks a partial update gated by 32-bit masks. Vector chunks cover the strip's 4x4 blocks in raster
    // order: 0x3000 with a mask bit per block (1: four V4 indices, 0: one V1 index), 0x3100 with a skip bit before
    // each, 0x3200 V1 only. A V1 entry is scaled up 2x; color is R = Y + 2V, G = Y - U/2 - V (U/2 truncated), B = Y + 2U.
    internal static class CinepakCodec
    {
        private struct Entry
        {
            public byte Y0, Y1, Y2, Y3;
            public sbyte U, V;
        }

        public static PictBitmap? Decode(PictImageDescription d, byte[] data)
        {
            if (data.Length < 10) return null;
            int strips = (data[8] << 8) | data[9];
            var v4 = new Entry[256];
            var v1 = new Entry[256];
            bool palette = d.Depth <= 8, gray = d.Depth > 32;
            var colors = palette ? QuickTimeCodecs.Palette(d) : null;
            var img = new PictBitmap(d.Width, d.Height);
            int p = 10, top = 0;
            for (int s = 0; s < strips && p + 12 <= data.Length; s++)
            {
                int size = (data[p + 2] << 8) | data[p + 3];
                int y1 = (data[p + 4] << 8) | data[p + 5], y2 = (data[p + 8] << 8) | data[p + 9];
                int x2 = (data[p + 10] << 8) | data[p + 11];
                int stripHeight = y2 - y1;
                if (stripHeight <= 0) stripHeight = d.Height - top;
                int end = Math.Min(data.Length, p + size);
                int c = p + 12;
                while (c + 4 <= end)
                {
                    int id = (data[c] << 8) | data[c + 1];
                    int chunkSize = (data[c + 2] << 8) | data[c + 3];
                    if (chunkSize < 4) break;
                    int chunkEnd = Math.Min(end, c + chunkSize);
                    var chunk = data.AsSpan(c + 4, chunkEnd - c - 4);
                    if ((id & 0xF000) == 0x2000)
                        ReadCodebook((id & 0x0200) != 0 ? v1 : v4, chunk.ToArray(), (id & 0x0400) != 0, (id & 0x0100) != 0);
                    else if ((id & 0xF000) == 0x3000)
                        Vectors(img, chunk, id, v4, v1, top, stripHeight, Math.Min(x2 > 0 ? x2 : d.Width, d.Width), colors, gray);
                    c = chunkEnd;
                }
                top += stripHeight;
                p += Math.Max(size, 12);
            }
            return img;
        }

        private static void ReadCodebook(Entry[] book, byte[] chunk, bool fourBytes, bool partial)
        {
            int entrySize = fourBytes ? 4 : 6, p = 0, index = 0;
            Entry Read()
            {
                var e = new Entry { Y0 = chunk[p], Y1 = chunk[p + 1], Y2 = chunk[p + 2], Y3 = chunk[p + 3] };
                if (!fourBytes) { e.U = (sbyte)chunk[p + 4]; e.V = (sbyte)chunk[p + 5]; }
                p += entrySize;
                return e;
            }
            if (!partial)
            {
                while (p + entrySize <= chunk.Length && index < 256) book[index++] = Read();
                return;
            }
            while (p + 4 <= chunk.Length && index < 256)
            {
                uint mask = (uint)((chunk[p] << 24) | (chunk[p + 1] << 16) | (chunk[p + 2] << 8) | chunk[p + 3]);
                p += 4;
                for (int bit = 31; bit >= 0 && index < 256; bit--, index++)
                    if ((mask >> bit & 1) != 0)
                    {
                        if (p + entrySize > chunk.Length) return;
                        book[index] = Read();
                    }
            }
        }

        private static void Vectors(PictBitmap img, ReadOnlySpan<byte> chunk, int id, Entry[] v4, Entry[] v1, int top,
            int height, int width, PictColor[]? palette, bool gray)
        {
            int blocksWide = (width + 3) / 4, blocks = blocksWide * ((height + 3) / 4);
            int p = 0, block = 0;
            uint mask = 0;
            int bits = 0;
            bool NextBit(ReadOnlySpan<byte> c)
            {
                if (bits == 0)
                {
                    if (p + 4 > c.Length) { bits = -1; return false; }
                    mask = (uint)((c[p] << 24) | (c[p + 1] << 16) | (c[p + 2] << 8) | c[p + 3]);
                    p += 4;
                    bits = 32;
                }
                bits--;
                return (mask >> bits & 1) != 0;
            }

            for (; block < blocks && p < chunk.Length; block++)
            {
                int bx = block % blocksWide * 4, by = top + block / blocksWide * 4;
                bool useV4;
                if (id == 0x3200) useV4 = false;
                else if (id == 0x3100)
                {
                    bool changed = NextBit(chunk);
                    if (bits < 0) return;
                    if (!changed) continue;
                    useV4 = NextBit(chunk);
                }
                else useV4 = NextBit(chunk);
                if (bits < 0) return;
                if (useV4)
                {
                    if (p + 4 > chunk.Length) return;
                    for (int q = 0; q < 4; q++)
                        Put2x2(img, bx + (q & 1) * 2, by + (q >> 1) * 2, v4[chunk[p + q]], palette, gray);
                    p += 4;
                }
                else
                {
                    if (p + 1 > chunk.Length) return;
                    var e = v1[chunk[p++]];
                    for (int q = 0; q < 4; q++)
                    {
                        byte y = q switch { 0 => e.Y0, 1 => e.Y1, 2 => e.Y2, _ => e.Y3 };
                        var solid = new Entry { Y0 = y, Y1 = y, Y2 = y, Y3 = y, U = e.U, V = e.V };
                        Put2x2(img, bx + (q & 1) * 2, by + (q >> 1) * 2, solid, palette, gray);
                    }
                }
            }
        }

        private static void Put2x2(PictBitmap img, int x, int y, Entry e, PictColor[]? palette, bool gray)
        {
            for (int k = 0; k < 4; k++)
            {
                int lum = k switch { 0 => e.Y0, 1 => e.Y1, 2 => e.Y2, _ => e.Y3 };
                PictColor c;
                if (palette != null) c = palette[lum % palette.Length];
                else if (gray) c = new PictColor((byte)lum, (byte)lum, (byte)lum);
                else
                    c = new PictColor(Clamp(lum + 2 * e.V), Clamp(lum - e.U / 2 - e.V), Clamp(lum + 2 * e.U));
                QuickTimeCodecs.Set(img, x + (k & 1), y + (k >> 1), c);
            }
        }

        private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
    }

    // 'tga ': a Targa file (18-byte header, image id, color map, pixels): color-mapped, true-color and grayscale, raw
    // or run-length encoded, 8/15/16/24/32 bits, bottom-up unless the descriptor's bit 5 says top-down.
    internal static class TargaCodec
    {
        public static PictBitmap? Decode(byte[] data)
        {
            if (data.Length < 18) return null;
            int idLength = data[0], mapType = data[1], type = data[2];
            int mapFirst = data[3] | (data[4] << 8), mapLength = data[5] | (data[6] << 8), mapBits = data[7];
            int width = data[12] | (data[13] << 8), height = data[14] | (data[15] << 8), bits = data[16], descriptor = data[17];
            if (width == 0 || height == 0) return null;
            int p = 18 + idLength;
            var map = new PictColor[mapType == 1 ? mapFirst + mapLength : 0];
            for (int i = 0; i < mapLength && mapType == 1; i++)
            {
                map[mapFirst + i] = ReadColor(data, ref p, mapBits);
            }
            int baseType = type & 7;
            bool rle = (type & 8) != 0;
            var img = new PictBitmap(width, height);
            bool topDown = (descriptor & 0x20) != 0;
            int pixel = 0, count = width * height;

            PictColor Next()
            {
                if (baseType == 1)
                {
                    int idx = bits == 16 ? data[p] | (data[p + 1] << 8) : data[p];
                    p += bits / 8;
                    return idx < map.Length ? map[idx] : new PictColor(0, 0, 0);
                }
                if (baseType == 3)
                {
                    byte g = data[p];
                    p += bits / 8;
                    return new PictColor(g, g, g);
                }
                return ReadColor(data, ref p, bits);
            }

            void Put(PictColor c)
            {
                int x = pixel % width, y = pixel / width;
                QuickTimeCodecs.Set(img, x, topDown ? y : height - 1 - y, c);
                pixel++;
            }

            while (pixel < count && p < data.Length)
            {
                if (!rle) { Put(Next()); continue; }
                int packet = data[p++];
                int n = (packet & 0x7F) + 1;
                if ((packet & 0x80) != 0)
                {
                    var c = Next();
                    for (int i = 0; i < n && pixel < count; i++) Put(c);
                }
                else
                    for (int i = 0; i < n && pixel < count; i++) Put(Next());
            }
            return img;
        }

        private static PictColor ReadColor(byte[] data, ref int p, int bits)
        {
            switch (bits)
            {
                case 15:
                case 16:
                {
                    int v = data[p] | (data[p + 1] << 8);
                    p += 2;
                    return QuickTimeCodecs.Rgb555(v);
                }
                case 24:
                    p += 3;
                    return new PictColor(data[p - 1], data[p - 2], data[p - 3]);
                case 32:
                    p += 4;
                    return new PictColor(data[p - 2], data[p - 3], data[p - 4], data[p - 1]);
                default:
                    p += Math.Max(1, bits / 8);
                    return new PictColor(0, 0, 0);
            }
        }
    }
}
