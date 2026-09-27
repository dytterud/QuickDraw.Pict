using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace QuickDraw.Pict
{
    /// <summary>
    /// QuickTime image files (<c>QTIF</c>; <c>.qtif</c>, <c>.qti</c>, <c>.qif</c>): the standalone form of the images
    /// QuickTime embeds in pictures. The file is a sequence of atoms (<c>u32</c> size including the 8-byte header, a
    /// four-character type), among them <c>idsc</c> (the image description), <c>idat</c> (the compressed image) and
    /// optionally <c>iicc</c> (an ICC profile).
    /// </summary>
    /// <remarks>
    /// The image is decompressed by the codecs built into the core (<c>raw </c>, <c>rle </c>, <c>rpza</c>,
    /// <c>smc </c>, <c>cvid</c>, <c>8BPS</c>, <c>yuv2</c>, <c>YVU9</c>, <c>tga </c>, <c>PNTG</c>), or by the
    /// <see cref="IPictImageCodec"/> given (JPEG, PNG and other embedded file formats).
    /// </remarks>
    public static class QuickTimeImageFile
    {
        /// <summary>Bytes needed to recognize a QuickTime image file.</summary>
        public const int SignatureLength = 8;

        /// <summary>True if <paramref name="data"/> starts with a QuickTime image file's first atom.</summary>
        public static bool IsQuickTimeImageFile(ReadOnlySpan<byte> data)
        {
            if (data.Length < SignatureLength) return false;
            uint size = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (size != 1 && size < 8) return false;
            string type = Encoding.Latin1.GetString(data.Slice(4, 4));
            return type is "idsc" or "idat" or "iicc";
        }

        /// <summary>Reads the file's image description.</summary>
        /// <exception cref="NotSupportedException">The data has no readable image description.</exception>
        public static PictImageDescription ReadDescription(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            foreach (var (type, offset, _) in Atoms(data))
                if (type == "idsc" && ImageDescriptionReader.Read(data, offset, out _) is { } description)
                    return description;
            throw new NotSupportedException("Not a QuickTime image file: it has no image description ('idsc').");
        }

        /// <summary>The file's embedded ICC profile (<c>iicc</c> atom), or null.</summary>
        public static byte[]? ReadIccProfile(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            foreach (var (type, offset, length) in Atoms(data))
                if (type == "iicc") return data.AsSpan(offset, length).ToArray();
            return null;
        }

        /// <summary>Decodes the image.</summary>
        /// <param name="data">The file's bytes.</param>
        /// <param name="codec">Decoder for codecs the core lacks (e.g. JPEG); null for built-in codecs only.</param>
        /// <exception cref="NotSupportedException">The file has no image, or its codec is not supported.</exception>
        public static PictBitmap Decode(byte[] data, IPictImageCodec? codec = null)
        {
            var description = ReadDescription(data);
            byte[]? image = null;
            foreach (var (type, offset, length) in Atoms(data))
                if (type == "idat")
                {
                    image = data.AsSpan(offset, length).ToArray();
                    break;
                }
            if (image == null) throw new NotSupportedException("The QuickTime image file has no image data ('idat').");
            return QuickTimeCodecs.Decode(description, image) ?? codec?.Decode(description, image)
                ?? throw new NotSupportedException($"QuickTime codec '{description.CodecType}' is not supported.");
        }

        // Top-level atoms (type, content offset, content length). Size 0 runs to the end of the file, size 1 has a
        // 64-bit size after the type; an atom running past the end is cut there.
        private static IEnumerable<(string type, int offset, int length)> Atoms(byte[] data)
        {
            long p = 0;
            while (p + 8 <= data.Length)
            {
                long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)p));
                string type = Encoding.Latin1.GetString(data, (int)p + 4, 4);
                int header = 8;
                if (size == 1)
                {
                    if (p + 16 > data.Length) yield break;
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan((int)p + 8));
                    header = 16;
                }
                else if (size == 0) size = data.Length - p;
                if (size < header) yield break;
                long end = Math.Min(data.Length, p + size);
                yield return (type, (int)(p + header), (int)(end - p - header));
                p += size;
            }
        }
    }
}
