namespace QuickDraw.Pict
{
    // Toolbox fixed-point math (16.16 Fixed) with the Macintosh ROM's exact rounding and saturation, so scan-converted
    // edges land on the same pixels as QuickDraw's.
    internal static class FixedMath
    {
        // FixMul: the 64-bit product shifted right 16, rounded to nearest with ties away from zero, saturating to
        // ±0x7FFFFFFF past 2^47. A factor of exactly 1.0 returns the other factor unchanged.
        public static int FixMul(int a, int b)
        {
            if (b == 0x10000) return a;
            if (a == 0x10000) return b;
            long product = (long)a * b;
            if (product >= 1L << 47 || product < -(1L << 47))
                return (a < 0) != (b < 0) ? int.MinValue : int.MaxValue;
            long result = product >> 16;
            bool half = (product & 0x8000) != 0;
            if (half && (result >= 0 || (product & 0x7FFF) != 0)) result++;
            return unchecked((int)result);
        }

        // Mac OS 9's internal fixed multiply: the product + 0x8000, shifted right 16 (halves toward +infinity),
        // saturating to the int range.
        public static int FixMulHalfUp(int a, int b)
        {
            long r = ((long)a * b + 0x8000) >> 16;
            return r > int.MaxValue ? int.MaxValue : r < int.MinValue ? int.MinValue : (int)r;
        }

        // FixRatio: numer / denom of two integers as a truncated Fixed; a zero denominator saturates by the
        // numerator's sign.
        public static int FixRatio(short numer, short denom)
        {
            if (denom == 0) return numer < 0 ? unchecked((int)0x80000001) : 0x7FFFFFFF;
            if (numer == denom) return 0x10000;
            if (numer == short.MinValue && denom == -1) return int.MinValue;
            return (numer << 16) / denom;
        }

        // FixRound: + 0x8000 (x >= 0) or + 0x7FFF (x < 0), then the high word - halves away from zero; saturates at
        // 32767.
        public static int FixRound(int x) =>
            x >= 0x7FFF8000 ? short.MaxValue : (short)((x + (x >= 0 ? 0x8000 : 0x7FFF)) >> 16);

        // tan(a) as Fixed for a = 0..90 degrees, the values QuickDraw's arc code uses (not exactly rounded tangents).
        private static readonly int[] Tangent =
        {
            0x00000000, 0x00000478, 0x000008F1, 0x00000D6B, 0x000011E7, 0x00001666,
            0x00001AE8, 0x00001F6F, 0x000023FA, 0x0000288C, 0x00002D24, 0x000031C3,
            0x0000366A, 0x00003B1A, 0x00003FD4, 0x00004498, 0x00004968, 0x00004E44,
            0x0000532E, 0x00005826, 0x00005D2D, 0x00006245, 0x0000676E, 0x00006CAA,
            0x000071FB, 0x00007760, 0x00007CDC, 0x00008270, 0x0000881E, 0x00008DE7,
            0x000093CD, 0x000099D2, 0x00009FF7, 0x0000A640, 0x0000ACAD, 0x0000B341,
            0x0000B9FF, 0x0000C0E9, 0x0000C802, 0x0000CF4E, 0x0000D6CF, 0x0000DE8A,
            0x0000E681, 0x0000EEB9, 0x0000F737, 0x00010000, 0x00010919, 0x00011287,
            0x00011C51, 0x0001267F, 0x00013117, 0x00013C22, 0x000147AA, 0x000153B9,
            0x0001605B, 0x00016D9B, 0x00017B89, 0x00018A35, 0x000199AF, 0x0001AA0E,
            0x0001BB68, 0x0001CDD6, 0x0001E177, 0x0001F66E, 0x00020CE1, 0x000224FE,
            0x00023EFC, 0x00025B19, 0x0002799F, 0x00029AE7, 0x0002BF5B, 0x0002E77A,
            0x000313E3, 0x00034556, 0x00037CC7, 0x0003BB68, 0x000402C2, 0x000454DB,
            0x0004B462, 0x00052501, 0x0005ABD9, 0x00065051, 0x00071D88, 0x000824F3,
            0x000983AD, 0x000B6E17, 0x000E4CF5, 0x001314BD, 0x001CA2D7, 0x00394A30,
            0x7FFFFFFF,
        };

        // SlopeFromAngle: dh/dv of the ray at a QuickDraw angle (0 = up, clockwise, degrees), i.e. -tan(angle),
        // taken modulo 180.
        public static int SlopeFromAngle(int angle)
        {
            int a = (short)angle % 180;
            if (a < 0) a += 180;
            return a > 90 ? Tangent[180 - a] : -Tangent[a];
        }
    }
}
