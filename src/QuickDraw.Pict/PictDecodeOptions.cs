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
        /// Decodes QuickTime-compressed images whose codec the core lacks (e.g. JPEG, PNG). The core decodes 'raw ',
        /// 'rle ', 'rpza', 'smc ', 'cvid', '8BPS', 'yuv2', 'YVU9', 'tga ' and 'PNTG' itself.
        /// </summary>
        public IPictImageCodec? ImageCodec { get; init; }

        /// <summary>
        /// The system highlight color, used by hilite-mode drawing (opcode 0x001C) until the picture sets its own
        /// (0x001D); DrawPicture takes it from the system setting. Null (the default) uses the default of the
        /// chosen <see cref="QuickDraw"/>: Mac OS 9's lavender (0xCCCC, 0xCCCC, 0xFFFF), or the light cyan
        /// (0x9999, 0xCCCC, 0xCCCC) for the ROM.
        /// </summary>
        public PictColor? HiliteColor { get; init; }

        /// <summary>The size to draw the picture at. Defaults to <see cref="PictResolution.Native"/>.</summary>
        public PictResolution Resolution { get; init; } = PictResolution.Native;

        /// <summary>
        /// Keeps the alpha channel of 32-bit pixel maps that carry one (four components) when they are copied with
        /// srcCopy. QuickDraw itself ignores it, and many pictures leave it zero, so it is off by default.
        /// </summary>
        public bool PreserveAlpha { get; init; }

        /// <summary>
        /// Which Macintosh QuickDraw to reproduce. Defaults to <see cref="PictQuickDraw.MacOS9"/>, the QuickDraw of
        /// Mac OS 9 (and of emulators running it); <see cref="PictQuickDraw.MacRom"/> draws as the classic 68k
        /// QuickDraw in the Macintosh ROM.
        /// </summary>
        public PictQuickDraw QuickDraw { get; init; } = PictQuickDraw.MacOS9;

        /// <summary>
        /// The depth of the screen the picture is drawn on: 32 (the default) draws in full color; 1, 2, 4 and 8 draw into
        /// an indexed screen with the Macintosh's default color table for that depth (black and white, four greys, the
        /// 16 and the 256 standard colors), and 16 into a 5-5-5 screen, with QuickDraw's own color matching, transfer
        /// modes on pixel values and ditherCopy dithering. The result is still RGBA: each pixel the color it has on that
        /// screen.
        /// </summary>
        public int ScreenDepth
        {
            get => screenDepth;
            init => screenDepth = value is 1 or 2 or 4 or 8 or 16 or 32
                ? value
                : throw new System.ArgumentOutOfRangeException(nameof(ScreenDepth), value, "The screen depth must be 1, 2, 4, 8, 16 or 32.");
        }
        private readonly int screenDepth = 32;
    }

    /// <summary>The QuickDraw implementation a picture is drawn as. The two differ in a few rounding and edge rules.</summary>
    public enum PictQuickDraw
    {
        /// <summary>Mac OS 9's native PowerPC QuickDraw and Font Manager.</summary>
        MacOS9,

        /// <summary>The 68k QuickDraw and Font Manager of the Macintosh ROM (Mac OS ROM 1.6), as on Macs before Mac OS 9.</summary>
        MacRom,
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
