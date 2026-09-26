namespace QuickDraw.Pict
{
    // Toolbox fixed-point math (16.16 Fixed), ported from Executor toolutil.cpp (MIT) so arc end points land on the
    // same pixels as QuickDraw's.
    internal static class FixedMath
    {
        // FixMul: sign-magnitude 16.16 multiply that saturates instead of wrapping.
        public static int FixMul(int a, int b)
        {
            int sign = 1;
            if (a < 0) { a = -a; sign = -sign; }
            if (b < 0) { b = -b; sign = -sign; }
            uint ms1 = (uint)a >> 16, ms2 = (uint)b >> 16, ls1 = (uint)a & 0xFFFF, ls2 = (uint)b & 0xFFFF;
            uint mm = ms1 * ms2;
            if (mm >= 1u << 15) return sign == 1 ? 0x7FFFFFFF : unchecked((int)0x80000000);
            uint ml = ms1 * ls2, lm = ls1 * ms2, ll = ls1 * ls2;
            mm <<= 16;
            mm += ml + lm + (ll >> 16);
            if ((mm & 0x80000000) != 0) return sign == 1 ? 0x7FFFFFFF : unchecked((int)0x80000000);
            return unchecked(sign * (int)mm);
        }

        // Slopes for 90..135 degrees, as QuickDraw's SlopeFromAngle table.
        private static readonly int[] SlopeTable =
        {
            unchecked((int)0x80000001), 0x00394a30, 0x001ca2d7, 0x001314bd, 0x000e4cf5,
            0x000b6e17, 0x000983ad, 0x000824f3, 0x00071d88, 0x00065051,
            0x0005abd9, 0x00052501, 0x00046462, 0x000454db, 0x000402c2,
            0x0003bb68, 0x00037cc7, 0x00034556, 0x000313e3, 0x0002e77a,
            0x0002bf5b, 0x00029ae7, 0x000279af, 0x00025b19, 0x00023efc,
            0x000224fe, 0x00020ce1, 0x0001f66e, 0x0001e177, 0x0001cdd6,
            0x0001bb68, 0x0001aa0e, 0x000199af, 0x00018a35, 0x00017689,
            0x00016dab, 0x0001605b, 0x000153b9, 0x000147aa, 0x00013c22,
            0x00013117, 0x0001267f, 0x00011c51, 0x00011287, 0x00010919,
            0x00010000,
        };

        private static int Invert(int f) => (int)(((0x80000000u / (uint)f) << 1) & 0x7FFFFFFF);

        // SlopeFromAngle: the slope (dh/dv, Fixed) of a line at a QuickDraw angle (0 = up, clockwise, degrees).
        public static int SlopeFromAngle(int a)
        {
            if (a < 0) a -= 360 * ((a / 360) - 1);
            if (a > 360) a %= 360;
            if (a > 180) a -= 180;
            int sign;
            if (a < 90) { sign = -1; a = 180 - a; }
            else sign = 1;
            bool reciprocal = a > 135;
            if (reciprocal) a = 270 - a;
            int value = SlopeTable[a - 90];
            if (reciprocal) value = Invert(value);
            return sign == -1 ? -value : value;
        }
    }
}
