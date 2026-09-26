using System;
using System.Diagnostics.CodeAnalysis;
using SixLabors.ImageSharp.Formats;

namespace QuickDraw.Pict.ImageSharp
{
    // Detects PICT data. ImageSharp only consults a detector when the stream holds at least HeaderSize bytes and
    // passes it no more than the largest HeaderSize registered, so two instances are needed: one sized to reach a
    // .pict file's picture past its 512-byte header, and one small enough for short bare pictures (PICT resources).
    internal sealed class PictImageFormatDetector : IImageFormatDetector
    {
        public static readonly PictImageFormatDetector File = new(PictHeader.FileHeaderSize + PictHeader.SignatureLength, file: true);
        public static readonly PictImageFormatDetector Resource = new(PictHeader.SignatureLength, file: false);

        private readonly bool file;

        private PictImageFormatDetector(int headerSize, bool file)
        {
            HeaderSize = headerSize;
            this.file = file;
        }

        public int HeaderSize { get; }

        public bool TryDetectFormat(ReadOnlySpan<byte> header, [NotNullWhen(true)] out IImageFormat? format)
        {
            bool match = header.Length >= HeaderSize && (file ? PictHeader.IsPictFile(header) : PictHeader.IsPicture(header));
            format = match ? PictFormat.Instance : null;
            return match;
        }
    }
}
