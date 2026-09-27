using System;
using System.Collections.Generic;

namespace QuickDraw.Pict
{
    // A 1, 2, 4, 8 or 16-bit screen (a GWorld of that depth with its default color table) that a picture is drawn into
    // instead of the 32-bit canvas. Indexed depths hold color-table indices; 16 bits holds 5-5-5 pixels. The canvas keeps
    // each pixel's color (the table entry, or the 5-bit components replicated to 8 bits), so indices are read back
    // through the table.
    //
    // RGB -> index:
    //  - Blitters converting direct pixels (and arithmetic results) use the inverse table directly: the top 4 bits of
    //    each 8-bit component (res 4, the default) index a 4096-entry table; grey tables (clut 1 and 2) use
    //    links[(5R + 9G + 2B) >> 4].
    //  - Color2Index (fg / bk / hilite, indexed sources) also walks the chain of hidden colors (entries whose cell another
    //    entry owns) by Manhattan distance, as Mac OS 9 does; grey tables use links[(5R + 9G + 2B) >> 12] on 16-bit
    //    components.
    // The inverse table: seeds entry 0, the last entry, then 1..n-1 at their quantised cells (the first to reach a cell
    // owns it), then a breadth-first fill over a bordered cube in neighbour order +B -B +G -G +R -R. A grey table maps each
    // luminance to the entry of nearest red (ties to the darker, lowest index on equal reds).
    internal sealed class ScreenDevice
    {
        private const int Res = 4;
        private readonly byte[] table = Array.Empty<byte>();
        private readonly int[] links = new int[256];
        private readonly int[] grey = Array.Empty<int>();
        private readonly int hidden;
        private readonly Dictionary<int, int> indexOfColor = new Dictionary<int, int>();

        private ScreenDevice(int depth, bool macOS9)
        {
            Depth = depth;
            MacOS9 = macOS9;
            if (depth == 16)
            {
                Clut = Array.Empty<PictColor>();
                Clut16 = Array.Empty<(ushort, ushort, ushort)>();
                return;
            }
            Clut = StandardColorTables.ForId(depth)!;
            Clut16 = depth == 4 ? FourBit16 : Array.ConvertAll(Clut, c => ((ushort)(c.R * 257), (ushort)(c.G * 257), (ushort)(c.B * 257)));
            for (int i = Clut.Length - 1; i >= 0; i--) indexOfColor[Key(Clut[i])] = i;
            bool isGrey = Array.TrueForAll(Clut, c => c.R == c.G && c.G == c.B);
            if (isGrey) grey = GreyLinks(Clut);
            else (table, hidden) = MakeInverseTable(Clut, links);
        }

        public static ScreenDevice? For(int depth, bool macOS9) => depth switch
        {
            32 => null,
            1 or 2 or 4 or 8 or 16 => new ScreenDevice(depth, macOS9),
            _ => throw new ArgumentOutOfRangeException(nameof(depth), depth, "The screen depth must be 1, 2, 4, 8, 16 or 32."),
        };

        public int Depth { get; }
        public bool MacOS9 { get; }
        public PictColor[] Clut { get; }
        // The table's exact 16-bit components (clut 4's are not byte-replicated).
        public (ushort r, ushort g, ushort b)[] Clut16 { get; }

        private static readonly (ushort, ushort, ushort)[] FourBit16 =
        {
            (0xFFFF, 0xFFFF, 0xFFFF), (0xFC00, 0xF37D, 0x052F), (0xFFFF, 0x648A, 0x028C), (0xDD6B, 0x08C2, 0x06A2),
            (0xF2D7, 0x0856, 0x84EC), (0x46E3, 0x0000, 0xA53E), (0x0000, 0x0000, 0xD400), (0x0241, 0xAB54, 0xEAFF),
            (0x1F21, 0xB793, 0x1431), (0x0000, 0x64AF, 0x11B0), (0x5600, 0x2C9D, 0x0524), (0x90D7, 0x7160, 0x3A34),
            (0xC000, 0xC000, 0xC000), (0x8000, 0x8000, 0x8000), (0x4000, 0x4000, 0x4000), (0x0000, 0x0000, 0x0000),
        };

        // Index2Color: the table entry, or on a 16-bit screen each 5-bit field replicated to 16 bits.
        public (int r, int g, int b) Index2Color16(int value)
        {
            if (Depth == 16)
            {
                static int E(int c) => (c << 11) | (c << 6) | (c << 1) | (c >> 4);
                return (E((value >> 10) & 31), E((value >> 5) & 31), E(value & 31));
            }
            (ushort r, ushort g, ushort b) e = value < Clut16.Length ? Clut16[value] : default;
            return (e.r, e.g, e.b);
        }
        public bool Indexed => Depth <= 8;
        public bool Grey => grey.Length != 0;
        // All the value bits of a pixel (the plane mask for XOR / invert).
        public int Mask => Depth == 16 ? 0x7FFF : (1 << Depth) - 1;

        // ---- conversions ----

        // The blitters' conversion of an 8-bit RGB color (no hidden-color search).
        public int Lookup(PictColor c)
        {
            if (Depth == 16) return ((c.R >> 3) << 10) | ((c.G >> 3) << 5) | (c.B >> 3);
            if (Grey) return grey[(5 * c.R + 9 * c.G + 2 * c.B) >> 4];
            return table[((c.R >> (8 - Res)) << (2 * Res)) | ((c.G >> (8 - Res)) << Res) | (c.B >> (8 - Res))];
        }

        // The inverse table itself (arithmetic results): as Lookup, except that a grey table quantises first — each
        // component's top 4 bits, the luminance of the cell's low corner.
        public int TableLookup(PictColor c)
        {
            if (!Grey) return Lookup(c);
            return grey[(5 * (c.R & 0xF0) + 9 * (c.G & 0xF0) + 2 * (c.B & 0xF0)) >> 4];
        }

        // Color2Index (Mac OS 9): the table, then the hidden-color chain.
        public int Color2Index(PictColor c) => Color2Index(c.R * 257, c.G * 257, c.B * 257);

        public int Color2Index(int r, int g, int b)
        {
            if (Depth == 16) return ((r >> 11) << 10) | ((g >> 11) << 5) | (b >> 11);
            // Grey tables: Mac OS 9 weighs (5R + 9G + 2B) / 16, the ROM halves its way to ((((R + G)/2 + B)/2 + R)/2 + G)/2.
            if (Grey) return grey[MacOS9 ? (5 * r + 9 * g + 2 * b) >> 12 : ((((((r + g) >> 1) + b) >> 1) + r >> 1) + g >> 1) >> 8];
            int idx = Lookup(new PictColor((byte)(r >> 8), (byte)(g >> 8), (byte)(b >> 8)));
            if (hidden == 0 || links[idx] == idx) return idx;
            int best = idx, bestDistance = int.MaxValue, cur = idx;
            for (int n = 256; n >= 1; n--)
            {
                var e = Clut16[cur];
                int d = Math.Abs(e.r - r) + Math.Abs(e.g - g) + Math.Abs(e.b - b);
                if (d == 0) return cur;
                if (d < bestDistance) (bestDistance, best) = (d, cur);
                cur = links[cur];
                if (cur == idx) break;
            }
            return best;
        }

        public PictColor ColorOf(int value)
        {
            if (Depth == 16)
                return new PictColor(Expand5((value >> 10) & 31), Expand5((value >> 5) & 31), Expand5(value & 31));
            return value < Clut.Length ? Clut[value] : new PictColor(0, 0, 0);
        }

        private static byte Expand5(int c) => (byte)((c << 3) | (c >> 2));

        // A canvas pixel's value; pixels never drawn are the erased port's white.
        public int Read(PictBitmap canvas, int x, int y)
        {
            int i = (y * canvas.Width + x) * 4;
            var p = canvas.Pixels;
            var c = p[i + 3] == 0 ? new PictColor(255, 255, 255) : new PictColor(p[i], p[i + 1], p[i + 2]);
            if (Depth == 16) return Lookup(c);
            return indexOfColor.TryGetValue(Key(c), out int index) ? index : Lookup(c);
        }

        public void Write(PictBitmap canvas, int x, int y, int value) => Painter.WritePixel(canvas, x, y, ColorOf(value & Mask));

        private static int Key(PictColor c) => (c.R << 16) | (c.G << 8) | c.B;

        // ---- inverse tables ----

        private static (byte[] table, int hidden) MakeInverseTable(PictColor[] clut, int[] links)
        {
            const int n = 1 << Res, S = n + 2;
            const int Bound = 0x7FFF, Undefined = 0x8000;
            var cube = new int[S * S * S];
            for (int r = 0; r < S; r++)
                for (int g = 0; g < S; g++)
                    for (int b = 0; b < S; b++)
                        cube[(r * S + g) * S + b] = r == 0 || g == 0 || b == 0 || r == S - 1 || g == S - 1 || b == S - 1 ? Bound : Undefined;
            var queue = new Queue<int>();
            int hidden = 0;
            void Seed(int i)
            {
                var c = clut[i];
                int cell = (((c.R >> (8 - Res)) + 1) * S + (c.G >> (8 - Res)) + 1) * S + (c.B >> (8 - Res)) + 1;
                int owner = cube[cell];
                if (owner < Undefined)
                {
                    links[i] = links[owner];
                    links[owner] = i;
                    hidden++;
                }
                else
                {
                    links[i] = i;
                    cube[cell] = i;
                    queue.Enqueue(cell);
                }
            }
            int last = clut.Length - 1;
            Seed(0);
            if (last > 0) Seed(last);
            for (int i = 1; i < last; i++) Seed(i);
            int[] deltas = { 1, -1, S, -S, S * S, -S * S };
            while (queue.Count > 0)
            {
                int p = queue.Dequeue(), owner = cube[p];
                foreach (int d in deltas)
                    if (cube[p + d] == Undefined)
                    {
                        cube[p + d] = owner;
                        queue.Enqueue(p + d);
                    }
            }
            var table = new byte[n * n * n];
            int k = 0;
            foreach (int w in cube)
                if (w != Bound) table[k++] = (byte)w;
            return (table, hidden);
        }

        // MakeGrayITab's luminance -> index map: each table entry at its red, the gaps filled alternately from the left
        // and the right until none is left (nearest luminance, exact ties to the darker entry).
        private static int[] GreyLinks(PictColor[] clut)
        {
            var t = new int[256];
            Array.Fill(t, -1);
            t[255] = 0;
            t[0] = 255;
            for (int i = clut.Length - 1; i >= 0; i--) t[clut[i].R] = i;
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int k = 1; k < 256; )
                {
                    if (t[k] >= 0) { k++; continue; }
                    t[k] = t[k - 1];
                    changed = true;
                    k += 2;
                }
                for (int k = 255; k > 0; )
                {
                    k--;
                    if (t[k] >= 0) continue;
                    t[k] = t[k + 1];
                    changed = true;
                    k--;
                }
            }
            for (int k = 0; k < 256; k++) t[k] &= 0xFF;
            return t;
        }

        // ---- PatDither: an RGB pattern as a 2 x 2 cell of indices [0 1; 2 3], filled in the order 0, 1, 3, 2 with the
        // running error of 16-bit components (Color2Index / Index2Color).
        public int[] PatDither((ushort r, ushort g, ushort b) rgb)
        {
            var cell = new int[4];
            int tr = 0, tg = 0, tb = 0;
            foreach (int slot in new[] { 0, 1, 3, 2 })
            {
                tr += rgb.r; tg += rgb.g; tb += rgb.b;
                int i = Color2Index(Math.Clamp(tr, 0, 0xFFFF), Math.Clamp(tg, 0, 0xFFFF), Math.Clamp(tb, 0, 0xFFFF));
                var got = Index2Color16(i);
                tr -= got.r; tg -= got.g; tb -= got.b;
                cell[slot] = i;
            }
            return cell;
        }
    }

    // Transfer modes on a ScreenDevice's pixel values (Mac OS 9 rules; the fg / bk / hilite indices are the port's colors
    // through Color2Index).
    internal static class DeviceModes
    {
        // 1-bit screens turn arithmetic modes into Boolean ones and hilite into xor.
        private static readonly int[] OneBitArithmetic = { 0, 3, 2, 1, 1, 3, 2, 1 };

        private static int ForDevice(int m, ScreenDevice d)
        {
            if (d.Depth != 1) return m;
            if (TransferModes.IsArithmetic(m)) return OneBitArithmetic[m & 7];
            return m == TransferModes.Hilite ? TransferModes.SrcXor : m;
        }

        // A 1-bit source or pattern bit.
        public static bool Bit(int m, bool bit, int dst, in PortColors c, out int result)
        {
            var d = c.Device!;
            m = ForDevice(m, d);
            if (m >= TransferModes.Blend)
                return Colored(m, bit ? c.Fore : c.Back, bit ? c.FgIndex : c.BkIndex, dst, c, out result);
            bool on = (m & 4) != 0 ? !bit : bit;
            switch (m & 3)
            {
                case 0: result = on ? c.FgIndex : c.BkIndex; return true;
                case 1: result = c.FgIndex; return on;
                case 2: result = dst ^ d.Mask; return on;
                default: result = c.BkIndex; return on;
            }
        }

        // A color source pixel. Direct sources go through the 32-bit rules on RGB and then the inverse table; indexed
        // sources are converted with Color2Index and combined as indices (or: (s & fg) | (~s & d); bic: (s & bk) |
        // (~s & d); xor: d ^ s; the not modes invert s); an indexed copy maps (rgb & bk) | (~rgb & fg).
        public static bool Source(int m, PictColor s, bool direct, int dst, in PortColors c, out int result,
            (int r, int g, int b)? exact = null)
        {
            var d = c.Device!;
            m = ForDevice(m, d);
            if (m >= TransferModes.Blend)
            {
                // An indexed source is first mapped to the screen's indices; arithmetic then uses the screen's colors.
                int mapped = direct ? d.Lookup(s) : d.Color2Index(s);
                return Colored(m, direct || d.Depth == 16 ? s : d.ColorOf(mapped), mapped, dst, c, out result);
            }
            if (d.Depth == 16 && c.MacOS9 && Colorize16(m, s, dst, c, direct) is int colorized)
            {
                result = colorized;
                return true;
            }
            if (direct || d.Depth == 16)
            {
                var dstColor = d.ColorOf(dst);
                if (c.MacOS9 && TransferModes.ColorizeBlend(m, s, dstColor, c, direct) is PictColor blended)
                    result = d.TableLookup(blended);
                else
                    result = d.Lookup(TransferModes.ApplyBoolean(m, s, dstColor, c));
                return true;
            }
            if ((m & 3) == 0)
            {
                // The copy table maps (rgb & bk) | (~rgb & fg); when the 1-2 bit collision rule replaced the foreground's
                // index, the foreground is that index's color.
                var colors = c;
                if (d.Depth <= 2 && d.Color2Index(c.Fore16.r, c.Fore16.g, c.Fore16.b) != c.FgIndex)
                {
                    var e = d.Index2Color16(c.FgIndex);
                    colors = new PortColors(d.ColorOf(c.FgIndex), c.Back, c.Op, c.Hilite, c.MacOS9, null,
                        ((ushort)e.r, (ushort)e.g, (ushort)e.b), c.Back16);
                }
                var (r, g, b) = exact ?? (s.R * 257, s.G * 257, s.B * 257);
                if ((m & 4) != 0) (r, g, b) = (0xFFFF - r, 0xFFFF - g, 0xFFFF - b);
                var f = colors.Fore16;
                var k = colors.Back16;
                result = d.Color2Index((r & k.r) | (~r & f.r & 0xFFFF), (g & k.g) | (~g & f.g & 0xFFFF), (b & k.b) | (~b & f.b & 0xFFFF));
                return true;
            }
            int si = exact is var (er, eg, eb) ? d.Color2Index(er, eg, eb) : d.Color2Index(s);
            if ((m & 4) != 0) si ^= d.Mask;
            result = (m & 3) switch
            {
                1 => (si & c.FgIndex) | (~si & dst),
                2 => dst ^ si,
                _ => (si & c.BkIndex) | (~si & dst),
            };
            result &= d.Mask;
            return true;
        }

        // A pixel pattern's value (already a device value): drawn with fg all ones and bk 0 — copy p, or d | p,
        // xor d ^ p, bic d & ~p (p inverted for the not modes).
        public static bool PatternValue(int m, int p, PictColor rgb, int dst, in PortColors c, out int result)
        {
            var d = c.Device!;
            m = ForDevice(m, d);
            if (m >= TransferModes.Blend) return Colored(m, rgb, p, dst, c, out result);
            if ((m & 4) != 0) p ^= d.Mask;
            result = (m & 3) switch
            {
                0 => p,
                1 => dst | p,
                2 => dst ^ p,
                _ => dst & ~p,
            } & d.Mask;
            return true;
        }

        // Arithmetic, transparent and hilite with a source color s (value si).
        private static bool Colored(int m, PictColor s, int si, int dst, in PortColors c, out int result)
        {
            var d = c.Device!;
            switch (m)
            {
                case TransferModes.Transparent:
                    result = si;
                    return si != c.BkIndex;
                case TransferModes.Hilite:
                    result = dst;
                    if (si == c.BkIndex) return false;
                    if (dst == c.BkIndex) { result = c.HiliteIndex; return true; }
                    if (dst == c.HiliteIndex) { result = c.BkIndex; return true; }
                    return false;
            }
            if (d.Depth == 16)
            {
                result = Arithmetic16(m, s, dst, c);
                return true;
            }
            TransferModes.ApplyColor(m, s, d.ColorOf(dst), c, out var rgb);
            result = d.TableLookup(rgb);
            return true;
        }

        // Mac OS 9's colorizing on a 16-bit screen, on 5-bit fields: copy ((32 - s) F + (s + 1) B) >> 5 (direct
        // sources only), or / bic ((32 - s) C + (s + 1) d) >> 5; null where no colorizing applies (as ColorizeBlend).
        private static int? Colorize16(int m, PictColor src, int dst, in PortColors c, bool direct)
        {
            if (TransferModes.ColorizeBlend(m, src, default, c, direct) == null) return null;
            int op = m & 3;
            bool not = (m & 4) != 0;
            int Field(int s, int f, int b, int dd)
            {
                if (op == 0) return not ? ((32 - s) * b + (s + 1) * f) >> 5 : ((32 - s) * f + (s + 1) * b) >> 5;
                if (not) s = 31 - s;
                return ((32 - s) * (op == 1 ? f : b) + (s + 1) * dd) >> 5;
            }
            return (Field(src.R >> 3, c.Fore.R >> 3, c.Back.R >> 3, (dst >> 10) & 31) << 10)
                | (Field(src.G >> 3, c.Fore.G >> 3, c.Back.G >> 3, (dst >> 5) & 31) << 5)
                | Field(src.B >> 3, c.Fore.B >> 3, c.Back.B >> 3, dst & 31);
        }

        // 5-bit fields: blend (s w + d (65536 - w) + $8000) >> 16 (the average when all weights are $7F80..$807F), pins at the
        // OpColor's top 5 bits, addOver / subOver modulo 32, addMax / adMin.
        private static int Arithmetic16(int m, PictColor src, int dst, in PortColors c)
        {
            static bool Half(int w) => w >= 0x7F80 && w <= 0x807F;
            bool half = Half(Math.Max(1, (int)c.Op.r)) && Half(Math.Max(1, (int)c.Op.g)) && Half(Math.Max(1, (int)c.Op.b));
            int Field(int s, int dd, int w)
            {
                w = Math.Max(1, w);
                int pin = w >> 11;
                return m switch
                {
                    TransferModes.Blend => half ? (s + dd) >> 1 : (int)(((long)s * w + (long)dd * (65536 - w) + 0x8000) >> 16),
                    TransferModes.AddPin => Math.Min(s + dd, pin),
                    TransferModes.AddOver => (s + dd) & 31,
                    TransferModes.SubPin => Math.Max(dd - s, pin),
                    TransferModes.SubOver => (dd - s) & 31,
                    TransferModes.AddMax => Math.Max(s, dd),
                    TransferModes.AdMin => Math.Min(s, dd),
                    _ => s,
                };
            }
            return (Field(src.R >> 3, (dst >> 10) & 31, c.Op.r) << 10) | (Field(src.G >> 3, (dst >> 5) & 31, c.Op.g) << 5)
                | Field(src.B >> 3, dst & 31, c.Op.b);
        }

        // ---- ditherCopy. Mac OS 9 error diffusion: the rows of the visible bounds, serpentine from a left-to-right
        // first row, the error buffer and carry reset per call; v = clamp(c + carry + buf[x]); the error e = v - (the
        // chosen entry's 8-bit color) goes floor(e/2) to the next pixel and the rest (a signed byte) to the next row;
        // clipped pixels reset their buffer entry and the carry. 16 bits: the error is the low 3 bits. Grey tables
        // dither the luminance (5R + 9G + 2B) >> 4 against the entry's blue byte; 1 bit: black below 128.
        // The ROM: rows from the first visible one but always the destination rect's full width (clipped pixels are
        // converted, not drawn); the error goes floor(e/2) to the next row and ceil(e/2) to the next pixel, unwrapped;
        // grey tables dither ((R + G + 2B)/4 + R + 2G)/4 against the entry's red; 16 bits dither ordered:
        // min(c + D[row & 3][x & 3], 255) >> 3 with rows counted from the first visible one and x from the rect's left.
        private static readonly int[,] Ordered = { { 0, 5, 1, 4 }, { 6, 3, 7, 2 }, { 1, 4, 0, 5 }, { 7, 2, 6, 3 } };

        public static void Dither(PictBitmap canvas, ScreenDevice d, int top, int bottom, int left, int right,
            Func<int, int, PictColor?> source, Func<int, int, bool> visible)
        {
            int w = right - left;
            if (w <= 0) return;
            bool rom = !d.MacOS9;
            var err = new int[w, 3];
            bool leftToRight = true;
            for (int y = top; y < bottom; y++, leftToRight = !leftToRight)
            {
                int cr = 0, cg = 0, cb = 0;
                for (int k = 0; k < w; k++)
                {
                    int i = leftToRight ? k : w - 1 - k, x = left + i;
                    bool shown = visible(x, y);
                    if ((!shown && !rom) || source(x, y) is not PictColor c)
                    {
                        err[i, 0] = err[i, 1] = err[i, 2] = 0;
                        cr = cg = cb = 0;
                        continue;
                    }
                    if (rom && d.Depth == 16)
                    {
                        int dd = Ordered[(y - top) & 3, i & 3];
                        if (shown)
                            d.Write(canvas, x, y, ((Math.Min(c.R + dd, 255) >> 3) << 10) | ((Math.Min(c.G + dd, 255) >> 3) << 5)
                                | (Math.Min(c.B + dd, 255) >> 3));
                        continue;
                    }
                    if (d.Grey)
                    {
                        int lum = rom ? (((c.R + c.G + 2 * c.B) >> 2) + c.R + 2 * c.G) >> 2 : (5 * c.R + 9 * c.G + 2 * c.B) >> 4;
                        int v = Math.Clamp(lum + err[i, 0] + cr, 0, 255);
                        int index;
                        int e;
                        if (d.Depth == 1 && !rom)
                        {
                            index = v < 128 ? 1 : 0;
                            e = v < 128 ? v : v - 255;
                        }
                        else if (rom)
                        {
                            index = GreyIndex(d, v);
                            e = v - d.ColorOf(index).R;
                            err[i, 0] = e >> 1;
                            cr = (e >> 1) + (e & 1);
                            if (shown) d.Write(canvas, x, y, index);
                            continue;
                        }
                        else
                        {

                            index = GreyIndex(d, v);
                            e = v - d.ColorOf(index).B;
                        }
                        cr = e >> 1;
                        err[i, 0] = e - (e >> 1);
                        if (shown) d.Write(canvas, x, y, index);
                        continue;
                    }
                    int vr = Math.Clamp(c.R + cr + err[i, 0], 0, 255), vg = Math.Clamp(c.G + cg + err[i, 1], 0, 255),
                        vb = Math.Clamp(c.B + cb + err[i, 2], 0, 255);
                    var v3 = new PictColor((byte)vr, (byte)vg, (byte)vb);
                    int value = d.Lookup(v3);
                    int ar, ag, ab;
                    if (d.Depth == 16) (ar, ag, ab) = (vr & 0xF8, vg & 0xF8, vb & 0xF8);
                    else
                    {
                        var a = d.ColorOf(value);
                        (ar, ag, ab) = (a.R, a.G, a.B);
                    }
                    int er = vr - ar, eg = vg - ag, eb = vb - ab;
                    if (rom)
                    {
                        err[i, 0] = er >> 1; err[i, 1] = eg >> 1; err[i, 2] = eb >> 1;
                        cr = (er >> 1) + (er & 1); cg = (eg >> 1) + (eg & 1); cb = (eb >> 1) + (eb & 1);
                    }
                    else
                    {
                        cr = er >> 1; cg = eg >> 1; cb = eb >> 1;
                        err[i, 0] = (sbyte)(er - (er >> 1)); err[i, 1] = (sbyte)(eg - (eg >> 1)); err[i, 2] = (sbyte)(eb - (eb >> 1));
                    }
                    if (shown) d.Write(canvas, x, y, value);
                }
            }
        }

        private static int GreyIndex(ScreenDevice d, int luminance) =>
            d.Lookup(new PictColor((byte)luminance, (byte)luminance, (byte)luminance));
    }
}
