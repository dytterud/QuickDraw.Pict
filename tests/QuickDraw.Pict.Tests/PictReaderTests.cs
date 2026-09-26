using Xunit;

namespace QuickDraw.Pict.Tests;

// Library-agnostic core: PictWriter/PictReader round trip, bitmap opcodes, and the IPictRenderer hand-off
// for the vector/text opcodes the core does not rasterize itself.
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
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(83, 4)]    // rowBytes 249: byte-sized PackBits row counts
    [InlineData(84, 4)]    // rowBytes 252: word-sized PackBits row counts
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
    public void ReadFrameSize_SkipsTheFileHeader()
    {
        using var ms = new MemoryStream(Write(TestCard(40, 20)));
        Assert.Equal((40, 20), PictHeader.ReadFrameSize(ms));
    }

    [Fact]
    public void VectorOpcodes_WithoutRenderer_AreConsumedAndSkipped()
    {
        var pict = PictBuilder.V2(0, 0, 4, 4)
            .U16(0x001A).Rgb(0xFFFF, 0, 0)
            .U16(0x0031).Rect(1, 1, 3, 3)
            .U16(0x00FF).ToArray();

        var bmp = PictReader.Decode(pict);

        Assert.Equal((4, 4), (bmp.Width, bmp.Height));
        Assert.All(bmp.Pixels, b => Assert.Equal(0, b));   // untouched canvas stays transparent
    }

    [Fact]
    public void ShapeOpcodes_ReachRendererInCanvasSpaceWithQuickDrawVerbs()
    {
        // Frame origin (top 10, left 20) is subtracted, so the canvas starts at (0,0).
        var pict = PictBuilder.V2(10, 20, 14, 24)
            .U16(0x001A).Rgb(0xFFFF, 0, 0x8000)          // RGBFgCol
            .U16(0x001B).Rgb(0, 0xFFFF, 0)               // RGBBkCol
            .U16(0x0031).Rect(11, 21, 13, 23)            // PaintRect
            .U16(0x0007).Point(2, 3)                     // PnSize (h = 3)
            .U16(0x0030).Rect(10, 20, 12, 22)            // FrameRect
            .U16(0x003A)                                 // EraseSameRect
            .U16(0x0053).Rect(10, 20, 14, 24)            // InvertOval
            .U16(0x00FF).ToArray();
        var renderer = new RecordingRenderer();

        PictReader.Decode(pict, _ => renderer);

        var fore = new PictColor(255, 0, 128);
        var back = new PictColor(0, 255, 0);
        Assert.Collection(renderer.Calls,
            c => Assert.Equal(("Fill", PictShapeKind.Rectangle, new PictRectangleF(1, 1, 2, 2), fore, 0), c),
            c => Assert.Equal(("Frame", PictShapeKind.Rectangle, new PictRectangleF(0, 0, 2, 2), fore, 3), c),
            c => Assert.Equal(("Fill", PictShapeKind.Rectangle, new PictRectangleF(0, 0, 2, 2), back, 0), c),
            c => Assert.Equal(("Invert", PictShapeKind.Oval, new PictRectangleF(0, 0, 4, 4), default(PictColor), 0), c));
        Assert.True(renderer.Disposed);
    }

    [Fact]
    public void TextOpcodes_ReachRendererWithStyleAndAdvanceThePen()
    {
        var pict = PictBuilder.V2(0, 0, 20, 40)
            .U16(0x0003).U16(21)                         // TxFont
            .U16(0x0004).U8(1).Align()                   // TxFace bold
            .U16(0x000D).U16(9)                          // TxSize
            .U16(0x0028).Point(15, 2).Text("Hi").Align() // LongText at h=2, v=15
            .U16(0x0029).U8(3).Text("!").Align()         // DHText: 3 past the pen
            .U16(0x00FF).ToArray();
        var renderer = new RecordingRenderer { TextAdvance = 10.4f };

        PictReader.Decode(pict, _ => renderer);

        var style = new PictTextStyle(21, 1, 9);
        // pen after "Hi" = 2 + round(10.4) = 12; DHText adds 3.
        Assert.Equal(new[] { ("Hi", new PictPoint(2, 15), style), ("!", new PictPoint(15, 15), style) }, renderer.Texts);
    }

    private static PictColor[] Row(PictBitmap bmp, int y) =>
        Enumerable.Range(0, bmp.Width).Select(x => bmp[x, y]).ToArray();

    private sealed class RecordingRenderer : IPictRenderer, IDisposable
    {
        public readonly List<(string Op, PictShapeKind Kind, PictRectangleF Bounds, PictColor Color, int Pen)> Calls = new();
        public readonly List<(string Text, PictPoint Origin, PictTextStyle Style)> Texts = new();
        public float TextAdvance;
        public bool Disposed;

        public void Fill(in PictShape shape, PictColor color, PictRectangleF? clip) =>
            Calls.Add(("Fill", shape.Kind, shape.Bounds, color, 0));
        public void Frame(in PictShape shape, PictColor color, int penWidth, PictRectangleF? clip) =>
            Calls.Add(("Frame", shape.Kind, shape.Bounds, color, penWidth));
        public void Invert(in PictShape shape) =>
            Calls.Add(("Invert", shape.Kind, shape.Bounds, default, 0));
        public void DrawPolyline(ReadOnlySpan<PictPoint> points, PictColor color, int penWidth, PictRectangleF? clip) =>
            Calls.Add(("Polyline", PictShapeKind.Polygon, default, color, penWidth));
        public float DrawText(string text, PictPoint baselineOrigin, PictTextStyle style, PictColor color, PictRectangleF? clip)
        {
            Texts.Add((text, baselineOrigin, style));
            return TextAdvance;
        }
        public void Dispose() => Disposed = true;
    }
}
