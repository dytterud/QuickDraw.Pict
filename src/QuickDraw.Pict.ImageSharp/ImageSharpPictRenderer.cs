using System;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QuickDraw.Pict.ImageSharp
{
    // Rasterizes a picture's vector/text opcodes with ImageSharp.Drawing, anti-aliasing off (QuickDraw is aliased /
    // pixel-exact). Draws straight into the core's RGBA canvas by wrapping its buffer as an Image<Rgba32>.
    internal sealed class ImageSharpPictRenderer : IPictRenderer, IDisposable
    {
        private static readonly DrawingOptions Aliased =
            new DrawingOptions { GraphicsOptions = new GraphicsOptions { Antialias = false } };

        private readonly Image<Rgba32> canvas;
        private readonly Func<int, FontFamily?>? fontResolver;

        public ImageSharpPictRenderer(Configuration configuration, PictBitmap bitmap, Func<int, FontFamily?>? fontResolver)
        {
            canvas = Image.WrapMemory<Rgba32>(configuration, bitmap.Pixels.AsMemory(), bitmap.Width, bitmap.Height);
            this.fontResolver = fontResolver;
        }

        public void Dispose() => canvas.Dispose();

        public void Fill(in PictShape shape, PictColor color, PictRectangleF? clip)
        {
            var path = ToPath(shape);
            Rgba32 c = ToRgba(color);
            Apply(clip, ctx => ctx.Fill(Aliased, c, path));
        }

        public void Frame(in PictShape shape, PictColor color, int penWidth, PictRectangleF? clip)
        {
            var path = ToPath(shape);
            Rgba32 c = ToRgba(color);
            Apply(clip, ctx => ctx.Draw(Aliased, Pens.Solid(c, penWidth), path));
        }

        // QuickDraw invert = XOR the shape's pixels. ImageSharp has no XOR brush and IPath has no
        // point-in-path test here, so invert the path's bounding box (exact for rect invert, which is
        // the common case; approximate for other shapes). Best-effort.
        public void Invert(in PictShape shape)
        {
            var b = ToPath(shape).Bounds;
            int x0 = Math.Max(0, (int)Math.Floor(b.Left)), x1 = Math.Min(canvas.Width, (int)Math.Ceiling(b.Right));
            int y0 = Math.Max(0, (int)Math.Floor(b.Top)), y1 = Math.Min(canvas.Height, (int)Math.Ceiling(b.Bottom));
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    var px = canvas[x, y];
                    canvas[x, y] = new Rgba32((byte)~px.R, (byte)~px.G, (byte)~px.B, px.A);
                }
        }

        public void DrawPolyline(ReadOnlySpan<PictPoint> points, PictColor color, int penWidth, PictRectangleF? clip)
        {
            var pts = ToPoints(points);
            Rgba32 c = ToRgba(color);
            Apply(clip, ctx => ctx.DrawLine(Aliased, Pens.Solid(c, penWidth), pts));
        }

        // Best-effort: classic Mac bitmap fonts are unavailable, so use the resolver's family or a system font
        // of the requested family/style/size.
        public float DrawText(string text, PictPoint baselineOrigin, PictTextStyle style, PictColor color, PictRectangleF? clip)
        {
            var family = ResolveFontFamily(style.FontId);
            if (family == null) return 0;            // no usable font; skip text
            var font = family.Value.CreateFont(style.Size <= 0 ? 12 : style.Size, FaceToStyle(style.Face));

            float ascent = font.Size * 0.8f;       // approximate ascent for baseline placement
            var origin = new PointF(baselineOrigin.X, baselineOrigin.Y - ascent);
            var rto = new RichTextOptions(font) { Origin = origin };
            Rgba32 c = ToRgba(color);
            Apply(clip, ctx => ctx.DrawText(Aliased, rto, text, new SolidBrush(c), null));

            return TextMeasurer.MeasureAdvance(text, rto).Width;
        }

        private void Apply(PictRectangleF? clip, Action<IImageProcessingContext> op)
        {
            canvas.Mutate(ctx =>
            {
                if (clip is { } c)
                    ctx.Clip(new RectangularPolygon(c.X, c.Y, c.Width, c.Height), op);
                else
                    op(ctx);
            });
        }

        private static Rgba32 ToRgba(PictColor c) => new Rgba32(c.R, c.G, c.B, c.A);

        private static PointF[] ToPoints(ReadOnlySpan<PictPoint> points)
        {
            var pts = new PointF[points.Length];
            for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(points[i].X, points[i].Y);
            return pts;
        }

        private static IPath ToPath(in PictShape shape)
        {
            var r = shape.Bounds;
            switch (shape.Kind)
            {
                case PictShapeKind.Rectangle:
                    return new RectangularPolygon(r.X, r.Y, r.Width, r.Height);
                case PictShapeKind.Oval:
                    return new EllipsePolygon(r.X + r.Width / 2f, r.Y + r.Height / 2f, r.Width, r.Height);
                case PictShapeKind.RoundRectangle:
                    return RoundRectPath(r, shape.CornerRadiusX, shape.CornerRadiusY);
                default:
                    return new Polygon(new LinearLineSegment(ToPoints(shape.Points)));
            }
        }

        private static IPath RoundRectPath(PictRectangleF r, float rx, float ry)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            var pb = new PathBuilder();
            pb.AddLine(x + rx, y, x + w - rx, y);
            pb.AddArc(x + w - 2 * rx, y, 2 * rx, 2 * ry, 0, 270, 90);
            pb.AddLine(x + w, y + ry, x + w, y + h - ry);
            pb.AddArc(x + w - 2 * rx, y + h - 2 * ry, 2 * rx, 2 * ry, 0, 0, 90);
            pb.AddLine(x + w - rx, y + h, x + rx, y + h);
            pb.AddArc(x, y + h - 2 * ry, 2 * rx, 2 * ry, 0, 90, 90);
            pb.AddLine(x, y + h - ry, x, y + ry);
            pb.AddArc(x, y, 2 * rx, 2 * ry, 0, 180, 90);
            pb.CloseFigure();
            return pb.Build();
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
