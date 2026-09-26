using System;

namespace QuickDraw.Pict
{
    /// <summary>An 8-bit RGBA color.</summary>
    public readonly record struct PictColor(byte R, byte G, byte B, byte A = 255);

    /// <summary>A point in canvas pixel space.</summary>
    public readonly record struct PictPoint(float X, float Y);

    /// <summary>A rectangle in canvas pixel space.</summary>
    public readonly record struct PictRectangleF(float X, float Y, float Width, float Height);

    /// <summary>QuickDraw text state for a text opcode.</summary>
    /// <param name="FontId">QuickDraw font number (TxFont), e.g. 0 system, 2 New York, 3 Geneva, 4 Monaco, 20 Times, 21 Helvetica, 22 Courier.</param>
    /// <param name="Face">QuickDraw style bits (TxFace): 1 bold, 2 italic, 4 underline, 8 outline, 16 shadow, 32 condense, 64 extend.</param>
    /// <param name="Size">Point size (TxSize); 0 means the font's default size.</param>
    public readonly record struct PictTextStyle(int FontId, int Face, int Size);

    /// <summary>The geometry kinds a picture hands to an <see cref="IPictRenderer"/>.</summary>
    public enum PictShapeKind
    {
        /// <summary>An axis-aligned rectangle (<see cref="PictShape.Bounds"/>).</summary>
        Rectangle,

        /// <summary>The ellipse inscribed in <see cref="PictShape.Bounds"/>.</summary>
        Oval,

        /// <summary>A rectangle with elliptical corners of radii <see cref="PictShape.CornerRadiusX"/> / <see cref="PictShape.CornerRadiusY"/>.</summary>
        RoundRectangle,

        /// <summary>A closed polygon through <see cref="PictShape.Points"/> (polygons, arcs and wedges).</summary>
        Polygon,
    }

    /// <summary>A shape in canvas pixel space, ready to fill, frame or invert.</summary>
    public readonly struct PictShape
    {
        private PictShape(PictShapeKind kind, PictRectangleF bounds, float rx, float ry, PictPoint[]? points)
        {
            Kind = kind;
            Bounds = bounds;
            CornerRadiusX = rx;
            CornerRadiusY = ry;
            this.points = points;
        }

        private readonly PictPoint[]? points;

        /// <summary>The shape kind.</summary>
        public PictShapeKind Kind { get; }

        /// <summary>The bounding rectangle (unused for <see cref="PictShapeKind.Polygon"/>).</summary>
        public PictRectangleF Bounds { get; }

        /// <summary>Horizontal corner radius of a <see cref="PictShapeKind.RoundRectangle"/>.</summary>
        public float CornerRadiusX { get; }

        /// <summary>Vertical corner radius of a <see cref="PictShapeKind.RoundRectangle"/>.</summary>
        public float CornerRadiusY { get; }

        /// <summary>The vertices of a <see cref="PictShapeKind.Polygon"/>.</summary>
        public ReadOnlySpan<PictPoint> Points => points;

        /// <summary>Creates a rectangle.</summary>
        public static PictShape Rectangle(PictRectangleF bounds) => new(PictShapeKind.Rectangle, bounds, 0, 0, null);

        /// <summary>Creates an oval inscribed in <paramref name="bounds"/>.</summary>
        public static PictShape Oval(PictRectangleF bounds) => new(PictShapeKind.Oval, bounds, 0, 0, null);

        /// <summary>Creates a rounded rectangle.</summary>
        public static PictShape RoundRectangle(PictRectangleF bounds, float radiusX, float radiusY) =>
            new(PictShapeKind.RoundRectangle, bounds, radiusX, radiusY, null);

        /// <summary>Creates a closed polygon.</summary>
        public static PictShape Polygon(PictPoint[] points) => new(PictShapeKind.Polygon, default, 0, 0, points);
    }
}
