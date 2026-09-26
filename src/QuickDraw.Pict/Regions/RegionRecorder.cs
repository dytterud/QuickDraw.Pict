using System;
using System.Collections.Generic;

namespace QuickDraw.Pict
{
    // An open region (OpenRgn ... CloseRgn): lines drawn while it is open XOR their boundary into it as inversion
    // points. Horizontal lines contribute a row, vertical lines nothing, and other lines a one-pixel step per scan
    // line where their x changes. Ported from the region-save branch of Executor's C_StdLine and its scr* scan
    // converters (qStdLine.cpp, MIT); closing a polygon of such lines yields QuickDraw's polygon interior.
    internal sealed class RegionRecorder
    {
        private readonly List<(int y, List<int> xs)> rows = new List<(int y, List<int> xs)>();
        private int penH, penV;

        public void MoveTo(int h, int v) { penH = h; penV = v; }

        public void LineTo(int h, int v)
        {
            Line(penH, penV, h, v);
            penH = h;
            penV = v;
        }

        public Region Close() => Region.FromInversionRows(rows);

        private void Row(int y, int xa, int xb) => rows.Add((y, new List<int> { xa, xb }));

        private void Line(int x1, int y1, int x2, int y2)
        {
            if (x1 == x2 || y1 == y2)
            {
                if (x2 < x1) (x1, x2) = (x2, x1);
                if (y1 == y2 && x1 != x2) Row(y1, x1, x2);
                return;
            }
            if (y1 > y2)
            {
                (y1, y2) = (y2, y1);
                (x1, x2) = (x2, x1);
            }
            int dy = y2 - y1, dx = Math.Abs(x2 - x1);
            if (dy >= dx)
            {
                if (x2 > x1) StepsDyDxRight(y1, x1, dy, dx);
                else StepsDyDxLeft(y1, x1, dy, dx);
            }
            else if (x2 > x1) StepsDxDyRight(y1, x1, dy, dx);
            else StepsDxDyLeft(y1, x1 + 1, dy, dx);
        }

        // scrdydxx1x2: steep, x decreasing.
        private void StepsDyDxLeft(long y1, int x1, int dy, int dx)
        {
            int x2 = x1 - dx;
            if (dy > dx)
            {
                long incr = ((long)dy << 16) / (dx + 1) + 1;
                y1 = (y1 << 16) | (1L << 15);
                while (x1 != x2)
                {
                    y1 += incr;
                    Row((int)(y1 >> 16), x1 - 1, x1);
                    --x1;
                }
            }
            else
            {
                while (x1 != x2)
                {
                    Row((int)++y1, x1 - 1, x1);
                    --x1;
                }
            }
        }

        // scrdydxx2x1: steep, x increasing.
        private void StepsDyDxRight(long y1, int x1, int dy, int dx)
        {
            int x2 = x1 + dx;
            if (dy > dx)
            {
                long incr = ((long)dy << 16) / (dx + 1) + 1;
                y1 = (y1 << 16) | (1L << 15);
                while (x1 != x2)
                {
                    y1 += incr;
                    Row((int)(y1 >> 16), x1, x1 + 1);
                    ++x1;
                }
            }
            else
            {
                while (x1 != x2)
                {
                    Row((int)++y1, x1, x1 + 1);
                    ++x1;
                }
            }
        }

        // scrdxdyx1x2: shallow, x decreasing (called with x1 + 1). The last row's left point snaps to x2 - 1.
        private void StepsDxDyLeft(int y1, int x1Start, int dy, int dx)
        {
            int x2 = x1Start - dx, y2 = y1 + dy;
            long incr = ((long)dx << 16) / (dy + 1) + 1;
            int ox = x1Start - 1;
            long x1 = (((long)x1Start << 16) | (1L << 15)) - 1;
            x1 -= incr;
            if ((int)(x1 >> 16) < ox)
            {
                Row(y1++, (int)(x1 >> 16), ox);
                ox = (int)(x1 >> 16);
            }
            else
                y1++;
            while (y1 <= y2)
            {
                x1 -= incr;
                Row(y1++, (int)(x1 >> 16), ox);
                ox = (int)(x1 >> 16);
            }
            if (rows.Count > 0) rows[^1].xs[0] = x2 - 1;
        }

        // scrdxdyx2x1: shallow, x increasing.
        private void StepsDxDyRight(int y1, int x1Start, int dy, int dx)
        {
            int y2 = y1 + dy;
            long incr = ((long)dx << 16) / (dy + 1) + 1;
            int ox = x1Start;
            long x1 = ((long)x1Start << 16) | (1L << 15);
            while (y1 <= y2)
            {
                x1 += incr;
                int nx = (int)(x1 >> 16);
                Row(y1++, ox, nx);
                ox = nx;
            }
        }
    }
}
