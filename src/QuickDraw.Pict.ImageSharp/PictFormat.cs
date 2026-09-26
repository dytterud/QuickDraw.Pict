using System.Collections.Generic;
using SixLabors.ImageSharp.Formats;

namespace QuickDraw.Pict.ImageSharp
{
    /// <summary>The Apple QuickDraw PICT image format.</summary>
    public sealed class PictFormat : IImageFormat
    {
        private static readonly string[] Mimes = { "image/x-pict", "image/pict" };
        private static readonly string[] Extensions = { "pict", "pct", "pic" };

        private PictFormat()
        {
        }

        /// <summary>The shared format instance.</summary>
        public static PictFormat Instance { get; } = new PictFormat();

        /// <inheritdoc/>
        public string Name => "PICT";

        /// <inheritdoc/>
        public string DefaultMimeType => "image/x-pict";

        /// <inheritdoc/>
        public IEnumerable<string> MimeTypes => Mimes;

        /// <inheritdoc/>
        public IEnumerable<string> FileExtensions => Extensions;
    }
}
