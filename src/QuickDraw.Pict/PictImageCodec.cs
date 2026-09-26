namespace QuickDraw.Pict
{
    /// <summary>
    /// Decodes QuickTime-compressed images embedded in pictures (the CompressedQuickTime opcode) for codecs the core
    /// does not implement, such as JPEG, PNG, GIF, TIFF or BMP.
    /// </summary>
    public interface IPictImageCodec
    {
        /// <summary>
        /// Decodes one image, or returns null if the codec is not supported (the picture's own fallback drawing is then
        /// shown, as on a Macintosh without that decompressor).
        /// </summary>
        /// <param name="description">The QuickTime image description.</param>
        /// <param name="data">The compressed image data.</param>
        PictBitmap? Decode(PictImageDescription description, byte[] data);
    }

    /// <summary>A QuickTime image description (<c>ImageDescription</c>) of an image embedded in a picture.</summary>
    /// <param name="CodecType">The four-character compressor type, such as <c>"jpeg"</c> or <c>"rle "</c>.</param>
    /// <param name="Width">Width in pixels.</param>
    /// <param name="Height">Height in pixels.</param>
    /// <param name="Depth">Pixel depth: 1-32, or 33-40 for 1-8 bit grayscale.</param>
    /// <param name="ClutId">The standard color table id, or -1 for none (0 when the table is stored with the image).</param>
    /// <param name="HorizontalResolution">Horizontal resolution in dpi.</param>
    /// <param name="VerticalResolution">Vertical resolution in dpi.</param>
    /// <param name="CompressorName">The compressor's display name.</param>
    public sealed record PictImageDescription(string CodecType, int Width, int Height, int Depth, int ClutId,
        double HorizontalResolution, double VerticalResolution, string CompressorName)
    {
        /// <summary>The image's color table (standard or stored), for depths up to 8 and grayscale; else null.</summary>
        public PictColor[]? ColorTable { get; init; }
    }
}
