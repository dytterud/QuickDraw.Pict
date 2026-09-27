using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace QuickDraw.Pict
{
    /// <summary>A decoded cursor: the pixels it paints, the pixels where it changes the screen, and its hotspot.</summary>
    /// <param name="Image">The painted pixels (mask bit 1); transparent elsewhere.</param>
    /// <param name="Inverted">Width × height flags, row-major: true where the cursor inverts what is under it. Those
    /// pixels are transparent in <paramref name="Image"/>.</param>
    /// <param name="HotspotH">The hotspot's horizontal position (clamped to 0..15, as SetCursor does).</param>
    /// <param name="HotspotV">The hotspot's vertical position (clamped to 0..15).</param>
    /// <remarks>
    /// On a screen the cursor is drawn as <c>screen = (screen AND NOT mask) XOR image</c>. Where the mask is 0 the
    /// screen's RGB is XORed with <see cref="Xor"/>: 0 leaves it, $FFFFFF inverts it (<see cref="Inverted"/>).
    /// </remarks>
    public sealed record MacCursor(PictBitmap Image, bool[] Inverted, int HotspotH, int HotspotV)
    {
        /// <summary>Width × height RGB values (0xRRGGBB) XORed into the screen where the mask is 0, row-major. On a
        /// 32-bit screen a color cursor's unmasked pixel p XORs NOT p: white leaves the screen, black inverts it, other
        /// colors XOR their complement.</summary>
        public int[] Xor { get; init; } = Array.Empty<int>();
    }

    /// <summary>
    /// Decodes the QuickDraw image resources of classic Mac OS resource forks: icons (<c>ICON</c>, <c>ICN#</c>,
    /// <c>ics#</c>, <c>icm#</c>, <c>SICN</c>, <c>icl4</c>, <c>icl8</c>, <c>ics4</c>, <c>ics8</c>, <c>icm4</c>,
    /// <c>icm8</c>, <c>cicn</c>), cursors (<c>CURS</c>, <c>crsr</c>) and patterns (<c>PAT </c>, <c>PAT#</c>,
    /// <c>ppat</c>, <c>ppt#</c>). Each method takes one resource's data. 1-bit images are black on white; masked
    /// pixels are transparent.
    /// </summary>
    /// <remarks>
    /// Reading resource forks is up to the caller (for example a resource extractor); these decoders only need the
    /// bytes of one resource. The 4- and 8-bit icons use the Macintosh's standard 4- and 8-bit color tables and take
    /// their mask from the 1-bit icon list of the same size and id (<c>ICN#</c>, <c>ics#</c>, <c>icm#</c>).
    /// </remarks>
    public static class QuickDrawResources
    {
        private static readonly PictColor White = new PictColor(255, 255, 255);
        private static readonly PictColor Black = new PictColor(0, 0, 0);

        /// <summary>Decodes any single-image resource by type (<c>ICON</c>, the icon lists, the 4/8-bit icons without
        /// masks, <c>cicn</c>, <c>PAT </c>, <c>ppat</c>, and the image of <c>CURS</c> / <c>crsr</c>); null for other types.</summary>
        public static PictBitmap? Decode(string type, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(data);
            return type switch
            {
                "ICON" => DecodeIcon(data),
                "ICN#" or "ics#" or "icm#" => DecodeIconList(type, data),
                "icl4" or "icl8" or "ics4" or "ics8" or "icm4" or "icm8" => DecodeColorIcon(type, data),
                "cicn" => DecodeCicn(data),
                "PAT " => DecodePattern(data),
                "ppat" => DecodePixelPattern(data),
                "CURS" => DecodeCursor(data).Image,
                "crsr" => DecodeColorCursor(data).Image,
                _ => null,
            };
        }

        /// <summary><c>ICON</c>: a 32 × 32 1-bit icon (128 bytes), unmasked.</summary>
        public static PictBitmap DecodeIcon(byte[] data) => Mono(data, 0, 32, 32, null, 0);

        /// <summary>
        /// An icon list: <c>ICN#</c> (32 × 32), <c>ics#</c> (16 × 16) or <c>icm#</c> (16 × 12) — a 1-bit icon followed
        /// by its mask. Without the mask half the Icon Utilities compute one with CalcMask (the icon's silhouette:
        /// every pixel not reachable from the edges through white pixels).
        /// </summary>
        public static PictBitmap DecodeIconList(string type, byte[] data)
        {
            var (w, h) = IconSize(type);
            Require(data, w / 8 * h, type);
            return Mono(data, 0, w, h, IconMask(data, w, h), 0);
        }

        /// <summary>
        /// A 4- or 8-bit icon in the standard color table: <c>icl4</c>, <c>icl8</c> (32 × 32), <c>ics4</c>,
        /// <c>ics8</c> (16 × 16), <c>icm4</c>, <c>icm8</c> (16 × 12). <paramref name="iconList"/> is the icon list of the
        /// same size and id (<c>ICN#</c>, <c>ics#</c>, <c>icm#</c>), whose mask masks the icon (CalcMask of its icon
        /// when the list has no mask half). Without a list the icon is returned opaque; the Icon Utilities draw
        /// nothing then (noMaskFoundErr).
        /// </summary>
        public static PictBitmap DecodeColorIcon(string type, byte[] data, byte[]? iconList = null)
        {
            var (w, h) = IconSize(type);
            int depth = type[3] == '4' ? 4 : 8;
            Require(data, w * h * depth / 8, type);
            var palette = StandardColorTables.ForId(depth)!;
            var pm = new PixMap
            {
                Bounds = new PictRect(0, 0, h, w), RowBytes = w * depth / 8, PixelSize = depth, IsPixMap = true,
                Palette = palette, Data = data,
            };
            var mask = iconList != null && iconList.Length >= w / 8 * h ? IconMask(iconList, w, h) : null;
            return Render(pm, mask, 0, w / 8);
        }

        /// <summary><c>SICN</c>: a list of 16 × 16 1-bit icons (32 bytes each), unmasked.</summary>
        public static IReadOnlyList<PictBitmap> DecodeSmallIcons(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            var icons = new List<PictBitmap>();
            for (int o = 0; o + 32 <= data.Length; o += 32) icons.Add(Mono(data, o, 16, 16, null, 0));
            return icons;
        }

        /// <summary>
        /// <c>cicn</c>: a color icon — a PixMap, mask and 1-bit BitMap (each with its base address), the icon data
        /// handle, then the mask bits, the BitMap bits, a color table and the pixels.
        /// </summary>
        public static PictBitmap DecodeCicn(byte[] data)
        {
            Require(data, 82, "cicn");
            var pm = ReadPixMap(data, 0, out _);
            int maskRowBytes = U16(data, 54) & 0x3FFF, maskHeight = Height(data, 56);
            int bitmapRowBytes = U16(data, 68) & 0x3FFF, bitmapHeight = Height(data, 70);
            int p = 82;
            int maskAt = p;
            p += maskRowBytes * maskHeight + bitmapRowBytes * bitmapHeight;
            if (p + 8 > data.Length) throw Truncated("cicn");
            using (var b = new BinaryReader(new MemoryStream(data, p, data.Length - p)))
            {
                pm.Palette = PixMap.ReadColorTable(b, pm.PixelSize);
                p += (int)b.BaseStream.Position;
            }
            pm.Data = Slice(data, p, pm.RowBytes * pm.Height, "cicn");
            return Render(pm, maskRowBytes > 0 ? data : null, maskAt, maskRowBytes);
        }

        /// <summary><c>CURS</c>: a 16 × 16 cursor — 32 bytes of data, 32 bytes of mask, the hotspot (v, h).</summary>
        public static MacCursor DecodeCursor(byte[] data)
        {
            Require(data, 68, "CURS");
            return CursorBits(data, 0, null, I16(data, 66), I16(data, 64));
        }

        /// <summary>
        /// <c>crsr</c>: a color cursor — its header (type, PixMap and pixel offsets, the 1-bit data and mask, the
        /// hotspot), the PixMap (its color table offset in pmTable) and the pixels. SetCCursor never reads the 1-bit
        /// data: mask 1 paints the color pixel; mask 0 XORs the screen with the pixel's complement (white: transparent,
        /// black: invert). Without a PixMap the 1-bit data and mask are used as for <c>CURS</c>.
        /// </summary>
        public static MacCursor DecodeColorCursor(byte[] data)
        {
            Require(data, 96, "crsr");
            if ((U16(data, 0) & 0xFFFE) != 0x8000) throw new NotSupportedException("Unknown crsr type.");
            int mapAt = (int)U32(data, 2), pixelsAt = (int)U32(data, 6);
            PixMap? color = null;
            if (U16(data, 0) == 0x8001 && mapAt > 0)
            {
                color = ReadPixMap(data, mapAt, out int tableAt);
                if (tableAt < 0 || tableAt + 8 > data.Length) throw Truncated("crsr");
                using (var b = new BinaryReader(new MemoryStream(data, tableAt, data.Length - tableAt)))
                    color.Palette = PixMap.ReadColorTable(b, color.PixelSize);
                color.Data = Slice(data, pixelsAt, color.RowBytes * color.Height, "crsr");
            }
            return CursorBits(data, 20, color, I16(data, 86), I16(data, 84));
        }

        /// <summary><c>PAT </c>: an 8 × 8 1-bit pattern (8 bytes).</summary>
        public static PictBitmap DecodePattern(byte[] data) => Mono(Require(data, 8, "PAT "), 0, 8, 8, null, 0);

        /// <summary><c>PAT#</c>: a count, then 8-byte patterns.</summary>
        public static IReadOnlyList<PictBitmap> DecodePatternList(byte[] data)
        {
            Require(data, 2, "PAT#");
            var list = new List<PictBitmap>();
            int count = U16(data, 0);
            for (int i = 0, o = 2; i < count && o + 8 <= data.Length; i++, o += 8) list.Add(Mono(data, o, 8, 8, null, 0));
            return list;
        }

        /// <summary>
        /// <c>ppat</c>: a pixel pattern — type, PixMap and pixel offsets, the 1-bit fallback pattern, then the PixMap,
        /// its pixels and its color table (located through pmTable). Type 0 decodes the first 8 bytes of the pixel data
        /// as a 1-bit pattern; type 2 (RGB) is an 8 × 8 solid of the color in the table's entry 4, as a 32-bit screen
        /// draws it (the resource's pixels are ignored).
        /// </summary>
        public static PictBitmap DecodePixelPattern(byte[] data) => PixelPattern(data, 0, data.Length);

        /// <summary><c>ppt#</c>: a count, that many offsets from the resource start, then the <c>ppat</c> data each offset
        /// points to — complete flattened <c>ppat</c>s whose own offsets are relative to their start.</summary>
        public static IReadOnlyList<PictBitmap> DecodePixelPatternList(byte[] data)
        {
            Require(data, 2, "ppt#");
            int count = U16(data, 0);
            var offsets = new List<int>();
            for (int i = 0; i < count && 2 + 4 * i + 4 <= data.Length; i++) offsets.Add((int)U32(data, 2 + 4 * i));
            var list = new List<PictBitmap>();
            for (int i = 0; i < offsets.Count; i++)
            {
                int end = i + 1 < offsets.Count ? offsets[i + 1] : data.Length;
                list.Add(PixelPattern(data, offsets[i], end));
            }
            return list;
        }

        // ---- helpers ----

        // GetPixPat: the pixel data runs from patData to pmTable (a table before the data fails to load); the color
        // table is read unless the PixMap is RGB direct (pixelType 16). Type 0 draws the first 8 bytes of the pixel
        // data as a 1-bit pattern; types 1 and 3 the PixMap; type 2 the RGB of table entry 4 (table + $2A), solid on a
        // 32-bit screen. Other types fail to load (Mac OS 9).
        private static PictBitmap PixelPattern(byte[] data, int start, int end)
        {
            if (start < 0 || end > data.Length || end - start < 28) throw Truncated("ppat");
            int type = U16(data, start);
            if (type > 3) throw new NotSupportedException($"ppat type {type} is not 0-3.");
            int mapAt = start + (int)U32(data, start + 2), pixelsAt = start + (int)U32(data, start + 6);
            if (type == 0)
            {
                if (pixelsAt + 8 > data.Length) throw Truncated("ppat");
                return Mono(data, pixelsAt, 8, 8, null, 0);
            }
            var pm = ReadPixMap(data, mapAt, out int tableAt, start);
            if (tableAt < pixelsAt) throw new NotSupportedException("The ppat's color table does not follow its pixel data.");
            if (type == 2)
            {
                if (tableAt + 0x30 > data.Length) throw Truncated("ppat");
                var solid = new PictBitmap(8, 8);
                var rgb = new PictColor(data[tableAt + 0x2A], data[tableAt + 0x2C], data[tableAt + 0x2E]);
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++) Set(solid, x, y, rgb);
                return solid;
            }
            if (tableAt < pixelsAt + pm.RowBytes * pm.Height)
                throw new NotSupportedException("The ppat's pixel data is shorter than its PixMap.");
            pm.Data = Slice(data, pixelsAt, pm.RowBytes * pm.Height, "ppat");
            if (pm.PixelType != 16)
            {
                if (tableAt + 8 > data.Length) throw Truncated("ppat");
                using var b = new BinaryReader(new MemoryStream(data, tableAt, data.Length - tableAt));
                pm.Palette = PixMap.ReadColorTable(b, pm.PixelSize);
            }
            return Render(pm, null, 0, 0);
        }

        // A 50-byte PixMap record (baseAddr, rowBytes, bounds, ..., pmTable as an offset from `origin`).
        private static PixMap ReadPixMap(byte[] data, int at, out int tableAt, int origin = 0)
        {
            if (at < 0 || at + 50 > data.Length) throw Truncated("PixMap");
            var pm = new PixMap
            {
                RowBytes = U16(data, at + 4) & 0x3FFF,
                Bounds = new PictRect(I16(data, at + 6), I16(data, at + 8), I16(data, at + 10), I16(data, at + 12)),
                PackType = U16(data, at + 16),
                PixelType = U16(data, at + 30),
                PixelSize = U16(data, at + 32),
                CmpCount = U16(data, at + 34),
                IsPixMap = true,
            };
            if (pm.PixelSize is not (1 or 2 or 4 or 8 or 16 or 32))
                throw new NotSupportedException($"PixMap pixelSize {pm.PixelSize} is not a QuickDraw depth");
            tableAt = origin + (int)U32(data, at + 42);
            return pm;
        }

        // A pixel map's colors with an optional 1-bit mask (maskRowBytes per row at maskAt in maskData).
        private static PictBitmap Render(PixMap pm, byte[]? maskData, int maskAt, int maskRowBytes)
        {
            int w = Math.Max(1, pm.Width), h = Math.Max(1, pm.Height);
            var bmp = new PictBitmap(w, h);
            for (int y = 0; y < pm.Height; y++)
                for (int x = 0; x < pm.Width; x++)
                {
                    // Rows or columns past the mask's data are unmasked.
                    int maskByte = maskAt + y * maskRowBytes + (x >> 3);
                    if (maskData != null && (x >> 3) < maskRowBytes && maskByte < maskData.Length && !Bit(maskData, maskAt + y * maskRowBytes, x))
                        continue;
                    Set(bmp, x, y, pm.GetPixel(x, y));
                }
            return bmp;
        }

        // 1-bit image at `at` (w/8 bytes per row), masked by the bits at maskAt of maskData when given.
        private static PictBitmap Mono(byte[] data, int at, int w, int h, byte[]? maskData, int maskAt)
        {
            Require(data, at + w / 8 * h, "icon");
            var bmp = new PictBitmap(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (maskData != null && !Bit(maskData, maskAt + y * (w / 8), x)) continue;
                    Set(bmp, x, y, Bit(data, at + y * (w / 8), x) ? Black : White);
                }
            return bmp;
        }

        // 16 x 16 cursor: data at `at`, mask 32 bytes later; drawn as screen = (screen & ~mask) ^ image. 1-bit: mask 1
        // paints the data bit black / white, mask 0 with data 1 inverts. Color (the 1-bit data unused): mask 1 paints
        // the pixel, mask 0 XORs its complement (pixels outside the PixMap read as black).
        private static MacCursor CursorBits(byte[] data, int at, PixMap? color, int hotH, int hotV)
        {
            var bmp = new PictBitmap(16, 16);
            var inverted = new bool[256];
            var xor = new int[256];
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    bool mask = Bit(data, at + 32 + 2 * y, x);
                    PictColor c;
                    if (color != null)
                        c = x < color.Width && y < color.Height ? color.GetPixel(x, y) : Black;
                    else
                        c = Bit(data, at + 2 * y, x) ? Black : White;
                    if (mask) Set(bmp, x, y, c);
                    else
                    {
                        xor[y * 16 + x] = ~((c.R << 16) | (c.G << 8) | c.B) & 0xFFFFFF;
                        inverted[y * 16 + x] = xor[y * 16 + x] == 0xFFFFFF;
                    }
                }
            return new MacCursor(bmp, inverted, Math.Clamp(hotH, 0, 15), Math.Clamp(hotV, 0, 15)) { Xor = xor };
        }

        // An icon list's mask bits (w/8 bytes per row): its second half, or CalcMask of the icon when that is missing.
        private static byte[] IconMask(byte[] list, int w, int h)
        {
            int bytes = w / 8 * h;
            return list.Length >= 2 * bytes ? list.AsSpan(bytes, bytes).ToArray() : CalcMask(list, w, h);
        }

        // CalcMask: flood the white pixels 4-connected to the edges; the mask is every pixel the flood does not reach.
        internal static byte[] CalcMask(byte[] image, int w, int h)
        {
            int rowBytes = w / 8;
            var outside = new bool[w * h];
            var stack = new Stack<int>();
            void Push(int x, int y)
            {
                if (x < 0 || y < 0 || x >= w || y >= h || outside[y * w + x] || Bit(image, y * rowBytes, x)) return;
                outside[y * w + x] = true;
                stack.Push(y * w + x);
            }
            for (int x = 0; x < w; x++) { Push(x, 0); Push(x, h - 1); }
            for (int y = 0; y < h; y++) { Push(0, y); Push(w - 1, y); }
            while (stack.Count > 0)
            {
                int p = stack.Pop(), x = p % w, y = p / w;
                Push(x + 1, y); Push(x - 1, y); Push(x, y + 1); Push(x, y - 1);
            }
            var mask = new byte[rowBytes * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (!outside[y * w + x]) mask[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            return mask;
        }

        private static (int w, int h) IconSize(string type) => type switch
        {
            "ICN#" or "icl4" or "icl8" => (32, 32),
            "ics#" or "ics4" or "ics8" => (16, 16),
            "icm#" or "icm4" or "icm8" => (16, 12),
            _ => throw new ArgumentException($"'{type}' is not an icon type.", nameof(type)),
        };

        private static bool Bit(byte[] data, int rowStart, int x) =>
            ((data[rowStart + (x >> 3)] >> (7 - (x & 7))) & 1) != 0;

        private static void Set(PictBitmap bmp, int x, int y, PictColor c)
        {
            int i = (y * bmp.Width + x) * 4;
            bmp.Pixels[i] = c.R; bmp.Pixels[i + 1] = c.G; bmp.Pixels[i + 2] = c.B; bmp.Pixels[i + 3] = 255;
        }

        private static int Height(byte[] data, int boundsAt) => Math.Max(0, I16(data, boundsAt + 4) - I16(data, boundsAt));

        private static byte[] Slice(byte[] data, int at, int length, string type)
        {
            if (at < 0 || length < 0 || at + length > data.Length) throw Truncated(type);
            return data.AsSpan(at, length).ToArray();
        }

        private static byte[] Require(byte[] data, int length, string type)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length < length) throw Truncated(type);
            return data;
        }

        private static Exception Truncated(string type) => new EndOfStreamException($"The '{type}' resource data is truncated.");

        private static int U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
        private static short I16(byte[] d, int o) => BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(o));
        private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
    }
}
