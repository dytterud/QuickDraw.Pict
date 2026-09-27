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
    public void PixelPattern_Type2_IsSolidInTheColorOfTableEntry4()
    {
        // ppat type 2: PixMap at 28, pixels at 78 (8 bytes), table at 86 with 5 entries; entry 4 = $12xx $34xx $56xx.
        var b = new PictBuilder().U16(2).U16(0).U16(28).U16(0).U16(78).Zeros(4).U16(0).Zeros(4).Zeros(8);
        PixMap8x1(b, 86).Bytes(1, 1, 1, 1, 1, 1, 1, 1).U16(0).U16(0).U16(0).U16(4);
        for (int i = 0; i < 4; i++) b.U16(i).Rgb(0, 0, 0);
        b.U16(4).Rgb(0x1299, 0x3499, 0x5699);
        var pattern = QuickDrawResources.DecodePixelPattern(b.ToArray());
        Assert.Equal((8, 8), (pattern.Width, pattern.Height));
        Assert.Equal(new PictColor(0x12, 0x34, 0x56), pattern[5, 7]);
    }

    [Fact]
    public void IconList_WithoutItsMaskHalf_UsesCalcMask()
    {
        // ics# with only the icon: a 4 x 4 black ring at (0..3, 0..3) encloses a white hole, which CalcMask keeps.
        var data = new byte[32];
        data[0] = 0xF0; data[2] = 0x90; data[4] = 0x90; data[6] = 0xF0;
        var icon = QuickDrawResources.DecodeIconList("ics#", data);
        Assert.Equal(White, icon[1, 1]);                       // enclosed: opaque white
        Assert.Equal(255, A(icon, 1, 1));
        Assert.Equal(0, A(icon, 4, 1));                        // reachable from the edge: transparent
    }

    [Fact]
    public void ColorCursor_IgnoresThe1BitDataAndXorsUnmaskedPixels()
    {
        // crsr: 2-bit 16 x 16 PixMap, table 0 white 1 black 2 red; row 0 pixels 1 (black), 0 (white), 2 (red), 1.
        // Mask row 0 = $80 (pixel 0 only); the 1-bit data ($FF..) is never read.
        var b = new PictBuilder().U16(0x8001).U16(0).U16(96).U16(0).U16(146).Zeros(10);
        for (int i = 0; i < 16; i++) b.U16(0xFFFF);                                            // 1-bit data (unused)
        b.U16(0x8000); for (int i = 1; i < 16; i++) b.U16(0);                                  // mask
        b.U16(20).U16(40).Zeros(8);                                                            // hotspot v 20 h 40
        b.U16(0).U16(0).U16(0x8004).Rect(0, 0, 16, 16).U16(0).U16(0).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(0).U16(2).U16(1).U16(2).U16(0).U16(0).U16(0).U16(210).U16(0).U16(0);           // PixMap, pmTable 210
        b.U8(0b01001001).Zeros(63);                                                            // pixels (4 bytes x 16)
        b.U16(0).U16(0).U16(0).U16(2).U16(0).Rgb(0xFFFF, 0xFFFF, 0xFFFF).U16(1).Rgb(0, 0, 0).U16(2).Rgb(0xFFFF, 0, 0);
        var cursor = QuickDrawResources.DecodeColorCursor(b.ToArray());
        Assert.Equal(Black, cursor.Image[0, 0]);
        Assert.Equal(0, cursor.Xor[1]);                                                        // white: transparent
        Assert.Equal(0x00FFFF, cursor.Xor[2]);                                                 // red XORs cyan
        Assert.True(cursor.Inverted[3]);                                                       // black inverts
        Assert.Equal((15, 15), (cursor.HotspotH, cursor.HotspotV));                            // clamped
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
