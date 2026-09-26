using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;

namespace QuickDraw.Pict.ImageSharp
{
    /// <summary><c>SaveAsPict</c> extensions, mirroring ImageSharp's built-in <c>SaveAsPng</c> etc.</summary>
    public static class PictImageExtensions
    {
        /// <summary>Saves the image as a PICT file at <paramref name="path"/>.</summary>
        public static void SaveAsPict(this Image image, string path, PictEncoder? encoder = null) =>
            image.Save(path, encoder ?? new PictEncoder());

        /// <summary>Writes the image as PICT to <paramref name="stream"/>.</summary>
        public static void SaveAsPict(this Image image, Stream stream, PictEncoder? encoder = null) =>
            image.Save(stream, encoder ?? new PictEncoder());

        /// <summary>Saves the image as a PICT file at <paramref name="path"/>.</summary>
        public static Task SaveAsPictAsync(this Image image, string path, PictEncoder? encoder = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(path, encoder ?? new PictEncoder(), cancellationToken);

        /// <summary>Writes the image as PICT to <paramref name="stream"/>.</summary>
        public static Task SaveAsPictAsync(this Image image, Stream stream, PictEncoder? encoder = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(stream, encoder ?? new PictEncoder(), cancellationToken);
    }
}
