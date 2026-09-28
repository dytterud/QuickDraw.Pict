using Xunit;

namespace QuickDraw.Pict.Tests;

// The drawing engine: verbs, pen, patterns, pen modes, clip, origin and hilite, drawn by the core per pixel
// (Inside Macintosh: Imaging With QuickDraw — PenMode, Table 4-1, Arithmetic Transfer Modes, Highlighting;
// Executor C_StdRgn / C_StdLine / qIMVxfer.cpp). Untouched pixels stay transparent.
public class DrawingTests
{
    private static readonly PictColor Black = new(0, 0, 0);
    private static readonly PictColor White = new(255, 255, 255);
    private static readonly PictColor Clear = new(0, 0, 0, 0);
    private static readonly PictColor Red = new(255, 0, 0);
    private static readonly PictColor Blue = new(0, 0, 255);

    private static readonly PictDecodeOptions Rom = new() { QuickDraw = PictQuickDraw.MacRom };

    private static PictBitmap Draw(int width, int height, Action<PictBuilder> ops, PictDecodeOptions? options = null)
    {
        var b = PictBuilder.V2(0, 0, height, width);
        ops(b);
        b.Align().U16(0x00FF);
        return PictReader.Decode(b.ToArray(), options);
    }

    // '#' = black, '.' = untouched, 'w' = white, 'r' = red, 'b' = blue, '?' = anything else.
    private static string[] Picture(PictBitmap bmp) =>
        Enumerable.Range(0, bmp.Height).Select(y => new string(Enumerable.Range(0, bmp.Width).Select(x =>
        {
            var c = bmp[x, y];
            if (c.A == 0) return '.';
            if (c == Black) return '#';
            if (c == White) return 'w';
            if (c == Red) return 'r';
            if (c == Blue) return 'b';
            return '?';
        }).ToArray())).ToArray();

    [Fact]
    public void PaintRect_UsesTheForegroundColor()
    {
        var bmp = Draw(4, 3, b => b.U16(0x001A).Rgb(0xFFFF, 0, 0).U16(0x0031).Rect(1, 1, 3, 3));
        Assert.Equal(new[] { "....", ".rr.", ".rr." }, Picture(bmp));
    }

    [Fact]
    public void FrameRect_PenHangsInsideTheRect()
    {
        var bmp = Draw(6, 5, b => b.U16(0x0007).Point(1, 2).U16(0x0030).Rect(0, 0, 5, 6));
        Assert.Equal(new[] { "######", "##..##", "##..##", "##..##", "######" }, Picture(bmp));
    }

    [Fact]
    public void EraseRect_UsesTheBackgroundPatternAndColor()
    {
        var bmp = Draw(3, 1, b => b.U16(0x001B).Rgb(0, 0, 0xFFFF).U16(0x0032).Rect(0, 0, 1, 3));
        Assert.Equal(new[] { "bbb" }, Picture(bmp));
    }

    [Fact]
    public void InvertRect_InvertsDrawnPixels()
    {
        var bmp = Draw(4, 1, b => b.U16(0x0031).Rect(0, 0, 1, 2).U16(0x0033).Rect(0, 0, 1, 4));
        // Painted black inverts to white; untouched pixels read as a white background and invert to black.
        Assert.Equal(new[] { "ww##" }, Picture(bmp));
    }

    [Fact]
    public void FillRect_WithCheckerPattern_AlignsToTheCanvasOrigin()
    {
        var bmp = Draw(4, 2, b => b.U16(0x000A).U8(0xAA).U8(0x55).U8(0xAA).U8(0x55).U8(0xAA).U8(0x55).U8(0xAA).U8(0x55)
            .U16(0x0034).Rect(0, 1, 2, 4));
        Assert.Equal(new[] { ".w#w", ".#w#" }, Picture(bmp));
    }

    [Fact]
    public void PaintOval_MatchesTheQuickDrawOval()
    {
        var bmp = Draw(5, 5, b => b.U16(0x0051).Rect(0, 0, 5, 5));
        Assert.Equal(new[] { ".###.", "#####", "#####", "#####", ".###." }, Picture(bmp));
    }

    [Fact]
    public void PaintOval_FlatOvalIsVerticallyAsymmetric()
    {
        // ROM DrawArc: 16x3 gives [2,14) [0,16) [1,15).
        var bmp = Draw(16, 3, b => b.U16(0x0051).Rect(0, 0, 3, 16));
        Assert.Equal(new[] { "..############..", "################", ".##############." }, Picture(bmp));
    }

    [Fact]
    public void PaintRoundRect_WithZeroOvalHeight_InsetsEveryRowByHalfTheOvalWidth()
    {
        var bmp = Draw(10, 3, b => b.U16(0x000B).Point(0, 6).U16(0x0041).Rect(0, 0, 3, 10), Rom);
        Assert.Equal(new[] { "...####...", "...####...", "...####..." }, Picture(bmp));
    }

    [Fact]
    public void PaintRoundRect_WithZeroOvalHeight_IsAPlainRectOnMacOS9()
    {
        var bmp = Draw(10, 3, b => b.U16(0x000B).Point(0, 6).U16(0x0041).Rect(0, 0, 3, 10));
        Assert.Equal(new[] { "##########", "##########", "##########" }, Picture(bmp));
    }

    [Fact]
    public void DitherCopy_DrawsNoOvalButCopiesRects()
    {
        var bmp = Draw(4, 2, b => b.U16(0x0008).U16(64).U16(0x0051).Rect(0, 0, 2, 2).U16(0x0031).Rect(0, 2, 2, 4), Rom);
        Assert.Equal(new[] { "..##", "..##" }, Picture(bmp));
    }

    [Fact]
    public void OvalWithPenMode16To31_DrawsNothing()
    {
        // DrawArc accepts only modes 8-15, 40-47 and 58 (bit 3 forced): PnMode 23 paints and frames nothing, on both.
        foreach (var options in new[] { null, Rom })
        {
            var bmp = Draw(4, 4, b => b.U16(0x0008).U16(23).U16(0x0051).Rect(0, 0, 4, 4).U16(0x0050).Rect(0, 0, 4, 4), options);
            Assert.All(Picture(bmp), row => Assert.Equal("....", row));
        }
    }

    [Fact]
    public void DitherCopy_OnMacOS9_DrawsOvalsAsCopy()
    {
        var bmp = Draw(4, 2, b => b.U16(0x0008).U16(64).U16(0x0051).Rect(0, 0, 2, 2).U16(0x0031).Rect(0, 2, 2, 4));
        Assert.Equal(new[] { "####", "####" }, Picture(bmp));
    }

    [Fact]
    public void FrameOval_WithAPenWiderThanHalf_OnMacOS9_PaintsBothSlabs()
    {
        // 2 x 8 oval, pen (v 0, h 2): every row is [0, 2) twice (copy: drawn; xor: cancelled); the ROM paints the oval.
        var copy = Draw(2, 8, b => b.U16(0x0007).Point(0, 2).U16(0x0050).Rect(0, 0, 8, 2));
        Assert.All(Picture(copy), row => Assert.Equal("##", row));
        // Under patXor the overlap cancels (back to the white background) except rows 0 and 7, whose slabs don't
        // overlap - as the Mac OS 9 harness shows.
        var xor = Draw(2, 8, b => b.U16(0x0007).Point(0, 2).U16(0x0008).U16(10).U16(0x0050).Rect(0, 0, 8, 2));
        Assert.Equal(new[] { "##", "ww", "ww", "ww", "ww", "ww", "ww", "##" }, Picture(xor));
    }

    [Fact]
    public void PenMode_PatOr_OnlyDrawsPatternOnes()
    {
        // pen pattern: left column only; patOr leaves the pattern's 0 bits untouched.
        var bmp = Draw(3, 2, b => b.U16(0x0009).U8(0x80).U8(0x80).U8(0x80).U8(0x80).U8(0x80).U8(0x80).U8(0x80).U8(0x80)
            .U16(0x0008).U16(9).U16(0x0031).Rect(0, 0, 2, 3));
        Assert.Equal(new[] { "#..", "#.." }, Picture(bmp));
    }

    [Fact]
    public void PenMode_PatXor_InvertsTheDestination()
    {
        var bmp = Draw(3, 1, b => b.U16(0x001A).Rgb(0xFFFF, 0, 0).U16(0x0031).Rect(0, 0, 1, 3)
            .U16(0x001A).Rgb(0, 0, 0).U16(0x0008).U16(10).U16(0x0031).Rect(0, 1, 1, 2));
        Assert.Equal(new PictColor(0, 255, 255), bmp[1, 0]);
        Assert.Equal(Red, bmp[0, 0]);
    }

    [Fact]
    public void Line_UsesThePenAndIncludesBothEnds()
    {
        var bmp = Draw(5, 3, b => b.U16(0x0007).Point(2, 1).U16(0x0020).Point(0, 0).Point(0, 3));
        Assert.Equal(new[] { "####.", "####.", "....." }, Picture(bmp));
    }

    [Fact]
    public void FramePoly_IsNotClosed()
    {
        // (h, v): (0,0) -> (3,0) -> (3,3): two sides of a square, the closing edges are not drawn.
        var bmp = Draw(4, 4, b => b.U16(0x0070).U16(22).Rect(0, 0, 3, 3).Point(0, 0).Point(0, 3).Point(3, 3));
        Assert.Equal(new[] { "####", "...#", "...#", "...#" }, Picture(bmp));
    }

    [Fact]
    public void ClipRegion_RestrictsDrawing()
    {
        var bmp = Draw(4, 1, b => b.U16(0x0001).U16(10).Rect(0, 1, 1, 3).U16(0x0031).Rect(0, 0, 1, 4));
        Assert.Equal(new[] { ".##." }, Picture(bmp));
    }

    [Fact]
    public void OriginOpcode_ShiftsLaterCoordinatesCumulatively()
    {
        // Origin(dh=1) twice: a rect at h=2 lands at canvas x=0 (Executor origin(): OffsetRect(srcpicframe, dh, dv)).
        var bmp = Draw(4, 1, b => b.Origin(1, 0).Origin(1, 0).U16(0x0031).Rect(0, 2, 1, 3));
        Assert.Equal(new[] { "#..." }, Picture(bmp));
    }

    [Fact]
    public void OriginOpcode_ShiftsThePatternAlignment()
    {
        // DrawPicture adds Origin's dh to patAlign, so the pattern stays fixed to the shapes' own coordinates:
        // fill pattern column 1 (0x40) lands on canvas x = 0 after Origin(dh = 1).
        var bmp = Draw(4, 1, b => b.U16(0x000A).U8(0x40).U8(0x40).U8(0x40).U8(0x40).U8(0x40).U8(0x40).U8(0x40).U8(0x40)
            .Origin(1, 0).U16(0x0034).Rect(0, 1, 1, 5), Rom);
        Assert.Equal(new[] { "#www" }, Picture(bmp));
    }

    [Fact]
    public void OriginOpcode_OnMacOS9_LeavesThePatternOnTheCanvas()
    {
        var bmp = Draw(4, 1, b => b.U16(0x000A).U8(0x40).U8(0x40).U8(0x40).U8(0x40).U8(0x40).U8(0x40).U8(0x40).U8(0x40)
            .Origin(1, 0).U16(0x0034).Rect(0, 1, 1, 5));
        Assert.Equal(new[] { "w#ww" }, Picture(bmp));
    }

    [Fact]
    public void HiliteMode_SwapsBackgroundAndHiliteColorForTheNextInvert()
    {
        var hilite = new PictColor(0xCC, 0xCC, 0xFF);                    // Mac OS 9's default highlight color
        var bmp = Draw(3, 1, b => b.U16(0x0032).Rect(0, 0, 1, 3)        // erase: white background
            .U16(0x0031).Rect(0, 2, 1, 3)                                  // black pixel stays black under hilite
            .U16(0x001C).U16(0x0033).Rect(0, 0, 1, 3)                      // hilited invert
            .U16(0x0033).Rect(0, 1, 1, 2));                                // plain invert again: hilite bit reset
        Assert.Equal(hilite, bmp[0, 0]);
        Assert.Equal(new PictColor(255 - 0xCC, 255 - 0xCC, 255 - 0xFF), bmp[1, 0]);
        Assert.Equal(Black, bmp[2, 0]);
    }

    [Theory]
    [InlineData(8, "bb")]      // patCopy: the pattern's pixels
    [InlineData(9, "ww")]      // patOr: dst | P (white | blue = white)
    [InlineData(11, "")]       // patBic: dst & ~P (white & ~blue = yellow)
    public void PixelPattern_BooleanModesActOnPixelValues(int mode, string expected)
    {
        var bmp = Draw(2, 1, b =>
        {
            b.U16(0x0013).U16(2).Zeros(8).Rgb(0, 0, 0xFFFF);   // PnPixPat ditherPat: solid blue
            b.U16(0x0008).U16(mode);
            b.U16(0x0031).Rect(0, 0, 1, 2);
        });
        if (mode == 11) Assert.Equal(new PictColor(255, 255, 0), bmp[0, 0]);
        else Assert.Equal(new[] { expected }, Picture(bmp));
    }

    [Fact]
    public void ArithmeticBlend_WeightsByOpColor()
    {
        // Red painted, then blue blended at OpColor 0x8000 on every component: the exact average (s + d) >> 1.
        var bmp = Draw(1, 1, b => b.U16(0x001A).Rgb(0xFFFF, 0, 0).U16(0x0031).Rect(0, 0, 1, 1)
            .U16(0x001F).Rgb(0x8000, 0x8000, 0x8000).U16(0x001A).Rgb(0, 0, 0xFFFF)
            .U16(0x0008).U16(32).U16(0x0031).Rect(0, 0, 1, 1));
        Assert.Equal(new PictColor(0x7F, 0, 0x7F), bmp[0, 0]);
    }

    [Fact]
    public void ArithmeticBlend_TruncatesPerComponent()
    {
        // Weights R $FFFF, G $4000, B $8000 (not all equal): (s * w + d * (65536 - w)) >> 16 with 8-bit s, d.
        var bmp = Draw(1, 1, b => b.U16(0x001A).Rgb(0, 0, 0).U16(0x0031).Rect(0, 0, 1, 1)
            .U16(0x001F).Rgb(0xFFFF, 0x4000, 0x8000).U16(0x001A).Rgb(0xFFFF, 0xFFFF, 0xFFFF)
            .U16(0x0008).U16(32).U16(0x0031).Rect(0, 0, 1, 1), Rom);
        Assert.Equal(new PictColor(254, 63, 127), bmp[0, 0]);
    }

    [Fact]
    public void ArithmeticBlend_OnMacOS9_Rounds()
    {
        // (s * w + d * (65536 - w) + $8000) >> 16.
        var bmp = Draw(1, 1, b => b.U16(0x001A).Rgb(0, 0, 0).U16(0x0031).Rect(0, 0, 1, 1)
            .U16(0x001F).Rgb(0xFFFF, 0x4000, 0x8000).U16(0x001A).Rgb(0xFFFF, 0xFFFF, 0xFFFF)
            .U16(0x0008).U16(32).U16(0x0031).Rect(0, 0, 1, 1));
        Assert.Equal(new PictColor(255, 64, 128), bmp[0, 0]);
    }

    [Fact]
    public void ArithmeticSubPin_SubtractsTheSourceFromTheDestination()
    {
        // dst 0xC0, src 0x40: d - s = 0x80, pinned below at the OpColor's high byte (0x90 for red).
        var bmp = Draw(1, 1, b => b.U16(0x001A).Rgb(0xC0C0, 0xC0C0, 0xC0C0).U16(0x0031).Rect(0, 0, 1, 1)
            .U16(0x001F).Rgb(0x9000, 0x1000, 0x1000).U16(0x001A).Rgb(0x4040, 0x4040, 0xFFFF)
            .U16(0x0008).U16(35).U16(0x0031).Rect(0, 0, 1, 1));
        Assert.Equal(new PictColor(0x90, 0x80, 0x10), bmp[0, 0]);   // blue: 0xC0 - 0xFF borrows -> pin
    }

    [Fact]
    public void ArithmeticAddPin_ClampsToOpColor()
    {
        var bmp = Draw(1, 1, b => b.U16(0x001A).Rgb(0x8000, 0x8000, 0x8000).U16(0x0031).Rect(0, 0, 1, 1)
            .U16(0x001F).Rgb(0xC000, 0xFFFF, 0x4000).U16(0x0008).U16(33).U16(0x0031).Rect(0, 0, 1, 1));
        Assert.Equal(new PictColor(0xC0, 0xFF, 0x40), bmp[0, 0]);
    }

    [Fact]
    public void TextFallback_MaskIsPaintedWithTheTextModeAndForeColor()
    {
        var fallback = new BoxFont();
        var pict = PictBuilder.V2(0, 0, 4, 6)
            .U16(0x001A).Rgb(0xFFFF, 0, 0)
            .U16(0x0003).U16(21).U16(0x000D).U16(9)
            .U16(0x0028).Point(3, 1).Text("ab").Align()
            .U16(0x00FF).ToArray();

        var bmp = PictReader.Decode(pict, new PictDecodeOptions { TextFallback = fallback });

        Assert.Equal(new PictTextStyle(21, 0, 9), fallback.Styles.Single());
        // Each glyph is a 2x2 box sitting on the baseline (v=3) starting at the pen (h=1); srcOr leaves 0 bits alone.
        Assert.Equal(new[] { "......", ".rrrr.", ".rrrr.", "......" }, Picture(bmp));
    }

    private sealed class BoxFont : IPictTextFallback
    {
        public readonly List<PictTextStyle> Styles = new();

        public PictTextMask? Render(string text, PictTextStyle style)
        {
            Styles.Add(style);
            int w = 2 * text.Length;
            var bits = Enumerable.Repeat((byte)1, w * 2).ToArray();
            return new PictTextMask(w, 2, originX: 0, originY: 2, bits, advance: w);
        }
    }
}
