using System;
using System.Buffers.Binary;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

namespace QuickDraw.Pict.ImageSharp
{
    /// <summary>
    /// Decodes QuickTime-compressed picture images with ImageSharp's decoders: <c>jpeg</c>, <c>png </c>,
    /// <c>gif </c>, <c>tiff</c>, <c>webp</c> and <c>WRLE</c> (Windows BMP, stored without its file header).
    /// </summary>
    public sealed class ImageSharpImageCodec : IPictImageCodec
    {
        private readonly Configuration configuration;

        /// <summary>Creates the codec for a configuration (the default one when null).</summary>
        public ImageSharpImageCodec(Configuration? configuration = null) =>
            this.configuration = configuration ?? Configuration.Default;

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
                case "tiff":
                case "webp":
                    bytes = data;
                    break;
                case "WRLE":
                    bytes = WithBmpHeaders(description, data);
                    break;
                default:
                    return null;
            }
            try
            {
                using var image = Image.Load<Rgba32>(new DecoderOptions { Configuration = configuration }, new MemoryStream(bytes));
                var pixels = new byte[image.Width * image.Height * 4];
                image.CopyPixelDataTo(pixels);
                return new PictBitmap(image.Width, image.Height, pixels);
            }
            catch (Exception e) when (e is ImageFormatException or UnknownImageFormatException or InvalidImageContentException
                                          or NotSupportedException)
            {
                return null;
            }
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
