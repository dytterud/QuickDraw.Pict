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

        /// <summary>
        /// The size to decode the picture at: its native resolution (default) or its 72 dpi picture frame, scaled the
        /// way the Macintosh draws it.
        /// </summary>
        public PictResolution Resolution { get; init; } = PictResolution.Native;

        /// <summary>Keeps the alpha channel of 32-bit pixel maps that carry one; see <see cref="PictDecodeOptions.PreserveAlpha"/>.</summary>
        public bool PreserveAlpha { get; init; }
    }
}
