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
        /// Classic Mac bitmap fonts (FOND/NFNT/FONT resources you supply) to draw text with exactly as QuickDraw does.
        /// Text in a font the library lacks is drawn with an outline font instead (see <see cref="FontResolver"/>).
        /// </summary>
        public PictFontLibrary? BitmapFonts { get; init; }

        /// <summary>
        /// Maps a QuickDraw font number (the picture's TxFont) to the outline font family used for text that no
        /// bitmap font covers. Returning null, or leaving this unset, falls back to an installed system font
        /// resembling the classic Mac font; such text is an approximation.
        /// </summary>
        public Func<int, FontFamily?>? FontResolver { get; init; }

        /// <summary>
        /// The size to decode the picture at: its native resolution (default) or its 72 dpi picture frame, scaled the
        /// way the Macintosh draws it.
        /// </summary>
        public PictResolution Resolution { get; init; } = PictResolution.Native;

        /// <summary>Keeps the alpha channel of 32-bit pixel maps that carry one; see <see cref="PictDecodeOptions.PreserveAlpha"/>.</summary>
        public bool PreserveAlpha { get; init; }

        /// <summary>
        /// Which Macintosh QuickDraw to reproduce: Mac OS 9's (default) or the classic ROM's; see
        /// <see cref="PictDecodeOptions.QuickDraw"/>.
        /// </summary>
        public PictQuickDraw QuickDraw { get; init; } = PictQuickDraw.MacOS9;

        /// <summary>
        /// The depth of the screen to draw on (1, 2, 4, 8, 16 or 32, the default), for the look of the picture on an
        /// indexed or thousands-of-colors display; see <see cref="PictDecodeOptions.ScreenDepth"/>.
        /// </summary>
        public int ScreenDepth { get; init; } = 32;
    }
}
