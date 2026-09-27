using Xunit;

namespace QuickDraw.Pict.Tests;

// QuickDraw regions: the inversion-point format, set operations, InsetRgn, and the shape rasterizers. Golden masks
// for shapes are the Macintosh's pixels (confirmed against an independent reference of QuickDraw's scan
// converters); '#' = inside.
public class RegionTests
{
    private static string[] Mask(Region r, int width, int height) =>
        Enumerable.Range(0, height)
            .Select(y => new string(Enumerable.Range(0, width).Select(x => r.Contains(x, y) ? '#' : '.').ToArray()))
            .ToArray();

    private static int Area(Region r) => r.Rectangles().Sum(t => (t.Bottom - t.Top) * (t.Right - t.Left));

    [Fact]
    public void RectRegion_CoversRectExclusiveOfRightAndBottom()
    {
        var r = Region.FromRect(new PictRect(1, 2, 3, 5));
        Assert.Equal(new[] { ".....", "..###", "..###" }, Mask(r, 5, 3));
        Assert.Equal(new PictRect(1, 2, 3, 5), r.Bounds);
        Assert.True(Region.FromRect(new PictRect(3, 3, 3, 8)).IsEmpty);
    }

    [Fact]
    public void QuickDrawData_DecodesInversionPoints()
    {
        // An L shape: rows 0-1 span x 0..4, rows 2-3 span x 0..2. Row 2 inverts x 2 and 4 (turning them off).
        short[] data = { 0, 0, 4, 0x7FFF, 2, 2, 4, 0x7FFF, 4, 0, 2, 0x7FFF, 0x7FFF };
        var r = Region.FromQuickDrawData(new PictRect(0, 0, 4, 4), data);
        Assert.Equal(new[] { "####", "####", "##..", "##.." }, Mask(r, 4, 4));
        Assert.Equal(data, r.ToQuickDrawData());
    }

    [Fact]
    public void QuickDrawData_WithoutRows_IsTheBoundingRect()
    {
        var r = Region.FromQuickDrawData(new PictRect(0, 0, 2, 3), Array.Empty<short>());
        Assert.Equal(new[] { "###", "###" }, Mask(r, 3, 2));
        Assert.Empty(r.ToQuickDrawData());
    }

    [Fact]
    public void SetOperations_OnOverlappingRects()
    {
        var a = Region.FromRect(new PictRect(0, 0, 10, 10));
        var b = Region.FromRect(new PictRect(5, 5, 15, 15));

        Assert.Equal(175, Area(a.Union(b)));
        Assert.Equal(25, Area(a.Intersect(b)));
        Assert.Equal(75, Area(a.Difference(b)));
        Assert.Equal(150, Area(a.Xor(b)));
        Assert.True(a.Xor(b).Contains(2, 2));
        Assert.False(a.Xor(b).Contains(7, 7));
        Assert.True(a.Xor(b).Contains(12, 12));
        Assert.True(a.Intersect(Region.FromRect(new PictRect(20, 20, 30, 30))).IsEmpty);
    }

    [Fact]
    public void Inset_ErodesAndDilatesPerRowThenColumn()
    {
        var l = Region.FromQuickDrawData(new PictRect(0, 0, 6, 6),
            new short[] { 0, 0, 6, 0x7FFF, 3, 3, 6, 0x7FFF, 6, 0, 3, 0x7FFF, 0x7FFF });
        Assert.Equal(new[]
        {
            "......",
            ".####.",
            ".#....",
            ".#....",
            ".#....",
            "......",
        }, Mask(l.Inset(1, 1), 6, 6));

        var grown = Region.FromRect(new PictRect(2, 2, 3, 3)).Inset(-1, -2);
        Assert.Equal(new PictRect(0, 1, 5, 4), grown.Bounds);
        Assert.True(Region.FromRect(new PictRect(0, 0, 2, 2)).Inset(1, 1).IsEmpty);
    }

    [Fact]
    public void Offset_MovesTheRegion()
    {
        var r = Region.FromRect(new PictRect(0, 0, 2, 2)).Offset(3, 4);
        Assert.Equal(new PictRect(4, 3, 6, 5), r.Bounds);
    }

    [Fact]
    public void Oval_4x4_And_5x5_MatchQuickDraw()
    {
        Assert.Equal(new[] { ".##.", "####", "####", ".##." },
            Mask(RegionShapes.Oval(new PictRect(0, 0, 4, 4)), 4, 4));
        Assert.Equal(new[] { ".###.", "#####", "#####", "#####", ".###." },
            Mask(RegionShapes.Oval(new PictRect(0, 0, 5, 5)), 5, 5));
    }

    [Fact]
    public void Oval_3x3_FillsItsRect()
    {
        Assert.Equal(new[] { "###", "###", "###" }, Mask(RegionShapes.Oval(new PictRect(0, 0, 3, 3)), 3, 3));
    }

    [Theory]
    [InlineData(17, 11)]
    [InlineData(30, 30)]
    [InlineData(8, 41)]
    public void Oval_IsSymmetricAndFillsItsRect(int w, int h)
    {
        var r = RegionShapes.Oval(new PictRect(0, 0, h, w));
        Assert.Equal(new PictRect(0, 0, h, w), r.Bounds);
        var m = Mask(r, w, h);
        for (int y = 0; y < h; y++)
        {
            Assert.Equal(m[y], new string(m[y].Reverse().ToArray()));   // left-right mirror
            Assert.Equal(m[y], m[h - 1 - y]);                             // top-bottom mirror
        }
        Assert.True(r.Contains(w / 2, h / 2));
        Assert.False(r.Contains(0, 0));
    }

    [Fact]
    public void RoundRect_SplitsTheCornerOvalAtItsMidpoints()
    {
        Assert.Equal(new[]
        {
            ".########.",
            "##########",
            "##########",
            "##########",
            "##########",
            ".########.",
        }, Mask(RegionShapes.RoundRect(new PictRect(0, 0, 6, 10), 4, 4), 10, 6));
    }

    [Fact]
    public void FrameOval_Pen1_IsTheRing()
    {
        Assert.Equal(new[] { ".###.", "#...#", "#...#", "#...#", ".###." },
            Mask(RegionShapes.FrameOval(new PictRect(0, 0, 5, 5), 1, 1), 5, 5));
    }

    [Fact]
    public void FrameRect_PenHangsInsideTheRect()
    {
        Assert.Equal(new[] { "#####", "##.##", "#####" },
            Mask(RegionShapes.FrameRect(new PictRect(0, 0, 3, 5), 2, 1), 5, 3));
    }

    [Fact]
    public void Polygon_AxisAlignedSquare_IsItsRect()
    {
        var r = RegionShapes.Polygon(new[] { (2, 2), (6, 2), (6, 6), (2, 6) });
        Assert.Equal(new PictRect(2, 2, 6, 6), r.Bounds);
        Assert.Equal(16, Area(r));
    }

    [Fact]
    public void Polygon_Diagonal_FollowsTheEdgeStaircase()
    {
        // (h, v) points: (0,0) -> (4,4) -> (0,4), closed back to (0,0). The diagonal's h rounds to the pixel centre
        // of each scan line, so its own pixels are inside.
        Assert.Equal(new[] { "#...", "##..", "###.", "####" },
            Mask(RegionShapes.Polygon(new[] { (0, 0), (4, 4), (0, 4) }), 4, 4));
    }

    [Fact]
    public void Arc_QuarterIsTheOvalInsideTheWedge()
    {
        var oval = RegionShapes.Oval(new PictRect(0, 0, 20, 20));
        var arc = RegionShapes.Arc(new PictRect(0, 0, 20, 20), 0, 90, false);    // 12 o'clock clockwise to 3 o'clock
        Assert.True(arc.Contains(14, 5));
        Assert.False(arc.Contains(5, 5));
        Assert.False(arc.Contains(14, 14));
        Assert.True(arc.Difference(oval).IsEmpty);
        Assert.Equal(Area(oval), Area(RegionShapes.Arc(new PictRect(0, 0, 20, 20), 30, 360, false)));
    }

    [Fact]
    public void Arc_NegativeSweepCoversTheSameWedge()
    {
        var cw = RegionShapes.Arc(new PictRect(0, 0, 20, 20), 0, 90, false);
        var ccw = RegionShapes.Arc(new PictRect(0, 0, 20, 20), 90, -90, false);
        Assert.True(cw.Xor(ccw).IsEmpty);
    }

    [Fact]
    public void Line_Horizontal_IncludesBothEndpointsAndPenHeight()
    {
        Assert.Equal(new[] { "######", "######", "......" },
            Mask(RegionShapes.Line(0, 0, 5, 0, 1, 2), 6, 3));
    }

    [Fact]
    public void Line_Diagonal_Pen1_HitsEachStep()
    {
        Assert.Equal(new[] { "#...", ".#..", "..#.", "...#" },
            Mask(RegionShapes.Line(0, 0, 3, 3, 1, 1), 4, 4));
        Assert.Equal(new[] { "...#", "..#.", ".#..", "#..." },
            Mask(RegionShapes.Line(3, 0, 0, 3, 1, 1), 4, 4));
    }

    [Fact]
    public void Read_ParsesPictRegionOperand()
    {
        // rgnSize 36 = 10 + 13 words: the L shape above, as stored after a 0x0081 paintRgn opcode.
        var bytes = new PictBuilder().U16(36).Rect(0, 0, 4, 4)
            .U16(0).U16(0).U16(4).U16(0x7FFF).U16(2).U16(2).U16(4).U16(0x7FFF).U16(4).U16(0).U16(2).U16(0x7FFF).U16(0x7FFF)
            .ToArray();
        using var b = new BinaryReader(new MemoryStream(bytes));

        var r = Region.Read(b);

        Assert.Equal(new[] { "####", "####", "##..", "##.." }, Mask(r, 4, 4));
        Assert.Equal(bytes.Length, b.BaseStream.Position);
    }

    [Fact]
    public void Read_RectangularRegion_IsItsBoundingBox()
    {
        var bytes = new PictBuilder().U16(10).Rect(1, 2, 3, 4).ToArray();
        using var b = new BinaryReader(new MemoryStream(bytes));
        Assert.Equal(new PictRect(1, 2, 3, 4), Region.Read(b).Bounds);
    }

    [Fact]
    public void Line_PenSizeZero_DrawsNothing()
    {
        Assert.True(RegionShapes.Line(0, 0, 5, 5, 0, 3).IsEmpty);
    }
}
