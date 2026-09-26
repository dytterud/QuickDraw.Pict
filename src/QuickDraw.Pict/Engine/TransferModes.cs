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
        // for 1-bit data). Components are Color QuickDraw's 16-bit values (8-bit x 257), truncated back to 8 bits.
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
            result = new PictColor(
                Arithmetic(mode, src.R, dst.R, c.Op.r),
                Arithmetic(mode, src.G, dst.G, c.Op.g),
                Arithmetic(mode, src.B, dst.B, c.Op.b));
            return true;
        }

        private static byte Arithmetic(int mode, byte src8, byte dst8, ushort op)
        {
            int s = src8 * 257, d = dst8 * 257, r;
            switch (mode)
            {
                case Blend: r = (int)(((long)s * op + (long)d * (65535 - op)) / 65535); break;
                case AddPin: r = Math.Min(s + d, op); break;
                case AddOver: r = (s + d) & 0xFFFF; break;
                case SubPin: r = Math.Max(s - d, op); break;
                case SubOver: r = (s - d) & 0xFFFF; break;
                case AddMax: r = Math.Max(s, d); break;
                case AdMin: r = Math.Min(s, d); break;
                default: r = s; break;
            }
            return (byte)(r >> 8);
        }

        public static PictColor Invert(PictColor c) => new PictColor((byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B));

        public static bool SameRgb(PictColor a, PictColor b) => a.R == b.R && a.G == b.G && a.B == b.B;
    }
}
