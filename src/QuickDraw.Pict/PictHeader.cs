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
        /// Reads the header of the picture at the current position of <paramref name="stream"/> (bare picture or
        /// <c>.pict</c> file) without decoding it. Comments and the ICC profile are only collected by
        /// <see cref="PictReader"/>.
        /// </summary>
        /// <exception cref="EndOfStreamException">The stream ends inside the picture header.</exception>
        /// <exception cref="NotSupportedException">The data does not start with a PICT version opcode.</exception>
        public static PictInfo ReadInfo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            using var b = new BinaryReader(stream, System.Text.Encoding.Latin1, leaveOpen: true);
            return Parse(b, stream.Length - stream.Position, out _);
        }

        // Parses picSize, picFrame (skipping a 512-byte file header), the version opcode(s) and, for version 2, the
        // HeaderOp (0x0C00) if present, leaving the reader at the first drawing opcode.
        // - An all-zero picSize+picFrame means a .pict file header precedes the picture.
        // - Extended version 2 (header version -2): hRes/vRes are the picture's resolution and a non-empty srcRect is
        //   the coordinate space its opcodes draw in (Listing A-5; Executor DrawPicture). Version -1 carries a Fixed
        //   bounding box instead and draws in picFrame at 72 dpi (Listing A-6).
        internal static PictInfo Parse(BinaryReader b, long length, out bool version1)
        {
            long start = b.BaseStream.Position;
            // A .pict file's 512-byte application header is usually zero, but some creators (MacDraw: "DRWG...",
            // MacDraft: "pictDF...") fill it; skip it whenever the picture's version opcode is found after it
            // rather than at the start.
            if (length >= FileHeaderSize + 12 && !HasVersionOpcode(b, start) && HasVersionOpcode(b, start + FileHeaderSize))
                start += FileHeaderSize;
            b.BaseStream.Position = start;
            b.ReadU16BE();                                            // picSize: unreliable in v2
            var frame = b.ReadRectBE();

            ushort versionOp = b.ReadU16BE();
            if (versionOp == 0x1101)                                  // 0x11 VersionOp, 0x01: 1-byte opcodes follow
            {
                version1 = true;
                return new PictInfo(1, false, frame, frame, 72, 72);
            }
            if (versionOp != 0x0011)
                throw new NotSupportedException($"Unexpected PICT version opcode 0x{versionOp:X4}");

            version1 = false;
            b.ReadU16BE();                                            // Version (0x02FF)
            long afterVersion = b.BaseStream.Position;
            if (b.BaseStream.Length - afterVersion < 2 + 24 || b.ReadU16BE() != 0x0C00)
            {
                b.BaseStream.Position = afterVersion;                 // no HeaderOp: that word is the first opcode
                return new PictInfo(2, false, frame, frame, 72, 72);
            }

            short headerVersion = b.ReadI16BE();
            b.ReadU16BE();                                            // reserved
            int hRes = b.ReadI32BE(), vRes = b.ReadI32BE();           // Fixed 16.16
            var srcRect = b.ReadRectBE();
            b.ReadU32BE();                                            // reserved
            if (headerVersion != -2)
                return new PictInfo(2, false, frame, frame, 72, 72);
            return new PictInfo(2, true, frame, srcRect.IsEmpty ? frame : srcRect,
                hRes > 0 ? hRes / 65536.0 : 72, vRes > 0 ? vRes / 65536.0 : 72);
        }

        // True if the picture starting at offset has a v1 (0x1101) or v2 (0x0011 0x02FF) version opcode after picSize
        // and picFrame. Leaves the stream position unspecified.
        private static bool HasVersionOpcode(BinaryReader b, long offset)
        {
            if (b.BaseStream.Length - offset < 14) return false;
            b.BaseStream.Position = offset + 10;
            ushort op = b.ReadU16BE();
            return op == 0x1101 || (op == 0x0011 && b.ReadU16BE() == 0x02FF);
        }

        private static bool IsVersion1(ReadOnlySpan<byte> picture) => picture[10] == 0x11 && picture[11] == 0x01;
        private static short I16(ReadOnlySpan<byte> d, int offset) => BinaryPrimitives.ReadInt16BigEndian(d.Slice(offset));
        private static ushort U16(ReadOnlySpan<byte> d, int offset) => BinaryPrimitives.ReadUInt16BigEndian(d.Slice(offset));
    }
}
