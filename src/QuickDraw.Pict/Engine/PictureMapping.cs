using System.Collections.Generic;

namespace QuickDraw.Pict
{
    // DrawPicture's coordinate mapping from the picture's frame (fromRect) to the destination rectangle (toRect), with
    // QuickDraw's 16-bit arithmetic: points scale about the rects' top-left corners, rounding half up on magnitudes;
    // sizes (pen, oval) scale separately and never shrink a positive size to zero; regions map their inversion points.
    internal static class PictureMapping
    {
        public static (int h, int v) MapPoint(int h, int v, PictRect from, PictRect to) =>
            (MapCoordinate(h, from.Left, from.Right, to.Left, to.Right),
             MapCoordinate(v, from.Top, from.Bottom, to.Top, to.Bottom));

        public static PictRect MapRect(PictRect r, PictRect from, PictRect to)
        {
            var (left, top) = MapPoint(r.Left, r.Top, from, to);
            var (right, bottom) = MapPoint(r.Right, r.Bottom, from, to);
            return new PictRect(top, left, bottom, right);
        }

        // (coord - fromLo) * toSize / fromSize + toLo, with the magnitude rounded (+ fromSize / 2) as unsigned words.
        private static int MapCoordinate(int coord, int fromLo, int fromHi, int toLo, int toHi)
        {
            int fromSize = (short)(fromHi - fromLo), toSize = (short)(toHi - toLo);
            int c = (short)(coord - fromLo);
            if (fromSize != toSize)
            {
                bool negative = c < 0;
                if (negative) c = (short)-c;
                uint product = (uint)(ushort)c * (ushort)toSize + ((uint)(ushort)fromSize >> 1);
                uint quotient = product / (ushort)fromSize;
                c = quotient > 0xFFFF ? (short)product : (short)quotient;   // an overflowing divide leaves the product
                if (negative) c = (short)-c;
            }
            return (short)(c + toLo);
        }

        // Pen and oval sizes: size * toSize / fromSize rounded; zero or negative becomes 0, a positive size at least 1.
        public static (int h, int v) ScaleSize(int h, int v, PictRect from, PictRect to) =>
            (ScaleOne(h, from.Width, to.Width), ScaleOne(v, from.Height, to.Height));

        private static int ScaleOne(int size, int fromSize, int toSize)
        {
            if ((short)fromSize == (short)toSize) return size;
            if ((short)size <= 0) return 0;
            uint scaled = ((uint)(ushort)size * (ushort)toSize + ((uint)(ushort)fromSize >> 1)) / (ushort)fromSize;
            int result = (short)scaled;
            return result == 0 ? 1 : result;
        }

        // The wide-open region (-32767, -32767, 32767, 32767), which the ROM's MapRgn leaves unmapped.
        private static readonly PictRect WideOpen = new PictRect(-32767, -32767, 32767, 32767);

        public static Region MapRegion(Region region, PictRect from, PictRect to)
        {
            if (region.Bounds == WideOpen && region.Bands.Count == 1 && region.Bands[0].Spans.Length == 2)
                return region;
            if (from.Width == to.Width && from.Height == to.Height)
                return region.Offset(to.Left - from.Left, to.Top - from.Top);
            var rows = new Dictionary<int, List<int>>();
            foreach (var (x, y) in region.InversionPoints())
            {
                var (h, v) = MapPoint(x, y, from, to);
                if (!rows.TryGetValue(v, out var xs)) rows[v] = xs = new List<int>();
                xs.Add(h);
            }
            var list = new List<(int y, List<int> xs)>();
            foreach (var (y, xs) in rows) list.Add((y, xs));
            return Region.FromInversionRows(list);
        }
    }
}
