using Xunit;

namespace QuickDraw.Pict.Tests;

// Icon, cursor and pattern resources (QuickDrawResources).
public class QuickDrawResourceTests
{
    private static readonly PictColor Black = new(0, 0, 0);
    private static readonly PictColor White = new(255, 255, 255);

    private static byte A(PictBitmap b, int x, int y) => b.Pixels[(y * b.Width + x) * 4 + 3];

    [Fact]
    public void IconList_MasksTheIcon()
    {
        // ICN#: icon row 0 = $80.. (pixel 0 black), mask row 0 = $C0.. (pixels 0-1 opaque), other rows masked out.
        var data = new byte[256];
        data[0] = 0x80;
        data[128] = 0xC0;
        var icon = QuickDrawResources.DecodeIconList("ICN#", data);
        Assert.Equal((32, 32), (icon.Width, icon.Height));
        Assert.Equal(Black, icon[0, 0]);
        Assert.Equal(White, icon[1, 0]);
        Assert.Equal(0, A(icon, 2, 0));
        Assert.Equal(0, A(icon, 0, 1));
    }

    [Fact]
    public void ColorIcon8_UsesTheStandardTableAndTheIconListMask()
    {
        // icl8: pixel 0 = index 215 (the red ramp's $EE), pixel 1 = 255 (black), pixel 2 = 0 (white); ICN# mask row 0
        // keeps pixels 0-2 only.
        var data = new byte[1024];
        data[0] = 215; data[1] = 255; data[2] = 0;
        var list = new byte[256];
        list[128] = 0xE0;
        var icon = QuickDrawResources.DecodeColorIcon("icl8", data, list);
        Assert.Equal(new PictColor(0xEE, 0, 0), icon[0, 0]);
        Assert.Equal(Black, icon[1, 0]);
        Assert.Equal(White, icon[2, 0]);
        Assert.Equal(0, A(icon, 3, 0));
        Assert.Equal(255, A(QuickDrawResources.DecodeColorIcon("icl8", data), 3, 0));   // no icon list: opaque
    }

    [Fact]
    public void Cursor_PaintsMaskedBitsInvertsTheRestAndReadsTheHotspot()
    {
        // CURS row 0: data $C0 00, mask $80 00 -> pixel 0 black, pixel 1 inverts, pixel 2 transparent; hotspot (v 3, h 5).
        var data = new byte[68];
        data[0] = 0xC0;
        data[32] = 0x80;
        data[64] = 0; data[65] = 3; data[66] = 0; data[67] = 5;
        var cursor = QuickDrawResources.DecodeCursor(data);
        Assert.Equal(Black, cursor.Image[0, 0]);
        Assert.True(cursor.Inverted[1]);
        Assert.Equal(0, A(cursor.Image, 1, 0));
        Assert.False(cursor.Inverted[2]);
        Assert.Equal((5, 3), (cursor.HotspotH, cursor.HotspotV));
    }

    // A 50-byte PixMap record: 8 x 1, 8-bit, pmTable = tableOffset.
    private static PictBuilder PixMap8x1(PictBuilder b, int tableOffset) =>
        b.U16(0).U16(0).U16(8).Rect(0, 0, 1, 8).U16(0).U16(0).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(0).U16(8).U16(1).U16(8).U16(0).U16(0).U16(tableOffset >> 16).U16(tableOffset).U16(0).U16(0);

    [Fact]
    public void Cicn_DrawsItsPixelsThroughItsColorTableAndMask()
    {
        // 8 x 1 8-bit icon; mask $F0 (pixels 0-3); no 1-bit BitMap; table: 1 = red, 2 = blue.
        var b = PixMap8x1(new PictBuilder(), 0)
            .U16(0).U16(0).U16(1).Rect(0, 0, 1, 8)                                      // mask BitMap: rowBytes 1
            .U16(0).U16(0).U16(0).Rect(0, 0, 0, 0)                                      // no 1-bit BitMap
            .U16(0).U16(0)                                                              // iconData
            .U8(0xF0)                                                                   // mask bits
            .U16(0).U16(0).U16(0).U16(1).U16(1).Rgb(0xFFFF, 0, 0).U16(2).Rgb(0, 0, 0xFFFF)
            .Bytes(1, 2, 1, 2, 1, 2, 1, 2);
        var icon = QuickDrawResources.DecodeCicn(b.ToArray());
        Assert.Equal(new PictColor(255, 0, 0), icon[0, 0]);
        Assert.Equal(new PictColor(0, 0, 255), icon[3, 0]);
        Assert.Equal(0, A(icon, 4, 0));
    }

    [Fact]
    public void PixelPattern_ReadsItsPixMapAndColorTable()
    {
        // ppat type 1: header (28), PixMap at 28 (pmTable 86: the table after the pixels), pixels at 78.
        var b = new PictBuilder().U16(1).U16(0).U16(28).U16(0).U16(78).Zeros(4).U16(0).Zeros(4).Zeros(8);
        PixMap8x1(b, 86).Bytes(0, 1, 0, 1, 0, 1, 0, 1)
            .U16(0).U16(0).U16(0).U16(1).U16(0).Rgb(0xFFFF, 0xFFFF, 0xFFFF).U16(1).Rgb(0, 0x8000, 0);
        var pattern = QuickDrawResources.DecodePixelPattern(b.ToArray());
        Assert.Equal((8, 1), (pattern.Width, pattern.Height));
        Assert.Equal(White, pattern[0, 0]);
        Assert.Equal(new PictColor(0, 0x80, 0), pattern[1, 0]);
        // A table before the pixels (pmTable 0 here) fails to load, as GetPixPat does.
        var bad = b.ToArray();
        bad[28 + 42] = bad[28 + 43] = bad[28 + 44] = bad[28 + 45] = 0;
        Assert.Throws<NotSupportedException>(() => QuickDrawResources.DecodePixelPattern(bad));
    }

    [Fact]
    public void PixelPattern_Type0_UsesTheFirstBytesOfThePixelData()
    {
        // Mac OS 9 fills a type-0 ppat with patData's first 8 bytes, not the 1-bit fallback at offset 20.
        var b = new PictBuilder().U16(0).U16(0).U16(0).U16(0).U16(28).Zeros(4).U16(0).Zeros(4)
            .U8(0xFF).Zeros(7)                                   // 1-bit fallback: row 0 black
            .U8(0x00).U8(0xFF).Zeros(6);                         // patData: row 1 black
        var pattern = QuickDrawResources.DecodePixelPattern(b.ToArray());
        Assert.Equal(White, pattern[0, 0]);
        Assert.Equal(Black, pattern[0, 1]);
    }

    [Fact]
    public void PatternList_AndSmallIcons_DecodeEveryEntry()
    {
        var pats = QuickDrawResources.DecodePatternList(new byte[] { 0, 2, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0x80, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Equal(2, pats.Count);
        Assert.Equal(Black, pats[0][7, 0]);
        Assert.Equal(White, pats[1][1, 0]);
        Assert.Equal(3, QuickDrawResources.DecodeSmallIcons(new byte[96]).Count);
        Assert.Null(QuickDrawResources.Decode("snd ", new byte[4]));
    }
}
