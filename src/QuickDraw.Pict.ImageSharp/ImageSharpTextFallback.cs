using System;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QuickDraw.Pict.ImageSharp
{
    // Rasterizes picture text with SixLabors.Fonts into an aliased 1-bit mask (QuickDraw text is not anti-aliased).
    // Classic Mac bitmap fonts are unavailable here, so the family comes from the resolver or an installed system font
    // resembling the classic one; the result approximates the original text.
    internal sealed class ImageSharpTextFallback : IPictTextFallback
    {
        private static readonly DrawingOptions Aliased =
            new DrawingOptions { GraphicsOptions = new GraphicsOptions { Antialias = false } };

        private readonly Configuration configuration;
        private readonly Func<int, FontFamily?>? fontResolver;

        public ImageSharpTextFallback(Configuration configuration, Func<int, FontFamily?>? fontResolver)
        {
            this.configuration = configuration;
            this.fontResolver = fontResolver;
        }

        public PictTextMask? Render(string text, PictTextStyle style)
        {
            var family = ResolveFontFamily(style.FontId);
            if (family == null) return null;
            var font = family.Value.CreateFont(style.Size <= 0 ? 12 : style.Size, FaceToStyle(style.Face));

            int ascent = (int)Math.Round(font.Size * 0.8f);        // approximate ascent for baseline placement
            int pad = (int)Math.Ceiling(font.Size / 2);             // room for overhangs (italic, bold)
            var options = new RichTextOptions(font) { Origin = new PointF(pad, 0) };
            float advance = TextMeasurer.MeasureAdvance(text, options).Width;
            int width = (int)Math.Ceiling(advance) + 2 * pad;
            int height = (int)Math.Ceiling(font.Size * 1.6f);
            if (width <= 0 || height <= 0) return null;

            using var image = new Image<L8>(configuration, width, height);
            image.Mutate(ctx => ctx.DrawText(Aliased, options, text, Brushes.Solid(Color.White), null));
            var bits = new byte[width * height];
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                        bits[y * width + x] = row[x].PackedValue >= 128 ? (byte)1 : (byte)0;
                }
            });
            return new PictTextMask(width, height, pad, ascent, bits, advance);
        }

        private static FontStyle FaceToStyle(int face)
        {
            bool bold = (face & 0x01) != 0, italic = (face & 0x02) != 0;
            if (bold && italic) return FontStyle.BoldItalic;
            if (bold) return FontStyle.Bold;
            if (italic) return FontStyle.Italic;
            return FontStyle.Regular;
        }

        private FontFamily? ResolveFontFamily(int fontId)
        {
            if (fontResolver?.Invoke(fontId) is { } resolved) return resolved;
            foreach (var name in MacFontNames(fontId))
                if (SystemFonts.TryGet(name, out var fam))
                    return fam;
            foreach (var fam in SystemFonts.Families)   // any installed font as a last resort
                return fam;
            return null;
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
