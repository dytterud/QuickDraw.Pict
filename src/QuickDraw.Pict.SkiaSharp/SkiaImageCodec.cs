using System;
using System.Buffers.Binary;
using SkiaSharp;

namespace QuickDraw.Pict.SkiaSharp
{
    /// <summary>
    /// Decodes QuickTime-compressed picture images with Skia's codecs: <c>jpeg</c>, <c>png </c>, <c>gif </c>,
    /// <c>webp</c> and <c>WRLE</c> (Windows BMP, stored without its file header). Skia has no TIFF decoder, so
    /// <c>tiff</c> images show the picture's own fallback.
    /// </summary>
    public sealed class SkiaImageCodec : IPictImageCodec
    {
        /// <inheritdoc/>
        public PictBitmap? Decode(PictImageDescription description, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(description);
            ArgumentNullException.ThrowIfNull(data);
            byte[] bytes;
            switch (description.CodecType)
            {
                case "jpeg":
                case "png ":
                case "gif ":
                case "webp":
                    bytes = data;
                    break;
                case "WRLE":
                    bytes = WithBmpHeaders(description, data);
                    break;
                default:
                    return null;
            }
            using var bitmap = SKBitmap.Decode(bytes);
            return bitmap == null ? null : PictSkia.ToPictBitmap(bitmap);
        }

        // QuickTime stores BMP pixel data without the file header and with only an OS/2 core header's worth of
        // information in the image description: rebuild a BITMAPFILEHEADER + BITMAPCOREHEADER.
        private static byte[] WithBmpHeaders(PictImageDescription d, byte[] data)
        {
            const int fileHeader = 14, coreHeader = 12;
            var result = new byte[fileHeader + coreHeader + data.Length];
            result[0] = (byte)'B';
            result[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(2), result.Length);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(10), fileHeader + coreHeader);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(14), coreHeader);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(18), (short)d.Width);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(20), (short)d.Height);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(22), 1);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(24), (short)d.Depth);
            data.CopyTo(result, fileHeader + coreHeader);
            return result;
        }
    }
}
