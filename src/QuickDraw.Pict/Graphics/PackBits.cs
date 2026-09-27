using System;

namespace QuickDraw.Pict
{
    internal static class PackBits
    {
        // Standard PackBits (flag n >= 0: copy n + 1 bytes; n < 0: repeat the next byte 1 - n times; -128: no-op).
        public static int Unpack(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int ip = 0, op = 0;
            while (ip < src.Length && op < dst.Length)
            {
                sbyte flag = (sbyte)src[ip++];
                if (flag == -128) continue;
                if (flag < 0)
                {
                    if (ip >= src.Length) break;
                    byte b = src[ip++];
                    for (int i = 0; i < 1 - flag && op < dst.Length; i++) dst[op++] = b;
                }
                else
                    for (int i = 0; i <= flag && ip < src.Length && op < dst.Length; i++) dst[op++] = src[ip++];
            }
            return ip;
        }
    }
}
