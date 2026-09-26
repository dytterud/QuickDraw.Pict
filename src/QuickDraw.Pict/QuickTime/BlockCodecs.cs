using System;

namespace QuickDraw.Pict
{
    // 'rle ' (Animation): a u32 chunk size, a u16 header (bit 3: a starting line and line count follow, each padded to
    // a long), then per line a skip byte (0 ends the frame; else skip - 1 units) and RLE codes until -1: 0 = another
    // skip byte, n > 0 = n literal units, n < -1 = the next unit repeated -n times. A unit is 16 pixels at 1 and 2
    // bits, 8 at 4 bits, 4 at 8 bits, else one pixel (RGB 555, RGB, ARGB).
    internal static class AnimationCodec
    {
        public static PictBitmap? Decode(PictImageDescription d, byte[] data)
        {
            int depth = d.Depth > 32 ? d.Depth - 32 : d.Depth;
            int unitBytes, unitPixels;
            switch (depth)
            {
                case 1: unitBytes = 2; unitPixels = 16; break;
                case 2: unitBytes = 4; unitPixels = 16; break;
                case 4: unitBytes = 4; unitPixels = 8; break;
                case 8: unitBytes = 4; unitPixels = 4; break;
                case 16: unitBytes = 2; unitPixels = 1; break;
                case 24: unitBytes = 3; unitPixels = 1; break;
                case 32: unitBytes = 4; unitPixels = 1; break;
                default: return null;
            }
            var img = new PictBitmap(d.Width, d.Height);
            if (data.Length < 8) return img;
            int p = 4;
            int header = (data[p] << 8) | data[p + 1];
            p += 2;
            int y = 0, lines = d.Height;
            if ((header & 0x0008) != 0)
            {
                y = (data[p] << 8) | data[p + 1];
                lines = (data[p + 4] << 8) | data[p + 5];
                p += 8;
            }
            var palette = QuickTimeCodecs.Palette(d);
            int rowPixels = (d.Width + unitPixels - 1) / unitPixels * unitPixels;
            var row = new byte[rowPixels * unitBytes / unitPixels + unitBytes];

            void PutUnit(int x, ReadOnlySpan<byte> unit)
            {
                for (int k = 0; k < unitPixels; k++)
                {
                    int px = x + k;
                    if (px >= d.Width) break;
                    var c = depth == 32 ? new PictColor(unit[1], unit[2], unit[3])
                        : QuickTimeCodecs.Pixel(unit, k, d.Depth, palette);
                    QuickTimeCodecs.Set(img, px, y, c);
                }
            }

            for (; lines > 0 && p < data.Length; lines--, y++)
            {
                int skip = data[p++];
                if (skip == 0) break;
                int x = (skip - 1) * unitPixels;
                while (p < data.Length)
                {
                    int code = (sbyte)data[p++];
                    if (code == -1) break;
                    if (code == 0)
                    {
                        if (p >= data.Length) return img;
                        x += (data[p++] - 1) * unitPixels;
                    }
                    else if (code > 0)
                    {
                        for (int i = 0; i < code; i++, x += unitPixels)
                        {
                            if (p + unitBytes > data.Length) return img;
                            PutUnit(x, data.AsSpan(p, unitBytes));
                            p += unitBytes;
                        }
                    }
                    else
                    {
                        if (p + unitBytes > data.Length) return img;
                        var unit = data.AsSpan(p, unitBytes);
                        p += unitBytes;
                        for (int i = 0; i < -code; i++, x += unitPixels) PutUnit(x, unit);
                    }
                }
            }
            return img;
        }
    }

    // 'rpza' (Road Pizza): 0xE1 and a 24-bit length, then opcodes over 4x4 blocks in raster order. RGB 555 colors;
    // a two-color block interpolates colors 1 and 2 as (11 A + 21 B) >> 5 and (21 A + 11 B) >> 5 per component, and
    // takes 2-bit indices, most significant first, one byte per row.
    internal static class RoadPizzaCodec
    {
        public static PictBitmap? Decode(PictImageDescription d, byte[] data)
        {
            if (data.Length < 4 || data[0] != 0xE1) return null;
            var img = new PictBitmap(d.Width, d.Height);
            int blocksWide = (d.Width + 3) / 4, total = blocksWide * ((d.Height + 3) / 4);
            int p = 4, block = 0;
            int Word() { int v = (data[p] << 8) | data[p + 1]; p += 2; return v & 0x7FFF; }

            void Fill(int b, Func<int, int, int> color)
            {
                int bx = b % blocksWide * 4, by = b / blocksWide * 4;
                for (int yy = 0; yy < 4; yy++)
                    for (int xx = 0; xx < 4; xx++)
                        QuickTimeCodecs.Set(img, bx + xx, by + yy, QuickTimeCodecs.Rgb555(color(xx, yy)));
            }

            void FourColor(int b, int a, int bc)
            {
                var colors = new[] { bc, Mix(a, bc, 11, 21), Mix(a, bc, 21, 11), a };
                var rows = data.AsSpan(p, 4).ToArray();
                p += 4;
                Fill(b, (x, y) => colors[(rows[y] >> (6 - 2 * x)) & 3]);
            }

            while (p < data.Length && block < total)
            {
                int op = data[p++];
                if ((op & 0x80) == 0)
                {
                    int a = ((op << 8) | data[p++]) & 0x7FFF;
                    if ((data[p] & 0x80) != 0)
                        FourColor(block, a, Word());
                    else
                    {
                        var colors = new int[16];
                        colors[0] = a;
                        for (int i = 1; i < 16; i++) colors[i] = Word();
                        Fill(block, (x, y) => colors[y * 4 + x]);
                    }
                    block++;
                    continue;
                }
                int n = (op & 0x1F) + 1;
                switch (op & 0xE0)
                {
                    case 0x80:
                        block += n;
                        break;
                    case 0xA0:
                    {
                        int c = Word();
                        for (int i = 0; i < n; i++) Fill(block++, (_, _) => c);
                        break;
                    }
                    case 0xC0:
                    {
                        int a = Word(), b = Word();
                        for (int i = 0; i < n; i++) FourColor(block++, a, b);
                        break;
                    }
                    default:
                        return img;                                // 0xE0: undefined
                }
            }
            return img;
        }

        private static int Mix(int a, int b, int wa, int wb)
        {
            int Component(int shift) => ((((a >> shift) & 31) * wa + ((b >> shift) & 31) * wb) >> 5) << shift;
            return Component(10) | Component(5) | Component(0);
        }
    }

    // 'smc ' (Graphics): a flags byte and a 24-bit length, then opcodes over 4x4 blocks of 8-bit palette indices in
    // raster order, with 256-entry circular caches of 2-, 4- and 8-color sets.
    internal static class GraphicsCodec
    {
        public static PictBitmap? Decode(PictImageDescription d, byte[] data)
        {
            int blocksWide = (d.Width + 3) / 4, total = blocksWide * ((d.Height + 3) / 4);
            var indices = new byte[total * 16];
            var pairs = new byte[256 * 2];
            var quads = new byte[256 * 4];
            var octets = new byte[256 * 8];
            int pairPos = 0, quadPos = 0, octetPos = 0;
            int p = 4, block = 0;

            void Copy(int from, int to)
            {
                if (from < 0 || to >= total) return;
                Array.Copy(indices, from * 16, indices, to * 16, 16);
            }

            void Paint(int b, Func<int, byte> pixel)
            {
                if (b >= total) return;
                for (int i = 0; i < 16; i++) indices[b * 16 + i] = pixel(i);
            }

            while (p < data.Length && block < total)
            {
                int op = data[p++];
                int n = (op & 0x0F) + 1;
                switch (op & 0xF0)
                {
                    case 0x00: block += n; break;
                    case 0x10: block += data[p++] + 1; break;
                    case 0x20:
                    case 0x30:
                    {
                        if ((op & 0xF0) == 0x30) n = data[p++] + 1;
                        for (int i = 0; i < n; i++, block++) Copy(block - 1, block);
                        break;
                    }
                    case 0x40:
                    case 0x50:
                    {
                        if ((op & 0xF0) == 0x50) n = data[p++] + 1;
                        int a = block - 2, b = block - 1;
                        for (int i = 0; i < n; i++)
                        {
                            Copy(a, block++);
                            Copy(b, block++);
                        }
                        break;
                    }
                    case 0x60:
                    case 0x70:
                    {
                        if ((op & 0xF0) == 0x70) n = data[p++] + 1;
                        byte c = data[p++];
                        for (int i = 0; i < n; i++) Paint(block++, _ => c);
                        break;
                    }
                    case 0x80:
                    case 0x90:
                    {
                        int set;
                        if ((op & 0xF0) == 0x80)
                        {
                            set = pairPos;
                            pairs[2 * set] = data[p++]; pairs[2 * set + 1] = data[p++];
                            pairPos = (pairPos + 1) & 255;
                        }
                        else set = data[p++];
                        for (int i = 0; i < n; i++)
                        {
                            int flags = (data[p] << 8) | data[p + 1];
                            p += 2;
                            Paint(block++, k => pairs[2 * set + ((flags >> (15 - k)) & 1)]);
                        }
                        break;
                    }
                    case 0xA0:
                    case 0xB0:
                    {
                        int set;
                        if ((op & 0xF0) == 0xA0)
                        {
                            set = quadPos;
                            for (int k = 0; k < 4; k++) quads[4 * set + k] = data[p++];
                            quadPos = (quadPos + 1) & 255;
                        }
                        else set = data[p++];
                        for (int i = 0; i < n; i++)
                        {
                            uint flags = (uint)((data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3]);
                            p += 4;
                            Paint(block++, k => quads[4 * set + (int)((flags >> (30 - 2 * k)) & 3)]);
                        }
                        break;
                    }
                    case 0xC0:
                    case 0xD0:
                    {
                        int set;
                        if ((op & 0xF0) == 0xC0)
                        {
                            set = octetPos;
                            for (int k = 0; k < 8; k++) octets[8 * set + k] = data[p++];
                            octetPos = (octetPos + 1) & 255;
                        }
                        else set = data[p++];
                        for (int i = 0; i < n; i++)
                        {
                            // Nibbles n0..nB of the 6 bytes: pixels 0-7 are n0 n1 n2 n4 n5 n6, pixels 8-15 n8 n9 nA n3 n7 nB.
                            var nib = new int[12];
                            for (int k = 0; k < 6; k++)
                            {
                                nib[2 * k] = data[p + k] >> 4;
                                nib[2 * k + 1] = data[p + k] & 15;
                            }
                            p += 6;
                            int a = (nib[0] << 20) | (nib[1] << 16) | (nib[2] << 12) | (nib[4] << 8) | (nib[5] << 4) | nib[6];
                            int b = (nib[8] << 20) | (nib[9] << 16) | (nib[10] << 12) | (nib[3] << 8) | (nib[7] << 4) | nib[11];
                            Paint(block++, k => octets[8 * set + (((k < 8 ? a : b) >> (21 - 3 * (k & 7))) & 7)]);
                        }
                        break;
                    }
                    case 0xE0:
                    {
                        for (int i = 0; i < n; i++)
                        {
                            int start = p;
                            Paint(block++, k => data[start + k]);
                            p += 16;
                        }
                        break;
                    }
                    default:
                        block = total;                              // 0xF0: undefined
                        break;
                }
            }

            var palette = QuickTimeCodecs.Palette(d);
            var img = new PictBitmap(d.Width, d.Height);
            for (int b = 0; b < total; b++)
            {
                int bx = b % blocksWide * 4, by = b / blocksWide * 4;
                for (int k = 0; k < 16; k++)
                    QuickTimeCodecs.Set(img, bx + (k & 3), by + (k >> 2), palette[indices[b * 16 + k] % palette.Length]);
            }
            return img;
        }
    }
}
