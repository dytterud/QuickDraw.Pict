using System.IO;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;

namespace QuickDraw.Pict.ImageSharp
{
    /// <summary>
    /// Decodes QuickDraw PICT (v1/v2) pictures, bare or with a <c>.pict</c> file header. Bitmap opcodes are decoded
    /// exactly and shapes are drawn by QuickDraw.Pict's QuickDraw engine; text is rasterized (aliased, like QuickDraw)
    /// with SixLabors.Fonts, using <see cref="PictDecoderOptions.FontResolver"/> or a system font.
    /// </summary>
    public sealed class PictDecoder : SpecializedImageDecoder<PictDecoderOptions>
    {
        private PictDecoder()
        {
        }

        /// <summary>The shared decoder instance.</summary>
        public static PictDecoder Instance { get; } = new PictDecoder();

        /// <inheritdoc/>
        protected override ImageInfo Identify(DecoderOptions options, Stream stream, CancellationToken cancellationToken)
        {
            PictInfo info = Guard(() => PictHeader.ReadInfo(stream));
            var size = new Size(System.Math.Max(1, info.Bounds.Width), System.Math.Max(1, info.Bounds.Height));
            var metadata = new ImageMetadata();
            if (!options.SkipMetadata)
                ApplyMetadata(metadata, info, PictResolution.Native);
            return new ImageInfo(new PixelTypeInfo(32), size, metadata);
        }

        /// <inheritdoc/>
        protected override Image<TPixel> Decode<TPixel>(PictDecoderOptions options, Stream stream, CancellationToken cancellationToken)
        {
            DecoderOptions general = options.GeneralOptions;
            Configuration configuration = general.Configuration;
            var pictOptions = new PictDecodeOptions
            {
                TextFallback = new ImageSharpTextFallback(configuration, options.FontResolver),
                Fonts = options.BitmapFonts,
                ImageCodec = new ImageSharpImageCodec(configuration),
                Resolution = options.Resolution,
                PreserveAlpha = options.PreserveAlpha,
                QuickDraw = options.QuickDraw,
                ScreenDepth = options.ScreenDepth,
            };
            PictBitmap bitmap = Guard(() => PictReader.Decode(stream, pictOptions, cancellationToken));

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

            if (!general.SkipMetadata && bitmap.Info != null)
                ApplyMetadata(image.Metadata, bitmap.Info, options.Resolution);
            ScaleToTargetSize(general, image);
            return image;
        }

        /// <inheritdoc/>
        protected override Image Decode(PictDecoderOptions options, Stream stream, CancellationToken cancellationToken) =>
            Decode<Rgba32>(options, stream, cancellationToken);

        /// <inheritdoc/>
        protected override PictDecoderOptions CreateDefaultSpecializedOptions(DecoderOptions options) =>
            new PictDecoderOptions { GeneralOptions = options };

        // Resolution from the picture header (72 dpi unless an extended version 2 header says otherwise, and always 72
        // when drawn at its picture frame) and the embedded ICC profile, if the picture carries one.
        private static void ApplyMetadata(ImageMetadata metadata, PictInfo info, PictResolution resolution)
        {
            bool frame = resolution == PictResolution.PictureFrame;
            metadata.ResolutionUnits = PixelResolutionUnit.PixelsPerInch;
            metadata.HorizontalResolution = frame ? 72 : info.HorizontalResolution;
            metadata.VerticalResolution = frame ? 72 : info.VerticalResolution;
            if (info.IccProfile is { Length: > 0 } icc)
                metadata.IccProfile = new IccProfile(icc);
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
