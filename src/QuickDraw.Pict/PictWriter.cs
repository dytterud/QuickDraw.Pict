using System;
using System.Collections.Generic;
using System.IO;

namespace QuickDraw.Pict
{
    /// <summary>The pixel format <see cref="PictWriter"/> stores.</summary>
    public enum PictPixelFormat
    {
        /// <summary>1-bit indexed. A white/black palette is stored as a plain BitMap, as classic QuickDraw writes it.</summary>
        Indexed1,
        /// <summary>2-bit indexed (PixMap with a color table).</summary>
        Indexed2,
        /// <summary>4-bit indexed (PixMap with a color table).</summary>
        Indexed4,
        /// <summary>8-bit indexed (PixMap with a color table).</summary>
        Indexed8,
        /// <summary>16-bit direct, 5 bits per component (packed by words, packType 3).</summary>
        Rgb555,
        /// <summary>32-bit direct without alpha (component planes packed, packType 4, 3 components; unpacked when a row
        /// would not fit Mac OS 9's row buffer).</summary>
        Rgb888,
        /// <summary>32-bit direct with an alpha plane (packType 4, 4 components).</summary>
        Argb8888,
    }

    /// <summary>Options for <see cref="PictWriter"/>.</summary>
    public sealed class PictWriteOptions
    {
        /// <summary>The stored pixel format. Defaults to <see cref="PictPixelFormat.Rgb888"/>.</summary>
        public PictPixelFormat Format { get; init; } = PictPixelFormat.Rgb888;

        /// <summary>
        /// The palette of an indexed format (at most 2^bits colors). Rows are then written as palette indices. When
        /// null, <see cref="PictWriter.Write(Stream, PictBitmap, PictWriteOptions?)"/> builds one from the bitmap's
        /// colors, which must fit.
        /// </summary>
        public IReadOnlyList<PictColor>? Palette { get; init; }

        /// <summary>Horizontal resolution in dpi. Defaults to 72; other values write a 72 dpi picture frame of the
        /// image's physical size (an extended version 2 header).</summary>
        public double HorizontalResolution { get; init; } = 72;

        /// <summary>Vertical resolution in dpi. Defaults to 72.</summary>
        public double VerticalResolution { get; init; } = 72;

        /// <summary>An ICC profile to embed (picture comment 224).</summary>
        public byte[]? IccProfile { get; init; }

        /// <summary>Whether to write the 512-byte file header of a <c>.pict</c> file (true) or a bare picture as
        /// stored in a <c>PICT</c> resource (false). Defaults to true.</summary>
        public bool FileHeader { get; init; } = true;
    }

    /// <summary>
    /// PICT version 2 encoder: an extended version 2 header, the clip, an optional ICC profile comment, and the image
    /// as bitmap opcodes (PackBitsRect for indexed formats, DirectBitsRect for direct ones), PackBits-compressed the
    /// way QuickDraw's own reader expects. Images wider than one opcode's row limit are split into vertical strips.
    /// Rows are written top to bottom with <see cref="WriteRow"/>; call <see cref="Finish"/> after the last.
    /// </summary>
    public sealed class PictWriter
    {
        private readonly Stream stream;
        private readonly int width, height;
        private readonly PictWriteOptions options;
        private readonly PictColor[] palette;
        private readonly int bits;                       // pixel depth as stored
        private readonly bool bitMap;                    // plain 1-bit BitMap (white/black palette)
        private readonly List<byte[]>? buffered;         // rows kept for a multi-strip image
        private long written;
        private int rows;

        /// <summary>Starts a picture of the given size on <paramref name="stream"/> and writes its header.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Width or height is not 1..32767.</exception>
        /// <exception cref="ArgumentException">An indexed format has no palette, or too many colors.</exception>
        public PictWriter(Stream stream, int width, int height, PictWriteOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (width <= 0 || width > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0 || height > short.MaxValue) throw new ArgumentOutOfRangeException(nameof(height));
            this.stream = stream;
            this.width = width;
            this.height = height;
            this.options = options ?? new PictWriteOptions();
            bits = this.options.Format switch
            {
                PictPixelFormat.Indexed1 => 1,
                PictPixelFormat.Indexed2 => 2,
                PictPixelFormat.Indexed4 => 4,
                PictPixelFormat.Indexed8 => 8,
                PictPixelFormat.Rgb555 => 16,
                _ => 32,
            };
            palette = Array.Empty<PictColor>();
            if (bits <= 8)
            {
                var p = this.options.Palette ?? throw new ArgumentException("An indexed format needs a palette.", nameof(options));
                if (p.Count == 0 || p.Count > 1 << bits) throw new ArgumentException($"The palette must have 1..{1 << bits} colors.", nameof(options));
                palette = new PictColor[p.Count];
                for (int i = 0; i < p.Count; i++) palette[i] = p[i];
                bitMap = bits == 1 && palette.Length == 2 && TransferModes.SameRgb(palette[0], new PictColor(255, 255, 255)) &&
                    TransferModes.SameRgb(palette[1], new PictColor(0, 0, 0));
            }
            // 32-bit strips are buffered to choose between packed and unpacked rows (see StripFitsMacOS9).
            if (width > StripWidth || bits == 32) buffered = new List<byte[]>(height);
            WriteHeader();
        }

        /// <summary>The bytes <see cref="WriteRow"/> expects per row: one index per pixel for indexed formats,
        /// else R, G, B, A per pixel.</summary>
        public int RowLength => bits <= 8 ? width : width * 4;

        // Row bytes are 14 bits (the top two are flags): the widest strip one bitmap opcode can hold.
        private int StripWidth => bits <= 8 ? (0x3FFE * 8 / bits) & ~15 : 0x3FFE / (bits / 8);

        /// <summary>Writes a whole bitmap as a picture. For an indexed format without a palette, the bitmap's own
        /// colors form the palette (they must fit); with a palette, each pixel takes its nearest palette color.</summary>
        public static void Write(Stream stream, PictBitmap bitmap, PictWriteOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            options ??= new PictWriteOptions();
            bool indexed = options.Format <= PictPixelFormat.Indexed8;
            Dictionary<int, int>? lookup = null;
            if (indexed)
            {
                int max = 1 << (options.Format switch { PictPixelFormat.Indexed1 => 1, PictPixelFormat.Indexed2 => 2, PictPixelFormat.Indexed4 => 4, _ => 8 });
                if (options.Palette == null)
                {
                    var colors = new List<PictColor>();
                    lookup = new Dictionary<int, int>();
                    for (int i = 0; i < bitmap.Width * bitmap.Height; i++)
                    {
                        int key = Key(bitmap.Pixels, i);
                        if (lookup.ContainsKey(key)) continue;
                        if (colors.Count == max)
                            throw new ArgumentException($"The bitmap has more than {max} colors; supply a palette.", nameof(bitmap));
                        lookup[key] = colors.Count;
                        colors.Add(new PictColor(bitmap.Pixels[4 * i], bitmap.Pixels[4 * i + 1], bitmap.Pixels[4 * i + 2]));
                    }
                    if (options.Format == PictPixelFormat.Indexed1 && colors.Count <= 2)
                        colors = OrderAsWhiteBlack(colors, lookup);
                    options = new PictWriteOptions
                    {
                        Format = options.Format, Palette = colors, FileHeader = options.FileHeader, IccProfile = options.IccProfile,
                        HorizontalResolution = options.HorizontalResolution, VerticalResolution = options.VerticalResolution,
                    };
                }
            }
            var writer = new PictWriter(stream, bitmap.Width, bitmap.Height, options);
            var row = new byte[writer.RowLength];
            var nearest = new Dictionary<int, int>();
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int i = y * bitmap.Width + x;
                    if (!indexed)
                    {
                        Array.Copy(bitmap.Pixels, 4 * i, row, 4 * x, 4);
                        continue;
                    }
                    int key = Key(bitmap.Pixels, i);
                    if (lookup == null || !lookup.TryGetValue(key, out int index))
                    {
                        if (!nearest.TryGetValue(key, out index))
                            nearest[key] = index = Nearest(writer.palette, bitmap.Pixels[4 * i], bitmap.Pixels[4 * i + 1], bitmap.Pixels[4 * i + 2]);
                    }
                    row[x] = (byte)index;
                }
                writer.WriteRow(row);
            }
            writer.Finish();
        }

        private static int Key(byte[] px, int i) => (px[4 * i] << 16) | (px[4 * i + 1] << 8) | px[4 * i + 2];

        // A two-color 1-bit palette as white then black when those are its colors (so it is written as a BitMap).
        private static List<PictColor> OrderAsWhiteBlack(List<PictColor> colors, Dictionary<int, int> lookup)
        {
            var white = new PictColor(255, 255, 255);
            var black = new PictColor(0, 0, 0);
            bool onlyWhiteBlack = colors.TrueForAll(c => TransferModes.SameRgb(c, white) || TransferModes.SameRgb(c, black));
            if (!onlyWhiteBlack) return colors;
            lookup.Clear();
            lookup[0xFFFFFF] = 0;
            lookup[0] = 1;
            return new List<PictColor> { white, black };
        }

        private static int Nearest(PictColor[] palette, int r, int g, int b)
        {
            int best = 0, bestDistance = int.MaxValue;
            for (int k = 0; k < palette.Length; k++)
            {
                int dr = palette[k].R - r, dg = palette[k].G - g, db = palette[k].B - b;
                int d = dr * dr + dg * dg + db * db;
                if (d < bestDistance) { best = k; bestDistance = d; }
            }
            return best;
        }

        /// <summary>Writes the next row (<see cref="RowLength"/> bytes): palette indices for indexed formats, else
        /// R, G, B, A per pixel (alpha is stored only by <see cref="PictPixelFormat.Argb8888"/>).</summary>
        public void WriteRow(ReadOnlySpan<byte> row)
        {
            if (row.Length != RowLength) throw new ArgumentException($"Expected {RowLength} bytes per row.", nameof(row));
            if (rows == height) throw new InvalidOperationException("All rows have already been written.");
            if (bits <= 8)
                foreach (byte index in row)
                    if (index >= palette.Length) throw new ArgumentException($"Palette index {index} out of range.", nameof(row));
            if (buffered != null) buffered.Add(row.ToArray());
            else WriteStripRow(row, 0, width);
            rows++;
        }

        /// <summary>Ends the picture. Every row must have been written.</summary>
        public void Finish()
        {
            if (rows != height) throw new InvalidOperationException($"Wrote {rows} of {height} rows.");
            if (buffered != null)
            {
                for (int left = 0; left < width; left += StripWidth)
                {
                    int w = Math.Min(StripWidth, width - left);
                    bool unpacked = bits == 32 && !StripFitsMacOS9(left, w);
                    if (left > 0 || bits == 32) BitmapHeader(left, w, unpacked);
                    foreach (var row in buffered) WriteStripRow(row, left, w, unpacked);
                }
            }
            if ((written & 1) == 1) U8(0);                  // word-align before OpEndPic
            U16(0x00FF);
        }

        // ---- header ----

        private void WriteHeader()
        {
            if (options.FileHeader) Bytes(new byte[PictHeader.FileHeaderSize]);
            double hRes = options.HorizontalResolution > 0 ? options.HorizontalResolution : 72;
            double vRes = options.VerticalResolution > 0 ? options.VerticalResolution : 72;
            int frameWidth = Math.Max(1, (int)Math.Round(width * 72 / hRes));
            int frameHeight = Math.Max(1, (int)Math.Round(height * 72 / vRes));

            U16(0);                                          // picSize (unused for version 2)
            Rect(0, 0, frameHeight, frameWidth);             // picFrame, 72 dpi
            U16(0x0011); U16(0x02FF);                        // version 2
            U16(0x0C00);                                     // extended version 2 header
            U16(0xFFFE); U16(0);
            U32(Fixed(hRes)); U32(Fixed(vRes));
            Rect(0, 0, height, width);                       // source rect: the image at its resolution
            U32(0);
            U16(0x0001); U16(10); Rect(0, 0, height, width); // clip
            if (options.IccProfile is { Length: > 0 } icc) WriteIccProfile(icc);
            if (bits != 32) BitmapHeader(0, Math.Min(width, StripWidth), unpacked: false);
        }

        // Mac OS 9 unpacks component-plane rows through a buffer of (n + (n >> 7) + 3) & ~3 bytes for n unpacked bytes,
        // and misreads any row packed into more (PixMap.ReadPlanesMacOS9). Incompressible rows need n + n / 128 + 1,
        // so a strip with such a row is stored unpacked (packType 1) instead.
        private bool StripFitsMacOS9(int left, int w)
        {
            int n = w * (options.Format == PictPixelFormat.Argb8888 ? 4 : 3);
            int buffer = (n + (n >> 7) + 3) & ~3;
            if (RowBytes(w) < 8) return true;
            foreach (var row in buffered!)
                if (PackBits(PlaneLine(row, left, w)).Length > buffer) return false;
            return true;
        }

        private static uint Fixed(double dpi) => (uint)Math.Round(dpi * 65536);

        // ICC profile comment (kind 224): selector 0 with the first chunk, selector 1 per further chunk, selector 2.
        private void WriteIccProfile(byte[] icc)
        {
            const int Chunk = 32000;
            for (int offset = 0; offset < icc.Length; offset += Chunk)
                Comment(offset == 0 ? 0u : 1u, icc.AsSpan(offset, Math.Min(Chunk, icc.Length - offset)));
            Comment(2, ReadOnlySpan<byte>.Empty);

            void Comment(uint selector, ReadOnlySpan<byte> data)
            {
                if ((written & 1) == 1) U8(0);
                U16(0x00A1); U16(224); U16(4 + data.Length);
                U32(selector);
                Bytes(data.ToArray());
            }
        }

        // The opcode and pixel map fields up to the pixel data, for the strip [left, left + w).
        private void BitmapHeader(int left, int w, bool unpacked)
        {
            if ((written & 1) == 1) U8(0);
            int rowBytes = RowBytes(w);
            var bounds = (0, left, height, left + w);
            if (bits > 8)
            {
                U16(0x009A);                                 // DirectBitsRect
                U32(0x000000FF);                             // baseAddr
                U16(rowBytes | 0x8000);
                Rect(bounds);
                PixMapFields(packType: bits == 16 ? 3 : unpacked ? 1 : 4, pixelType: 16, cmpCount: bits == 16 || options.Format == PictPixelFormat.Rgb888 ? 3 : 4,
                    cmpSize: bits == 16 ? 5 : 8);
            }
            else
            {
                U16(rowBytes < 8 ? 0x0090 : 0x0098);         // BitsRect (unpacked) / PackBitsRect
                U16(bitMap ? rowBytes : rowBytes | 0x8000);
                Rect(bounds);
                if (!bitMap)
                {
                    PixMapFields(packType: 0, pixelType: 0, cmpCount: 1, cmpSize: bits);
                    U32(0);                                  // ctSeed
                    U16(0);                                  // ctFlags: entries carry their pixel values
                    U16(palette.Length - 1);
                    for (int i = 0; i < palette.Length; i++)
                    {
                        U16(i);
                        U16(palette[i].R * 257); U16(palette[i].G * 257); U16(palette[i].B * 257);
                    }
                }
            }
            Rect(bounds);                                    // srcRect
            Rect(bounds);                                    // dstRect
            U16(0);                                          // srcCopy
        }

        private void PixMapFields(int packType, int pixelType, int cmpCount, int cmpSize)
        {
            U16(0);                                          // pmVersion
            U16(packType);
            U32(0);                                          // packSize
            U32(Fixed(options.HorizontalResolution > 0 ? options.HorizontalResolution : 72));
            U32(Fixed(options.VerticalResolution > 0 ? options.VerticalResolution : 72));
            U16(pixelType);
            U16(bits);
            U16(cmpCount);
            U16(cmpSize);
            U32(0); U32(0); U32(0);                          // planeBytes, pmTable, reserved
        }

        // Even row bytes for the strip's pixels.
        private int RowBytes(int w) => bits <= 8 ? (w * bits + 15) / 16 * 2 : w * bits / 8;

        // ---- pixel data ----

        private void WriteStripRow(ReadOnlySpan<byte> row, int left, int w, bool unpacked = false)
        {
            int rowBytes = RowBytes(w);
            var line = new byte[rowBytes];
            if (bits <= 8)
            {
                for (int x = 0; x < w; x++)
                {
                    int bit = x * bits;
                    line[bit >> 3] |= (byte)(row[left + x] << (8 - bits - (bit & 7)));
                }
            }
            else if (bits == 16)
            {
                for (int x = 0; x < w; x++)
                {
                    var p = row.Slice(4 * (left + x), 4);
                    int v = ((p[0] >> 3) << 10) | ((p[1] >> 3) << 5) | (p[2] >> 3);
                    line[2 * x] = (byte)(v >> 8);
                    line[2 * x + 1] = (byte)v;
                }
            }
            else if (rowBytes < 8 || unpacked)
            {
                for (int x = 0; x < w; x++)                  // unpacked 32-bit: alpha (or pad), R, G, B
                {
                    var p = row.Slice(4 * (left + x), 4);
                    line[4 * x] = options.Format == PictPixelFormat.Argb8888 ? p[3] : (byte)0;
                    line[4 * x + 1] = p[0]; line[4 * x + 2] = p[1]; line[4 * x + 3] = p[2];
                }
            }
            else line = PlaneLine(row, left, w);

            if (rowBytes < 8 || unpacked)
            {
                Bytes(line);
                return;
            }
            var packed = bits == 16 ? PackWords(line) : PackBits(line);
            if (rowBytes > 250) U16(packed.Length); else U8(packed.Length);
            Bytes(packed);
        }

        // Component planes, each w bytes: alpha first when stored, then R, G, B.
        private byte[] PlaneLine(ReadOnlySpan<byte> row, int left, int w)
        {
            bool alpha = options.Format == PictPixelFormat.Argb8888;
            int planes = alpha ? 4 : 3, first = alpha ? 0 : 1;
            var line = new byte[w * planes];
            for (int x = 0; x < w; x++)
            {
                var p = row.Slice(4 * (left + x), 4);
                for (int k = 0; k < planes; k++)
                    line[k * w + x] = (k + first) switch { 0 => p[3], 1 => p[0], 2 => p[1], _ => p[2] };
            }
            return line;
        }

        // PackBits: runs of 3 or more equal bytes as (1 - count, byte), otherwise literal blocks (count - 1, bytes);
        // at most 128 bytes per run or block.
        internal static byte[] PackBits(byte[] data) => Pack(data, 1);

        // PackBits over 16-bit units (16-bit pixel rows, packType 3): counts are in words.
        internal static byte[] PackWords(byte[] data) => Pack(data, 2);

        private static byte[] Pack(byte[] data, int unit)
        {
            var output = new List<byte>(data.Length + data.Length / 64 + 2);
            int n = data.Length / unit;
            bool Same(int a, int b) => data.AsSpan(a * unit, unit).SequenceEqual(data.AsSpan(b * unit, unit));
            int i = 0;
            while (i < n)
            {
                int run = 1;
                while (i + run < n && run < 128 && Same(i, i + run)) run++;
                if (run >= 3 || (run == 2 && unit == 2))
                {
                    output.Add((byte)(1 - run));
                    output.AddRange(data.AsSpan(i * unit, unit).ToArray());
                    i += run;
                    continue;
                }
                int start = i++;
                while (i < n && i - start < 128 && !(i + 2 < n && Same(i, i + 1) && Same(i, i + 2))) i++;
                output.Add((byte)(i - start - 1));
                output.AddRange(data.AsSpan(start * unit, (i - start) * unit).ToArray());
            }
            return output.ToArray();
        }

        private void U8(int v) { stream.WriteByte((byte)v); written++; }
        private void U16(int v) { U8(v >> 8); U8(v); }
        private void U32(uint v) { U8((int)(v >> 24)); U8((int)(v >> 16)); U8((int)(v >> 8)); U8((int)v); }
        private void Rect(int t, int l, int b, int r) { U16(t); U16(l); U16(b); U16(r); }
        private void Rect((int t, int l, int b, int r) x) => Rect(x.t, x.l, x.b, x.r);
        private void Bytes(byte[] data) { stream.Write(data, 0, data.Length); written += data.Length; }
    }
}
