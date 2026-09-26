using System;
using System.Collections.Generic;

namespace QuickDraw.Pict
{
    // QuickDraw's shape rasterizers as regions, ported from Executor (MIT): ROMlib_circrgn + bresenham (qStdOval.cpp),
    // roundRectRgn (qStdRRect.cpp), C_StdArc (qStdArc.cpp), polyrgn (qStdPoly.cpp), C_StdLine (qStdLine.cpp), and the
    // frame verbs of C_StdRect / C_StdOval / C_StdRRect / C_StdRgn. Frames lie inside the shape: shape minus the shape
    // inset by the pen size. Rects are (top, left, bottom, right) with exclusive right/bottom; points are (h, v).
    internal static class RegionShapes
    {
        public static Region Rect(PictRect r) => Region.FromRect(r);

        // StdOval: ovals under 4x4 are drawn as rects.
        public static Region Oval(PictRect r)
        {
            if (r.IsEmpty) return Region.Empty;
            if (r.Height < 4 && r.Width < 4) return Region.FromRect(r);
            return CircleRegion(r);
        }

        // ROMlib_circrgn: inversion rows from a Bresenham quarter-ellipse, mirrored. Under 3 pixels in either
        // dimension it is the bounding rect.
        private static Region CircleRegion(PictRect rect) => Region.FromQuickDrawData(rect, CircleData(rect).ToArray());

        private static List<short> CircleData(PictRect rect)
        {
            var rgn = new List<short>();
            long width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width < 3 || height < 3) return rgn;

            var xsteps = Bresenham(width, height);
            var ysteps = Bresenham(height, width);
            int centl = rect.Left + (int)(width / 2), centr = rect.Right - (int)(width / 2);
            int centt = rect.Top + (int)(height / 2), centb = rect.Bottom - (int)(height / 2);
            int ox = -1;

            void PointUpper(int x, int y)
            {
                if (x == ox) return;
                rgn.Add((short)y);
                rgn.Add((short)(centl - x));
                if (ox >= 0)
                {
                    rgn.Add((short)(centl - ox));
                    rgn.Add((short)(centr + ox));
                }
                rgn.Add((short)(centr + x));
                rgn.Add(0x7FFF);
                ox = x;
            }

            void PointLower(int x, int y)
            {
                if (x == ox) return;
                rgn.Add((short)y);
                rgn.Add((short)(centl - ox));
                rgn.Add((short)(centl - x));
                rgn.Add((short)(centr + x));
                rgn.Add((short)(centr + ox));
                rgn.Add(0x7FFF);
                ox = x;
            }

            for (int i = 0; i < xsteps.Count; i++)
                PointUpper(xsteps[i], rect.Top + i);
            for (int i = ysteps.Count - 1; i >= 0; i--)
                PointUpper((int)(width / 2) - i, centt - ysteps[i]);
            for (int i = 0; i < ysteps.Count - 1; i++)
                PointLower((int)(width / 2) - i - 1, centb + ysteps[i]);
            for (int i = xsteps.Count - 1; i >= 0; i--)
                PointLower(xsteps[i], rect.Bottom - i - 1);

            rgn.Add((short)rect.Bottom);
            rgn.Add((short)(centl - ox));
            rgn.Add((short)(centr + ox));
            rgn.Add(0x7FFF);
            rgn.Add(0x7FFF);
            return rgn;
        }

        private static List<int> Bresenham(long width, long height)
        {
            var steps = new List<int>((int)height);
            long a = height * height, b = width * width;
            bool oddWidth = width % 2 != 0, oddHeight = height % 2 != 0;
            long d = (oddWidth ? 4 : 1) * a + (1 - 2 * height) * b;
            int x = 0;
            long y = height / 2;
            long deltaD;
            do
            {
                while (d < 0)
                {
                    d += 4 * a * (2 * x + 1 + (oddWidth ? 2 : 1));
                    ++x;
                }
                steps.Add(x);
                deltaD = 4 * a * (2 * x + 1 + (oddWidth ? 2 : 1)) - 4 * b * (2 * y - (oddHeight ? 1 : 2));
                d += deltaD;
                --y;
                ++x;
            } while (deltaD <= 0);
            return steps;
        }

        // StdRRect: corner ovals of ovalWidth x ovalHeight (diameters), clamped to the rect; under 4x4 a plain rect.
        public static Region RoundRect(PictRect r, int ovalWidth, int ovalHeight)
        {
            if (r.IsEmpty) return Region.Empty;
            if (ovalWidth < 4 && ovalHeight < 4) return Region.FromRect(r);
            ovalWidth = Math.Min(ovalWidth, r.Width);
            ovalHeight = Math.Min(ovalHeight, r.Height);
            return RoundRectRegion(r, ovalWidth, ovalHeight);
        }

        // roundRectRgn: the corner oval's inversion points split at its middle and pushed out to the rect's edges.
        private static Region RoundRectRegion(PictRect r, int width, int height)
        {
            width = Math.Min(width, r.Width);
            height = Math.Min(height, r.Height);
            var data = CircleData(new PictRect(r.Top, r.Left, r.Top + height, r.Left + width));
            int midX = r.Left + width / 2, midY = r.Top + height / 2;
            int insertX = r.Width - width, insertY = r.Height - height;
            for (int i = 0; i < data.Count && data[i] != 0x7FFF; i++)
            {
                if (data[i] >= midY) data[i] = (short)(data[i] + insertY);
                for (++i; data[i] != 0x7FFF; ++i)
                    if (data[i] >= midX) data[i] = (short)(data[i] + insertX);
            }
            return Region.FromQuickDrawData(r, data.ToArray());
        }

        // StdArc: the oval clipped to the wedge from the center through the start and end angle points, walking the
        // rect's walls clockwise. |arcAngle| >= 360 is the whole oval.
        public static Region Arc(PictRect r, int startAngle, int arcAngle) =>
            ArcClip(r, startAngle, arcAngle, Oval(r));

        public static Region FrameArc(PictRect r, int startAngle, int arcAngle, int penH, int penV) =>
            ArcClip(r, startAngle, arcAngle, FrameOval(r, penH, penV));

        private static Region ArcClip(PictRect r, int starta, int arca, Region shape)
        {
            if (r.IsEmpty) return Region.Empty;
            if (arca <= -360 || arca >= 360) return shape;
            return shape.Intersect(Wedge(r, starta, arca));
        }

        private const int WallTop = 0, WallRight = 1, WallBottom = 2, WallLeft = 3;

        private static Region Wedge(PictRect r, int starta, int arca)
        {
            int left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
            int enda = starta + arca;
            if (arca < 0) (starta, enda) = (enda, starta);
            while (starta < 0) starta += 360;
            while (enda < 0) enda += 360;
            starta %= 360;
            enda %= 360;

            var spt = ArcPoint(starta, r);
            var ept = ArcPoint(enda, r);
            var rec = new RegionRecorder();
            rec.MoveTo(left + (right - left) / 2, top + (bottom - top) / 2);
            int ewall = FindWall(r, ept.h, ept.v);
            int h = spt.h, v = spt.v;
            rec.LineTo(h, v);

            // The C original falls through the wall cases in order: top, right, bottom, left, then loops.
            int wall = FindWall(r, h, v);
            for (bool done = false; !done;)
            {
                switch (wall)
                {
                    case WallTop:
                        if (ewall == WallTop && h <= ept.h) { rec.LineTo(ept.h, top); done = true; break; }
                        rec.LineTo(h = right, v = top);
                        goto case WallRight;
                    case WallRight:
                        if (ewall == WallRight && v <= ept.v) { rec.LineTo(right, ept.v); done = true; break; }
                        rec.LineTo(h = right, v = bottom);
                        goto case WallBottom;
                    case WallBottom:
                        if (ewall == WallBottom && h >= ept.h) { rec.LineTo(ept.h, bottom); done = true; break; }
                        rec.LineTo(h = left, v = bottom);
                        goto default;
                    default:                                   // WallLeft
                        if (ewall == WallLeft && v >= ept.v) { rec.LineTo(left, ept.v); done = true; break; }
                        rec.LineTo(h = left, v = top);
                        wall = FindWall(r, h, v);
                        break;
                }
            }
            rec.LineTo(left + (right - left) / 2, top + (bottom - top) / 2);
            return rec.Close();
        }

        // The order of tests matters: a corner belongs to the first wall that matches.
        private static int FindWall(PictRect r, int h, int v)
        {
            if (v == r.Top) return WallTop;
            if (h == r.Right) return WallRight;
            if (v == r.Bottom) return WallBottom;
            return WallLeft;
        }

        // getpoint: where the ray at a QuickDraw angle from the rect's center meets the rect.
        private static (int h, int v) ArcPoint(int angle, PictRect r)
        {
            int radh = (r.Right - r.Left) / 2, radv = (r.Bottom - r.Top) / 2;
            int centh = r.Left + radh, centv = r.Top + radv;
            switch (angle)
            {
                case 0: return (centh, r.Top);
                case 90: return (r.Right, centv);
                case 180: return (centh, r.Bottom);
                case 270: return (r.Left, centv);
            }
            if (angle >= 45 && angle <= 135)
                return (r.Right, centv - (short)(FixedMath.FixMul(FixedMath.SlopeFromAngle(90 - angle), -(radv << 16)) >> 16));
            if (angle >= 225 && angle <= 315)
                return (r.Left, centv + (short)(FixedMath.FixMul(FixedMath.SlopeFromAngle(90 - angle), -(radv << 16)) >> 16));
            if (angle < 45 || angle > 315)
                return (centh + (short)(FixedMath.FixMul(FixedMath.SlopeFromAngle(angle), -(radh << 16)) >> 16), r.Top);
            return (centh - (short)(FixedMath.FixMul(FixedMath.SlopeFromAngle(angle), -(radh << 16)) >> 16), r.Bottom);
        }

        // polyrgn: MoveTo the first point, LineTo each point, LineTo the first (a repeated closing point is dropped).
        public static Region Polygon(IReadOnlyList<(int h, int v)> points)
        {
            if (points.Count < 2) return Region.Empty;
            int count = points.Count;
            if (points[count - 1] == points[0]) count--;
            var rec = new RegionRecorder();
            rec.MoveTo(points[0].h, points[0].v);
            for (int i = 1; i < count; i++) rec.LineTo(points[i].h, points[i].v);
            rec.LineTo(points[0].h, points[0].v);
            return rec.Close();
        }

        // ---- frames: the shape minus the shape inset by the pen ----

        public static Region FrameRect(PictRect r, int penH, int penV)
        {
            var rect = Region.FromRect(r);
            return rect.Xor(rect.Inset(penH, penV));
        }

        public static Region FrameOval(PictRect r, int penH, int penV)
        {
            if (r.IsEmpty) return Region.Empty;
            if (r.Height < 4 && r.Width < 4) return FrameRect(r, penH, penV);
            var outer = CircleRegion(r);
            var inner = new PictRect(r.Top + penV, r.Left + penH, r.Bottom - penV, r.Right - penH);
            return inner.IsEmpty ? outer : outer.Xor(CircleRegion(inner));
        }

        public static Region FrameRoundRect(PictRect r, int ovalWidth, int ovalHeight, int penH, int penV)
        {
            if (r.IsEmpty) return Region.Empty;
            if (ovalWidth < 4 && ovalHeight < 4) return FrameRect(r, penH, penV);
            ovalWidth = Math.Min(ovalWidth, r.Width);
            ovalHeight = Math.Min(ovalHeight, r.Height);
            var outer = RoundRectRegion(r, ovalWidth, ovalHeight);
            var inner = new PictRect(r.Top + penV, r.Left + penH, r.Bottom - penV, r.Right - penH);
            if (inner.IsEmpty) return outer;
            return outer.Difference(RoundRectRegion(inner, ovalWidth - 2 * penH, ovalHeight - 2 * penV));
        }

        public static Region FrameRegion(Region region, int penH, int penV) => region.Xor(region.Inset(penH, penV));

        // ---- lines ----

        // C_StdLine: the pen (penH x penV) swept from (x1, y1) to (x2, y2), hanging below and to the right of the path,
        // both end points included. A zero-sized pen draws nothing.
        public static Region Line(int x1, int y1, int x2, int y2, int px, int py)
        {
            if (px <= 0 || py <= 0) return Region.Empty;
            if (x1 == x2 || y1 == y2)
            {
                if (x2 < x1) (x1, x2) = (x2, x1);
                if (y2 < y1) (y1, y2) = (y2, y1);
                return Region.FromRect(new PictRect(y1, x1, y2 + py, x2 + px));
            }
            if (y1 > y2)
            {
                (y1, y2) = (y2, y1);
                (x1, x2) = (x2, x1);
            }
            int dy = y2 - y1, dx = Math.Abs(x2 - x1);
            var op = new List<int>();     // left/right edge lists of (y, x) pairs
            var op2 = new List<int>();
            const int Stop = 32767;

            if (dy >= dx)
            {
                if (x2 > x1)
                {
                    if (py > 1) { op2.Add(y1); op2.Add(x1); }
                    EdgeDyDx(y1, x1 + px, dy, dx, +1, op, op2, py - 1, -px);
                }
                else
                {
                    if (py > 1) { op2.Add(y1); op2.Add(x1 + px); }
                    EdgeDyDx(y1, x1, dy, dx, -1, op, op2, py - 1, px);
                }
                op.Add(y2 + py); op.Add(Stop);
                op2.Add(y2 + py); op2.Add(Stop);
            }
            else if (x2 > x1)
            {
                op2.Add(y1); op2.Add(x1);
                EdgeDxDyRight(y1, x1 + px - 1, dy, dx, op, op2, py, -(px - 1));
                op[^1] = x2 + px;
                op.Add(y2 + py); op.Add(Stop);
                op2[^1] = Stop;
            }
            else
            {
                op2.Add(y1); op2.Add(x1 + px);
                EdgeDxDyLeft(y1, x1 + 1, dy, dx, op, op2, py, px - 1);
                op[^1] = x2;
                op.Add(y2 + py); op.Add(Stop);
                op2[^1] = Stop;
            }
            op.Add(Stop);
            op2.Add(Stop);
            return SpansToRegion(op, op2);
        }

        // sco* OUT(y, x): one edge point, and the matching point of the opposite edge offset by (offy, offx).
        private static void Out(List<int> op, List<int> op2, long y, long x, int offy, int offx)
        {
            op.Add((int)y); op.Add((int)x);
            op2.Add((int)y + offy); op2.Add((int)x + offx);
        }

        // scodydxx2x1 (dir +1) / scodydxx1x2 (dir -1): steep lines, one point per x step.
        private static void EdgeDyDx(long y1, int x1, int dy, int dx, int dir, List<int> op, List<int> op2, int offy, int offx)
        {
            int x2 = x1 + dir * dx;
            if (dy > dx)
            {
                long incr = ((long)dy << 16) / (dx + 1) + 1;
                Out(op, op2, y1, x1, offy, offx);
                y1 = (y1 << 16) | (1L << 15);
                while (x1 != x2)
                {
                    y1 += incr;
                    x1 += dir;
                    Out(op, op2, y1 >> 16, x1, offy, offx);
                }
            }
            else
            {
                Out(op, op2, y1, x1, offy, offx);
                while (x1 != x2)
                {
                    ++y1;
                    x1 += dir;
                    Out(op, op2, y1, x1, offy, offx);
                }
            }
        }

        // scodxdyx2x1: shallow, x increasing, one point per scan line.
        private static void EdgeDxDyRight(int y1, int x1Start, int dy, int dx, List<int> op, List<int> op2, int offy, int offx)
        {
            int y2 = y1 + dy;
            long incr = ((long)dx << 16) / (dy + 1) + 1;
            long x1 = ((long)x1Start << 16) | (1L << 15);
            while (y1 <= y2)
            {
                x1 += incr;
                Out(op, op2, y1++, x1 >> 16, offy, offx);
            }
        }

        // scodxdyx1x2: shallow, x decreasing.
        private static void EdgeDxDyLeft(int y1, int x1Start, int dy, int dx, List<int> op, List<int> op2, int offy, int offx)
        {
            int y2 = y1 + dy;
            long incr = ((long)dx << 16) / (dy + 1) + 1;
            long x1 = (((long)x1Start << 16) | (1L << 15)) - 1;
            while (y1 <= y2)
            {
                x1 -= incr;
                Out(op, op2, y1++, x1 >> 16, offy, offx);
            }
        }

        // regionify1: merges the two monotone edge lists into one span per scan line (explicit spans, each holding
        // until the next row), ending at the row whose left x is 32767.
        private static Region SpansToRegion(List<int> ip1, List<int> ip2)
        {
            if (ip1[1] > ip2[1]) (ip1, ip2) = (ip2, ip1);
            var rows = new List<(int y, int x1, int x2)>();
            int i1 = 0, i2 = 0;
            int y1 = ip1[i1++], y2 = ip2[i2++];
            int x1 = ip1[i1], x2 = ip2[i2];
            while (true)
            {
                if (y1 < y2)
                {
                    x1 = ip1[i1++];
                    rows.Add((y1, x1, x2));
                    if (x1 == 32767) break;
                    y1 = ip1[i1++];
                }
                else if (y1 > y2)
                {
                    x2 = ip2[i2++];
                    rows.Add((y2, x1, x2));
                    if (x1 == 32767) break;
                    y2 = ip2[i2++];
                }
                else
                {
                    if (y1 == 32767) break;
                    x1 = ip1[i1++];
                    x2 = ip2[i2++];
                    rows.Add((y1, x1, x2));
                    if (x1 == 32767) break;
                    y1 = ip1[i1++];
                    y2 = ip2[i2++];
                }
            }

            var region = Region.Empty;
            for (int i = 0; i + 1 < rows.Count; i++)
            {
                var (top, left, right) = rows[i];
                int bottom = rows[i + 1].y;
                if (right > left && bottom > top)
                    region = region.Union(Region.FromRect(new PictRect(top, left, bottom, right)));
            }
            return region;
        }
    }
}
