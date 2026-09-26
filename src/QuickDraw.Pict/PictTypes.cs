namespace QuickDraw.Pict
{
    /// <summary>An 8-bit RGBA color.</summary>
    public readonly record struct PictColor(byte R, byte G, byte B, byte A = 255);

    /// <summary>A QuickDraw rectangle in picture coordinates: <see cref="Right"/> and <see cref="Bottom"/> are exclusive.</summary>
    public readonly record struct PictRect(int Top, int Left, int Bottom, int Right)
    {
        /// <summary>Right − Left.</summary>
        public int Width => Right - Left;

        /// <summary>Bottom − Top.</summary>
        public int Height => Bottom - Top;

        /// <summary>True when the rectangle encloses no pixels (QuickDraw <c>EmptyRect</c>).</summary>
        public bool IsEmpty => Bottom <= Top || Right <= Left;
    }

    /// <summary>A picture comment (opcodes 0x00A0 / 0x00A1); <see cref="Data"/> is empty for short comments.</summary>
    public readonly record struct PictComment(int Kind, byte[] Data);

    /// <summary>QuickDraw text state for a text opcode.</summary>
    /// <param name="FontId">QuickDraw font number (TxFont), e.g. 0 system, 2 New York, 3 Geneva, 4 Monaco, 20 Times, 21 Helvetica, 22 Courier.</param>
    /// <param name="Face">QuickDraw style bits (TxFace): 1 bold, 2 italic, 4 underline, 8 outline, 16 shadow, 32 condense, 64 extend.</param>
    /// <param name="Size">Point size (TxSize); 0 means the font's default size.</param>
    /// <param name="FontName">The font's name, when the picture names it (opcode 0x002C fontName).</param>
    public readonly record struct PictTextStyle(int FontId, int Face, int Size, string? FontName = null);
}
