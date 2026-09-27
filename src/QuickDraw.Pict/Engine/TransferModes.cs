using System;

namespace QuickDraw.Pict
{
    // The foreground/background/op/hilite colors a transfer mode draws with.
    // (and which QuickDraw's rounding rules the blitters follow).
    internal readonly struct PortColors
    {
        public PortColors(PictColor fore, PictColor back, (ushort r, ushort g, ushort b) op, PictColor hilite, bool macOS9,
            ScreenDevice? device = null, (ushort r, ushort g, ushort b)? fore16 = null, (ushort r, ushort g, ushort b)? back16 = null)
        {
            Fore = fore; Back = back; Op = op; Hilite = hilite; MacOS9 = macOS9; Device = device;
            Fore16 = fore16 ?? ((ushort)(fore.R * 257), (ushort)(fore.G * 257), (ushort)(fore.B * 257));
            Back16 = back16 ?? ((ushort)(back.R * 257), (ushort)(back.G * 257), (ushort)(back.B * 257));
            FgIndex = BkIndex = HiliteIndex = 0;
            if (device == null) return;
            // The port's indices (Color2Index when the colors are set). On 1- and 2-bit screens a foreground that maps
            // to the background's index although the colors differ takes its inverse's index instead.
            FgIndex = device.Color2Index(Fore16.r, Fore16.g, Fore16.b);
            BkIndex = device.Color2Index(Back16.r, Back16.g, Back16.b);
            if (device.Depth <= 2 && FgIndex == BkIndex && Fore16 != Back16)
                FgIndex = device.Color2Index(0xFFFF - Fore16.r, 0xFFFF - Fore16.g, 0xFFFF - Fore16.b);
            HiliteIndex = device.Color2Index(hilite);
            if (HiliteIndex == BkIndex) HiliteIndex = device.Color2Index(TransferModes.Invert(hilite));
        }
        public readonly PictColor Fore, Back, Hilite;
        public readonly (ushort r, ushort g, ushort b) Op;
        public readonly bool MacOS9;
        public readonly ScreenDevice? Device;                 // null: the 32-bit canvas
        public readonly (ushort r, ushort g, ushort b) Fore16, Back16;   // the exact fore and back colors
        public readonly int FgIndex, BkIndex, HiliteIndex;    // on the device
    }

    // QuickDraw transfer modes on a 32-bit direct destination, per Inside Macintosh: Imaging With QuickDraw:
    // PenMode (pattern modes use the foreground color for 1 bits and the background color for 0 bits), Table 4-1
    // (Boolean modes with colored pixels), "Arithmetic Transfer Modes" and "Highlighting"; arithmetic formulas and the
    // hilite swap follow Executor qIMVxfer.cpp (MIT). Each function returns false to leave the pixel untouched.
    internal static class TransferModes
    {
        public const int SrcCopy = 0, SrcOr = 1, SrcXor = 2, SrcBic = 3;
        public const int PatCopy = 8, PatXor = 10, PatBic = 11;
        public const int Blend = 32, AddPin = 33, AddOver = 34, SubPin = 35, Transparent = 36, AddMax = 37, SubOver = 38, AdMin = 39;
        public const int GrayishTextOr = 49, Hilite = 50, DitherCopy = 64;

        // Reduces a mode to one this engine dispatches on: Boolean modes to 0..7 (source and pattern variants mean the
        // same for 1-bit data), arithmetic modes to 32..39 (a "+ patCopy" pattern variant is the same mode), or Hilite.
        public static int Normalize(int mode, bool hilitePending)
        {
            mode &= ~DitherCopy;
            if (mode >= Hilite) return Hilite;
            if (mode == GrayishTextOr) return SrcOr;
            if (mode >= Blend && mode < Blend + 16) return mode & ~8;
            int boolean = mode & 7;
            if (hilitePending && boolean == SrcXor) return Hilite;
            return boolean;
        }

        public static bool IsArithmetic(int normalizedMode) => normalizedMode >= Blend && normalizedMode <= AdMin;

        // A 1-bit source or pattern pixel (bit = black/on) under a normalized mode. For Boolean modes the "not"
        // variants swap which bit value acts; arithmetic, transparent and hilite modes use the colorized pixel.
        public static bool ApplyBit(int mode, bool bit, PictColor dst, in PortColors c, out PictColor result)
        {
            if (mode >= Blend)
                return ApplyColor(mode, bit ? c.Fore : c.Back, dst, c, out result);
            bool on = (mode & 4) != 0 ? !bit : bit;
            switch (mode & 3)
            {
                case 0:                                         // copy (notCopy: white bits take the foreground)
                    result = on ? c.Fore : c.Back;
                    return true;
                case 1:                                         // or: force the foreground color
                    result = c.Fore;
                    return on;
                case 2:                                         // xor: invert
                    result = Invert(dst);
                    return on;
                default:                                        // bic: force the background color
                    result = c.Back;
                    return on;
            }
        }

        // A full-color source pixel under an arithmetic, transparent or hilite mode (the colorized pattern pixel
        // for 1-bit data), as the ROM's 32-bit loops compute them per 8-bit component: blend
        // (s * w + d * (65536 - w)) >> 16 truncating, with the exact average (s + d) >> 1 when all three weights are
        // $7FFF or $8000; addPin / subPin (d - s) pinned to the OpColor's high byte on overflow or past it;
        // addOver / subOver modulo 256; addMax / adMin. A zero OpColor component counts as 1.
        public static bool ApplyColor(int mode, PictColor src, PictColor dst, in PortColors c, out PictColor result)
        {
            switch (mode)
            {
                case Transparent:
                    result = src;
                    return !SameRgb(src, c.Back);
                case Hilite:
                    result = dst;
                    if (SameRgb(src, c.Back)) return false;
                    if (SameRgb(dst, c.Back)) { result = c.Hilite; return true; }
                    if (SameRgb(dst, c.Hilite)) { result = c.Back; return true; }
                    return false;
            }
            int wr = Math.Max(1, (int)c.Op.r), wg = Math.Max(1, (int)c.Op.g), wb = Math.Max(1, (int)c.Op.b);
            // Mac OS 9 blends rounded, and averages whenever every weight is within $7F80..$807F.
            bool average = mode == Blend && (c.MacOS9
                ? Half(wr) && Half(wg) && Half(wb)
                : wr == wg && wg == wb && ((wr + 1) & ~1) == 0x8000);
            result = new PictColor(
                Arithmetic(mode, src.R, dst.R, wr, average, c.MacOS9),
                Arithmetic(mode, src.G, dst.G, wg, average, c.MacOS9),
                Arithmetic(mode, src.B, dst.B, wb, average, c.MacOS9));
            return true;
        }

        private static bool Half(int w) => w >= 0x7F80 && w <= 0x807F;

        private static byte Arithmetic(int mode, int s, int d, int w, bool average, bool macOS9)
        {
            int pin = w >> 8;
            switch (mode)
            {
                case Blend: return (byte)(average ? (s + d) >> 1
                    : (int)(((long)s * w + (long)d * (65536 - w) + (macOS9 ? 0x8000 : 0)) >> 16));
                case AddPin: { int r = s + d; return (byte)(r > 255 || r > pin ? pin : r); }
                case AddOver: return (byte)(s + d);
                case SubPin: { int r = d - s; return (byte)(r < 0 || r < pin ? pin : r); }
                case SubOver: return (byte)(d - s);
                case AddMax: return (byte)Math.Max(s, d);
                case AdMin: return (byte)Math.Min(s, d);
                default: return (byte)s;
            }
        }

        // A full-color source pixel (or pixel pattern) under a Boolean mode on a 32-bit destination, bitwise on the
        // RGB values as the ROM's loops compute them (fore/back colors F and B as pixel values; the direct
        // destination works on inverted values, so with the default black/white colors srcOr is an AND):
        //   srcCopy (s & B) | (~s & F)     srcOr (~s & F) | (s & d)     srcXor d ^ ~s     srcBic (~s & B) | (s & d)
        //   notSrcCopy (~s & B) | (s & F)  notSrcOr (s & F) | (~s & d)  notSrcXor d ^ s   notSrcBic (s & B) | (~s & d)
        public static PictColor ApplyBoolean(int mode, PictColor src, PictColor dst, in PortColors c)
        {
            int s = Rgb(src), d = Rgb(dst), f = Rgb(c.Fore), b = Rgb(c.Back), r;
            switch (mode & 7)
            {
                case 0: r = (s & b) | (~s & f); break;
                case 1: r = (~s & f) | (s & d); break;
                case 2: r = d ^ ~s; break;
                case 3: r = (~s & b) | (s & d); break;
                case 4: r = (~s & b) | (s & f); break;
                case 5: r = (s & f) | (~s & d); break;
                case 6: r = d ^ s; break;
                default: r = (s & b) | (~s & d); break;
            }
            return new PictColor((byte)(r >> 16), (byte)(r >> 8), (byte)r);
        }

        // Mac OS 9's colorizing blend, or null where it does not apply. Colorizing happens for copy modes when fore is not
        // black or back not white, for or modes when fore is not black, for bic modes when back is not white (never
        // for xor). An indexed srcCopy / notSrcCopy stays bitwise (ApplyBoolean); every other colorizing case blends
        // each channel s with weights 256 - s and s + 1:
        //   copy (direct sources) ((256 - s) F + (s + 1) B) >> 8        notCopy ((256 - s) B + (s + 1) F) >> 8
        //   or / bic              ((256 - s) C + (s + 1) d) >> 8        with C = F (or) or B (bic), s = 255 - s for not
        public static PictColor? ColorizeBlend(int mode, PictColor src, PictColor dst, in PortColors c, bool directSource)
        {
            int op = mode & 3;
            bool not = (mode & 4) != 0;
            bool foreBlack = SameRgb(c.Fore, new PictColor(0, 0, 0)), backWhite = SameRgb(c.Back, new PictColor(255, 255, 255));
            bool colorize = op switch { 0 => !foreBlack || !backWhite, 1 => !foreBlack, 3 => !backWhite, _ => false };
            if (!colorize || (op == 0 && !directSource)) return null;
            var fore = c.Fore;
            var back = c.Back;
            byte Channel(int s, int f, int b, int d)
            {
                if (op == 0) return (byte)(not ? ((256 - s) * b + (s + 1) * f) >> 8 : ((256 - s) * f + (s + 1) * b) >> 8);
                int color = op == 1 ? f : b;
                if (not) s = 255 - s;
                return (byte)(((256 - s) * color + (s + 1) * d) >> 8);
            }
            return new PictColor(Channel(src.R, fore.R, back.R, dst.R), Channel(src.G, fore.G, back.G, dst.G),
                Channel(src.B, fore.B, back.B, dst.B));
        }

        private static int Rgb(PictColor c) => (c.R << 16) | (c.G << 8) | c.B;

        public static PictColor Invert(PictColor c) => new PictColor((byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B));

        public static bool SameRgb(PictColor a, PictColor b) => a.R == b.R && a.G == b.G && a.B == b.B;
    }
}
