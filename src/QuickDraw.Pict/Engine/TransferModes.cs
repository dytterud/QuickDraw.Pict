using System;

namespace QuickDraw.Pict
{
    // The foreground/background/op/hilite colors a transfer mode draws with.
    internal readonly struct PortColors
    {
        public PortColors(PictColor fore, PictColor back, (ushort r, ushort g, ushort b) op, PictColor hilite)
        {
            Fore = fore; Back = back; Op = op; Hilite = hilite;
        }
        public readonly PictColor Fore, Back, Hilite;
        public readonly (ushort r, ushort g, ushort b) Op;
    }

    // QuickDraw transfer modes on a 32-bit direct destination, per Inside Macintosh: Imaging With QuickDraw:
    // PenMode (pattern modes use the foreground color for 1 bits and the background color for 0 bits), Table 4-1
    // (Boolean modes with colored pixels), "Arithmetic Transfer Modes" and "Highlighting"; arithmetic formulas and the
    // hilite swap follow Executor qIMVxfer.cpp (MIT). Each function returns false to leave the pixel untouched.
    internal static class TransferModes
    {
        public const int SrcCopy = 0, SrcOr = 1, SrcXor = 2, SrcBic = 3;
        public const int PatCopy = 8, PatXor = 10;
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
            bool average = mode == Blend && wr == wg && wg == wb && ((wr + 1) & ~1) == 0x8000;
            result = new PictColor(
                Arithmetic(mode, src.R, dst.R, wr, average),
                Arithmetic(mode, src.G, dst.G, wg, average),
                Arithmetic(mode, src.B, dst.B, wb, average));
            return true;
        }

        private static byte Arithmetic(int mode, int s, int d, int w, bool average)
        {
            int pin = w >> 8;
            switch (mode)
            {
                case Blend: return (byte)(average ? (s + d) >> 1 : (int)(((long)s * w + (long)d * (65536 - w)) >> 16));
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

        private static int Rgb(PictColor c) => (c.R << 16) | (c.G << 8) | c.B;

        public static PictColor Invert(PictColor c) => new PictColor((byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B));

        public static bool SameRgb(PictColor a, PictColor b) => a.R == b.R && a.G == b.G && a.B == b.B;
    }
}
