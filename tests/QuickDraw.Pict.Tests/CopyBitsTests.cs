using Xunit;

namespace QuickDraw.Pict.Tests;

// DrawPicture's coordinate mapping (MapPt / ScalePt / MapRgn), picture scaling (PictResolution), and CopyBits:
// StretchBits scaling, mask regions, source transfer modes, colorizing and alpha.
public class CopyBitsTests
{
    private static readonly PictColor Black = new(0, 0, 0);
    private static readonly PictColor White = new(255, 255, 255);
    private static readonly PictColor Red = new(255, 0, 0);
    private static readonly PictColor Green = new(0, 255, 0);
    private static readonly PictColor Blue = new(0, 0, 255);

    private static PictBitmap Draw(int width, int height, Action<PictBuilder> ops, PictDecodeOptions? options = null)
    {
        var b = PictBuilder.V2(0, 0, height, width);
        ops(b);
        b.Align().U16(0x00FF);
        return PictReader.Decode(b.ToArray(), options);
    }

    // '#' black, 'w' white, 'r' red, 'g' green, 'b' blue, '.' untouched, '?' other.
    private static string[] Picture(PictBitmap bmp) =>
        Enumerable.Range(0, bmp.Height).Select(y => new string(Enumerable.Range(0, bmp.Width).Select(x =>
        {
            var c = bmp[x, y];
            if (c.A == 0) return '.';
            if (c == Black) return '#';
            if (c == White) return 'w';
            if (c == Red) return 'r';
            if (c == Green) return 'g';
            if (c == Blue) return 'b';
            return '?';
        }).ToArray())).ToArray();

    // BitsRect (0x90) of a 1-bit bitmap: rowBytes, bounds, srcRect, dstRect, mode, unpacked rows.
    private static PictBuilder Bits(PictBuilder b, int rowBytes, (int t, int l, int b, int r) bounds,
        (int t, int l, int b, int r) src, (int t, int l, int b, int r) dst, int mode, params byte[] rows)
    {
        b.Align().U16(0x0090).U16(rowBytes).Rect(bounds.t, bounds.l, bounds.b, bounds.r)
            .Rect(src.t, src.l, src.b, src.r).Rect(dst.t, dst.l, dst.b, dst.r).U16(mode);
        foreach (var x in rows) b.U8(x);
        return b;
    }

    // DirectBitsRect (0x9A) of a w x h 32-bit pixmap, unpacked (packType 1), pixels as (alpha, r, g, b).
    private static PictBuilder Direct32(PictBuilder b, int w, int h, (int t, int l, int b, int r) dst, int mode,
        int cmpCount, params (byte a, byte r, byte g, byte b)[] pixels)
    {
        b.Align().U16(0x009A).U16(0).U16(0xFF).U16(0x8000 | 4 * w).Rect(0, 0, h, w)
            .U16(0).U16(1).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(16).U16(32).U16(cmpCount).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .Rect(0, 0, h, w).Rect(dst.t, dst.l, dst.b, dst.r).U16(mode);
        foreach (var p in pixels) b.U8(p.a).U8(p.r).U8(p.g).U8(p.b);
        return b;
    }

    // ---- mapping ----

    [Fact]
    public void MapPoint_ScalesAboutTheFrameCorner_RoundingMagnitudesHalfUp()
    {
        var from = new PictRect(0, 0, 3, 3);
        var to = new PictRect(10, 20, 12, 22);
        Assert.Equal((21, 11), PictureMapping.MapPoint(1, 2, from, to));      // 1*2/3 → 1 (0.67 rounds up), 2*2/3 → 1
        Assert.Equal((19, 9), PictureMapping.MapPoint(-1, -2, from, to));     // negatives round their magnitude
        Assert.Equal((22, 13), PictureMapping.MapPoint(4, 5, new PictRect(1, 1, 4, 4), to)); // (4-1)*2/3 → 2, (5-1)*2/3 → 3
    }

    [Fact]
    public void ScaleSize_KeepsPositiveSizesAtLeastOnePixel()
    {
        var from = new PictRect(0, 0, 100, 100);
        var to = new PictRect(0, 0, 10, 10);
        Assert.Equal((1, 2), PictureMapping.ScaleSize(1, 20, from, to));
        Assert.Equal((0, 0), PictureMapping.ScaleSize(0, -3, from, to));
        Assert.Equal((7, 9), PictureMapping.ScaleSize(7, 9, from, from));
    }

    [Fact]
    public void MapRegion_MapsInversionPoints()
    {
        // An L: rows 0-1 x 0-4, rows 2-3 x 0-2; doubled.
        var l = Region.FromRect(new PictRect(0, 0, 2, 4)).Union(Region.FromRect(new PictRect(2, 0, 4, 2)));
        var mapped = PictureMapping.MapRegion(l, new PictRect(0, 0, 4, 4), new PictRect(0, 0, 8, 8));
        var expected = Region.FromRect(new PictRect(0, 0, 4, 8)).Union(Region.FromRect(new PictRect(4, 0, 8, 4)));
        Assert.True(mapped.Xor(expected).IsEmpty);
    }

    // Extended v2 picture: 72 dpi frame 10x10, drawn at 144 dpi in a 20x20 source rect.
    private static PictBuilder HighRes() =>
        new PictBuilder().U16(0).Rect(0, 0, 10, 10).U16(0x0011).U16(0x02FF)
            .U16(0x0C00).U16(0xFFFE).U16(0).U16(0x0090).U16(0).U16(0x0090).U16(0).Rect(0, 0, 20, 20).U16(0).U16(0);

    [Fact]
    public void Resolution_PictureFrame_ScalesTheDrawingToTheFrame()
    {
        var pict = HighRes().U16(0x0031).Rect(0, 0, 20, 10).U16(0x00FF).ToArray();   // left half

        var native = PictReader.Decode(pict);
        var frame = PictReader.Decode(pict, new PictDecodeOptions { Resolution = PictResolution.PictureFrame });

        Assert.Equal((20, 20), (native.Width, native.Height));
        Assert.Equal((10, 10), (frame.Width, frame.Height));
        Assert.All(Picture(frame), row => Assert.Equal("#####.....", row));
    }

    [Fact]
    public void Resolution_PictureFrame_ScalesThePenSize()
    {
        // PnSize 4x4 at 144 dpi is 2x2 at 72 dpi.
        var pict = HighRes().U16(0x0007).Point(4, 4).U16(0x0030).Rect(0, 0, 20, 20).U16(0x00FF).ToArray();
        var frame = PictReader.Decode(pict, new PictDecodeOptions { Resolution = PictResolution.PictureFrame });
        Assert.Equal("##......##", Picture(frame)[5]);
    }

    [Fact]
    public void MapRegion_LeavesTheWideOpenRegionUnmapped()
    {
        // The ROM's MapRgn skips it; scaling ±32767 would overflow QuickDraw's 16-bit coordinates.
        var wide = Region.FromRect(new PictRect(-32767, -32767, 32767, 32767));
        Assert.Same(wide, PictureMapping.MapRegion(wide, new PictRect(0, 0, 20, 20), new PictRect(0, 0, 10, 10)));
    }

    [Fact]
    public void Resolution_PictureFrame_WideOpenClipStillCoversTheCanvas()
    {
        var pict = HighRes().U16(0x0001).U16(10).Rect(-32767, -32767, 32767, 32767)
            .U16(0x0031).Rect(0, 0, 20, 20).U16(0x00FF).ToArray();
        var frame = PictReader.Decode(pict, new PictDecodeOptions { Resolution = PictResolution.PictureFrame });
        Assert.All(Picture(frame), row => Assert.Equal("##########", row));
    }

    [Fact]
    public void OriginOpcode_RemapsTheClip()
    {
        // Clip h 1..3, then Origin dh 1: the clip lands on canvas x 0..2.
        var bmp = Draw(4, 1, b => b.U16(0x0001).U16(10).Rect(0, 1, 1, 3).Origin(1, 0).U16(0x0031).Rect(0, 1, 1, 5));
        Assert.Equal(new[] { "##.." }, Picture(bmp));
    }

    // ---- CopyBits ----

    [Fact]
    public void CopyBits_Stretch_DoublesOneBitPixels()
    {
        var bmp = Draw(4, 2, b => Bits(b, 2, (0, 0, 1, 2), (0, 0, 1, 2), (0, 0, 2, 4), 0, 0x80, 0x00));
        Assert.Equal(new[] { "##ww", "##ww" }, Picture(bmp));
    }

    [Fact]
    public void CopyBits_Shrink_OrsTheMergedOneBitPixels()
    {
        // 4x2 source with one black pixel at (3, 0) shrunk to 2x1: it survives in the right pixel.
        var bmp = Draw(2, 1, b => Bits(b, 2, (0, 0, 2, 4), (0, 0, 2, 4), (0, 0, 1, 2), 0, 0x10, 0x00, 0x00, 0x00));
        Assert.Equal(new[] { "w#" }, Picture(bmp));
    }

    [Fact]
    public void CopyBits_MaskRegion_RestrictsTheCopy()
    {
        var bmp = Draw(4, 1, b => b.Align().U16(0x0091).U16(2).Rect(0, 0, 1, 4).Rect(0, 0, 1, 4).Rect(0, 0, 1, 4).U16(0)
            .U16(10).Rect(0, 1, 1, 3)                                   // maskRgn: x 1..2
            .U8(0xF0).U8(0));
        Assert.Equal(new[] { ".##." }, Picture(bmp));
    }

    [Fact]
    public void CopyBits_SrcOr_LeavesWhiteSourcePixelsAlone()
    {
        var bmp = Draw(2, 1, b => Bits(b.U16(0x001A).Rgb(0xFFFF, 0, 0).U16(0x0031).Rect(0, 0, 1, 2).U16(0x001A).Rgb(0, 0, 0),
            2, (0, 0, 1, 2), (0, 0, 1, 2), (0, 0, 1, 2), 1, 0x80, 0x00));
        Assert.Equal(new[] { "#r" }, Picture(bmp));
    }

    [Fact]
    public void CopyBits_ColorizesOneBitSourcesWithForeAndBackColors()
    {
        var bmp = Draw(2, 1, b => Bits(b.U16(0x001A).Rgb(0xFFFF, 0, 0).U16(0x001B).Rgb(0, 0, 0xFFFF),
            2, (0, 0, 1, 2), (0, 0, 1, 2), (0, 0, 1, 2), 0, 0x80, 0x00));
        Assert.Equal(new[] { "rb" }, Picture(bmp));
    }

    [Fact]
    public void CopyBits_NotSrcCopy_InvertsTheSource()
    {
        var bmp = Draw(2, 1, b => Bits(b, 2, (0, 0, 1, 2), (0, 0, 1, 2), (0, 0, 1, 2), 4, 0x80, 0x00));
        Assert.Equal(new[] { "w#" }, Picture(bmp));
    }

    [Fact]
    public void CopyBits_DeepSourceStretch_RepeatsPixels()
    {
        var bmp = Draw(4, 1, b => Direct32(b, 2, 1, (0, 0, 1, 4), 0, 3, (0, 255, 0, 0), (0, 0, 0, 255)));
        Assert.Equal(new[] { "rrbb" }, Picture(bmp));
    }

    [Fact]
    public void CopyBits_DeepSource_ColorizesWithForeAndBackColors()
    {
        // Black source pixels take the foreground color, white ones the background color.
        var bmp = Draw(2, 1, b => Direct32(b.U16(0x001A).Rgb(0, 0xFFFF, 0).U16(0x001B).Rgb(0xFFFF, 0, 0),
            2, 1, (0, 0, 1, 2), 0, 3, (0, 0, 0, 0), (0, 255, 255, 255)));
        Assert.Equal(new[] { "gr" }, Picture(bmp));
    }

    [Theory]
    [InlineData(1, 0x80, 0x30, 0x90)]    // srcOr on a direct destination: ~s & F | s & d = s & d with black fore
    [InlineData(2, 0x8F, 0x7F, 0x9F)]    // srcXor: d ^ ~s
    [InlineData(3, 0xBF, 0xFF, 0xBF)]    // srcBic: ~s & B | s & d = ~s | (s & d) with white back
    [InlineData(6, 0x70, 0x80, 0x60)]    // notSrcXor: d ^ s
    public void CopyBits_DeepSource_BooleanModesAreBitwiseOnPixelValues(int mode, int r, int g, int b)
    {
        // dst (0xB0, 0xB0, 0xB0), src (0xC0, 0x30, 0xD0), default black fore / white back.
        var bmp = Draw(1, 1, p => Direct32(p.U16(0x001A).Rgb(0xB0B0, 0xB0B0, 0xB0B0).U16(0x0031).Rect(0, 0, 1, 1).U16(0x001A).Rgb(0, 0, 0),
            1, 1, (0, 0, 1, 1), mode, 3, (0, 0xC0, 0x30, 0xD0)));
        Assert.Equal(new PictColor((byte)r, (byte)g, (byte)b), bmp[0, 0]);
    }

    [Fact]
    public void CopyBits_Transparent_SkipsBackgroundColoredPixels()
    {
        var bmp = Draw(2, 1, b => Direct32(b.U16(0x001A).Rgb(0xFFFF, 0, 0).U16(0x0031).Rect(0, 0, 1, 2).U16(0x001A).Rgb(0, 0, 0),
            2, 1, (0, 0, 1, 2), 36, 3, (0, 255, 255, 255), (0, 0, 255, 0)));
        Assert.Equal(new[] { "rg" }, Picture(bmp));
    }

    [Theory]
    [InlineData(false, 255)]
    [InlineData(true, 0x80)]
    public void CopyBits_PreserveAlpha_KeepsTheAlphaChannel(bool preserve, int expectedAlpha)
    {
        var bmp = Draw(1, 1, b => Direct32(b, 1, 1, (0, 0, 1, 1), 0, 4, (0x80, 10, 20, 30)),
            new PictDecodeOptions { PreserveAlpha = preserve });
        Assert.Equal(new PictColor(10, 20, 30, (byte)expectedAlpha), bmp[0, 0]);
    }

    // ---- StretchBits geometry ----

    [Fact]
    public void ColumnGroups_OneAndAHalf_UsesThePairPattern()
    {
        // x1.5: source pair (a, b) → a, b, b.
        Assert.Equal(new[] { 0, 1, 1, 2, 3, 3 }, QuickDraw.Pict.Bits.ColumnGroups(4, 6).Select(g => g.first).ToArray());
    }

    [Fact]
    public void ColumnGroups_ThreeQuarters_MergesTheMiddlePair()
    {
        Assert.Equal(new[] { (0, 1), (1, 3), (3, 4) }, QuickDraw.Pict.Bits.ColumnGroups(4, 3));
    }

    [Fact]
    public void RowGroups_ExactIntegerShrink_StartsTheErrorAtSrcHMinusOne()
    {
        // 6 -> 2: even groups (the 1984 start of -srcH/2 would give {0,1}, {2,3,4}).
        Assert.Equal(new[] { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } }, QuickDraw.Pict.Bits.RowGroups(0, 6, 2, 6));
        // 5 -> 2: not an integer factor, so -srcH/2: {0,1}, {2,3}, row 4 never read.
        Assert.Equal(new[] { new[] { 0, 1 }, new[] { 2, 3 } }, QuickDraw.Pict.Bits.RowGroups(0, 5, 2, 5));
    }

    [Fact]
    public void DeepColumnGroups_OneAndAHalf_SteppsTheFractionWithoutTheOneBitFastPath()
    {
        Assert.Equal(new[] { 0, 0, 1, 2, 2, 3 }, QuickDraw.Pict.Bits.DeepColumnGroups(4, 6).Select(g => g.first).ToArray());
        Assert.Equal(new[] { (0, 2), (2, 4) }, QuickDraw.Pict.Bits.DeepColumnGroups(4, 2));
    }

    [Fact]
    public void CopyBits_DeepShrink_AveragesDirectPixelsTruncating()
    {
        // 32-bit 2x2 shrunk to 1x1: rows averaged per column, then the two columns: (10+21)/2=15, (15+30)/2=22 ... per component.
        var bmp = Draw(1, 1, b => Direct32(b, 2, 2, (0, 0, 1, 1), 0, 3,
            (0, 10, 0, 255), (0, 21, 1, 0), (0, 30, 3, 0), (0, 40, 4, 255)), new PictDecodeOptions { QuickDraw = PictQuickDraw.MacRom });
        // column 0: (10+30)/2 = 20, (0+3)/2 = 1, (255+0)/2 = 127; column 1: (21+40)/2 = 30, (1+4)/2 = 2, (0+255)/2 = 127
        Assert.Equal(new PictColor(25, 1, 127), bmp[0, 0]);
    }

    [Fact]
    public void CopyBits_DeepShrink_OnMacOS9_AveragesRounded()
    {
        // column 0: (10+30+1)/2 = 20, (0+3+1)/2 = 2, (255+0+1)/2 = 128; column 1: 31, 3, 128; then (20+31+1)/2 = 26 ...
        var bmp = Draw(1, 1, b => Direct32(b, 2, 2, (0, 0, 1, 1), 0, 3,
            (0, 10, 0, 255), (0, 21, 1, 0), (0, 30, 3, 0), (0, 40, 4, 255)));
        Assert.Equal(new PictColor(26, 3, 128), bmp[0, 0]);
    }

    [Fact]
    public void RowGroups_OnMacOS9_UseTheirOwnDda()
    {
        // 3 -> 4: rows 0, 1, 1, 2 (the ROM: 0, 0, 1, 2); 5 -> 2: {0, 1, 2}, {3, 4} (boundaries floor(2.5 + 0.5), 5).
        Assert.Equal(new[] { new[] { 0 }, new[] { 1 }, new[] { 1 }, new[] { 2 } }, QuickDraw.Pict.Bits.RowGroupsMacOS9(0, 3, 4, 3));
        Assert.Equal(new[] { new[] { 0, 1, 2 }, new[] { 3, 4 } }, QuickDraw.Pict.Bits.RowGroupsMacOS9(0, 5, 2, 5));
    }

    [Fact]
    public void ColumnGroups_OnMacOS9_UseTheRowDda()
    {
        // The harness's 3 -> 269: column 179 takes source 2; 191 -> 83 shrinks by rounded boundaries.
        Assert.Equal(2, QuickDraw.Pict.Bits.ColumnGroupsMacOS9(3, 269)[179].first);
        var shrink = QuickDraw.Pict.Bits.ColumnGroupsMacOS9(191, 83);
        Assert.Equal((0, 2), shrink[0]);
        Assert.Equal(191, shrink[82].end);
    }

    [Fact]
    public void CopyBits_ColorizingSrcOr_OnMacOS9_BlendsEachChannel()
    {
        // 8-bit source index 1 = (64, 128, 192) srcOr over white with fore red: ((256 - s) C + (s + 1) d) >> 8.
        var pict = PictBuilder.V2(0, 0, 1, 1).U16(0x001A).Rgb(0xFFFF, 0, 0)
            .U16(0x0090).U16(0x8002).Rect(0, 0, 1, 1)
            .U16(0).U16(0).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0).U16(0).U16(8).U16(1).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .U16(0).U16(0).U16(0).U16(1).U16(0).Rgb(0, 0, 0).U16(1).Rgb(0x4000, 0x8000, 0xC000)
            .Rect(0, 0, 1, 1).Rect(0, 0, 1, 1).U16(1).U8(1).U8(0).U16(0x00FF).ToArray();
        int C(int s, int c, int d) => ((256 - s) * c + (s + 1) * d) >> 8;
        Assert.Equal(new PictColor((byte)C(64, 255, 255), (byte)C(128, 0, 255), (byte)C(192, 0, 255)), PictReader.Decode(pict)[0, 0]);
        // The ROM's is bitwise: (~s & F) | (s & d).
        Assert.Equal(new PictColor(255, 128, 192),
            PictReader.Decode(pict, new PictDecodeOptions { QuickDraw = PictQuickDraw.MacRom })[0, 0]);
    }

    [Fact]
    public void ColumnGroups_OneAndAHalf_OnMacOS9_IsAabccd()
    {
        Assert.Equal(new[] { 0, 0, 1, 2, 2, 3 }, QuickDraw.Pict.Bits.ColumnGroups(4, 6, true).Select(g => g.first).ToArray());
    }

    [Fact]
    public void CopyBits_DeepShrink_TakesTheLargestIndex()
    {
        // 8-bit 2x1 shrunk to 1x1: indices 5 and 200 -> 200 (a darker cube color, not an average).
        var pict = PictBuilder.V2(0, 0, 1, 1).U16(0x0090).U16(0x8002).Rect(0, 0, 1, 2)
            .U16(0).U16(0).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0).U16(0).U16(8).U16(1).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .U16(0).U16(0).U16(0x8000).U16(255);
        var palette = StandardColorTables.ForId(8)!;
        for (int i = 0; i < 256; i++) pict.U16(i).Rgb(palette[i].R * 257, palette[i].G * 257, palette[i].B * 257);
        pict.Rect(0, 0, 1, 2).Rect(0, 0, 1, 1).U16(0).U8(5).U8(200).U16(0x00FF);
        Assert.Equal(palette[200], PictReader.Decode(pict.ToArray())[0, 0]);
    }

    [Fact]
    public void RowGroups_ShrinkMergesRowsAndStretchRepeatsThem()
    {
        Assert.Equal(new[] { new[] { 0, 1 }, new[] { 2, 3 } }, QuickDraw.Pict.Bits.RowGroups(0, 4, 2, 4));
        Assert.Equal(new[] { new[] { 0 }, new[] { 0 }, new[] { 1 }, new[] { 1 } }, QuickDraw.Pict.Bits.RowGroups(0, 2, 4, 2));
    }
}
