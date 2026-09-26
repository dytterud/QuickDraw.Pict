using System.IO;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;

namespace QuickDraw.Pict.ImageSharp
{
    /// <summary>
    /// Decodes QuickDraw PICT (v1/v2) pictures, bare or with a <c>.pict</c> file header. Bitmap opcodes are decoded
    /// exactly; vector and text opcodes are rasterized with ImageSharp.Drawing (aliased, like QuickDraw).
    /// </summary>
    public sealed class PictDecoder : SpecializedImageDecoder<PictDecoderOptions>
    {
        private const double QuickDrawDpi = 72;

        private PictDecoder()
        {
        }

        /// <summary>The shared decoder instance.</summary>
        public static PictDecoder Instance { get; } = new PictDecoder();

        /// <inheritdoc/>
        protected override ImageInfo Identify(DecoderOptions options, Stream stream, CancellationToken cancellationToken)
        {
            (int width, int height) = Guard(() => PictHeader.ReadFrameSize(stream));
            return new ImageInfo(new PixelTypeInfo(32), new Size(width, height), CreateMetadata(options));
        }

        /// <inheritdoc/>
        protected override Image<TPixel> Decode<TPixel>(PictDecoderOptions options, Stream stream, CancellationToken cancellationToken)
        {
            DecoderOptions general = options.GeneralOptions;
            Configuration configuration = general.Configuration;
            PictBitmap bitmap = Guard(() => PictReader.Decode(stream,
                canvas => new ImageSharpPictRenderer(configuration, canvas, options.FontResolver), cancellationToken));

            Image<Rgba32> rgba = Image.LoadPixelData<Rgba32>(configuration, bitmap.Pixels, bitmap.Width, bitmap.Height);
            Image<TPixel> image;
            if (rgba is Image<TPixel> same)
            {
                image = same;
            }
            else
            {
                using (rgba)
                    image = rgba.CloneAs<TPixel>(configuration);
            }

            if (!general.SkipMetadata)
                ApplyResolution(image.Metadata);
            ScaleToTargetSize(general, image);
            return image;
        }

        /// <inheritdoc/>
        protected override Image Decode(PictDecoderOptions options, Stream stream, CancellationToken cancellationToken) =>
            Decode<Rgba32>(options, stream, cancellationToken);

        /// <inheritdoc/>
        protected override PictDecoderOptions CreateDefaultSpecializedOptions(DecoderOptions options) =>
            new PictDecoderOptions { GeneralOptions = options };

        private static ImageMetadata CreateMetadata(DecoderOptions options)
        {
            var metadata = new ImageMetadata();
            if (!options.SkipMetadata)
                ApplyResolution(metadata);
            return metadata;
        }

        // QuickDraw coordinates are 72 dpi.
        private static void ApplyResolution(ImageMetadata metadata)
        {
            metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;
            metadata.HorizontalResolution = QuickDrawDpi;
            metadata.VerticalResolution = QuickDrawDpi;
        }

        // ImageSharp reports corrupt/truncated input as InvalidImageContentException.
        private static T Guard<T>(System.Func<T> read)
        {
            try
            {
                return read();
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidImageContentException("The PICT data ended unexpectedly.", ex);
            }
        }
    }
}
