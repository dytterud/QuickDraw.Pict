using System;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace QuickDraw.Pict.SkiaSharp
{
    /// <summary>Options for decoding with SkiaSharp.</summary>
    public sealed class PictSkiaOptions
    {
        /// <summary>Classic Mac bitmap fonts (FOND/NFNT/FONT resources you supply) to draw text with exactly as QuickDraw does.</summary>
        public PictFontLibrary? BitmapFonts { get; init; }

        /// <summary>
        /// Maps a QuickDraw font number (the picture's TxFont) to the typeface used for text that no bitmap font covers.
        /// Null uses an installed font resembling the classic one.
        /// </summary>
        public Func<int, SKTypeface?>? TypefaceResolver { get; init; }

        /// <summary>The size to draw the picture at; see <see cref="PictDecodeOptions.Resolution"/>.</summary>
        public PictResolution Resolution { get; init; } = PictResolution.Native;

        /// <summary>Keep the alpha channel of 32-bit pixel maps that have one; see <see cref="PictDecodeOptions.PreserveAlpha"/>.</summary>
        public bool PreserveAlpha { get; init; }

        /// <summary>Which Macintosh QuickDraw to reproduce; see <see cref="PictDecodeOptions.QuickDraw"/>.</summary>
        public PictQuickDraw QuickDraw { get; init; } = PictQuickDraw.MacOS9;

        /// <summary>The depth of the screen to draw on (1, 2, 4, 8, 16 or 32); see <see cref="PictDecodeOptions.ScreenDepth"/>.</summary>
        public int ScreenDepth { get; init; } = 32;
    }

    /// <summary>
    /// Decodes Apple QuickDraw pictures, QuickTime image files and MacPaint documents to SkiaSharp bitmaps and images,
    /// and encodes SkiaSharp bitmaps as pictures.
    /// </summary>
    public static class PictSkia
    {
        /// <summary>Decodes a picture (a <c>.pict</c> file or bare PICT data) to an unpremultiplied RGBA bitmap.</summary>
        public static SKBitmap Decode(byte[] data, PictSkiaOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(data);
            return ToSKBitmap(PictReader.Decode(data, CoreOptions(options)));
        }

        /// <summary>Decodes a picture from a stream to an unpremultiplied RGBA bitmap.</summary>
        public static SKBitmap Decode(Stream stream, PictSkiaOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            return ToSKBitmap(PictReader.Decode(stream, CoreOptions(options)));
        }

        /// <summary>Decodes a picture to an immutable <see cref="SKImage"/>.</summary>
        public static SKImage DecodeImage(byte[] data, PictSkiaOptions? options = null)
        {
            using var bitmap = Decode(data, options);
            return SKImage.FromBitmap(bitmap);
        }

        /// <summary>
        /// Decodes a PICT, a QuickTime image file (<c>QTIF</c>) or a MacPaint document, whichever the data is; null when
        /// it is none of them.
        /// </summary>
        public static SKBitmap? DecodeAny(byte[] data, PictSkiaOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (QuickTimeImageFile.IsQuickTimeImageFile(data))
                return ToSKBitmap(QuickTimeImageFile.Decode(data, new SkiaImageCodec()));
            if (PictHeader.IsPicture(data) || PictHeader.IsPictFile(data)) return Decode(data, options);
            if (MacPaintFile.IsMacPaintFile(data)) return ToSKBitmap(MacPaintFile.Decode(data));
            return null;
        }

        /// <summary>Encodes a bitmap as a picture; see <see cref="PictWriter.Write"/> for the formats.</summary>
        public static void Encode(SKBitmap bitmap, Stream stream, PictWriteOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            ArgumentNullException.ThrowIfNull(stream);
            PictWriter.Write(stream, ToPictBitmap(bitmap), options);
        }

        /// <summary>Encodes pixels as a picture.</summary>
        public static void Encode(SKPixmap pixmap, Stream stream, PictWriteOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(pixmap);
            using var bitmap = new SKBitmap();
            bitmap.InstallPixels(pixmap);
            Encode(bitmap, stream, options);
        }

        /// <summary>Encodes the bitmap as a picture to a stream.</summary>
        public static void SaveAsPict(this SKBitmap bitmap, Stream stream, PictWriteOptions? options = null) =>
            Encode(bitmap, stream, options);

        /// <summary>Encodes the bitmap as a <c>.pict</c> file.</summary>
        public static void SaveAsPict(this SKBitmap bitmap, string path, PictWriteOptions? options = null)
        {
            using var stream = File.Create(path);
            Encode(bitmap, stream, options);
        }

        /// <summary>Wraps decoded pixels in an unpremultiplied RGBA <see cref="SKBitmap"/> (the pixels are copied).</summary>
        public static SKBitmap ToSKBitmap(PictBitmap bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            var result = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            var destination = result.GetPixels();
            int rowBytes = bitmap.Width * 4;
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(bitmap.Pixels, y * rowBytes, destination + y * result.RowBytes, rowBytes);
            return result;
        }

        /// <summary>Converts a bitmap of any colour type to QuickDraw.Pict's unpremultiplied RGBA pixels.</summary>
        public static PictBitmap ToPictBitmap(SKBitmap bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var pixels = new byte[info.Width * info.Height * 4];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                using var source = bitmap.PeekPixels();
                if (source == null || !source.ReadPixels(info, handle.AddrOfPinnedObject(), info.Width * 4, 0, 0))
                    throw new NotSupportedException($"Cannot read pixels of colour type {bitmap.ColorType}.");
            }
            finally
            {
                handle.Free();
            }
            return new PictBitmap(info.Width, info.Height, pixels);
        }

        private static PictDecodeOptions CoreOptions(PictSkiaOptions? options)
        {
            options ??= new PictSkiaOptions();
            return new PictDecodeOptions
            {
                Fonts = options.BitmapFonts,
                TextFallback = new SkiaTextFallback(options.TypefaceResolver),
                ImageCodec = new SkiaImageCodec(),
                Resolution = options.Resolution,
                PreserveAlpha = options.PreserveAlpha,
                QuickDraw = options.QuickDraw,
                ScreenDepth = options.ScreenDepth,
            };
        }
    }
}
