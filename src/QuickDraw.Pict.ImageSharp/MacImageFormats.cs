using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;

namespace QuickDraw.Pict.ImageSharp
{
    /// <summary>The QuickTime image file format (<c>QTIF</c>).</summary>
    public sealed class QuickTimeImageFormat : IImageFormat
    {
        private static readonly string[] Mimes = { "image/x-quicktime", "image/qtif" };
        private static readonly string[] Extensions = { "qtif", "qti", "qif" };

        private QuickTimeImageFormat()
        {
        }

        /// <summary>The shared format instance.</summary>
        public static QuickTimeImageFormat Instance { get; } = new QuickTimeImageFormat();

        /// <inheritdoc/>
        public string Name => "QTIF";

        /// <inheritdoc/>
        public string DefaultMimeType => "image/x-quicktime";

        /// <inheritdoc/>
        public IEnumerable<string> MimeTypes => Mimes;

        /// <inheritdoc/>
        public IEnumerable<string> FileExtensions => Extensions;
    }

    /// <summary>The MacPaint document format (<c>PNTG</c>).</summary>
    public sealed class MacPaintFormat : IImageFormat
    {
        private static readonly string[] Mimes = { "image/x-macpaint" };
        private static readonly string[] Extensions = { "pntg", "pnt", "mac" };

        private MacPaintFormat()
        {
        }

        /// <summary>The shared format instance.</summary>
        public static MacPaintFormat Instance { get; } = new MacPaintFormat();

        /// <inheritdoc/>
        public string Name => "MacPaint";

        /// <inheritdoc/>
        public string DefaultMimeType => "image/x-macpaint";

        /// <inheritdoc/>
        public IEnumerable<string> MimeTypes => Mimes;

        /// <inheritdoc/>
        public IEnumerable<string> FileExtensions => Extensions;
    }

    internal sealed class QuickTimeImageFormatDetector : IImageFormatDetector
    {
        public int HeaderSize => QuickTimeImageFile.SignatureLength;

        public bool TryDetectFormat(ReadOnlySpan<byte> header, [NotNullWhen(true)] out IImageFormat? format)
        {
            bool match = QuickTimeImageFile.IsQuickTimeImageFile(header);
            format = match ? QuickTimeImageFormat.Instance : null;
            return match;
        }
    }

    // Needs the header plus the first packed row (at most 73 bytes).
    internal sealed class MacPaintFormatDetector : IImageFormatDetector
    {
        public int HeaderSize => MacPaintFile.HeaderSize + 80;

        public bool TryDetectFormat(ReadOnlySpan<byte> header, [NotNullWhen(true)] out IImageFormat? format)
        {
            bool match = MacPaintFile.IsMacPaintFile(header);
            format = match ? MacPaintFormat.Instance : null;
            return match;
        }
    }

    /// <summary>
    /// Decodes QuickTime image files (<c>QTIF</c>) with QuickDraw.Pict's built-in QuickTime codecs, and embedded
    /// JPEG, PNG, GIF, TIFF or BMP data with ImageSharp's own decoders. Resolution and the ICC profile become metadata.
    /// </summary>
    public sealed class QuickTimeImageDecoder : ImageDecoder
    {
        private QuickTimeImageDecoder()
        {
        }

        /// <summary>The shared decoder instance.</summary>
        public static QuickTimeImageDecoder Instance { get; } = new QuickTimeImageDecoder();

        /// <inheritdoc/>
        protected override ImageInfo Identify(DecoderOptions options, Stream stream, CancellationToken cancellationToken)
        {
            var data = MacImages.ReadAll(stream);
            var description = MacImages.Guard(() => QuickTimeImageFile.ReadDescription(data));
            var metadata = new ImageMetadata();
            if (!options.SkipMetadata) ApplyMetadata(metadata, description, data);
            return new ImageInfo(new PixelTypeInfo(32), new Size(Math.Max(1, description.Width), Math.Max(1, description.Height)), metadata);
        }

        /// <inheritdoc/>
        protected override Image<TPixel> Decode<TPixel>(DecoderOptions options, Stream stream, CancellationToken cancellationToken)
        {
            var data = MacImages.ReadAll(stream);
            var bitmap = MacImages.Guard(() => QuickTimeImageFile.Decode(data, new ImageSharpImageCodec(options.Configuration)));
            var image = MacImages.ToImage<TPixel>(options, bitmap);
            if (!options.SkipMetadata) ApplyMetadata(image.Metadata, QuickTimeImageFile.ReadDescription(data), data);
            ScaleToTargetSize(options, image);
            return image;
        }

        /// <inheritdoc/>
        protected override Image Decode(DecoderOptions options, Stream stream, CancellationToken cancellationToken) =>
            Decode<Rgba32>(options, stream, cancellationToken);

        private static void ApplyMetadata(ImageMetadata metadata, PictImageDescription description, byte[] data)
        {
            metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;
            metadata.HorizontalResolution = description.HorizontalResolution > 0 ? description.HorizontalResolution : 72;
            metadata.VerticalResolution = description.VerticalResolution > 0 ? description.VerticalResolution : 72;
            if (QuickTimeImageFile.ReadIccProfile(data) is { Length: > 0 } icc)
                metadata.IccProfile = new IccProfile(icc);
        }
    }

    /// <summary>Decodes MacPaint documents (<c>PNTG</c>): 576 × 720, black on white, 72 dpi.</summary>
    public sealed class MacPaintDecoder : ImageDecoder
    {
        private MacPaintDecoder()
        {
        }

        /// <summary>The shared decoder instance.</summary>
        public static MacPaintDecoder Instance { get; } = new MacPaintDecoder();

        /// <inheritdoc/>
        protected override ImageInfo Identify(DecoderOptions options, Stream stream, CancellationToken cancellationToken)
        {
            var metadata = new ImageMetadata();
            if (!options.SkipMetadata) ApplyMetadata(metadata);
            return new ImageInfo(new PixelTypeInfo(1), new Size(MacPaintFile.Width, MacPaintFile.Height), metadata);
        }

        /// <inheritdoc/>
        protected override Image<TPixel> Decode<TPixel>(DecoderOptions options, Stream stream, CancellationToken cancellationToken)
        {
            var data = MacImages.ReadAll(stream);
            var image = MacImages.ToImage<TPixel>(options, MacImages.Guard(() => MacPaintFile.Decode(data)));
            if (!options.SkipMetadata) ApplyMetadata(image.Metadata);
            ScaleToTargetSize(options, image);
            return image;
        }

        /// <inheritdoc/>
        protected override Image Decode(DecoderOptions options, Stream stream, CancellationToken cancellationToken) =>
            Decode<Rgba32>(options, stream, cancellationToken);

        private static void ApplyMetadata(ImageMetadata metadata)
        {
            metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;
            metadata.HorizontalResolution = 72;
            metadata.VerticalResolution = 72;
        }
    }

    internal static class MacImages
    {
        public static byte[] ReadAll(Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        public static Image<TPixel> ToImage<TPixel>(DecoderOptions options, PictBitmap bitmap)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            var rgba = Image.LoadPixelData<Rgba32>(options.Configuration, bitmap.Pixels, bitmap.Width, bitmap.Height);
            if (rgba is Image<TPixel> same) return same;
            using (rgba) return rgba.CloneAs<TPixel>(options.Configuration);
        }

        // ImageSharp reports unsupported or corrupt input as InvalidImageContentException.
        public static T Guard<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is NotSupportedException or EndOfStreamException or ArgumentException)
            {
                throw new InvalidImageContentException(ex.Message, ex);
            }
        }
    }
}
