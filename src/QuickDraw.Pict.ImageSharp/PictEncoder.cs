using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

namespace QuickDraw.Pict.ImageSharp
{
    /// <summary>
    /// Encodes the root frame as a PICT v2 file (512-byte header + one 24-bit PackBits DirectBitsRect, 72 dpi).
    /// Alpha is not stored.
    /// </summary>
    public sealed class PictEncoder : ImageEncoder
    {
        /// <inheritdoc/>
        protected override void Encode<TPixel>(Image<TPixel> image, Stream stream, CancellationToken cancellationToken)
        {
            Configuration configuration = image.Configuration;
            var writer = new PictWriter(stream, image.Width, image.Height);
            var rgb = new Rgb24[image.Width];
            image.Frames.RootFrame.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PixelOperations<TPixel>.Instance.ToRgb24(configuration, accessor.GetRowSpan(y), rgb);
                    writer.WriteRow(MemoryMarshal.AsBytes<Rgb24>(rgb));
                }
            });
            writer.Finish();
        }
    }
}
