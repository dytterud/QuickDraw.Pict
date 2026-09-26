using System;

namespace QuickDraw.Pict
{
    /// <summary>
    /// A decoded picture: 8-bit RGBA pixels, row-major, no row padding (stride = <see cref="Width"/> × 4).
    /// Pixels a picture never draws stay transparent black.
    /// </summary>
    public sealed class PictBitmap
    {
        /// <summary>Creates a transparent bitmap.</summary>
        public PictBitmap(int width, int height)
            : this(width, height, new byte[checked(width * height * 4)])
        {
        }

        /// <summary>Wraps an existing RGBA buffer of exactly <paramref name="width"/> × <paramref name="height"/> × 4 bytes.</summary>
        public PictBitmap(int width, int height, byte[] pixels)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            ArgumentNullException.ThrowIfNull(pixels);
            if (pixels.Length != width * height * 4)
                throw new ArgumentException($"Expected {width * height * 4} bytes of RGBA data, got {pixels.Length}.", nameof(pixels));
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        /// <summary>Width in pixels.</summary>
        public int Width { get; }

        /// <summary>Height in pixels.</summary>
        public int Height { get; }

        /// <summary>The RGBA pixel data.</summary>
        public byte[] Pixels { get; }

        /// <summary>The header and metadata of the picture this bitmap was decoded from, if any.</summary>
        public PictInfo? Info { get; init; }

        /// <summary>Gets or sets the pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public PictColor this[int x, int y]
        {
            get
            {
                int i = Offset(x, y);
                return new PictColor(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
            }
            set
            {
                int i = Offset(x, y);
                Pixels[i] = value.R;
                Pixels[i + 1] = value.G;
                Pixels[i + 2] = value.B;
                Pixels[i + 3] = value.A;
            }
        }

        private int Offset(int x, int y)
        {
            if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
            if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
            return (y * Width + x) * 4;
        }
    }
}
