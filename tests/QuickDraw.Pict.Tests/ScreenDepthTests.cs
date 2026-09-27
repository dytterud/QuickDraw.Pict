using Xunit;

namespace QuickDraw.Pict.Tests;

// Drawing on 1-16 bit screens (PictDecodeOptions.ScreenDepth). Expected values follow the SheepShaver-verified rules.
public class ScreenDepthTests
{
    private static PictBitmap Decode(PictBuilder b, int depth, PictQuickDraw quickDraw = PictQuickDraw.MacOS9) =>
        PictReader.Decode(b.ToArray(), new PictDecodeOptions { ScreenDepth = depth, QuickDraw = quickDraw });

    private static PictBuilder PaintRect(int r, int g, int b) =>
        PictBuilder.V2(0, 0, 2, 2).U16(0x001A).Rgb(r, g, b).U16(0x0031).Rect(0, 0, 2, 2);

    private static PictBuilder Direct32(int w, int h, int mode, params (byte r, byte g, byte b)[] pixels)
    {
        var b = PictBuilder.V2(0, 0, h, w).Align().U16(0x009A).U16(0).U16(0xFF).U16(0x8000 | 4 * w).Rect(0, 0, h, w)
            .U16(0).U16(1).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(16).U16(32).U16(3).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .Rect(0, 0, h, w).Rect(0, 0, h, w).U16(mode);
        foreach (var p in pixels) b.U8(0).U8(p.r).U8(p.g).U8(p.b);
        return b.Align().U16(0x00FF);
    }

    [Fact]
    public void EightBit_ForegroundTakesItsInverseTableEntry()
    {
        // $FFFF $3333 $0000 is itself in the standard table; $F000 $4000 $1000 falls in the cell of $FFFF $3333 $0000.
        Assert.Equal(new PictColor(0xFF, 0x33, 0x00), Decode(PaintRect(0xFFFF, 0x3333, 0).U16(0x00FF), 8)[1, 1]);
        Assert.Equal(new PictColor(0xFF, 0x33, 0x00), Decode(PaintRect(0xF000, 0x4000, 0x1000).U16(0x00FF), 8)[1, 1]);
    }

    [Fact]
    public void SixteenBit_TruncatesTo5BitsAndReplicates()
    {
        // $12 $34 $56 -> 2, 6, 10 -> $10 $31 $52.
        var bmp = Decode(Direct32(1, 1, 0, (0x12, 0x34, 0x56)), 16);
        Assert.Equal(new PictColor(0x10, 0x31, 0x52), bmp[0, 0]);
    }

    [Fact]
    public void OneBit_SplitsOnLuminance()
    {
        // (5R + 9G + 2B) / 16 below 128 is black: pure red (79) is black; yellow (223) is white on a black background,
        // but on white it would share white's index, so it takes its inverse's (blue: black).
        Assert.Equal(new PictColor(0, 0, 0), Decode(PaintRect(0xFFFF, 0, 0).U16(0x00FF), 1)[0, 0]);
        Assert.Equal(new PictColor(255, 255, 255), Decode(PaintRect(0xFFFF, 0xFFFF, 0).U16(0x001B).Rgb(0, 0, 0)
            .U16(0x0031).Rect(0, 0, 2, 2).U16(0x00FF), 1)[0, 0]);
        Assert.Equal(new PictColor(0, 0, 0), Decode(PaintRect(0xFFFF, 0xFFFF, 0).U16(0x00FF), 1)[0, 0]);
    }

    [Fact]
    public void DitherCopy_DiffusesTheErrorAlongTheRow()
    {
        // 1 bit, mid grey 128: the first pixel is white (error -127: -64 to the next pixel), the second (64) black.
        var bmp = Decode(Direct32(2, 1, 64, (128, 128, 128), (128, 128, 128)), 1);
        Assert.Equal(new PictColor(255, 255, 255), bmp[0, 0]);
        Assert.Equal(new PictColor(0, 0, 0), bmp[1, 0]);
        // Without ditherCopy both are white.
        Assert.Equal(new PictColor(255, 255, 255), Decode(Direct32(2, 1, 0, (128, 128, 128), (128, 128, 128)), 1)[1, 0]);
    }

    [Fact]
    public void SixteenBit_RomDitherIsOrdered()
    {
        // ROM ditherCopy to 16 bits: min(c + D, 255) >> 3 with D = 0, 5 on row 0: 3 -> 0, then 3 + 5 = 8 -> 1.
        var bmp = Decode(Direct32(2, 1, 64, (3, 3, 3), (3, 3, 3)), 16, PictQuickDraw.MacRom);
        Assert.Equal(new PictColor(0, 0, 0), bmp[0, 0]);
        Assert.Equal(new PictColor(8, 8, 8), bmp[1, 0]);
    }

    [Fact]
    public void Invert_ComplementsTheIndex()
    {
        // 4 bits: InvertRect over white (index 0) gives index 15, black.
        var b = PictBuilder.V2(0, 0, 2, 2).U16(0x0033).Rect(0, 0, 2, 2).U16(0x00FF);
        Assert.Equal(new PictColor(0, 0, 0), Decode(b, 4)[0, 0]);
    }

    [Fact]
    public void ScreenDepth_RejectsOtherDepths()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new PictDecodeOptions { ScreenDepth = 24 });
    }
}
