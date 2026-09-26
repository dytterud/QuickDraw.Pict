namespace QuickDraw.Pict
{
    /// <summary>Options for <see cref="PictReader"/>.</summary>
    public sealed class PictDecodeOptions
    {
        /// <summary>Default options.</summary>
        public static PictDecodeOptions Default { get; } = new PictDecodeOptions();

        /// <summary>
        /// Rasterizes text for the picture's text opcodes. Null skips text (its operands are still consumed and the
        /// pen still advances by nothing).
        /// </summary>
        public IPictTextFallback? TextFallback { get; init; }

        /// <summary>
        /// Bitmap fonts to draw text with, exactly as QuickDraw does. Text in a font the library lacks (or all text,
        /// when this is null) goes to <see cref="TextFallback"/>.
        /// </summary>
        public PictFontLibrary? Fonts { get; init; }

        /// <summary>
        /// The highlight color used by hilite-mode drawing (opcode 0x001C) until the picture sets its own
        /// (0x001D). Defaults to Color QuickDraw's standard light cyan (0x9999, 0xCCCC, 0xCCCC).
        /// </summary>
        public PictColor HiliteColor { get; init; } = new PictColor(0x99, 0xCC, 0xCC);

        /// <summary>The size to draw the picture at. Defaults to <see cref="PictResolution.Native"/>.</summary>
        public PictResolution Resolution { get; init; } = PictResolution.Native;

        /// <summary>
        /// Keeps the alpha channel of 32-bit pixel maps that carry one (four components) when they are copied with
        /// srcCopy. QuickDraw itself ignores it, and many pictures leave it zero, so it is off by default.
        /// </summary>
        public bool PreserveAlpha { get; init; }
    }

    /// <summary>The size a picture is drawn at.</summary>
    public enum PictResolution
    {
        /// <summary>
        /// At the picture's own resolution: the canvas covers <see cref="PictInfo.Bounds"/> (for an extended version 2
        /// picture, its source rectangle at <see cref="PictInfo.HorizontalResolution"/>), one pixel per unit.
        /// </summary>
        Native,

        /// <summary>
        /// At 72 dpi: the canvas covers <see cref="PictInfo.PictureFrame"/>, scaling everything the way
        /// <c>DrawPicture(picture, picFrame)</c> does on a Macintosh.
        /// </summary>
        PictureFrame,
    }

    /// <summary>
    /// Rasterizes a run of text into a 1-bit mask, which the decoder then transfers with the picture's text mode
    /// and foreground color, like QuickDraw's own text drawing.
    /// </summary>
    public interface IPictTextFallback
    {
        /// <summary>Renders <paramref name="text"/>, or returns null to draw nothing.</summary>
        PictTextMask? Render(string text, PictTextStyle style);
    }

    /// <summary>A rasterized text run: ink bits plus where the pen (on the baseline) sits inside the mask.</summary>
    public sealed class PictTextMask
    {
        /// <summary>Creates a mask.</summary>
        /// <param name="width">Mask width in pixels.</param>
        /// <param name="height">Mask height in pixels.</param>
        /// <param name="originX">Horizontal pen position within the mask (the left edge of the run on the baseline).</param>
        /// <param name="originY">Baseline row within the mask.</param>
        /// <param name="bits">Width × height bytes, row-major; non-zero is ink.</param>
        /// <param name="advance">How far the pen moves after the run, in pixels.</param>
        public PictTextMask(int width, int height, int originX, int originY, byte[] bits, float advance)
        {
            if (bits == null || bits.Length != width * height)
                throw new System.ArgumentException("Expected width × height mask bytes.", nameof(bits));
            Width = width; Height = height; OriginX = originX; OriginY = originY; Bits = bits; Advance = advance;
        }

        /// <summary>Mask width in pixels.</summary>
        public int Width { get; }

        /// <summary>Mask height in pixels.</summary>
        public int Height { get; }

        /// <summary>Horizontal pen position within the mask.</summary>
        public int OriginX { get; }

        /// <summary>Baseline row within the mask.</summary>
        public int OriginY { get; }

        /// <summary>Row-major ink bytes; non-zero is ink.</summary>
        public byte[] Bits { get; }

        /// <summary>Pen advance in pixels.</summary>
        public float Advance { get; }
    }
}
