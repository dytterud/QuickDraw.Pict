using System;
using SkiaSharp;

namespace QuickDraw.Pict.SkiaSharp
{
    /// <summary>
    /// Rasterizes picture text with Skia into an aliased 1-bit mask (QuickDraw text is not anti-aliased), for text that
    /// no bitmap font in <see cref="PictDecodeOptions.Fonts"/> covers. The typeface comes from the resolver, or an
    /// installed font resembling the classic Mac family; the result approximates the original text.
    /// </summary>
    public sealed class SkiaTextFallback : IPictTextFallback
    {
        private readonly Func<int, SKTypeface?>? typefaceResolver;

        /// <summary>Creates the fallback.</summary>
        /// <param name="typefaceResolver">Maps a QuickDraw font number to a typeface; null (or a null result) uses an
        /// installed font resembling the classic one.</param>
        public SkiaTextFallback(Func<int, SKTypeface?>? typefaceResolver = null) => this.typefaceResolver = typefaceResolver;

        /// <inheritdoc/>
        public PictTextMask? Render(string text, PictTextStyle style)
        {
            ArgumentNullException.ThrowIfNull(text);
            var typeface = Resolve(style.FontId, style.Face);
            if (typeface == null) return null;
            using var font = new SKFont(typeface, style.Size <= 0 ? 12 : style.Size) { Edging = SKFontEdging.Alias, Subpixel = false };
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false };

            float advance = font.MeasureText(text, paint);
            var metrics = font.Metrics;
            int ascent = (int)Math.Ceiling(-metrics.Ascent);
            int descent = (int)Math.Ceiling(metrics.Descent);
            int pad = (int)Math.Ceiling(font.Size / 2);               // room for overhangs (italic, bold)
            int width = (int)Math.Ceiling(advance) + 2 * pad, height = ascent + descent + 2;
            if (width <= 0 || height <= 0) return null;

            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawText(text, pad, ascent, SKTextAlign.Left, font, paint);
            }
            var pixels = bitmap.GetPixelSpan();
            var bits = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    bits[y * width + x] = pixels[y * bitmap.RowBytes + x] >= 128 ? (byte)1 : (byte)0;
            return new PictTextMask(width, height, pad, ascent, bits, advance);
        }

        private SKTypeface? Resolve(int fontId, int face)
        {
            if (typefaceResolver?.Invoke(fontId) is { } resolved) return resolved;
            bool bold = (face & 0x01) != 0, italic = (face & 0x02) != 0;
            var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            foreach (var name in MacFontNames(fontId))
            {
                var typeface = SKFontManager.Default.MatchFamily(name, style);
                if (typeface != null) return typeface;
            }
            return SKTypeface.Default;
        }

        private static string[] MacFontNames(int id) => id switch
        {
            2 => new[] { "Times New Roman", "Times" },          // New York
            4 => new[] { "Courier New", "Monaco" },             // Monaco
            22 => new[] { "Courier New", "Courier" },           // Courier
            20 => new[] { "Times New Roman", "Times" },         // Times
            21 => new[] { "Arial", "Helvetica" },               // Helvetica
            _ => new[] { "Arial", "Helvetica", "Geneva" },      // system / Geneva / unknown
        };
    }
}
