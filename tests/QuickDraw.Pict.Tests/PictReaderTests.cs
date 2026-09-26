using Xunit;

namespace QuickDraw.Pict.Tests;

// Library-agnostic core: PictWriter/PictReader round trip and bitmap opcodes.
public class PictReaderTests
{
    // Opaque test card: a flat left half (PackBits repeat runs) and a noisy right half (literal runs).
    internal static PictBitmap TestCard(int width, int height)
    {
        var bmp = new PictBitmap(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bmp[x, y] = x < width / 2
                    ? new PictColor(10, 200, 30)
                    : new PictColor((byte)(x * 37 + y), (byte)(x * 11 + y * 7), (byte)(x ^ y));
        return bmp;
    }

    internal static byte[] Write(PictBitmap bmp)
    {
        using var ms = new MemoryStream();
        PictWriter.Write(ms, bmp);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(1, 1)]     // rowBytes 4 < 8: rows stored unpacked
    [InlineData(3, 2)]
    [InlineData(62, 4)]    // rowBytes 248: byte-sized PackBits row counts
    [InlineData(63, 4)]    // rowBytes 252: word-sized PackBits row counts
    [InlineData(300, 5)]   // runs longer than 128 bytes split across PackBits packets
    public void WrittenPict_ReadsBackPixelIdentical(int w, int h)
    {
        var src = TestCard(w, h);
        var decoded = PictReader.Decode(Write(src));
        Assert.Equal(w, decoded.Width);
        Assert.Equal(h, decoded.Height);
        Assert.Equal(src.Pixels, decoded.Pixels);
    }

    [Fact]
    public void WrittenPict_HasZeroFileHeaderThenV2Signature()
    {
        var bytes = Write(TestCard(8, 2));
        Assert.All(bytes[..512], b => Assert.Equal(0, b));
        Assert.Equal(new byte[] { 0x00, 0x11, 0x02, 0xFF, 0x0C, 0x00 }, bytes[522..528]);
        Assert.Equal(new byte[] { 0x00, 0xFF }, bytes[^2..]);
        Assert.True(PictHeader.IsPictFile(bytes));
        Assert.True(PictHeader.IsPicture(bytes.AsSpan(512)));
        Assert.False(PictHeader.IsPicture(bytes));   // the zero file header is not itself a picture
    }

    [Fact]
    public void PngSignature_IsNotAPicture()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52 };
        Assert.False(PictHeader.IsPicture(png));
    }

    [Fact]
    public void V1BitsRect_DecodesMonochromeBitmap()
    {
        // 8x2 1-bit bitmap, rowBytes 2: set bits are black, clear bits white.
        var pict = PictBuilder.V1(0, 0, 2, 8)
            .U8(0x90).U16(2).Rect(0, 0, 2, 8)           // BitsRect: rowBytes (no PixMap flag), bounds
            .Rect(0, 0, 2, 8).Rect(0, 0, 2, 8).U16(0)   // srcRect, dstRect, mode
            .U8(0b1010_0000).U8(0).U8(0b0101_0000).U8(0)
            .U8(0xFF).ToArray();

        var bmp = PictReader.Decode(pict);

        var black = new PictColor(0, 0, 0);
        var white = new PictColor(255, 255, 255);
        Assert.Equal(new[] { black, white, black, white, white, white, white, white }, Row(bmp, 0));
        Assert.Equal(new[] { white, black, white, black, white, white, white, white }, Row(bmp, 1));
    }

    [Fact]
    public void ShapeOpcodes_DrawInCanvasSpace()
    {
        // Frame origin (top 10, left 20) is subtracted, so the canvas starts at (0,0).
        var pict = PictBuilder.V2(10, 20, 14, 24)
            .U16(0x001A).Rgb(0xFFFF, 0, 0x8000)          // RGBFgCol
            .U16(0x0031).Rect(11, 21, 13, 23)            // PaintRect
            .U16(0x00FF).ToArray();

        var bmp = PictReader.Decode(pict);

        Assert.Equal(new PictColor(0, 0, 0, 0), bmp[0, 0]);
        Assert.Equal(new PictColor(255, 0, 128), bmp[1, 1]);
        Assert.Equal(new PictColor(255, 0, 128), bmp[2, 2]);
        Assert.Equal(new PictColor(0, 0, 0, 0), bmp[3, 3]);
    }

    [Fact]
    public void TextOpcodes_PassStyleToTheFallback()
    {
        var pict = PictBuilder.V2(0, 0, 20, 40)
            .U16(0x0003).U16(21)                         // TxFont
            .U16(0x0004).U8(1).Align()                   // TxFace bold
            .U16(0x000D).U16(9)                          // TxSize
            .U16(0x0028).Point(15, 2).Text("Hi").Align() // LongText at h=2, v=15
            .U16(0x0029).U8(3).Text("!").Align()         // DHText: 3 past the pen
            .U16(0x00FF).ToArray();
        var fallback = new RecordingFallback();

        PictReader.Decode(pict, new PictDecodeOptions { TextFallback = fallback });

        var style = new PictTextStyle(21, 1, 9);
        Assert.Equal(new[] { ("Hi", style), ("!", style) }, fallback.Calls);
    }

    private static PictColor[] Row(PictBitmap bmp, int y) =>
        Enumerable.Range(0, bmp.Width).Select(x => bmp[x, y]).ToArray();

    private sealed class RecordingFallback : IPictTextFallback
    {
        public readonly List<(string Text, PictTextStyle Style)> Calls = new();

        public PictTextMask? Render(string text, PictTextStyle style)
        {
            Calls.Add((text, style));
            return new PictTextMask(1, 1, 0, 1, new byte[] { 1 }, 10.4f);
        }
    }
}
