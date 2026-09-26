using System;
using System.Buffers.Binary;
using System.IO;

namespace QuickDraw.Pict
{
    /// <summary>
    /// Recognizes QuickDraw pictures. PICT has no magic number: a picture starts with <c>u16 picSize</c> and
    /// <c>Rect picFrame</c>, then the version opcode, which is <c>0x1101</c> (v1) or <c>0x0011 0x02FF</c> (v2,
    /// always followed by the <c>0x0C00</c> header opcode). A <c>.pict</c> file puts a 512-byte application header
    /// before the picture; a <c>PICT</c> resource does not.
    /// </summary>
    public static class PictHeader
    {
        /// <summary>Size of the application header that precedes the picture in a <c>.pict</c> file.</summary>
        public const int FileHeaderSize = 512;

        /// <summary>Bytes of a bare picture needed to recognize it: picSize, picFrame and the v2 version + header opcodes.</summary>
        public const int SignatureLength = 16;

        /// <summary>True if <paramref name="data"/> starts with a bare picture (as stored in a <c>PICT</c> resource).</summary>
        public static bool IsPicture(ReadOnlySpan<byte> data)
        {
            if (data.Length < 12) return false;
            short top = I16(data, 2), left = I16(data, 4), bottom = I16(data, 6), right = I16(data, 8);
            if (bottom <= top || right <= left) return false;
            if (IsVersion1(data)) return true;
            return data.Length >= SignatureLength
                && U16(data, 10) == 0x0011 && U16(data, 12) == 0x02FF && U16(data, 14) == 0x0C00;
        }

        /// <summary>True if <paramref name="data"/> starts with a <c>.pict</c> file: a 512-byte header, then a picture.</summary>
        public static bool IsPictFile(ReadOnlySpan<byte> data)
        {
            if (data.Length < FileHeaderSize + 12) return false;
            var picture = data.Slice(FileHeaderSize);
            if (!IsPicture(picture)) return false;
            // v1's two-byte version opcode is a weak signature at an arbitrary offset, so for v1 also require the
            // conventional all-zero application header.
            return !IsVersion1(picture) || data.Slice(0, FileHeaderSize).IndexOfAnyExcept((byte)0) < 0;
        }

        /// <summary>
        /// Reads the picture frame size from the current position of <paramref name="stream"/> (bare picture or
        /// <c>.pict</c> file), without decoding. Sizes are clamped to at least 1, matching <see cref="PictReader"/>.
        /// </summary>
        /// <exception cref="EndOfStreamException">The stream ends inside the picture header.</exception>
        public static (int Width, int Height) ReadFrameSize(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            long start = stream.Position;
            Span<byte> head = stackalloc byte[10];
            stream.ReadExactly(head);
            // Same rule as PictReader: an all-zero picture header means a 512-byte file header precedes it.
            if (head.IndexOfAnyExcept((byte)0) < 0 && stream.Length - start > FileHeaderSize)
            {
                stream.Position = start + FileHeaderSize;
                stream.ReadExactly(head);
            }
            int top = I16(head, 2), left = I16(head, 4), bottom = I16(head, 6), right = I16(head, 8);
            return (Math.Max(1, right - left), Math.Max(1, bottom - top));
        }

        private static bool IsVersion1(ReadOnlySpan<byte> picture) => picture[10] == 0x11 && picture[11] == 0x01;
        private static short I16(ReadOnlySpan<byte> d, int offset) => BinaryPrimitives.ReadInt16BigEndian(d.Slice(offset));
        private static ushort U16(ReadOnlySpan<byte> d, int offset) => BinaryPrimitives.ReadUInt16BigEndian(d.Slice(offset));
    }
}
