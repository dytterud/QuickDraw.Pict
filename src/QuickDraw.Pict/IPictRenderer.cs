using System;

namespace QuickDraw.Pict
{
    /// <summary>
    /// Rasterizes the vector and text opcodes of a picture onto the <see cref="PictBitmap"/> being decoded.
    /// The core decodes bitmap opcodes itself and keeps all QuickDraw state (pen, colors, origin, clip, "same shape"
    /// memory); a renderer only draws already-resolved geometry in canvas pixel space. QuickDraw is aliased, so
    /// implementations should draw without anti-aliasing. If the renderer implements <see cref="IDisposable"/>,
    /// it is disposed when decoding ends.
    /// </summary>
    public interface IPictRenderer
    {
        /// <summary>Fills <paramref name="shape"/> (QuickDraw paint / fill / erase).</summary>
        /// <param name="shape">The shape to fill.</param>
        /// <param name="color">The pen (paint/fill) or background (erase) color.</param>
        /// <param name="clip">The clip rectangle, or null when the clip covers the whole canvas.</param>
        void Fill(in PictShape shape, PictColor color, PictRectangleF? clip);

        /// <summary>Strokes the outline of <paramref name="shape"/> (QuickDraw frame).</summary>
        /// <param name="shape">The shape to outline.</param>
        /// <param name="color">The pen color.</param>
        /// <param name="penWidth">The pen width in pixels (at least 1).</param>
        /// <param name="clip">The clip rectangle, or null when the clip covers the whole canvas.</param>
        void Frame(in PictShape shape, PictColor color, int penWidth, PictRectangleF? clip);

        /// <summary>Inverts the pixels covered by <paramref name="shape"/> (QuickDraw invert).</summary>
        void Invert(in PictShape shape);

        /// <summary>Strokes an open polyline (lines and framed polygons).</summary>
        /// <param name="points">The vertices, in order.</param>
        /// <param name="color">The pen color.</param>
        /// <param name="penWidth">The pen width in pixels (at least 1).</param>
        /// <param name="clip">The clip rectangle, or null when the clip covers the whole canvas.</param>
        void DrawPolyline(ReadOnlySpan<PictPoint> points, PictColor color, int penWidth, PictRectangleF? clip);

        /// <summary>Draws a run of text with its baseline starting at <paramref name="baselineOrigin"/>.</summary>
        /// <param name="text">The text.</param>
        /// <param name="baselineOrigin">The pen position: left edge of the text on its baseline.</param>
        /// <param name="style">The QuickDraw font, face and size.</param>
        /// <param name="color">The pen color.</param>
        /// <param name="clip">The clip rectangle, or null when the clip covers the whole canvas.</param>
        /// <returns>The horizontal advance in pixels (the pen moves by its rounded value); 0 if nothing was drawn.</returns>
        float DrawText(string text, PictPoint baselineOrigin, PictTextStyle style, PictColor color, PictRectangleF? clip);
    }
}
