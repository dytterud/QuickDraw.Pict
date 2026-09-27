using System;
using System.IO;

namespace QuickDraw.Pict
{
    // A BitMap or PixMap read from a picture, with its pixel data unpacked into QuickDraw's in-memory layout:
    // rows of RowBytes bytes; indexed pixels MSB-first; 16-bit big-endian xRRRRRGGGGGBBBBB; 32-bit chunky
    // (alpha/pad, R, G, B). Layout rules follow Inside Macintosh: Imaging With QuickDraw, Appendix A (PixData
    // pseudocode, packing types) and Executor's eatpixdata/eatbitdata (qPicstuff.cpp, MIT).
    internal sealed class PixMap
    {
        private const int RowBytesMask = 0x3FFF;     // high bits of rowBytes are flags (Executor ROWBYTES_VALUE_BITS)

        public PictRect Bounds;
        public int RowBytes;
        public int PixelSize = 1;
        public int CmpCount = 1;
        public int PackType;
        public int PixelType;                        // 0 indexed, 16 direct (RGBDirect)
        public bool IsPixMap;
        public bool MacOS9;                          // read the pixel data as Mac OS 9's QuickDraw does
        public PictColor[] Palette = Array.Empty<PictColor>();
        // The color table's exact 16-bit components, when read from one (else empty: the palette's bytes replicated).
        public (ushort r, ushort g, ushort b)[] Palette16 = Array.Empty<(ushort, ushort, ushort)>();

        // A palette entry's 16-bit components.
        public (int r, int g, int b) Exact(int index)
        {
            if (index < Palette16.Length) return Palette16[index];
            var c = index < Palette.Length ? Palette[index] : new PictColor(0, 0, 0);
            return (c.R * 257, c.G * 257, c.B * 257);
        }
        public byte[] Data = Array.Empty<byte>();

        public int Width => Bounds.Width;
        public int Height => Bounds.Height;
        public bool IsDirect => PixelSize == 16 || PixelSize == 32;

        // The color of pixel (x, y), relative to Bounds' top-left. Alpha is always opaque (QuickDraw ignores it).
        public PictColor GetPixel(int x, int y)
        {
            int row = y * RowBytes;
            switch (PixelSize)
            {
                case 16:
                {
                    int p = (Data[row + 2 * x] << 8) | Data[row + 2 * x + 1];
                    int r5 = (p >> 10) & 0x1F, g5 = (p >> 5) & 0x1F, b5 = p & 0x1F;
                    return new PictColor((byte)((r5 << 3) | (r5 >> 2)), (byte)((g5 << 3) | (g5 >> 2)), (byte)((b5 << 3) | (b5 >> 2)));
                }
                case 32:
                {
                    int i = row + 4 * x;
                    return new PictColor(Data[i + 1], Data[i + 2], Data[i + 3]);
                }
                default:
                {
                    int bitPos = x * PixelSize;
                    int value = 0;
                    for (int i = 0; i < PixelSize; i++)
                    {
                        int bit = bitPos + i;
                        value = (value << 1) | ((Data[row + (bit >> 3)] >> (7 - (bit & 7))) & 1);
                    }
                    return value < Palette.Length ? Palette[value] : new PictColor(0, 0, 0);
                }
            }
        }

        // The raw index of a pixel of an indexed map (1-8 bits).
        public int GetIndex(int x, int y)
        {
            int bit = x * PixelSize, value = 0, row = y * RowBytes;
            for (int i = 0; i < PixelSize; i++, bit++)
                value = (value << 1) | ((Data[row + (bit >> 3)] >> (7 - (bit & 7))) & 1);
            return value;
        }

        // The components of a direct pixel at its own depth: 5-bit fields for 16-bit, 8-bit bytes for 32-bit.
        public (int r, int g, int b) GetComponents(int x, int y)
        {
            int row = y * RowBytes;
            if (PixelSize == 16)
            {
                int p = (Data[row + 2 * x] << 8) | Data[row + 2 * x + 1];
                return ((p >> 10) & 0x1F, (p >> 5) & 0x1F, p & 0x1F);
            }
            int i = row + 4 * x;
            return (Data[i + 1], Data[i + 2], Data[i + 3]);
        }

        // The alpha byte of a 32-bit pixel (the first of its four; meaningful only when CmpCount is 4).
        public byte GetAlpha(int x, int y) => PixelSize == 32 ? Data[y * RowBytes + 4 * x] : (byte)255;

        // BitsRect/BitsRgn/PackBitsRect/PackBitsRgn operands up to (not including) srcRect: a 1-bit BitMap, or a
        // PixMap + ColorTable when rowBytes has its high bit set.
        // Mac OS 9 reads a color table whenever pixelSize < 9, whatever the opcode; the ROM by the opcode.
        public static PixMap ReadIndexedHeader(BinaryReader b, bool macOS9)
        {
            int rawRowBytes = b.ReadU16BE();
            var pm = new PixMap { RowBytes = rawRowBytes & RowBytesMask, IsPixMap = (rawRowBytes & 0x8000) != 0, MacOS9 = macOS9 };
            pm.Bounds = b.ReadRectBE();
            if (pm.IsPixMap)
            {
                pm.ReadPixMapFields(b);
                if (!macOS9 || pm.PixelSize < 9) (pm.Palette, pm.Palette16) = ReadColorTableExact(b, pm.PixelSize);
            }
            else
            {
                pm.Palette = new[] { new PictColor(255, 255, 255), new PictColor(0, 0, 0) };
            }
            return pm;
        }

        // DirectBitsRect/DirectBitsRgn operands up to srcRect: baseAddr (always $000000FF), then a PixMap.
        public static PixMap ReadDirectHeader(BinaryReader b, bool macOS9)
        {
            b.ReadU32BE();                                           // baseAddr
            int rawRowBytes = b.ReadU16BE();
            var pm = new PixMap { RowBytes = rawRowBytes & RowBytesMask, IsPixMap = true, MacOS9 = macOS9 };
            pm.Bounds = b.ReadRectBE();
            pm.ReadPixMapFields(b);
            if (macOS9 && pm.PixelSize < 9) (pm.Palette, pm.Palette16) = ReadColorTableExact(b, pm.PixelSize);
            return pm;
        }

        // BkPixPat/PnPixPat/FillPixPat full pattern (type 1): PixMap (rowBytes first, no baseAddr) + ColorTable + PixData.
        public static PixMap ReadPatternPixMap(BinaryReader b, bool macOS9)
        {
            int rawRowBytes = b.ReadU16BE();
            var pm = new PixMap { RowBytes = rawRowBytes & RowBytesMask, IsPixMap = true, MacOS9 = macOS9 };
            pm.Bounds = b.ReadRectBE();
            pm.ReadPixMapFields(b);
            if (!macOS9 || pm.PixelSize < 9) (pm.Palette, pm.Palette16) = ReadColorTableExact(b, pm.PixelSize);
            pm.ReadPixData(b);
            return pm;
        }

        // PixMap fields after rowBytes + bounds (Executor eatPixMap): pmVersion, packType, packSize, hRes, vRes,
        // pixelType, pixelSize, cmpCount, cmpSize, planeBytes, pmTable, pmReserved.
        private void ReadPixMapFields(BinaryReader b)
        {
            b.ReadU16BE();                  // pmVersion
            PackType = b.ReadU16BE();
            b.ReadU32BE();                  // packSize
            b.ReadU32BE();                  // hRes
            b.ReadU32BE();                  // vRes
            PixelType = b.ReadU16BE();
            PixelSize = b.ReadU16BE();
            CmpCount = b.ReadU16BE();
            b.ReadU16BE();                  // cmpSize
            b.ReadU32BE();                  // planeBytes
            b.ReadU32BE();                  // pmTable
            b.ReadU32BE();                  // pmReserved
            if (PixelSize != 1 && PixelSize != 2 && PixelSize != 4 && PixelSize != 8 && PixelSize != 16 && PixelSize != 32)
                throw new NotSupportedException($"PixMap pixelSize {PixelSize} is not a QuickDraw depth");
        }

        // ColorTable: ctSeed, ctFlags, ctSize (entries - 1), then (value, RGB) entries. A device table (ctFlags bit 15)
        // is indexed by position; otherwise each entry's value is its pixel index. Unlisted indices are black.
        internal static PictColor[] ReadColorTable(BinaryReader b, int pixelSize) => ReadColorTableExact(b, pixelSize).palette;

        internal static (PictColor[] palette, (ushort r, ushort g, ushort b)[] exact) ReadColorTableExact(BinaryReader b, int pixelSize)
        {
            b.ReadU32BE();                                           // ctSeed
            int ctFlags = b.ReadU16BE();
            int ctSize = b.ReadU16BE();
            bool positional = (ctFlags & 0x8000) != 0;
            var palette = new PictColor[1 << Math.Min(pixelSize, 8)];
            var exact = new (ushort r, ushort g, ushort b)[palette.Length];
            for (int i = 0; i < palette.Length; i++) palette[i] = new PictColor(0, 0, 0);
            for (int i = 0; i <= ctSize; i++)
            {
                int value = b.ReadU16BE();
                int r = b.ReadU16BE(), g = b.ReadU16BE(), bl = b.ReadU16BE();
                int index = positional ? i : value;
                if (index >= 0 && index < palette.Length)
                {
                    palette[index] = new PictColor((byte)(r >> 8), (byte)(g >> 8), (byte)(bl >> 8));
                    exact[index] = ((ushort)r, (ushort)g, (ushort)bl);
                }
            }
            return (palette, exact);
        }

        // PixData into the in-memory layout, as the ROM's pixel-data readers (GetPMData / GetDirectPMData) do: rows
        // under 8 bytes are unpacked; a direct PixMap (pixelType 16) is unpacked for packType 1 and otherwise
        // dispatched on packType whatever its pixel size (ReadDirect); everything else - BitMaps and indexed PixMaps,
        // with any packType and under any bitmap opcode - is one PackBits scan line per row, preceded by a byte count
        // (a word when rowBytes > 250).
        public void ReadPixData(BinaryReader b)
        {
            int height = Math.Max(0, Height);
            Data = new byte[RowBytes * height];
            bool direct = IsPixMap && PixelType == 16;

            if (RowBytes < 8 || (direct && PackType == 1))
            {
                var raw = b.ReadExactly(Data.Length);
                Buffer.BlockCopy(raw, 0, Data, 0, raw.Length);
                return;
            }
            if (direct)
            {
                ReadDirect(b, height);
                return;
            }

            bool sizesAreWords = RowBytes > 250;
            var line = new byte[RowBytes];
            for (int y = 0; y < height; y++)
            {
                UnpackRow(b, line, sizesAreWords, wordChunks: false, MacOS9);
                Buffer.BlockCopy(line, 0, Data, y * RowBytes, RowBytes);
            }
        }

        // Packed direct pixel data, dispatched on packType as the ROM's GetDirectPMData does (pixelSize is never checked,
        // so a 16-bit map with packType 0, 2 or 4 decodes as wrongly as on a Macintosh):
        // 0 or 2: rows of 3 bytes per pixel (R, G, B) without row counts, expanded to 0RGB; 3: word-chunk PackBits
        // rows; 4: component-plane PackBits rows, cmpCount planes rowBytes/4 wide landing on pixel bytes
        // 4 - cmpCount .. 3 (alpha stays 0 with three planes); 5 and up: the rows are read and discarded, leaving the
        // pixels zero.
        // Mac OS 9 fixes the ROM's 16-bit packType 0: word PackBits, like packType 3.
        private void ReadDirect(BinaryReader b, int height)
        {
            bool sizesAreWords = RowBytes > 250;
            int pixels = RowBytes / 4;
            int packType = MacOS9 && PixelSize == 16 && PackType == 0 ? 3 : PackType;
            switch (packType)
            {
                case 3:
                {
                    var line = new byte[RowBytes];
                    for (int y = 0; y < height; y++)
                    {
                        UnpackRow(b, line, sizesAreWords, wordChunks: true, MacOS9);
                        Buffer.BlockCopy(line, 0, Data, y * RowBytes, RowBytes);
                    }
                    return;
                }
                case 4 when MacOS9:
                    ReadPlanesMacOS9(b, height, sizesAreWords, pixels);
                    return;
                case 4:
                {
                    int planes = Math.Clamp(CmpCount, 1, 4), first = 4 - planes;
                    var packed = new byte[pixels * planes];
                    for (int y = 0; y < height; y++)
                    {
                        UnpackRow(b, packed, sizesAreWords, wordChunks: false, MacOS9);
                        int row = y * RowBytes;
                        for (int k = 0; k < planes; k++)
                            for (int x = 0; x < pixels; x++)
                                Data[row + 4 * x + first + k] = packed[k * pixels + x];
                    }
                    return;
                }
                default:
                    if (packType >= 5)
                    {
                        for (int y = 0; y < height; y++)
                            b.Skip(sizesAreWords ? b.ReadU16BE() : b.ReadByte());
                        return;
                    }
                    var raw = b.ReadExactly(pixels * height * 3);
                    for (int i = 0, s = 0; i < pixels * height; i++, s += 3)
                    {
                        Data[4 * i + 1] = raw[s]; Data[4 * i + 2] = raw[s + 1]; Data[4 * i + 3] = raw[s + 2];
                    }
                    return;
            }
        }

        // Mac OS 9's component-plane reader (native GetPMData): three planes (bytes 1-3, alpha 0) unless cmpCount is
        // 4, unpacking n = rowBytes - rowBytes/4 bytes (rowBytes with 4 planes) per row. Its packed-row buffer holds
        // only (n + (n >> 7) + 3) & ~3 bytes and the unpack buffer follows it directly, both kept for the whole
        // image: a row packed into more bytes than that spills into the unpack buffer, and the lazy unpacker, reading
        // those bytes after it has overwritten them, repeats early output there (a real Mac OS 9 artifact).
        private void ReadPlanesMacOS9(BinaryReader b, int height, bool sizesAreWords, int pixels)
        {
            int planes = CmpCount == 4 ? 4 : 3, first = 4 - planes;
            int n = CmpCount == 4 ? RowBytes : RowBytes - RowBytes / 4;
            int packSize = (n + (n >> 7) + 3) & ~3;
            var memory = new byte[packSize + n];
            for (int y = 0; y < height; y++)
            {
                int count = sizesAreWords ? b.ReadU16BE() : b.ReadByte();
                var packed = b.ReadExactly(count);
                Array.Copy(packed, 0, memory, 0, Math.Min(count, memory.Length));
                int src = 0, dst = 0;
                while (dst < n && src < memory.Length)
                {
                    sbyte flag = (sbyte)memory[src++];
                    if (flag >= 0)
                    {
                        for (int i = 0; i <= flag && dst < n && src < memory.Length; i++) memory[packSize + dst++] = memory[src++];
                    }
                    else
                    {
                        if (src >= memory.Length) break;
                        byte v = memory[src++];
                        for (int i = 0; i < 1 - flag && dst < n; i++) memory[packSize + dst++] = v;
                    }
                }
                int row = y * RowBytes;
                for (int k = 0; k < planes; k++)
                    for (int x = 0; x < pixels; x++)
                    {
                        int at = k * pixels + x;
                        if (at < n) Data[row + 4 * x + first + k] = memory[packSize + at];
                    }
            }
        }

        // One PackBits scan line: [byteCount] then flag-counted runs until byteCount is consumed. flag < 0 repeats the
        // next unit 1 - flag times; flag >= 0 copies flag + 1 units; -128 is a no-op in the ROM (Apple TN1023) and a
        // run of 129 in Mac OS 9. A unit is a byte, or a word for 16-bit pixels.
        private static void UnpackRow(BinaryReader b, byte[] outRow, bool sizesAreWords, bool wordChunks, bool macOS9)
        {
            int packedBytes = sizesAreWords ? b.ReadU16BE() : b.ReadByte();
            var src = b.ReadExactly(packedBytes);
            Array.Clear(outRow);
            int unit = wordChunks ? 2 : 1;
            int ip = 0, op = 0;
            while (ip < src.Length && op < outRow.Length)
            {
                sbyte flag = (sbyte)src[ip++];
                if (flag == -128 && !macOS9) continue;
                if (flag < 0)
                {
                    int n = 1 - flag;
                    if (ip + unit > src.Length) break;
                    for (int i = 0; i < n && op + unit <= outRow.Length; i++)
                        for (int k = 0; k < unit; k++) outRow[op++] = src[ip + k];
                    ip += unit;
                }
                else
                {
                    int n = (flag + 1) * unit;
                    for (int i = 0; i < n && ip < src.Length && op < outRow.Length; i++) outRow[op++] = src[ip++];
                }
            }
        }
    }
}
