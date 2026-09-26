using System;
using SixLabors.Fonts;
using SixLabors.ImageSharp.Formats;

namespace QuickDraw.Pict.ImageSharp
{
    /// <summary>PICT-specific decoder options.</summary>
    public sealed class PictDecoderOptions : ISpecializedDecoderOptions
    {
        /// <inheritdoc/>
        public DecoderOptions GeneralOptions { get; init; } = new DecoderOptions();

        /// <summary>
        /// Maps a QuickDraw font number (the picture's TxFont) to the font family used for its text opcodes.
        /// Returning null, or leaving this unset, falls back to an installed system font resembling the classic
        /// Mac font. Classic Mac bitmap fonts are not available, so text is always an approximation.
        /// </summary>
        public Func<int, FontFamily?>? FontResolver { get; init; }
    }
}
