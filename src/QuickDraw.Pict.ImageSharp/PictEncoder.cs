using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace QuickDraw.Pict.ImageSharp
{
    /// <summary>
    /// Encodes the root frame as a PICT version 2 file (or bare picture): 24-bit by default, or indexed (1, 2, 4, 8
    /// bits, colors reduced by <see cref="Quantizer"/>), 16-bit, or 32-bit with alpha. The image's resolution and ICC
    /// profile are stored.
    /// </summary>
    public sealed class PictEncoder : ImageEncoder
    {
        /// <summary>Bits per pixel: 1, 2, 4, 8 (indexed), 16, 24 (default) or 32 (with alpha).</summary>
        public int BitsPerPixel { get; init; } = 24;

        /// <summary>Reduces colors for indexed output. Defaults to Wu quantization without dithering.</summary>
        public IQuantizer? Quantizer { get; init; }

        /// <summary>Whether to write the 512-byte <c>.pict</c> file header (true) or a bare picture (false).</summary>
        public bool FileHeader { get; init; } = true;

        /// <inheritdoc/>
        protected override void Encode<TPixel>(Image<TPixel> image, Stream stream, CancellationToken cancellationToken)
        {
            var format = BitsPerPixel switch
            {
                1 => PictPixelFormat.Indexed1,
                2 => PictPixelFormat.Indexed2,
                4 => PictPixelFormat.Indexed4,
                8 => PictPixelFormat.Indexed8,
                16 => PictPixelFormat.Rgb555,
                24 => PictPixelFormat.Rgb888,
                32 => PictPixelFormat.Argb8888,
                _ => throw new NotSupportedException($"PICT cannot store {BitsPerPixel} bits per pixel."),
            };
            var (hRes, vRes) = Dpi(image.Metadata);
            var icc = image.Metadata.IccProfile?.ToByteArray();
            Configuration configuration = image.Configuration;
            ImageFrame<TPixel> frame = image.Frames.RootFrame;

            if (format > PictPixelFormat.Indexed8)
            {
                var writer = new PictWriter(stream, image.Width, image.Height, new PictWriteOptions
                {
                    Format = format, HorizontalResolution = hRes, VerticalResolution = vRes, IccProfile = icc, FileHeader = FileHeader,
                });
                var row = new Rgba32[image.Width];
                frame.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        PixelOperations<TPixel>.Instance.ToRgba32(configuration, accessor.GetRowSpan(y), row);
                        writer.WriteRow(MemoryMarshal.AsBytes(row.AsSpan()));
                    }
                });
                writer.Finish();
                return;
            }

            int colors = 1 << BitsPerPixel;
            var quantizer = Quantizer ?? new WuQuantizer(new QuantizerOptions { Dither = null, MaxColors = colors });
            var quantizerOptions = new QuantizerOptions
            {
                Dither = quantizer.Options.Dither,
                DitherScale = quantizer.Options.DitherScale,
                MaxColors = Math.Min(colors, quantizer.Options.MaxColors),
            };
            using IQuantizer<TPixel> frameQuantizer = quantizer.CreatePixelSpecificQuantizer<TPixel>(configuration, quantizerOptions);
            using IndexedImageFrame<TPixel> indexed = frameQuantizer.BuildPaletteAndQuantizeFrame(frame, frame.Bounds());
            var source = indexed.Palette.Span;
            var rgba = new Rgba32[source.Length];
            PixelOperations<TPixel>.Instance.ToRgba32(configuration, source, rgba);
            var palette = new PictColor[rgba.Length];
            for (int i = 0; i < rgba.Length; i++) palette[i] = new PictColor(rgba[i].R, rgba[i].G, rgba[i].B);
            var (ordered, remap) = WhiteBlackOrder(palette, format);

            var indexedWriter = new PictWriter(stream, image.Width, image.Height, new PictWriteOptions
            {
                Format = format, Palette = ordered, HorizontalResolution = hRes, VerticalResolution = vRes, IccProfile = icc,
                FileHeader = FileHeader,
            });
            var line = new byte[image.Width];
            for (int y = 0; y < image.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var src = indexed.DangerousGetRowSpan(y);
                for (int x = 0; x < line.Length; x++) line[x] = remap[src[x]];
                indexedWriter.WriteRow(line);
            }
            indexedWriter.Finish();
        }

        // A black-and-white 1-bit palette is reordered white, black so it is stored as a classic BitMap.
        private static (PictColor[] palette, byte[] remap) WhiteBlackOrder(PictColor[] palette, PictPixelFormat format)
        {
            var remap = new byte[Math.Max(palette.Length, 1)];
            for (int i = 0; i < remap.Length; i++) remap[i] = (byte)i;
            var white = new PictColor(255, 255, 255);
            var black = new PictColor(0, 0, 0);
            if (format == PictPixelFormat.Indexed1 && palette.Length == 2 && palette[0] == black && palette[1] == white)
                return (new[] { white, black }, new byte[] { 1, 0 });
            return (palette, remap);
        }

        private static (double h, double v) Dpi(ImageMetadata m)
        {
            double h = m.HorizontalResolution, v = m.VerticalResolution;
            switch (m.ResolutionUnits)
            {
                case PixelResolutionUnit.PixelsPerCentimeter: h *= 2.54; v *= 2.54; break;
                case PixelResolutionUnit.PixelsPerMeter: h *= 0.0254; v *= 0.0254; break;
                case PixelResolutionUnit.AspectRatio: h = v = 72; break;
            }
            return (h > 0 ? h : 72, v > 0 ? v : 72);
        }
    }
}
