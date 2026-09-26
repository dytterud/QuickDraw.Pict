using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickDraw.Pict
{
    // An immutable QuickDraw region: a set of pixels stored as horizontal bands, each a y-range with sorted,
    // non-overlapping, non-touching x spans [x0, x1). Bands are sorted, non-empty, and adjacent bands with identical
    // spans are merged, so equal regions have equal representations.
    //
    // QuickDraw stores regions as inversion points: a pixel is inside when the number of points (x', y') with
    // x' <= x and y' <= y is odd. That representation is linear under XOR, which is how QuickDraw records polygon
    // edges into an open region (see RegionShapes.Polygon); set operations here work on the equivalent band form.
    internal sealed class Region
    {
        internal readonly struct Band
        {
            public Band(int top, int bottom, int[] spans) { Top = top; Bottom = bottom; Spans = spans; }
            public readonly int Top, Bottom;
            public readonly int[] Spans;    // x0, x1, x2, x3, ... : pixels [x0, x1), [x2, x3), ...
        }

        private readonly Band[] bands;

        private Region(Band[] bands) { this.bands = bands; }

        public static readonly Region Empty = new Region(Array.Empty<Band>());

        public bool IsEmpty => bands.Length == 0;

        internal IReadOnlyList<Band> Bands => bands;

        public PictRect Bounds
        {
            get
            {
                if (bands.Length == 0) return default;
                int left = int.MaxValue, right = int.MinValue;
                foreach (var b in bands)
                {
                    left = Math.Min(left, b.Spans[0]);
                    right = Math.Max(right, b.Spans[^1]);
                }
                return new PictRect(bands[0].Top, left, bands[^1].Bottom, right);
            }
        }

        public static Region FromRect(PictRect r) =>
            r.IsEmpty ? Empty : new Region(new[] { new Band(r.Top, r.Bottom, new[] { r.Left, r.Right }) });

        // QuickDraw region data after rgnSize and rgnBBox: rows of (y, x..., 0x7FFF) inversion points, ended by
        // 0x7FFF. A region with no rows is its bounding rectangle.
        public static Region FromQuickDrawData(PictRect bbox, ReadOnlySpan<short> data)
        {
            if (data.Length == 0 || data[0] == 0x7FFF) return FromRect(bbox);
            var rows = new List<(int y, List<int> xs)>();
            int i = 0;
            while (i < data.Length && data[i] != 0x7FFF)
            {
                int y = data[i++];
                var xs = new List<int>();
                while (i < data.Length && data[i] != 0x7FFF) xs.Add(data[i++]);
                i++;                                                    // row terminator
                rows.Add((y, xs));
            }
            return FromInversionRows(rows);
        }

        // A Region operand in a picture: u16 rgnSize (bytes, including itself and the bounding box), Rect rgnBBox,
        // then (rgnSize - 10) / 2 words of inversion-point data. A 10-byte region is its bounding rectangle.
        public static Region Read(System.IO.BinaryReader b) => Read(b, out _);

        public static Region Read(System.IO.BinaryReader b, out PictRect bbox)
        {
            int size = b.ReadU16BE() & 0x7FFF;
            bbox = b.ReadRectBE();
            if (size <= 10)
                return FromRect(bbox);
            var data = new short[(size - 10) / 2];
            for (int i = 0; i < data.Length; i++) data[i] = b.ReadI16BE();
            if ((size & 1) != 0) b.ReadByte();
            return FromQuickDrawData(bbox, data);
        }

        // Builds a region from inversion-point rows (any order; points toggle, so duplicates cancel).
        public static Region FromInversionRows(IEnumerable<(int y, List<int> xs)> rows)
        {
            var byY = new SortedDictionary<int, HashSet<int>>();
            foreach (var (y, xs) in rows)
            {
                if (!byY.TryGetValue(y, out var set)) byY[y] = set = new HashSet<int>();
                foreach (var x in xs)
                    if (!set.Remove(x)) set.Add(x);
            }
            var result = new List<Band>();
            var current = new SortedSet<int>();
            int? top = null;
            foreach (var (y, toggles) in byY)
            {
                if (top is int t && current.Count > 0) Append(result, t, y, current.ToArray());
                foreach (var x in toggles)
                    if (!current.Remove(x)) current.Add(x);
                top = y;
            }
            return new Region(result.ToArray());
        }

        // Builds a region from one-pixel-high scan lines: rows[y] holds [x0, x1) runs in any order, possibly overlapping.
        public static Region FromScanlines(SortedDictionary<int, List<int>> rows)
        {
            var result = new List<Band>();
            var pairs = new List<(int x0, int x1)>();
            foreach (var (y, runs) in rows)
            {
                pairs.Clear();
                for (int i = 0; i + 1 < runs.Count; i += 2) pairs.Add((runs[i], runs[i + 1]));
                pairs.Sort();
                var spans = new List<int>(pairs.Count * 2);
                foreach (var (x0, x1) in pairs)
                {
                    if (spans.Count > 0 && x0 <= spans[^1]) spans[^1] = Math.Max(spans[^1], x1);
                    else
                    {
                        spans.Add(x0);
                        spans.Add(x1);
                    }
                }
                Append(result, y, y + 1, spans.ToArray());
            }
            return new Region(result.ToArray());
        }

        // Canonical QuickDraw region data (without rgnSize/rgnBBox): empty for a rectangular region.
        public short[] ToQuickDrawData()
        {
            if (bands.Length == 0 || (bands.Length == 1 && bands[0].Spans.Length == 2)) return Array.Empty<short>();
            var data = new List<short>();
            int[] previous = Array.Empty<int>();
            int previousBottom = int.MinValue;
            foreach (var band in bands)
            {
                if (band.Top != previousBottom && previous.Length > 0)
                {
                    EmitRow(data, previousBottom, previous, Array.Empty<int>());
                    previous = Array.Empty<int>();
                }
                EmitRow(data, band.Top, previous, band.Spans);
                previous = band.Spans;
                previousBottom = band.Bottom;
            }
            EmitRow(data, previousBottom, previous, Array.Empty<int>());
            data.Add(0x7FFF);
            return data.ToArray();
        }

        // The region's inversion points (x, y): where a pixel's inside-ness differs from the XOR of its left, upper
        // and upper-left neighbours.
        public IEnumerable<(int x, int y)> InversionPoints()
        {
            int[] previous = Array.Empty<int>();
            int previousBottom = int.MinValue;
            foreach (var band in bands)
            {
                if (band.Top != previousBottom && previous.Length > 0)
                {
                    foreach (var x in Toggles(previous, Array.Empty<int>())) yield return (x, previousBottom);
                    previous = Array.Empty<int>();
                }
                foreach (var x in Toggles(previous, band.Spans)) yield return (x, band.Top);
                previous = band.Spans;
                previousBottom = band.Bottom;
            }
            foreach (var x in Toggles(previous, Array.Empty<int>())) yield return (x, previousBottom);
        }

        private static SortedSet<int> Toggles(int[] before, int[] after)
        {
            var toggles = new SortedSet<int>(before);
            foreach (var x in after)
                if (!toggles.Remove(x)) toggles.Add(x);
            return toggles;
        }

        private static void EmitRow(List<short> data, int y, int[] before, int[] after)
        {
            var toggles = new SortedSet<int>(before);
            foreach (var x in after)
                if (!toggles.Remove(x)) toggles.Add(x);
            if (toggles.Count == 0) return;
            data.Add((short)y);
            foreach (var x in toggles) data.Add((short)x);
            data.Add(0x7FFF);
        }

        public bool Contains(int x, int y)
        {
            foreach (var b in bands)
            {
                if (y < b.Top) return false;
                if (y >= b.Bottom) continue;
                for (int i = 0; i < b.Spans.Length; i += 2)
                    if (x >= b.Spans[i] && x < b.Spans[i + 1]) return true;
                return false;
            }
            return false;
        }

        // The region as disjoint rectangles, top to bottom then left to right.
        public IEnumerable<PictRect> Rectangles()
        {
            foreach (var b in bands)
                for (int i = 0; i < b.Spans.Length; i += 2)
                    yield return new PictRect(b.Top, b.Spans[i], b.Bottom, b.Spans[i + 1]);
        }

        public Region Offset(int dh, int dv)
        {
            if (dh == 0 && dv == 0) return this;
            return new Region(bands.Select(b => new Band(b.Top + dv, b.Bottom + dv, b.Spans.Select(x => x + dh).ToArray())).ToArray());
        }

        public Region Union(Region other) => Combine(this, other, (a, b) => a || b);
        public Region Intersect(Region other) => Combine(this, other, (a, b) => a && b);
        public Region Difference(Region other) => Combine(this, other, (a, b) => a && !b);
        public Region Xor(Region other) => Combine(this, other, (a, b) => a != b);

        // QuickDraw InsetRgn: every span shrinks by dh at both ends (grows when negative, merging), then every
        // vertical run by dv — a separable erosion/dilation (Executor rhtopandinseth + hinset). A region inset to
        // nothing is empty.
        public Region Inset(int dh, int dv)
        {
            if (IsEmpty) return this;
            var horizontal = new List<Band>();
            foreach (var b in bands)
            {
                var spans = InsetSpans(b.Spans, dh);
                if (spans.Length > 0) Append(horizontal, b.Top, b.Bottom, spans);
            }
            var h = new Region(horizontal.ToArray());
            if (dv == 0 || h.IsEmpty) return h;
            return dv < 0 ? h.DilateVertically(-dv) : h.ErodeVertically(dv);
        }

        private static int[] InsetSpans(int[] spans, int d)
        {
            var result = new List<int>(spans.Length);
            for (int i = 0; i < spans.Length; i += 2)
            {
                int x0 = spans[i] + d, x1 = spans[i + 1] - d;
                if (x0 >= x1) continue;
                if (result.Count > 0 && x0 <= result[^1])
                    result[^1] = Math.Max(result[^1], x1);   // grown spans that now touch merge
                else
                {
                    result.Add(x0);
                    result.Add(x1);
                }
            }
            return result.ToArray();
        }

        // Each band spans x-ranges over [top, bottom); dilating vertically by d extends it to [top - d, bottom + d).
        private Region DilateVertically(int d)
        {
            var result = Empty;
            foreach (var b in bands)
                result = result.Union(new Region(new[] { new Band(b.Top - d, b.Bottom + d, b.Spans) }));
            return result;
        }

        // Erosion is the complement of the dilated complement (within a frame d larger than the region).
        private Region ErodeVertically(int d)
        {
            var r = Bounds;
            var frame = FromRect(new PictRect(r.Top - d - 1, r.Left, r.Bottom + d + 1, r.Right));
            var complement = frame.Difference(this);
            return frame.Difference(complement.DilateVertically(d)).Intersect(this);
        }

        private static Region Combine(Region a, Region b, Func<bool, bool, bool> op)
        {
            var ys = new SortedSet<int>();
            foreach (var band in a.bands) { ys.Add(band.Top); ys.Add(band.Bottom); }
            foreach (var band in b.bands) { ys.Add(band.Top); ys.Add(band.Bottom); }
            var result = new List<Band>();
            int? previous = null;
            int ia = 0, ib = 0;
            foreach (var y in ys)
            {
                if (previous is int top)
                {
                    while (ia < a.bands.Length && a.bands[ia].Bottom <= top) ia++;
                    while (ib < b.bands.Length && b.bands[ib].Bottom <= top) ib++;
                    var sa = ia < a.bands.Length && a.bands[ia].Top <= top ? a.bands[ia].Spans : Array.Empty<int>();
                    var sb = ib < b.bands.Length && b.bands[ib].Top <= top ? b.bands[ib].Spans : Array.Empty<int>();
                    var spans = CombineSpans(sa, sb, op);
                    if (spans.Length > 0) Append(result, top, y, spans);
                }
                previous = y;
            }
            return new Region(result.ToArray());
        }

        private static int[] CombineSpans(int[] a, int[] b, Func<bool, bool, bool> op)
        {
            var result = new List<int>();
            int i = 0, j = 0;
            bool inA = false, inB = false, inside = false;
            while (i < a.Length || j < b.Length)
            {
                int x = Math.Min(i < a.Length ? a[i] : int.MaxValue, j < b.Length ? b[j] : int.MaxValue);
                while (i < a.Length && a[i] == x) { inA = !inA; i++; }
                while (j < b.Length && b[j] == x) { inB = !inB; j++; }
                bool now = op(inA, inB);
                if (now != inside)
                {
                    result.Add(x);
                    inside = now;
                }
            }
            return result.ToArray();
        }

        // Adds a band, merging it with the previous one when they touch and have identical spans.
        private static void Append(List<Band> bands, int top, int bottom, int[] spans)
        {
            if (bottom <= top || spans.Length == 0) return;
            if (bands.Count > 0)
            {
                var last = bands[^1];
                if (last.Bottom == top && last.Spans.AsSpan().SequenceEqual(spans))
                {
                    bands[^1] = new Band(last.Top, bottom, last.Spans);
                    return;
                }
            }
            bands.Add(new Band(top, bottom, spans));
        }

        public override string ToString() => IsEmpty ? "Region(empty)" : $"Region({Bounds}, {bands.Length} bands)";
    }
}
