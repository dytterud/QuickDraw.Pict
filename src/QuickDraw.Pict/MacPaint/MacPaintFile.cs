using System;
using System.Buffers.Binary;
using System.Text;

namespace QuickDraw.Pict
{
    /// <summary>
    /// MacPaint documents (file type <c>PNTG</c>; <c>.pntg</c>, <c>.pnt</c>, <c>.mac</c>): a 576 × 720 1-bit image.
    /// The file is a 512-byte header (<c>u32</c> version 0, 2 or 3; for versions 2 and 3, 38 fill patterns of 8 bytes;
    /// then padding) followed by 720 rows of 72 bytes, each PackBits-compressed; a 1 bit is black. A MacBinary wrapper
    /// (a 128-byte header naming the file type <c>PNTG</c>) is skipped.
    /// </summary>
    public static class MacPaintFile
    {
        /// <summary>Width of every MacPaint image.</summary>
        public const int Width = 576;

        /// <summary>Height of every MacPaint image.</summary>
        public const int Height = 720;

        /// <summary>Size of the MacPaint header before the image rows.</summary>
        public const int HeaderSize = 512;

        private const int MacBinaryHeaderSize = 128;

        /// <summary>
        /// True if <paramref name="data"/> looks like a MacPaint document: MacBinary-wrapped with file type
        /// <c>PNTG</c>, or a header with version 0, 2 or 3 and zero padding whose first row unpacks to exactly 72 bytes.
        /// MacPaint has no magic number, so a bare header is only a strong hint; prefer the file extension or type.
        /// </summary>
        public static bool IsMacPaintFile(ReadOnlySpan<byte> data)
        {
            if (IsMacBinary(data)) return true;
            if (data.Length < HeaderSize + 2) return false;
            uint version = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (version != 0 && version != 2 && version != 3) return false;
            if (data.Slice(4 + 38 * 8, HeaderSize - 4 - 38 * 8).IndexOfAnyExcept((byte)0) >= 0) return false;
            return FirstRowIsWhole(data.Slice(HeaderSize));
        }

        /// <summary>Decodes the image: black on white, opaque. Rows missing from a truncated file stay white.</summary>
        /// <exception cref="NotSupportedException">The data is too short to hold a MacPaint image.</exception>
        public static PictBitmap Decode(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            ReadOnlySpan<byte> span = data;
            if (IsMacBinary(span))
            {
                int forkLength = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(span.Slice(83)), (uint)(data.Length - MacBinaryHeaderSize));
                span = span.Slice(MacBinaryHeaderSize, forkLength);
            }
            if (span.Length <= HeaderSize) throw new NotSupportedException("The data is too short to be a MacPaint document.");
            var image = DecodeRows(span.Slice(HeaderSize));
            return image ?? throw new NotSupportedException("The MacPaint document has no image data.");
        }

        // The image rows (after any header), PackBits-compressed back to back; 1 = black; rows missing from the data stay
        // white. Also QuickTime's 'PNTG' codec.
        internal static PictBitmap? DecodeRows(ReadOnlySpan<byte> data)
        {
            const int rowBytes = Width / 8;
            var bits = new byte[rowBytes * Height];
            int consumed = PackBits.Unpack(data, bits);
            if (consumed == 0) return null;
            var img = new PictBitmap(Width, Height);
            var px = img.Pixels;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    byte v = ((bits[y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1) != 0 ? (byte)0 : (byte)255;
                    int i = (y * Width + x) * 4;
                    px[i] = px[i + 1] = px[i + 2] = v;
                    px[i + 3] = 255;
                }
            return img;
        }

        // MacBinary (I/II/III): byte 0 zero, a 1-63 character name at 1, file type PNTG at 65, zero at 74 and 82.
        private static bool IsMacBinary(ReadOnlySpan<byte> data) =>
            data.Length >= MacBinaryHeaderSize && data[0] == 0 && data[1] >= 1 && data[1] <= 63 && data[74] == 0 &&
            data[82] == 0 && Encoding.Latin1.GetString(data.Slice(65, 4)) == "PNTG";

        // The first PackBits row fills exactly 72 bytes (a run or literal never straddles MacPaint's rows).
        private static bool FirstRowIsWhole(ReadOnlySpan<byte> rows)
        {
            int ip = 0, produced = 0;
            while (produced < Width / 8)
            {
                if (ip >= rows.Length) return false;
                sbyte flag = (sbyte)rows[ip++];
                if (flag == -128) continue;
                if (flag < 0)
                {
                    produced += 1 - flag;
                    ip++;
                }
                else
                {
                    produced += flag + 1;
                    ip += flag + 1;
                }
            }
            return produced == Width / 8 && ip <= rows.Length;
        }
    }
}
