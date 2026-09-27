using System;
using System.Collections.Generic;

namespace QuickDraw.Pict
{
    // QuickDraw's shape scan conversion, producing the exact pixel sets the Macintosh draws:
    //  - ovals, round rects and arcs share one scan converter: an incremental ellipse (the corner oval of a round rect,
    //    the whole rect's oval for ovals and arcs) walked one scan line at a time, its left and right edges moving in
    //    half pixels, optionally hollowed by an inner ellipse inset by the pen, and clipped to a wedge by the two
    //    angle rays for arcs;
    //  - lines are the pen swept along a fixed-point slope, one pen-wide run per scan line;
    //  - polygons are the inversion points of their edges (as an open region records them), closed automatically.
    // Rects are (top, left, bottom, right) with exclusive right/bottom; points are (h, v).
    internal static class RegionShapes
    {
        public static Region Rect(PictRect r) => Region.FromRect(r);

        public static Region FrameRect(PictRect r, int penH, int penV)
        {
            var rect = Region.FromRect(r);
            return rect.Xor(rect.Inset(penH, penV));
        }

        public static Region FrameRegion(Region region, int penH, int penV) => region.Xor(region.Inset(penH, penV));

        public static Region Oval(PictRect r) => Curve(r, r.Width, r.Height, false, 0, 0, 0, 360);

        public static Region FrameOval(PictRect r, int penH, int penV) =>
            Curve(r, r.Width, r.Height, true, penH, penV, 0, 360);

        // Frames as Mac OS 9 paints them: one region, or two when the pen leaves the inner shape horizontally empty
        // (its left and right slabs, painted one after the other, so XOR cancels where they overlap).
        public static Region[] FrameOvalParts(PictRect r, int penH, int penV, bool macOS9) =>
            macOS9 ? CurveParts(r, r.Width, r.Height, true, penH, penV, 0, 360, true)
                   : new[] { FrameOval(r, penH, penV) };

        public static Region[] FrameRoundRectParts(PictRect r, int ovalWidth, int ovalHeight, int penH, int penV, bool macOS9) =>
            macOS9 ? CurveParts(r, ovalWidth, ovalHeight, true, penH, penV, 0, 360, true)
                   : new[] { FrameRoundRect(r, ovalWidth, ovalHeight, penH, penV) };

        public static Region[] FrameArcParts(PictRect r, int startAngle, int arcAngle, int penH, int penV, bool macOS9) =>
            macOS9 ? CurveParts(r, r.Width, r.Height, true, penH, penV, startAngle, arcAngle, true)
                   : new[] { FrameArc(r, startAngle, arcAngle, penH, penV, false) };

        public static Region RoundRect(PictRect r, int ovalWidth, int ovalHeight) =>
            Curve(r, ovalWidth, ovalHeight, false, 0, 0, 0, 360);

        public static Region FrameRoundRect(PictRect r, int ovalWidth, int ovalHeight, int penH, int penV) =>
            Curve(r, ovalWidth, ovalHeight, true, penH, penV, 0, 360);

        // Mac OS 9 forms the arc slopes with a half-up fixed multiply (FixedMath.FixMulHalfUp).
        public static Region Arc(PictRect r, int startAngle, int arcAngle, bool macOS9) =>
            Curve(r, r.Width, r.Height, false, 0, 0, startAngle, arcAngle, macOS9);

        public static Region FrameArc(PictRect r, int startAngle, int arcAngle, int penH, int penV, bool macOS9) =>
            Curve(r, r.Width, r.Height, true, penH, penV, startAngle, arcAngle, macOS9);

        // ---- ellipse edges ----

        // The left and right edges (16.16) of an ellipse of ovalWidth x ovalHeight stretched to fill a rect, stepped
        // one scan line at a time from the rect's top. Along the way it keeps sum(k odd) (k * (h/w)^2) in 32.32 against
        // a running "r^2 - y^2" term (half-pixel units), widening or narrowing by half pixels until they meet.
        private sealed class EllipseEdges
        {
            public int Top, Bottom;
            private int oddY, target;
            private long sum, term, termStep;
            private int left, right;

            public int Left => (short)(left >> 16);
            public int Right => (short)(right >> 16);

            private bool fixedEdges;

            // An ellipse with no scan lines (a hollow shape whose pen fills it).
            public static EllipseEdges None() => new EllipseEdges { Top = short.MaxValue };

            // Mac OS 9's inner shape of zero width: constant edges over its rows.
            public static EllipseEdges Fixed(int top, int left, int bottom, int right) => new EllipseEdges
            {
                Top = top, Bottom = bottom, left = left << 16, right = right << 16, fixedEdges = true,
            };

            private EllipseEdges() { }

            public EllipseEdges(int top, int left, int bottom, int right, int ovalWidth, int ovalHeight)
            {
                Top = top;
                Bottom = bottom;
                int w = Math.Min(Math.Max(ovalWidth, 0), (short)(right - left));
                int h = Math.Min(Math.Max(ovalHeight, 0), (short)(bottom - top));
                int halfWidth = w << 15;
                this.left = (left << 16) + halfWidth;
                this.right = (right << 16) - halfWidth + 0x8000;
                oddY = (short)(1 - h);
                target = 2 * h - 1;
                int ratio = FixedMath.FixRatio((short)h, (short)w);
                term = (long)ratio * ratio;
                termStep = 2 * term;
            }

            public void Step(int row)
            {
                if (fixedEdges || row < Top || row >= Bottom) return;
                int y = oddY;
                oddY = (short)(oddY + 2);
                while ((int)(sum >> 32) < target)
                {
                    right += 0x8000;
                    left -= 0x8000;
                    sum += term;
                    term += termStep;
                }
                while ((int)(sum >> 32) > target)
                {
                    right -= 0x8000;
                    left += 0x8000;
                    term -= termStep;
                    sum -= term;
                }
                target -= 4 * (short)(y + 1);
            }
        }

        // ---- ovals, round rects and arcs ----

        // One scan converter for all three: the ellipse of ovalWidth x ovalHeight (the rect's size for ovals and arcs),
        // straight between its halves for round rects, hollowed by the pen for frames, and for
        // |arcAngle| < 360 clipped to the wedge between the rays at startAngle and startAngle + arcAngle (degrees,
        // 0 = 12 o'clock, clockwise).
        private static Region Curve(PictRect r, int ovalWidth, int ovalHeight, bool hollow, int penH, int penV,
            int startAngle, int arcAngle, bool macOS9 = false)
        {
            var parts = CurveParts(r, ovalWidth, ovalHeight, hollow, penH, penV, startAngle, arcAngle, macOS9);
            return parts.Length == 1 ? parts[0] : parts[0].Union(parts[1]);
        }

        // macOS9 hollow shapes: no emptiness test; the inner shape spans rows [top + penV, bottom - penV) with an oval
        // of max(0, ovalWidth - 2 penH) x max(0, ovalHeight - 2 penV); at zero width its edges stay at left + penH and
        // right - penH (crossing when the pen is wider than half the shape), and the right slab is its own part.
        private static Region[] CurveParts(PictRect r, int ovalWidth, int ovalHeight, bool hollow, int penH, int penV,
            int startAngle, int arcAngle, bool macOS9)
        {
            if (r.IsEmpty || arcAngle == 0) return new[] { Region.Empty };
            var rows = new Scanlines(r.Left, r.Right);
            Scanlines? rightSlabs = null;
            int top = r.Top, left = r.Left, bottom = r.Bottom, right = r.Right;

            // Wedge state: each ray is a line (16.16 h at the current row) stepping by its slope per row; a side is
            // clipped by its ray while that ray's flag is negative, and the rays trade sides at the middle row.
            if (arcAngle < 0)
            {
                startAngle = (short)(startAngle + arcAngle);
                arcAngle = -arcAngle;
            }
            bool isArc = arcAngle < 360;
            bool hidden = false;
            int midRow = 0, flag1 = 0, flag2 = 0, slope1 = 0, slope2 = 0, ray1 = 0, ray2 = 0;
            if (isArc)
            {
                startAngle = (short)startAngle % 360;
                if (startAngle < 0) startAngle += 360;
                int stopAngle = startAngle + arcAngle;
                if (stopAngle >= 360) stopAngle -= 360;
                midRow = (short)(top + bottom) >> 1;
                int midColumn = (short)(left + right) >> 1;
                int aspect = FixedMath.FixRatio((short)(right - left), (short)(bottom - top));
                Func<int, int, int> mul = macOS9 ? FixedMath.FixMulHalfUp : FixedMath.FixMul;
                slope1 = mul(FixedMath.SlopeFromAngle(startAngle), aspect);
                slope2 = mul(FixedMath.SlopeFromAngle(stopAngle), aspect);
                int halfHeight = (ushort)(bottom - top) >> 1;
                ray1 = (midColumn << 16) - TimesHalfHeight(slope1, halfHeight);
                ray2 = (midColumn << 16) - TimesHalfHeight(slope2, halfHeight);
                flag1 = startAngle < 180 ? startAngle - 90 : 270 - startAngle;
                flag2 = stopAngle < 180 ? stopAngle - 90 : 270 - stopAngle;
                // An arc of under 180 degrees whose rays both point down has nothing in the top half.
                if (arcAngle < 180) hidden = (short)(flag1 | flag2) >= 0;
                else if (arcAngle == 180) hidden = startAngle == 90;
            }

            var outer = new EllipseEdges(top, left, bottom, right, ovalWidth, ovalHeight);
            var inner = EllipseEdges.None();
            if (hollow && macOS9)
            {
                int iTop = top + penV, iBottom = bottom - penV, iLeft = left + penH, iRight = right - penH;
                if (iTop < iBottom)
                {
                    if (iLeft >= iRight || ovalWidth - 2 * penH <= 0)
                    {
                        inner = EllipseEdges.Fixed(iTop, iLeft, iBottom, iRight);
                        if (iLeft > iRight) rightSlabs = new Scanlines(r.Left, r.Right);
                    }
                    else
                        inner = new EllipseEdges(iTop, iLeft, iBottom, iRight, ovalWidth - 2 * penH, Math.Max(0, ovalHeight - 2 * penV));
                }
            }
            else if (hollow && left + penH < right - penH && top + penV < bottom - penV)
                inner = new EllipseEdges(top + penV, left + penH, bottom - penV, right - penH,
                    ovalWidth - 2 * penH, ovalHeight - 2 * penV);
            var second = rightSlabs ?? rows;

            // Round rects hold their edges still between the corner ovals' halves.
            int holdTop = (short)((short)ovalHeight >> 1) + top;
            int holdBottom = holdTop + bottom - top - ovalHeight;

            for (int row = top; row < bottom; row++)
            {
                if (row < holdTop || row >= holdBottom)
                {
                    outer.Step(row);
                    inner.Step(row);
                }

                if (row == midRow && isArc)
                {
                    flag1 = -flag1;
                    flag2 = -flag2;
                    hidden = false;
                    // Arcs of 180 degrees or less that fit in the top half end here.
                    if (arcAngle < 180 && (short)(flag1 | flag2) >= 0) break;
                    if (arcAngle == 180 && startAngle == 270) break;
                    (flag1, flag2) = (flag2, flag1);
                    (ray1, ray2) = (ray2, ray1);
                    (slope1, slope2) = (slope2, slope1);
                }

                if (!hidden)
                {
                    bool innerRow = row >= inner.Top && row < inner.Bottom;
                    if (!isArc)
                    {
                        if (innerRow)
                        {
                            rows.Add(row, outer.Left, inner.Left);
                            second.Add(row, inner.Right, outer.Right);
                        }
                        else
                            rows.Add(row, outer.Left, outer.Right);
                    }
                    else
                        ArcRow(rows, second, row, outer, inner, innerRow, (short)(ray1 >> 16), (short)(ray2 >> 16),
                            flag1 < 0, flag2 < 0, (short)(flag1 & flag2) < 0 && arcAngle > 180);
                }

                ray1 += slope1;
                ray2 += slope2;
            }
            return rightSlabs == null ? new[] { rows.ToRegion() } : new[] { rows.ToRegion(), rightSlabs.ToRegion() };
        }

        // One scan line of an arc: the ellipse's run(s) with the left side cut at ray 1 and the right side at ray 2
        // when their clips are active. When the cuts cross, a wedge over 180 degrees keeps both outer pieces instead.
        private static void ArcRow(Scanlines rows, Scanlines second, int row, EllipseEdges outer, EllipseEdges inner, bool innerRow,
            int ray1, int ray2, bool clip1, bool clip2, bool reflex)
        {
            int outerLeft = outer.Left, outerRight = outer.Right;
            int cutLeft = clip1 && outerLeft < ray1 ? ray1 : outerLeft;
            int cutRight = clip2 && outerRight > ray2 ? ray2 : outerRight;
            if (innerRow)
            {
                int innerLeft = clip2 && inner.Left > ray2 ? ray2 : inner.Left;
                int innerRight = clip1 && inner.Right < ray1 ? ray1 : inner.Right;
                if (cutLeft < cutRight)
                {
                    rows.Add(row, cutLeft, innerLeft);
                    second.Add(row, innerRight, cutRight);
                }
                else if (reflex)
                {
                    if (innerLeft == cutRight) rows.Add(row, cutLeft, inner.Left);
                    else if (cutLeft == innerRight) rows.Add(row, inner.Right, cutRight);
                    rows.Add(row, outerLeft, innerLeft);
                    rows.Add(row, innerRight, outerRight);
                }
            }
            else if (cutLeft < cutRight)
                rows.Add(row, cutLeft, cutRight);
            else if (reflex)
            {
                rows.Add(row, outerLeft, cutRight);
                rows.Add(row, cutLeft, outerRight);
            }
        }

        // slope (16.16) times a small positive integer as the 68000 forms it: the low word's unsigned product plus the
        // high word's signed product added into the high word only (so the high word wraps at 16 bits).
        private static int TimesHalfHeight(int slope, int halfHeight)
        {
            uint low = (uint)(ushort)slope * (uint)halfHeight;
            short high = (short)((short)(slope >> 16) * (short)halfHeight);
            ushort highWord = (ushort)((low >> 16) + (ushort)high);
            return (int)(((uint)highWord << 16) | (low & 0xFFFF));
        }

        // ---- lines ----

        // The pen (penH x penV, hanging below and right of the path) swept from (h1, v1) to (h2, v2), end points
        // included. Horizontal and vertical lines are the rectangle spanning both pen positions; slanted lines are one
        // pen-wide run per scan line whose ends follow the slope, with the first and last rows trimmed so the pen
        // covers exactly the path.
        public static Region Line(int h1, int v1, int h2, int v2, int penH, int penV)
        {
            int top = Math.Min(v1, v2), bottom = Math.Max(v1, v2) + penV;
            int left = Math.Min(h1, h2), right = Math.Max(h1, h2) + penH;
            if (top >= bottom || left >= right) return Region.Empty;
            if (h1 == h2 || v1 == v2) return Region.FromRect(new PictRect(top, left, bottom, right));
            if (penH <= 0 || penV <= 0) return Region.Empty;

            if (v2 < v1)
            {
                (v1, v2) = (v2, v1);
                (h1, h2) = (h2, h1);
            }
            int slope = FixedMath.FixRatio((short)(h2 - h1), (short)(v2 - v1));
            int penRise = FixedMath.FixMul(penV << 16, slope);
            int runLeft = (h1 << 16) + 0x8000 + (slope >> 1);
            int runRight = runLeft + (penH << 16);
            if (slope >= 0)
            {
                runLeft -= penRise;
                if (slope < 0x10000) runLeft += slope;
                else runRight -= 0x10000;
            }
            else
            {
                runRight -= penRise;
                if (slope < -0x10000) runLeft += 0x10000;
                else runRight += slope;
            }

            var rows = new Scanlines(left, right);
            for (int row = top; row < bottom; row++)
            {
                rows.Add(row, (short)(runLeft >> 16), (short)(runRight >> 16));
                runLeft += slope;
                runRight += slope;
            }
            return rows.ToRegion();
        }

        // ---- polygons ----

        // The region an open region records for the polygon's edges, closed from the last point to the first.
        public static Region Polygon(IReadOnlyList<(int h, int v)> points)
        {
            if (points.Count < 2) return Region.Empty;
            var inversions = new Dictionary<int, List<int>>();
            for (int i = 1; i < points.Count; i++)
                EdgeInversions(inversions, points[i - 1].h, points[i - 1].v, points[i].h, points[i].v);
            EdgeInversions(inversions, points[^1].h, points[^1].v, points[0].h, points[0].v);
            var rows = new List<(int y, List<int> xs)>();
            foreach (var (y, xs) in inversions) rows.Add((y, xs));
            return Region.FromInversionRows(rows);
        }

        // An edge's inversion points: wherever the edge's rounded h changes between scan lines, a pair of points
        // bounding the change. Vertical edges add none (their neighbours' points already bound them).
        private static void EdgeInversions(Dictionary<int, List<int>> points, int h1, int v1, int h2, int v2)
        {
            void Toggle(int v, int a, int b)
            {
                if (!points.TryGetValue(v, out var xs)) points[v] = xs = new List<int>();
                xs.Add(a);
                xs.Add(b);
            }

            if (h1 == h2) return;
            if (v1 == v2)
            {
                Toggle(v1, h1, h2);
                return;
            }
            if (v2 < v1)
            {
                (v1, v2) = (v2, v1);
                (h1, h2) = (h2, h1);
            }
            int slope = FixedMath.FixRatio((short)(h2 - h1), (short)(v2 - v1));
            int h = (h1 << 16) + 0x8000 + (slope >> 1);
            if (slope >= 0)
            {
                if (slope < 0x10000) h += slope;
            }
            else if (slope < -0x10000)
                h += 0x10000;

            int previous = h1, v = v1;
            do
            {
                int current = (short)(h >> 16);
                if (current != previous)
                {
                    Toggle(v, previous, current);
                    previous = current;
                }
                v++;
                h += slope;
            } while (v != v2);
            if (previous != h2) Toggle(v, previous, h2);
        }

        // ---- scan-line accumulation ----

        // Pixel runs per scan line, clipped horizontally to [clipLeft, clipRight); runs may overlap.
        private sealed class Scanlines
        {
            private readonly SortedDictionary<int, List<int>> rows = new SortedDictionary<int, List<int>>();
            private readonly int clipLeft, clipRight;

            public Scanlines(int clipLeft, int clipRight)
            {
                this.clipLeft = clipLeft;
                this.clipRight = clipRight;
            }

            public void Add(int row, int x0, int x1)
            {
                x0 = Math.Max(x0, clipLeft);
                x1 = Math.Min(x1, clipRight);
                if (x0 >= x1) return;
                if (!rows.TryGetValue(row, out var runs)) rows[row] = runs = new List<int>();
                runs.Add(x0);
                runs.Add(x1);
            }

            public Region ToRegion() => Region.FromScanlines(rows);
        }
    }
}
