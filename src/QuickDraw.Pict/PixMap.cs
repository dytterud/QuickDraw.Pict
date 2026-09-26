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
        public bool IsPixMap;
        public PictColor[] Palette = Array.Empty<PictColor>();
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

        // BitsRect/BitsRgn/PackBitsRect/PackBitsRgn operands up to (not including) srcRect: a 1-bit BitMap, or a
        // PixMap + ColorTable when rowBytes has its high bit set.
        public static PixMap ReadIndexedHeader(BinaryReader b)
        {
            int rawRowBytes = b.ReadU16BE();
            var pm = new PixMap { RowBytes = rawRowBytes & RowBytesMask, IsPixMap = (rawRowBytes & 0x8000) != 0 };
            pm.Bounds = b.ReadRectBE();
            if (pm.IsPixMap)
            {
                pm.ReadPixMapFields(b);
                pm.Palette = ReadColorTable(b, pm.PixelSize);
            }
            else
            {
                pm.Palette = new[] { new PictColor(255, 255, 255), new PictColor(0, 0, 0) };
            }
            return pm;
        }

        // DirectBitsRect/DirectBitsRgn operands up to srcRect: baseAddr (always $000000FF), then a PixMap.
        public static PixMap ReadDirectHeader(BinaryReader b)
        {
            b.ReadU32BE();                                           // baseAddr
            int rawRowBytes = b.ReadU16BE();
            var pm = new PixMap { RowBytes = rawRowBytes & RowBytesMask, IsPixMap = true };
            pm.Bounds = b.ReadRectBE();
            pm.ReadPixMapFields(b);
            return pm;
        }

        // BkPixPat/PnPixPat/FillPixPat full pattern (type 1): PixMap (rowBytes first, no baseAddr) + ColorTable + PixData.
        public static PixMap ReadPatternPixMap(BinaryReader b)
        {
            int rawRowBytes = b.ReadU16BE();
            var pm = new PixMap { RowBytes = rawRowBytes & RowBytesMask, IsPixMap = true };
            pm.Bounds = b.ReadRectBE();
            pm.ReadPixMapFields(b);
            pm.Palette = ReadColorTable(b, pm.PixelSize);
            pm.ReadPixData(b, packedOpcode: true);
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
            b.ReadU16BE();                  // pixelType
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
        private static PictColor[] ReadColorTable(BinaryReader b, int pixelSize)
        {
            b.ReadU32BE();                                           // ctSeed
            int ctFlags = b.ReadU16BE();
            int ctSize = b.ReadU16BE();
            bool positional = (ctFlags & 0x8000) != 0;
            var palette = new PictColor[1 << Math.Min(pixelSize, 8)];
            for (int i = 0; i < palette.Length; i++) palette[i] = new PictColor(0, 0, 0);
            for (int i = 0; i <= ctSize; i++)
            {
                int value = b.ReadU16BE();
                int r = b.ReadU16BE(), g = b.ReadU16BE(), bl = b.ReadU16BE();
                int index = positional ? i : value;
                if (index >= 0 && index < palette.Length)
                    palette[index] = new PictColor((byte)(r >> 8), (byte)(g >> 8), (byte)(bl >> 8));
            }
            return palette;
        }

        // PixData into the in-memory layout (Appendix A): unpacked when packType is 1 or rowBytes < 8; packType 2
        // drops the pad byte of 32-bit pixels (3 bytes per pixel); otherwise one PackBits scan line per row, preceded
        // by a byte count (a word when rowBytes > 250). 16-bit rows pack word chunks (packType 3); 32-bit rows pack
        // component planes (packType 4), each rowBytes/4 wide, alpha plane first when cmpCount is 4. For a 1-bit
        // BitMap, packing is chosen by the opcode (BitsRect is never packed).
        public void ReadPixData(BinaryReader b, bool packedOpcode)
        {
            int height = Math.Max(0, Height);
            Data = new byte[RowBytes * height];
            bool unpacked = RowBytes < 8 || (IsPixMap ? PackType == 1 : !packedOpcode);

            if (IsPixMap && PixelSize == 32 && PackType == 2 && RowBytes >= 8)
            {
                int pixels = RowBytes / 4;
                var raw = b.ReadExactly(pixels * 3 * height);
                for (int y = 0, s = 0; y < height; y++)
                    for (int x = 0; x < pixels; x++, s += 3)
                    {
                        int d = y * RowBytes + 4 * x;
                        Data[d + 1] = raw[s]; Data[d + 2] = raw[s + 1]; Data[d + 3] = raw[s + 2];
                    }
                return;
            }

            if (unpacked)
            {
                var raw = b.ReadExactly(Data.Length);
                Buffer.BlockCopy(raw, 0, Data, 0, raw.Length);
                return;
            }

            bool sizesAreWords = RowBytes > 250;
            if (PixelSize == 32)
            {
                int comp = RowBytes / 4;
                int planes = CmpCount == 4 ? 4 : 3;
                var packed = new byte[comp * planes];
                for (int y = 0; y < height; y++)
                {
                    UnpackRow(b, packed, sizesAreWords, wordChunks: false);
                    int row = y * RowBytes;
                    for (int x = 0; x < comp; x++)
                    {
                        if (planes == 4)
                        {
                            Data[row + 4 * x] = packed[x];
                            Data[row + 4 * x + 1] = packed[comp + x];
                            Data[row + 4 * x + 2] = packed[2 * comp + x];
                            Data[row + 4 * x + 3] = packed[3 * comp + x];
                        }
                        else
                        {
                            Data[row + 4 * x + 1] = packed[x];
                            Data[row + 4 * x + 2] = packed[comp + x];
                            Data[row + 4 * x + 3] = packed[2 * comp + x];
                        }
                    }
                }
                return;
            }

            var line = new byte[RowBytes];
            for (int y = 0; y < height; y++)
            {
                UnpackRow(b, line, sizesAreWords, wordChunks: PixelSize == 16);
                Buffer.BlockCopy(line, 0, Data, y * RowBytes, RowBytes);
            }
        }

        // One PackBits scan line: [byteCount] then flag-counted runs until byteCount is consumed. flag < 0 repeats the
        // next unit 1 - flag times; flag >= 0 copies flag + 1 units; -128 is a no-op (Apple TN1023). A unit is a
        // byte, or a word for 16-bit pixels.
        private static void UnpackRow(BinaryReader b, byte[] outRow, bool sizesAreWords, bool wordChunks)
        {
            int packedBytes = sizesAreWords ? b.ReadU16BE() : b.ReadByte();
            var src = b.ReadExactly(packedBytes);
            Array.Clear(outRow);
            int unit = wordChunks ? 2 : 1;
            int ip = 0, op = 0;
            while (ip < src.Length && op < outRow.Length)
            {
                sbyte flag = (sbyte)src[ip++];
                if (flag == -128) continue;
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
