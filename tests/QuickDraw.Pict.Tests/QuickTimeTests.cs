using System.Text;
using QuickDraw.Pict.ImageSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace QuickDraw.Pict.Tests;

// The CompressedQuickTime opcode: image description, matrix placement, fallback skipping, the codec hook, and the
// built-in decompressors on hand-assembled data (the samples in the corpora are checked against ffmpeg separately).
public class QuickTimeTests
{
    private static readonly PictColor Red = new(255, 0, 0), Green = new(0, 255, 0), Blue = new(0, 0, 255);

    // 0x8200 block: version, matrix (scale sx/sy, translate tx/ty), no matte, mode, srcRect, no mask, image description.
    private static byte[] QuickTimeOpcode(string codec, int width, int height, int depth, byte[] data,
        int sx = 1, int sy = 1, int tx = 0, int ty = 0, int clutId = -1, int mode = 64)
    {
        var b = new PictBuilder();
        void L(int v) => b.U16(v >> 16).U16(v & 0xFFFF);
        b.U16(0);
        L(sx << 16); L(0); L(0);
        L(0); L(sy << 16); L(0);
        L(tx << 16); L(ty << 16); L(1 << 30);
        L(0); b.Rect(0, 0, 0, 0);                                        // matte size, matte rect
        b.U16(mode).Rect(0, 0, height, width);                          // mode, srcRect
        L(0); L(0);                                                     // accuracy, mask size
        L(86); b.Bytes(Encoding.ASCII.GetBytes(codec)).Zeros(8).U16(0).U16(0).Zeros(4).Zeros(8);
        b.U16(width).U16(height); L(72 << 16); L(72 << 16); L(data.Length); b.U16(1).Zeros(32).U16(depth).U16(clutId);
        b.Bytes(data);
        var block = b.ToArray();
        return new PictBuilder().U16(0x8200).U16(block.Length >> 16).U16(block.Length & 0xFFFF).Bytes(block).ToArray();
    }

    private static PictBitmap Draw(int width, int height, Action<PictBuilder> ops, PictDecodeOptions? options = null)
    {
        var b = PictBuilder.V2(0, 0, height, width);
        ops(b);
        b.Align().U16(0x00FF);
        return PictReader.Decode(b.ToArray(), options);
    }

    private static byte[] Raw32(params PictColor[] pixels) =>
        pixels.SelectMany(c => new byte[] { 0, c.R, c.G, c.B }).ToArray();

    // An UncompressedQuickTime block: version, matrix (a, d as given; w = 1.0 in 2.30), no matte, then a 16 x 1
    // BitsRect (rowBytes 2, unpacked) with pixels ####........####.
    private static byte[] Uncompressed(int a, int d) => new PictBuilder().U16(0)
        .U16(a).U16(0).Zeros(4).Zeros(4)                         // a, b, u
        .Zeros(4).U16(d).U16(0).Zeros(4)                         // c, d, v
        .Zeros(4).Zeros(4).U16(0x4000).U16(0)                    // h, v, w
        .Zeros(4).Rect(0, 0, 0, 0)                               // matte size, rect
        .U16(0x0098).U16(2).Rect(0, 0, 1, 16).Rect(0, 0, 1, 16).Rect(0, 0, 1, 16).U16(0).U8(0xF0).U8(0x0F)
        .ToArray();

    [Fact]
    public void UncompressedQuickTime_DrawsItsBitmapOpcodeAndSkipsTheFallback()
    {
        var block = Uncompressed(1, 1);
        var pict = PictBuilder.V2(0, 0, 1, 16).U16(0x8201).U16(0).U16(block.Length).Bytes(block).Align()
            .U16(0x0007).U16(0x00AE).U16(10).U16(0x0031).Rect(0, 0, 1, 16)    // fallback: PaintRect, skipped
            .U16(0x00FF).ToArray();
        var bmp = PictReader.Decode(pict);
        Assert.Equal("####wwwwwwww####", string.Concat(Enumerable.Range(0, 16).Select(x =>
            bmp[x, 0] == new PictColor(0, 0, 0) ? '#' : bmp[x, 0] == new PictColor(255, 255, 255) ? 'w' : '?')));
    }

    [Fact]
    public void UncompressedQuickTime_WithAScalingMatrix_PlacesTheImageWhereTheMatrixPutsIt()
    {
        var block = Uncompressed(2, 1);
        var pict = PictBuilder.V2(0, 0, 1, 32).U16(0x8201).U16(0).U16(block.Length).Bytes(block).Align()
            .U16(0x00FF).ToArray();
        var bmp = PictReader.Decode(pict);
        Assert.Equal(new PictColor(0, 0, 0), bmp[7, 0]);
        Assert.Equal(new PictColor(255, 255, 255), bmp[8, 0]);
        Assert.Equal(new PictColor(0, 0, 0), bmp[24, 0]);
    }

    [Fact]
    public void CompressedQuickTime_Raw32_IsDrawnWhereTheMatrixPutsIt()
    {
        var bmp = Draw(6, 3, b => b.Bytes(QuickTimeOpcode("raw ", 2, 1, 32, Raw32(Red, Blue), sx: 2, sy: 2, tx: 1, ty: 1)));
        Assert.Equal(new[] { Red, Red, Blue, Blue }, new[] { bmp[1, 1], bmp[2, 2], bmp[3, 1], bmp[4, 2] });
        Assert.Equal(0, bmp[0, 0].A);
        Assert.Equal(0, bmp[5, 1].A);
    }

    [Fact]
    public void CompressedQuickTime_SkipsTheFallbackAfterThePnSizeMarker()
    {
        // PnSize (v 0x00AE, h 10) then 10 bytes of "fallback" drawing: a paint rect over everything, then padding.
        var bmp = Draw(2, 1, b => b.Bytes(QuickTimeOpcode("raw ", 2, 1, 32, Raw32(Red, Blue))).Align()
            .U16(0x0007).U16(0x00AE).U16(10).U16(0x0031).Rect(0, 0, 1, 2));
        Assert.Equal(new[] { Red, Blue }, new[] { bmp[0, 0], bmp[1, 0] });
    }

    [Fact]
    public void CompressedQuickTime_SuppressesABitmapThatFollowsIntoTheSameRect()
    {
        var bmp = Draw(8, 1, b => b.Bytes(QuickTimeOpcode("raw ", 8, 1, 32, Raw32(Enumerable.Repeat(Green, 8).ToArray()))).Align()
            .U16(0x0090).U16(2).Rect(0, 0, 1, 8).Rect(0, 0, 1, 8).Rect(0, 0, 1, 8).U16(0).U8(0xFF).U8(0));
        Assert.Equal(Green, bmp[3, 0]);
    }

    [Fact]
    public void CompressedQuickTime_UnknownCodec_AsksTheImageCodecThenFallsBack()
    {
        var codec = new RecordingCodec();
        var bmp = Draw(2, 1, b => b.Bytes(QuickTimeOpcode("zzzz", 2, 1, 24, new byte[] { 1, 2, 3 })).Align()
            .U16(0x0031).Rect(0, 0, 1, 2), new PictDecodeOptions { ImageCodec = codec });
        Assert.Equal("zzzz", codec.Seen?.CodecType);
        Assert.Equal(new byte[] { 1, 2, 3 }, codec.Data);
        Assert.Equal(new PictColor(0, 0, 0), bmp[0, 0]);                     // the picture's own drawing
    }

    private sealed class RecordingCodec : IPictImageCodec
    {
        public PictImageDescription? Seen;
        public byte[]? Data;
        public PictBitmap? Decode(PictImageDescription description, byte[] data) { Seen = description; Data = data; return null; }
    }

    [Fact]
    public void ImageSharpImageCodec_DecodesPng()
    {
        using var png = new Image<Rgba32>(2, 1);
        png[0, 0] = new Rgba32(255, 0, 0);
        png[1, 0] = new Rgba32(0, 0, 255);
        using var ms = new MemoryStream();
        png.SaveAsPng(ms);
        var bmp = Draw(2, 1, b => b.Bytes(QuickTimeOpcode("png ", 2, 1, 32, ms.ToArray())),
            new PictDecodeOptions { ImageCodec = new ImageSharpImageCodec() });
        Assert.Equal(new[] { Red, Blue }, new[] { bmp[0, 0], bmp[1, 0] });
    }

    // ---- built-in codecs ----

    private static PictBitmap Decode(string codec, int w, int h, int depth, byte[] data, int clutId = -1)
    {
        var d = new PictImageDescription(codec, w, h, depth, clutId, 72, 72, "")
        { ColorTable = StandardColorTables.ForId(clutId) ?? StandardColorTables.ForDepth(depth) };
        return QuickTimeCodecs.Decode(d, data) ?? throw new Xunit.Sdk.XunitException("undecodable");
    }

    [Fact]
    public void StandardColorTables_EightBitIsTheCubeThenRampsThenBlack()
    {
        var t = StandardColorTables.ForId(8)!;
        Assert.Equal(new PictColor(255, 255, 255), t[0]);
        Assert.Equal(new PictColor(255, 255, 204), t[1]);
        Assert.Equal(new PictColor(0, 0, 51), t[214]);
        Assert.Equal(new PictColor(0xEE, 0, 0), t[215]);
        Assert.Equal(new PictColor(0x11, 0x11, 0x11), t[254]);
        Assert.Equal(new PictColor(0, 0, 0), t[255]);
    }

    [Fact]
    public void Raw8_UsesTheStandardColorTable()
    {
        var img = Decode("raw ", 2, 1, 8, new byte[] { 0, 255 });
        Assert.Equal(new[] { new PictColor(255, 255, 255), new PictColor(0, 0, 0) }, new[] { img[0, 0], img[1, 0] });
    }

    [Fact]
    public void RoadPizza_SingleColorAndFourColorBlocks()
    {
        // 8x4: block 0 one color (red 0x7C00), block 1 four-color from A = blue, B = red, indices row 0: 3 0 1 2.
        var data = new byte[] { 0xE1, 0, 0, 16, 0xA0, 0x7C, 0x00, 0xC0, 0x00, 0x1F, 0x7C, 0x00, 0xC6, 0, 0, 0 };
        var img = Decode("rpza", 8, 4, 16, data);
        Assert.Equal(Red, img[0, 0]);
        Assert.Equal(new PictColor(0, 0, 255), img[4, 0]);                  // index 3 = A
        Assert.Equal(Red, img[5, 0]);                                       // index 0 = B
        Assert.Equal(new PictColor(165, 0, 82), img[6, 0]);                 // (11A + 21B) >> 5: r 20, b 10 (5-bit)
        Assert.Equal(Red, img[4, 1]);
    }

    [Fact]
    public void Graphics_TwoColorBlockAndRepeat()
    {
        // 8x4 smc: block 0 two colors (0 white, 255 black) with flags 0x8000 (first pixel black), block 1 repeats it.
        var data = new byte[] { 0x80, 0, 0, 12, 0x80, 0, 255, 0x80, 0x00, 0x20 };
        var img = Decode("smc ", 8, 4, 8, data, clutId: 8);
        Assert.Equal(new PictColor(0, 0, 0), img[0, 0]);
        Assert.Equal(new PictColor(255, 255, 255), img[1, 0]);
        Assert.Equal(new PictColor(0, 0, 0), img[4, 0]);
    }

    [Fact]
    public void Animation24_SkipLiteralAndRepeatCodes()
    {
        // 4x1 rle, 24-bit: header 0, line: skip 2 (1 pixel), literal 1 red, repeat 2 green, end of line, then end.
        var data = new byte[] { 0, 0, 0, 20, 0, 0, 2, 1, 255, 0, 0, 0xFE, 0, 255, 0, 0xFF, 0 };
        var img = Decode("rle ", 4, 1, 24, data);
        Assert.Equal(0, img[0, 0].A);
        Assert.Equal(new[] { Red, Green, Green }, new[] { img[1, 0], img[2, 0], img[3, 0] });
    }

    [Fact]
    public void Planar24_DecodesPackBitsPlanes()
    {
        // 2x1: row counts for R, G, B planes (2 bytes each: repeat), then the planes.
        var data = new byte[] { 0, 2, 0, 2, 0, 2, 0xFF, 200, 0xFF, 100, 0xFF, 50 };
        var img = Decode("8BPS", 2, 1, 24, data);
        Assert.Equal(new PictColor(200, 100, 50), img[1, 0]);
    }

    [Fact]
    public void Targa_BottomUpTrueColor()
    {
        var header = new byte[18];
        header[2] = 2; header[12] = 1; header[14] = 2; header[16] = 24;   // uncompressed true-color 1x2, bottom-up
        var data = header.Concat(new byte[] { 255, 0, 0, 0, 0, 255 }).ToArray();   // BGR: bottom blue, top red
        var img = Decode("tga ", 1, 2, 24, data);
        Assert.Equal(Red, img[0, 0]);
        Assert.Equal(new PictColor(0, 0, 255), img[0, 1]);
    }
}
