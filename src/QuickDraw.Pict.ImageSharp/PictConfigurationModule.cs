using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace QuickDraw.Pict.ImageSharp
{
    /// <summary>
    /// Registers the PICT format, detector, decoder and encoder with an ImageSharp <see cref="Configuration"/>:
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
        }
    }
}
