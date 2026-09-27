using Xunit;

namespace QuickDraw.Pict.Tests;

// Opcode-table completeness, picture headers, pixel-data layouts and comments, per Inside Macintosh: Imaging With
// QuickDraw, Appendix A (Table A-2, Listings A-1..A-6). Each opcode test brackets the opcode under test with a known
// 1-bit bitmap draw; a wrong operand size desyncs the stream and the marker pixel goes missing.
public class PictParserTests
{
    private static readonly PictColor Black = new(0, 0, 0);
    private static readonly PictColor White = new(255, 255, 255);

    // v2 BitsRect: 8x1 1-bit bitmap (rowBytes 2 < 8, so unpacked) at the frame origin, leftmost pixel set.
    private static PictBuilder MarkerBits(PictBuilder b) =>
        b.Align().U16(0x0090).U16(2).Rect(0, 0, 1, 8).Rect(0, 0, 1, 8).Rect(0, 0, 1, 8).U16(0).U8(0x80).U8(0);

    private static PictBitmap DecodeWith(Action<PictBuilder> opcodes)
    {
        var b = PictBuilder.V2(0, 0, 1, 8);
        opcodes(b);
        MarkerBits(b).Align().U16(0x00FF);
        return PictReader.Decode(b.ToArray());
    }

    public static TheoryData<string, byte[]> SkippableOpcodes => new()
    {
        { "0x0015 PnLocHFrac", Op(0x0015, 0, 5) },
        { "0x0016 ChExtra", Op(0x0016, 0, 1) },
        { "0x0017 reserved (no data)", Op(0x0017) },
        { "0x0018 reserved (no data)", Op(0x0018) },
        { "0x0019 reserved (no data)", Op(0x0019) },
        { "0x001C HiliteMode", Op(0x001C) },
        { "0x001D HiliteColor", Op(0x001D, 1, 2, 3, 4, 5, 6) },
        { "0x001E DefHilite", Op(0x001E) },
        { "0x0024 reserved var16", Op(0x0024, 0, 3, 9, 9, 9) },
        { "0x002C fontName", Op(0x002C, 0, 8, 0, 21, 5, (byte)'A', (byte)'r', (byte)'i', (byte)'a', (byte)'l') },
        { "0x002D lineJustify", Op(0x002D, 0, 8, 0, 1, 0, 0, 0, 10, 0, 0) },
        { "0x002E glyphState", Op(0x002E, 0, 4, 1, 0, 1, 0) },
        { "0x002F reserved var16", Op(0x002F, 0, 1, 7) },
        { "0x0035 reserved rect", Op(0x0035, 0, 0, 0, 0, 0, 1, 0, 1) },
        { "0x003D reserved (no data)", Op(0x003D) },
        { "0x0045 reserved rect", Op(0x0045, 0, 0, 0, 0, 0, 1, 0, 1) },
        { "0x004D reserved (no data)", Op(0x004D) },
        { "0x0055 reserved rect", Op(0x0055, 0, 0, 0, 0, 0, 1, 0, 1) },
        { "0x005D reserved (no data)", Op(0x005D) },
        { "0x0065 reserved rect+angles", Op(0x0065, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 90) },
        { "0x006D reserved angles", Op(0x006D, 0, 0, 0, 90) },
        { "0x0075 reserved poly", Op(0x0075, 0, 14, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0) },
        { "0x007D reserved (no data)", Op(0x007D) },
        { "0x0085 reserved rgn", Op(0x0085, 0, 10, 0, 0, 0, 0, 0, 1, 0, 1) },
        { "0x008D reserved (no data)", Op(0x008D) },
        { "0x0094 reserved var16", Op(0x0094, 0, 2, 1, 2) },
        { "0x009C reserved var16", Op(0x009C, 0, 0) },
        { "0x00A0 ShortComment", Op(0x00A0, 0, 130) },
        { "0x00A2 reserved var16", Op(0x00A2, 0, 1, 5) },
        { "0x00B0 reserved (no data)", Op(0x00B0) },
        { "0x00CF reserved (no data)", Op(0x00CF) },
        { "0x00D0 reserved var16 (ROM reads a word length)", Op(0x00D0, 0, 3, 1, 2, 3) },
        { "0x00DF reserved var16 (ROM reads a word length)", Op(0x00DF, 0, 0) },
        { "0x00E0 reserved var32", Op(0x00E0, 0, 0, 0, 3, 1, 2, 3) },
        { "0x00FE reserved var32", Op(0x00FE, 0, 0, 0, 0) },
        { "0x0100 reserved 2 bytes", Op(0x0100, 1, 2) },
        { "0x02FF version (2 bytes)", Op(0x02FF, 0, 0) },
        { "0x0300 reserved 6 bytes", Op(0x0300, 1, 2, 3, 4, 5, 6) },
        { "0x0C00 header (24 bytes)", Op(0x0C00, new byte[24]) },
        { "0x7F00 reserved 254 bytes", Op(0x7F00, new byte[254]) },
        { "0x8000 reserved (no data)", Op(0x8000) },
        { "0x80FF reserved (no data)", Op(0x80FF) },
        { "0x8100 reserved var32", Op(0x8100, 0, 0, 0, 2, 9, 9) },
        { "0x8202 reserved var32", Op(0x8202, 0, 0, 0, 1, 9) },
        { "0xFFFF reserved var32", Op(0xFFFF, 0, 0, 0, 0) },
    };

    private static byte[] Op(int op, params byte[] operands) =>
        new[] { (byte)(op >> 8), (byte)op }.Concat(operands).ToArray();

    [Theory]
    [MemberData(nameof(SkippableOpcodes))]
    public void LegalOpcode_IsSkippedWithItsSpecOperandSize(string name, byte[] opcodeAndOperands)
    {
        _ = name;
        var bmp = DecodeWith(b => { foreach (var x in opcodeAndOperands) b.U8(x); });
        Assert.Equal(Black, bmp[0, 0]);
        Assert.Equal(White, bmp[1, 0]);
    }

    [Fact]
    public void QuickTimeOpcode_WithoutCodecSupport_IsSkippedByItsLength()
    {
        // 0x8200 CompressedQuickTime: u32 length + private data. Undecoded, the stream must continue.
        var bmp = DecodeWith(b => b.U16(0x8200).U16(0).U16(4).U8(1).U8(2).U8(3).U8(4));
        Assert.Equal(Black, bmp[0, 0]);
    }

    [Fact]
    public void VersionOpcode_MidStream_SwitchesToOneByteOpcodes()
    {
        // 0x0011 0x01 inside a v2 picture: subsequent opcodes are single bytes (Executor nextop()).
        var pict = PictBuilder.V2(0, 0, 1, 8)
            .U16(0x0011).U8(0x01)
            .U8(0x90).U16(2).Rect(0, 0, 1, 8).Rect(0, 0, 1, 8).Rect(0, 0, 1, 8).U16(0).U8(0x80).U8(0)
            .U8(0xFF).ToArray();

        Assert.Equal(Black, PictReader.Decode(pict)[0, 0]);
    }

    [Fact]
    public void ExtendedV2Header_UsesSourceRectAsCoordinateSpaceAndReportsResolution()
    {
        // picFrame is 72 dpi (0,0,5,4); the header's optimal srcRect (100,200,110,208) at 144 dpi is the space the
        // opcodes draw in (Listing A-5; Executor DrawPicture). The marker is drawn at the srcRect origin.
        var pict = new PictBuilder().U16(0).Rect(0, 0, 5, 4).U16(0x0011).U16(0x02FF)
            .U16(0x0C00).U16(0xFFFE).U16(0).U16(0x0090).U16(0).U16(0x0090).U16(0).Rect(100, 200, 110, 208).U16(0).U16(0)
            .U16(0x0090).U16(2).Rect(100, 200, 101, 208).Rect(100, 200, 101, 208).Rect(100, 200, 101, 208).U16(0).U8(0x80).U8(0)
            .U16(0x00FF).ToArray();

        var bmp = PictReader.Decode(pict);

        Assert.Equal((8, 10), (bmp.Width, bmp.Height));
        Assert.Equal(Black, bmp[0, 0]);
        Assert.NotNull(bmp.Info);
        Assert.Equal(144.0, bmp.Info!.HorizontalResolution);
        Assert.Equal(144.0, bmp.Info.VerticalResolution);
        Assert.Equal(new PictRect(0, 0, 5, 4), bmp.Info.PictureFrame);
        Assert.Equal(new PictRect(100, 200, 110, 208), bmp.Info.Bounds);
        Assert.True(bmp.Info.IsExtendedVersion2);
    }

    [Fact]
    public void Version2Header_WithFixedBoundingBox_UsesPictureFrameAt72Dpi()
    {
        // Listing A-6: version -1 followed by a Fixed bounding box; drawing coordinates are picFrame's.
        var pict = new PictBuilder().U16(0).Rect(2, 2, 3, 10).U16(0x0011).U16(0x02FF)
            .U16(0x0C00).U16(0xFFFF).U16(0xFFFF)
            .U16(2).U16(0).U16(2).U16(0).U16(10).U16(0).U16(3).U16(0).U16(0).U16(0)
            .U16(0x0090).U16(2).Rect(2, 2, 3, 10).Rect(2, 2, 3, 10).Rect(2, 2, 3, 10).U16(0).U8(0x80).U8(0)
            .U16(0x00FF).ToArray();

        var bmp = PictReader.Decode(pict);

        Assert.Equal((8, 1), (bmp.Width, bmp.Height));
        Assert.Equal(Black, bmp[0, 0]);
        Assert.Equal(72.0, bmp.Info!.HorizontalResolution);
        Assert.False(bmp.Info.IsExtendedVersion2);
        Assert.Equal(2, bmp.Info.Version);
    }

    [Fact]
    public void Version2_WithoutHeaderOpcode_DoesNotLoseTheFirstOpcode()
    {
        var pict = new PictBuilder().U16(0).Rect(0, 0, 1, 8).U16(0x0011).U16(0x02FF)
            .U16(0x0090).U16(2).Rect(0, 0, 1, 8).Rect(0, 0, 1, 8).Rect(0, 0, 1, 8).U16(0).U8(0x80).U8(0)
            .U16(0x00FF).ToArray();

        Assert.Equal(Black, PictReader.Decode(pict)[0, 0]);
    }

    [Theory]
    [InlineData(2)]   // ditherPat: pattern + RGBColor
    [InlineData(1)]   // full PixMap pattern: 8x8 at 8 bpp -> rowBytes 8, so PixData is PackBits-compressed
    public void PixelPatternOpcodes_AreParsedWithoutDesync(int patType)
    {
        var bmp = DecodeWith(b =>
        {
            b.U16(0x0014).U16(patType).Zeros(8);
            if (patType == 2) { b.Rgb(0xFFFF, 0, 0); return; }
            b.U16(0x8008).Rect(0, 0, 8, 8)                       // rowBytes (PixMap flag) + bounds
             .U16(0).U16(0).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)   // pmVersion, packType, packSize, hRes, vRes
             .U16(0).U16(8).U16(1).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)  // pixelType, pixelSize, cmpCount, cmpSize, planeBytes, pmTable, reserved
             .U16(0).U16(0).U16(0).U16(1)                        // ctSeed, ctFlags, ctSize (2 entries)
             .U16(0).Rgb(0xFFFF, 0xFFFF, 0xFFFF).U16(1).Rgb(0, 0, 0);
            for (int row = 0; row < 8; row++) b.U8(2).U8(0xF9).U8(row & 1);   // byteCount, repeat 8x
        });
        Assert.Equal(Black, bmp[0, 0]);
    }

    [Fact]
    public void IccProfileComments_AreConcatenatedAcrossBeginContinuationEnd()
    {
        var bmp = DecodeWith(b => b
            .U16(0x00A1).U16(224).U16(7).U16(0).U16(0).U8(1).U8(2).U8(3).Align()   // selector 0: begin
            .U16(0x00A1).U16(224).U16(6).U16(0).U16(1).U8(4).U8(5)                 // selector 1: continuation
            .U16(0x00A1).U16(224).U16(4).U16(0).U16(2));                           // selector 2: end

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, bmp.Info!.IccProfile);
        Assert.Equal(3, bmp.Info.Comments.Count);
        Assert.Equal(224, bmp.Info.Comments[0].Kind);
    }

    [Theory]
    [InlineData(1)]   // packType 1: unpacked, chunky xRGB rows
    [InlineData(4)]   // rowBytes 4 < 8: unpacked regardless of packType
    public void DirectBits32_UnpackedRows_AreChunkyXrgb(int packType)
    {
        var pict = PictBuilder.V2(0, 0, 1, 1)
            .U16(0x009A).U16(0).U16(0xFF).U16(0x8004).Rect(0, 0, 1, 1)
            .U16(0).U16(packType).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(16).U16(32).U16(3).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .Rect(0, 0, 1, 1).Rect(0, 0, 1, 1).U16(0)
            .U8(0).U8(10).U8(20).U8(30)
            .U16(0x00FF).ToArray();

        Assert.Equal(new PictColor(10, 20, 30), PictReader.Decode(pict)[0, 0]);
    }

    // 32-bit direct pixels, 3 pixels wide (rowBytes 12, packed since >= 8) with the given packType and cmpCount.
    private static PictBitmap Direct32Row(int packType, int cmpCount, params byte[] pixData) =>
        Direct32Row(packType, cmpCount, PictQuickDraw.MacOS9, pixData);

    private static PictBitmap Direct32Row(int packType, int cmpCount, PictQuickDraw quickDraw, params byte[] pixData) =>
        PictReader.Decode(PictBuilder.V2(0, 0, 1, 3)
            .U16(0x009A).U16(0).U16(0xFF).U16(0x800C).Rect(0, 0, 1, 3)
            .U16(0).U16(packType).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(16).U16(32).U16(cmpCount).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .Rect(0, 0, 1, 3).Rect(0, 0, 1, 3).U16(0)
            .Bytes(pixData).Align()
            .U16(0x00FF).ToArray(), new PictDecodeOptions { QuickDraw = quickDraw });

    [Fact]
    public void Opcode0x92_IsDirectBitsRect()
    {
        // The ROM ignores bit 3 of the bitmap opcodes, so 0x92 is DirectBitsRect (1 pixel, rowBytes 4: unpacked).
        var pict = PictBuilder.V2(0, 0, 1, 1)
            .U16(0x0092).U16(0).U16(0xFF).U16(0x8004).Rect(0, 0, 1, 1)
            .U16(0).U16(1).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(16).U16(32).U16(3).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .Rect(0, 0, 1, 1).Rect(0, 0, 1, 1).U16(0)
            .U8(0).U8(10).U8(20).U8(30)
            .U16(0x00FF).ToArray();
        Assert.Equal(new PictColor(10, 20, 30), PictReader.Decode(pict, new PictDecodeOptions { QuickDraw = PictQuickDraw.MacRom })[0, 0]);
    }

    [Fact]
    public void DirectBits32_PackType4_OnMacOS9_OverflowingRowRepeatsItsFirstByte()
    {
        // 32 x 1, rowBytes 128, cmpCount 3: n = 96, Mac OS 9's packed buffer holds 96 bytes. A 97-byte row (one
        // literal run of 96) spills its last byte into the unpack buffer, which its first output byte overwrites, so
        // pixel 31's blue becomes pixel 0's red (1) instead of 96. The ROM reads it correctly.
        var b = PictBuilder.V2(0, 0, 1, 32)
            .U16(0x009A).U16(0).U16(0xFF).U16(0x8080).Rect(0, 0, 1, 32)
            .U16(0).U16(4).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(16).U16(32).U16(3).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .Rect(0, 0, 1, 32).Rect(0, 0, 1, 32).U16(0)
            .U8(97).U8(95);
        for (int i = 1; i <= 96; i++) b.U8(i);
        var pict = b.Align().U16(0x00FF).ToArray();
        Assert.Equal(new PictColor(32, 64, 1), PictReader.Decode(pict)[31, 0]);
        Assert.Equal(new PictColor(32, 64, 96), PictReader.Decode(pict, new PictDecodeOptions { QuickDraw = PictQuickDraw.MacRom })[31, 0]);
    }

    [Fact]
    public void Opcode0x92_OnMacOS9_IsReserved()
    {
        // Skipped as a word length + data; the PaintRect after it draws.
        var pict = PictBuilder.V2(0, 0, 1, 1).U16(0x0092).U16(4).Zeros(4).U16(0x0031).Rect(0, 0, 1, 1).U16(0x00FF).ToArray();
        Assert.Equal(new PictColor(0, 0, 0), PictReader.Decode(pict)[0, 0]);
    }

    [Fact]
    public void PackBits_OnMacOS9_FlagMinus128_IsARunOf129()
    {
        // 1-bit BitMap, rowBytes 130: $80 $FF = 129 bytes of $FF, then a literal $00.
        var pict = PictBuilder.V2(0, 0, 1, 1040).U16(0x0090).U16(130).Rect(0, 0, 1, 1040).Rect(0, 0, 1, 1040)
            .Rect(0, 0, 1, 1040).U16(0).U8(4).U8(0x80).U8(0xFF).U8(0x00).U8(0x00).Align().U16(0x00FF).ToArray();
        var bmp = PictReader.Decode(pict);
        Assert.Equal(new PictColor(0, 0, 0), bmp[1031, 0]);
        Assert.Equal(new PictColor(255, 255, 255), bmp[1032, 0]);
    }

    [Fact]
    public void BitsRect_WithRowsOf8BytesOrMore_IsReadPacked()
    {
        // 0x90 with rowBytes 8: a PackBits row (count 2: repeat 0xFF x 8), exactly like 0x98.
        var pict = PictBuilder.V2(0, 0, 1, 64).U16(0x0090).U16(8).Rect(0, 0, 1, 64).Rect(0, 0, 1, 64).Rect(0, 0, 1, 64)
            .U16(0).U8(2).U8(0xF9).U8(0xFF).Align().U16(0x00FF).ToArray();
        var bmp = PictReader.Decode(pict);
        Assert.Equal(new PictColor(0, 0, 0), bmp[63, 0]);
    }

    [Fact]
    public void IndexedPixMap_PackType1_IsStillPacked()
    {
        // 8-bit PixMap, packType 1, rowBytes 8: the ROM ignores packType for indexed data.
        var b = PictBuilder.V2(0, 0, 1, 8).U16(0x0098).U16(0x8008).Rect(0, 0, 1, 8)
            .U16(0).U16(1).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0).U16(0).U16(8).U16(1).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .U16(0).U16(0).U16(0).U16(1).U16(0).Rgb(0xFFFF, 0, 0).U16(1).Rgb(0, 0, 0xFFFF)   // ctab: 0 red, 1 blue
            .Rect(0, 0, 1, 8).Rect(0, 0, 1, 8).U16(0)
            .U8(2).U8(0xF9).U8(1).Align().U16(0x00FF);                                      // 8 x index 1
        Assert.Equal(new PictColor(0, 0, 255), PictReader.Decode(b.ToArray())[7, 0]);
    }

    [Fact]
    public void DirectBits16_PackType0_TakesTheThreeBytePath()
    {
        // pixelSize is never checked: a 16-bit map with packType 0 reads rowBytes*height*3/4 bytes as 0RGB longs.
        var pict = PictBuilder.V2(0, 0, 1, 4)
            .U16(0x009A).U16(0).U16(0xFF).U16(0x8008).Rect(0, 0, 1, 4)
            .U16(0).U16(0).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(16).U16(16).U16(3).U16(5).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .Rect(0, 0, 1, 4).Rect(0, 0, 1, 4).U16(0)
            .Bytes(0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC).Align()
            .U16(0x00FF).ToArray();
        var bmp = PictReader.Decode(pict, new PictDecodeOptions { QuickDraw = PictQuickDraw.MacRom });
        // Data = 00 12 34 56 | 00 78 9A BC read as 16-bit pixels 0x0012, 0x3456, 0x0078, 0x9ABC.
        Assert.Equal(new PictColor(0, 0, 0x94), bmp[0, 0]);
        Assert.Equal(new PictColor(0x6B, 0x10, 0xB5), bmp[1, 0]);
    }

    [Fact]
    public void DirectBits16_PackType0_OnMacOS9_IsWordPackBits()
    {
        // rowBytes 8 (4 pixels): count 9, flag 3 = 4 literal words.
        var pict = PictBuilder.V2(0, 0, 1, 4)
            .U16(0x009A).U16(0).U16(0xFF).U16(0x8008).Rect(0, 0, 1, 4)
            .U16(0).U16(0).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(16).U16(16).U16(3).U16(5).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .Rect(0, 0, 1, 4).Rect(0, 0, 1, 4).U16(0)
            .U8(9).U8(3).U16(0x7C00).U16(0x03E0).U16(0x001F).U16(0x7FFF).Align()
            .U16(0x00FF).ToArray();
        var bmp = PictReader.Decode(pict);
        Assert.Equal(new PictColor(255, 0, 0), bmp[0, 0]);
        Assert.Equal(new PictColor(0, 0, 255), bmp[2, 0]);
    }

    [Fact]
    public void DirectBits32_PackType0_IsThreeBytesPerPixelLikePackType2()
    {
        var bmp = Direct32Row(0, 3, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        Assert.Equal(new[] { new PictColor(1, 2, 3), new PictColor(4, 5, 6), new PictColor(7, 8, 9) },
            new[] { bmp[0, 0], bmp[1, 0], bmp[2, 0] });
    }

    [Fact]
    public void DirectBits32_PackType3_UnpacksWordChunkRows()
    {
        // Count 7: flag 0xFB repeats the next word 6 times → 12 bytes of 0x0A0B.
        var bmp = Direct32Row(3, 3, 7 - 3, 0xFB, 0x0A, 0x0B);
        Assert.Equal(new PictColor(0x0B, 0x0A, 0x0B), bmp[0, 0]);
    }

    [Fact]
    public void DirectBits32_PackType5AndUp_DiscardsTheRowsAndLeavesPixelsZero()
    {
        var bmp = Direct32Row(5, 3, 2, 0x01, 0x02);
        Assert.Equal(new PictColor(0, 0, 0), bmp[1, 0]);
    }

    [Fact]
    public void DirectBits32_PackType4_OnePlaneLandsOnTheBlueByte()
    {
        // cmpCount 1: the single plane is pixel byte 3 (the ROM; Mac OS 9 always reads 3 or 4 planes).
        var bmp = Direct32Row(4, 1, PictQuickDraw.MacRom, 4, 0x02, 0x10, 0x20, 0x30);
        Assert.Equal(new[] { new PictColor(0, 0, 0x10), new PictColor(0, 0, 0x20), new PictColor(0, 0, 0x30) },
            new[] { bmp[0, 0], bmp[1, 0], bmp[2, 0] });
    }

    [Fact]
    public void DirectBits32_PackType2_DropsThePadByte()
    {
        // 3 pixels, rowBytes 12: raw rows of 3 bytes per pixel.
        var pict = PictBuilder.V2(0, 0, 1, 3)
            .U16(0x009A).U16(0).U16(0xFF).U16(0x800C).Rect(0, 0, 1, 3)
            .U16(0).U16(2).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(16).U16(32).U16(3).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .Rect(0, 0, 1, 3).Rect(0, 0, 1, 3).U16(0)
            .U8(1).U8(2).U8(3).U8(4).U8(5).U8(6).U8(7).U8(8).U8(9).Align()
            .U16(0x00FF).ToArray();

        var bmp = PictReader.Decode(pict);
        Assert.Equal(new[] { new PictColor(1, 2, 3), new PictColor(4, 5, 6), new PictColor(7, 8, 9) },
            new[] { bmp[0, 0], bmp[1, 0], bmp[2, 0] });
    }

    [Fact]
    public void DirectBits32_PackType4_PlanesAreRowBytesOver4Wide()
    {
        // 3 pixels but rowBytes 16 (padded): each packed component plane is rowBytes/4 = 4 bytes wide.
        var pict = PictBuilder.V2(0, 0, 1, 3)
            .U16(0x009A).U16(0).U16(0xFF).U16(0x8010).Rect(0, 0, 1, 3)
            .U16(0).U16(4).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(16).U16(32).U16(3).U16(8).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .Rect(0, 0, 1, 3).Rect(0, 0, 1, 3).U16(0)
            .U8(13).U8(11)                                            // byteCount, literal of 12
            .U8(10).U8(11).U8(12).U8(0).U8(20).U8(21).U8(22).U8(0).U8(30).U8(31).U8(32).U8(0)
            .Align().U16(0x00FF).ToArray();

        var bmp = PictReader.Decode(pict);
        Assert.Equal(new[] { new PictColor(10, 20, 30), new PictColor(11, 21, 31), new PictColor(12, 22, 32) },
            new[] { bmp[0, 0], bmp[1, 0], bmp[2, 0] });
    }

    [Fact]
    public void DirectBits16_PackType3_UnpacksWordChunks()
    {
        // 4 pixels of xrgb1555, rowBytes 8: one repeat packet of 4 words (0x7C00 = pure red).
        var pict = PictBuilder.V2(0, 0, 1, 4)
            .U16(0x009A).U16(0).U16(0xFF).U16(0x8008).Rect(0, 0, 1, 4)
            .U16(0).U16(3).U16(0).U16(0).U16(0x48).U16(0).U16(0x48).U16(0)
            .U16(16).U16(16).U16(3).U16(5).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0)
            .Rect(0, 0, 1, 4).Rect(0, 0, 1, 4).U16(0)
            .U8(3).U8(0xFD).U8(0x7C).U8(0x00)
            .Align().U16(0x00FF).ToArray();

        var bmp = PictReader.Decode(pict);
        Assert.All(Enumerable.Range(0, 4), x => Assert.Equal(new PictColor(255, 0, 0), bmp[x, 0]));
    }

    [Fact]
    public void PictFile_WithNonZeroApplicationHeader_IsDecoded()
    {
        // MacDraw fills the 512-byte .pict header with its own data ("DRWG..."); the picture still follows it.
        var header = new byte[PictHeader.FileHeaderSize];
        "DRWGMD"u8.CopyTo(header);
        header[20] = 0x11; header[21] = 0x01;   // decoy: not at the picture's version-opcode offset
        var picture = PictBuilder.V1(0, 0, 1, 8)
            .U8(0x90).U16(2).Rect(0, 0, 1, 8).Rect(0, 0, 1, 8).Rect(0, 0, 1, 8).U16(0).U8(0x80).U8(0)
            .U8(0xFF).ToArray();

        var bmp = PictReader.Decode(header.Concat(picture).ToArray());

        Assert.Equal((8, 1), (bmp.Width, bmp.Height));
        Assert.Equal(Black, bmp[0, 0]);
    }

    [Fact]
    public void ReadInfo_ReportsHeaderWithoutDecoding()
    {
        using var ms = new MemoryStream(PictReaderTests.Write(PictReaderTests.TestCard(40, 20)));
        var info = PictHeader.ReadInfo(ms);

        Assert.Equal(new PictRect(0, 0, 20, 40), info.Bounds);
        Assert.Equal(2, info.Version);
        Assert.True(info.IsExtendedVersion2);
        Assert.Equal(72.0, info.HorizontalResolution);
    }
}
