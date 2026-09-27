using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace QuickDraw.Pict.ImageSharp
{
    /// <summary>
    /// Registers the PICT format (detector, decoder and encoder) and the QuickTime image (QTIF) and MacPaint (PNTG)
    /// formats (detectors and decoders) with an ImageSharp <see cref="Configuration"/>:
    /// <c>Configuration.Default.Configure(new PictConfigurationModule());</c>
    /// </summary>
    public sealed class PictConfigurationModule : IImageFormatConfigurationModule
    {
        /// <inheritdoc/>
        public void Configure(Configuration configuration)
        {
            configuration.ImageFormatsManager.SetEncoder(PictFormat.Instance, new PictEncoder());
            configuration.ImageFormatsManager.SetDecoder(PictFormat.Instance, PictDecoder.Instance);
            configuration.ImageFormatsManager.AddImageFormatDetector(PictImageFormatDetector.File);
            configuration.ImageFormatsManager.AddImageFormatDetector(PictImageFormatDetector.Resource);
            configuration.ImageFormatsManager.SetDecoder(QuickTimeImageFormat.Instance, QuickTimeImageDecoder.Instance);
            configuration.ImageFormatsManager.AddImageFormatDetector(new QuickTimeImageFormatDetector());
            configuration.ImageFormatsManager.SetDecoder(MacPaintFormat.Instance, MacPaintDecoder.Instance);
            configuration.ImageFormatsManager.AddImageFormatDetector(new MacPaintFormatDetector());
        }
    }
}
