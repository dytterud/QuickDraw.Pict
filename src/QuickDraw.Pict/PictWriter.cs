using System;
using System.Collections.Generic;
using System.IO;

namespace QuickDraw.Pict
{
    /// <summary>
    /// PICT v2 encoder: writes an image as a standard QuickDraw picture (512-byte file header + extended v2 header +
    /// a single 24-bit DirectBitsRect with component-wise PackBits). Round-trips with <see cref="PictReader"/> and is
    /// openable by other PICT readers. Alpha is not stored. Rows are written one at a time, top to bottom, via
    /// <see cref="WriteRow"/>; call <see cref="Finish"/> after the last row.
    /// </summary>
    public sealed class PictWriter
    {
        private readonly Stream stream;
        private readonly int width, height;
        private readonly byte[] planar;
        private readonly bool sizesAreWords;
        private long written;
        private int rows;

        /// <summary>Starts a picture of the given size on <paramref name="stream"/> and writes its header.</summary>
        public PictWriter(Stream stream, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (width <= 0 || width > short.MaxValue / 3) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0 || height > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(height));
            this.stream = stream;
            this.width = width;
            this.height = height;
            int rowBytes = width * 3;
            planar = new byte[rowBytes];
            sizesAreWords = rowBytes > 250;
            WriteHeader(rowBytes);
        }

        /// <summary>Writes a whole bitmap (alpha is dropped) as a PICT file.</summary>
        public static void Write(Stream stream, PictBitmap bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            var writer = new PictWriter(stream, bitmap.Width, bitmap.Height);
            var rgb = new byte[bitmap.Width * 3];
            for (int y = 0; y < bitmap.Height; y++)
            {
                var src = bitmap.Pixels.AsSpan(y * bitmap.Width * 4, bitmap.Width * 4);
                for (int x = 0; x < bitmap.Width; x++)
                {
                    rgb[3 * x] = src[4 * x];
                    rgb[3 * x + 1] = src[4 * x + 1];
                    rgb[3 * x + 2] = src[4 * x + 2];
                }
                writer.WriteRow(rgb);
            }
            writer.Finish();
        }

        /// <summary>Writes the next row, given as interleaved 8-bit R, G, B (width × 3 bytes).</summary>
        public void WriteRow(ReadOnlySpan<byte> rgb)
        {
            if (rgb.Length != width * 3) throw new ArgumentException($"Expected {width * 3} bytes of RGB data.", nameof(rgb));
            if (rows == height) throw new InvalidOperationException("All rows have already been written.");
            for (int x = 0; x < width; x++)
            {
                planar[x] = rgb[3 * x];
                planar[width + x] = rgb[3 * x + 1];
                planar[2 * width + x] = rgb[3 * x + 2];
            }
            var packed = PackBits(planar);
            if (sizesAreWords) U16(packed.Length); else U8(packed.Length);
            Bytes(packed);
            rows++;
        }

        /// <summary>Ends the picture. Every row must have been written.</summary>
        public void Finish()
        {
            if (rows != height) throw new InvalidOperationException($"Wrote {rows} of {height} rows.");
            if ((written & 1) == 1) U8(0);   // word-align before EndPic
            U16(0x00FF);
        }

        private void WriteHeader(int rowBytes)
        {
            int w = width, h = height;
            Bytes(new byte[PictHeader.FileHeaderSize]);  // file-format null header
            U16(0);                                  // picSize (ignored by readers)
            Rect(0, 0, h, w);                        // picFrame
            U16(0x0011); U16(0x02FF);                // version 2

            // extended v2 header (0x0C00 + 24 bytes): version -2, reserved, 72dpi hRes/vRes, srcRect, reserved
            U16(0x0C00);
            U16(0xFFFE); U16(0);
            U32(0x00480000); U32(0x00480000);
            Rect(0, 0, h, w);
            U32(0);

            // clip = frame
            U16(0x0001); U16(10); Rect(0, 0, h, w);

            // DirectBitsRect: 32-bit direct PixMap, packType 4 (component-wise), 3 components (RGB)
            U16(0x009A);
            U32(0x000000FF);                         // baseAddr (convention)
            U16(rowBytes | 0x8000);                  // rowBytes, high bit = PixMap
            Rect(0, 0, h, w);                        // bounds
            U16(0);                                  // pmVersion
            U16(4);                                  // packType = 4 (RLE by component)
            U32(0);                                  // packSize
            U32(0x00480000); U32(0x00480000);        // hRes, vRes
            U16(16);                                 // pixelType = RGBDirect
            U16(32);                                 // pixelSize
            U16(3);                                  // cmpCount
            U16(8);                                  // cmpSize
            U32(0);                                  // planeBytes
            U32(0);                                  // pmTable
            U32(0);                                  // reserved
            Rect(0, 0, h, w);                        // srcRect
            Rect(0, 0, h, w);                        // dstRect
            U16(0);                                  // mode = srcCopy
        }

        private void U8(int v) { stream.WriteByte((byte)v); written++; }
        private void U16(int v) { U8(v >> 8); U8(v); }
        private void U32(uint v) { U8((int)(v >> 24)); U8((int)(v >> 16)); U8((int)(v >> 8)); U8((int)v); }
        private void Rect(int t, int l, int b, int r) { U16(t); U16(l); U16(b); U16(r); }
        private void Bytes(byte[] data) { stream.Write(data, 0, data.Length); written += data.Length; }

        // Apple PackBits RLE encoder: runs of >=3 equal bytes -> (257-runLen, value); otherwise a
        // literal block (count-1, bytes).
        private static byte[] PackBits(byte[] data)
        {
            var outp = new List<byte>(data.Length);
            int i = 0, n = data.Length;
            while (i < n)
            {
                int runEnd = i;
                while (runEnd < n - 1 && runEnd - i < 127 && data[runEnd + 1] == data[i]) runEnd++;
                int runLen = runEnd - i + 1;
                if (runLen >= 3)
                {
                    outp.Add((byte)(257 - runLen));
                    outp.Add(data[i]);
                    i += runLen;
                }
                else
                {
                    int litStart = i;
                    i++;
                    while (i < n && i - litStart < 128)
                    {
                        if (i < n - 2 && data[i] == data[i + 1] && data[i + 1] == data[i + 2]) break;
                        i++;
                    }
                    int litLen = i - litStart;
                    outp.Add((byte)(litLen - 1));
                    for (int k = 0; k < litLen; k++) outp.Add(data[litStart + k]);
                }
            }
            return outp.ToArray();
        }
    }
}
