using QuickDraw.Pict.ImageSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Metadata.Profiles.Icc;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace QuickDraw.Pict.Tests;

// PictWriter formats (indexed 1/2/4/8, 16-bit, 32-bit with and without alpha), resolution, ICC, wide images, and the
// ImageSharp encoder's options. Everything round-trips through PictReader.
public class WriterTests
{
    private static PictBitmap Card(int w, int h, Func<int, int, PictColor> color)
    {
        var bmp = new PictBitmap(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) bmp[x, y] = color(x, y);
        return bmp;
    }

    private static byte[] Save(PictBitmap bmp, PictWriteOptions options)
    {
        using var ms = new MemoryStream();
        PictWriter.Write(ms, bmp, options);
        return ms.ToArray();
    }

    private static void AssertSame(PictBitmap expected, PictBitmap actual, Func<PictColor, PictColor>? expect = null)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
            {
                var e = expect == null ? expected[x, y] : expect(expected[x, y]);
                Assert.True(e == actual[x, y], $"({x},{y}): expected {e}, got {actual[x, y]}");
            }
    }

    private static readonly PictColor[] Four = { new(255, 255, 255), new(255, 0, 0), new(0, 0, 255), new(0, 0, 0) };

    // Widths cover unpacked rows (rowBytes < 8), byte counts and word counts (rowBytes > 250).
    [Theory]
    [InlineData(PictPixelFormat.Indexed1, 3)] [InlineData(PictPixelFormat.Indexed1, 70)] [InlineData(PictPixelFormat.Indexed1, 2100)]
    [InlineData(PictPixelFormat.Indexed2, 5)] [InlineData(PictPixelFormat.Indexed2, 1100)]
    [InlineData(PictPixelFormat.Indexed4, 9)] [InlineData(PictPixelFormat.Indexed4, 600)]
    [InlineData(PictPixelFormat.Indexed8, 5)] [InlineData(PictPixelFormat.Indexed8, 300)]
    [InlineData(PictPixelFormat.Rgb555, 3)] [InlineData(PictPixelFormat.Rgb555, 130)]
    [InlineData(PictPixelFormat.Rgb888, 1)] [InlineData(PictPixelFormat.Rgb888, 100)]
    [InlineData(PictPixelFormat.Argb8888, 1)] [InlineData(PictPixelFormat.Argb8888, 100)]
    public void Formats_RoundTrip(PictPixelFormat format, int width)
    {
        bool indexed = format <= PictPixelFormat.Indexed8;
        int colors = format switch { PictPixelFormat.Indexed1 => 2, PictPixelFormat.Indexed2 => 4, PictPixelFormat.Indexed4 => 16, _ => 256 };
        // Indexed: exactly `colors` distinct colors (black and white for 1 bit); direct: arbitrary RGBA.
        PictColor IndexedColor(int i) => colors == 2
            ? (i % 2 == 0 ? new PictColor(0, 0, 0) : new PictColor(255, 255, 255))
            : new PictColor((byte)(i * 255 / (colors - 1)), (byte)(255 - i), (byte)(i * 7 % 256));
        var src = Card(width, 3, (x, y) => indexed
            ? IndexedColor((x * 37 + y * 11) % colors)
            : new PictColor((byte)(x * 5), (byte)(y * 90 + x), (byte)(255 - x), (byte)(x * 3 + 1)));
        var bytes = Save(src, new PictWriteOptions { Format = format });
        var back = PictReader.Decode(bytes, new PictDecodeOptions { PreserveAlpha = true });
        Func<PictColor, PictColor>? expect = format switch
        {
            PictPixelFormat.Rgb555 => c => new PictColor(Five(c.R), Five(c.G), Five(c.B)),
            PictPixelFormat.Argb8888 => null,
            _ => c => new PictColor(c.R, c.G, c.B),
        };
        AssertSame(src, back, expect);
    }

    private static byte Five(byte v) => (byte)((v >> 3 << 3) | (v >> 5));

    [Fact]
    public void Writer_IncompressibleRows_StayReadableByMacOS9()
    {
        // 32 distinct pixels per row pack to more bytes than Mac OS 9's plane buffer holds, so the strip is written
        // unpacked (packType 1); both QuickDraws read it back exactly.
        var bmp = new PictBitmap(32, 2);
        for (int i = 0; i < 64; i++)
        {
            bmp.Pixels[4 * i] = (byte)(i * 7); bmp.Pixels[4 * i + 1] = (byte)(i * 13 + 1);
            bmp.Pixels[4 * i + 2] = (byte)(i * 29 + 2); bmp.Pixels[4 * i + 3] = 255;
        }
        using var ms = new MemoryStream();
        PictWriter.Write(ms, bmp, new PictWriteOptions { FileHeader = false });
        foreach (var quickDraw in new[] { PictQuickDraw.MacOS9, PictQuickDraw.MacRom })
            Assert.Equal(bmp.Pixels, PictReader.Decode(ms.ToArray(), new PictDecodeOptions { QuickDraw = quickDraw }).Pixels);
    }

    [Fact]
    public void Indexed1_WhiteAndBlack_IsWrittenAsAPlainBitMap()
    {
        var bytes = Save(Card(80, 1, (x, _) => x % 2 == 0 ? new PictColor(0, 0, 0) : new PictColor(255, 255, 255)),
            new PictWriteOptions { Format = PictPixelFormat.Indexed1, FileHeader = false });
        int op = FindOpcode(bytes, 0x0098);
        Assert.Equal(10, (bytes[op + 2] << 8) | bytes[op + 3]);                           // rowBytes 10, no PixMap flag
    }

    [Fact]
    public void Indexed_WithAPalette_MapsToNearestColors()
    {
        // (0,0,0), (80,0,0), (160,0,0), (240,0,0) against white, red, blue, black.
        var src = Card(4, 1, (x, _) => new PictColor((byte)(x * 80), 0, 0));
        var back = PictReader.Decode(Save(src, new PictWriteOptions { Format = PictPixelFormat.Indexed2, Palette = Four }));
        Assert.Equal(new[] { Four[3], Four[3], Four[1], Four[1] }, new[] { back[0, 0], back[1, 0], back[2, 0], back[3, 0] });
    }

    [Fact]
    public void Indexed_TooManyColorsWithoutAPalette_Throws()
    {
        var src = Card(5, 1, (x, _) => new PictColor((byte)x, 0, 0));
        Assert.Throws<ArgumentException>(() => Save(src, new PictWriteOptions { Format = PictPixelFormat.Indexed2 }));
    }

    [Fact]
    public void Resolution_IsStoredWithAPictureFrameOfThePhysicalSize()
    {
        var src = Card(40, 20, (x, y) => new PictColor((byte)x, (byte)y, 0));
        var bytes = Save(src, new PictWriteOptions { HorizontalResolution = 144, VerticalResolution = 144 });
        var back = PictReader.Decode(bytes);
        Assert.Equal((144.0, 144.0), (back.Info!.HorizontalResolution, back.Info.VerticalResolution));
        Assert.Equal(new PictRect(0, 0, 10, 20), back.Info.PictureFrame);
        AssertSame(src, back);
        Assert.Equal(20, PictReader.Decode(bytes, new PictDecodeOptions { Resolution = PictResolution.PictureFrame }).Width);
    }

    [Fact]
    public void IccProfile_RoundTripsAcrossSeveralComments()
    {
        var icc = Enumerable.Range(0, 70000).Select(i => (byte)(i * 7)).ToArray();
        var back = PictReader.Decode(Save(Card(2, 2, (_, _) => new PictColor(1, 2, 3)), new PictWriteOptions { IccProfile = icc }));
        Assert.Equal(icc, back.Info!.IccProfile);
    }

    [Theory]
    [InlineData(PictPixelFormat.Rgb888, 4200)]
    [InlineData(PictPixelFormat.Rgb555, 8300)]
    [InlineData(PictPixelFormat.Indexed8, 16500)]
    public void WideImages_AreSplitIntoStrips(PictPixelFormat format, int width)
    {
        var src = Card(width, 2, (x, y) => format == PictPixelFormat.Indexed8
            ? (x % 2 == 0 ? new PictColor(255, 0, 0) : new PictColor(0, 0, 255))
            : new PictColor((byte)(x & 0xF8), (byte)((x >> 8) << 3), (byte)(y * 8)));
        var back = PictReader.Decode(Save(src, new PictWriteOptions { Format = format }));
        AssertSame(src, back, format == PictPixelFormat.Rgb555 ? c => new PictColor(Five(c.R), Five(c.G), Five(c.B)) : null);
    }

    [Fact]
    public void BarePicture_WithoutTheFileHeader_Decodes()
    {
        var src = Card(3, 2, (x, _) => new PictColor((byte)(x * 40), 7, 9));
        var bytes = Save(src, new PictWriteOptions { FileHeader = false });
        Assert.True(PictHeader.IsPicture(bytes));
        AssertSame(src, PictReader.Decode(bytes));
    }

    private static int FindOpcode(byte[] data, int op)
    {
        for (int i = 0; i + 1 < data.Length; i += 2)
            if (((data[i] << 8) | data[i + 1]) == op) return i;
        throw new Xunit.Sdk.XunitException("opcode not found");
    }

    // ---- ImageSharp encoder ----

    private static readonly Configuration Config = CreateConfiguration();

    private static Configuration CreateConfiguration()
    {
        var c = Configuration.Default.Clone();
        c.Configure(new PictConfigurationModule());
        return c;
    }

    private static Image<Rgba32> Encoded(Image image, PictEncoder encoder, bool preserveAlpha = false)
    {
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        ms.Position = 0;
        return PictDecoder.Instance.Decode<Rgba32>(new PictDecoderOptions
        {
            GeneralOptions = new DecoderOptions { Configuration = Config }, PreserveAlpha = preserveAlpha,
        }, ms);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(8)]
    public void Encoder_IndexedDepths_KeepImagesWithFewEnoughColors(int bits)
    {
        using var src = new Image<Rgba32>(Config, 17, 3);
        var palette = new[] { new Rgba32(255, 255, 255), new Rgba32(0, 0, 0), new Rgba32(200, 30, 30), new Rgba32(20, 20, 220) };
        int n = Math.Min(4, 1 << bits);
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 17; x++) src[x, y] = palette[(x + y) % n];
        using var back = Encoded(src, new PictEncoder { BitsPerPixel = bits });
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 17; x++) Assert.Equal(src[x, y], back[x, y]);
    }

    [Fact]
    public void Encoder_32Bits_KeepsAlphaAndResolutionAndIcc()
    {
        using var src = new Image<Rgba32>(Config, 3, 1);
        src[0, 0] = new Rgba32(10, 20, 30, 40);
        src[1, 0] = new Rgba32(50, 60, 70, 255);
        src[2, 0] = new Rgba32(0, 0, 0, 0);
        src.Metadata.HorizontalResolution = src.Metadata.VerticalResolution = 300;
        src.Metadata.ResolutionUnits = SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerInch;
        // A minimal ICC profile: 128-byte header (size, 'acsp' signature) and an empty tag table.
        var icc = new byte[132];
        icc[3] = 132;
        icc[36] = (byte)'a'; icc[37] = (byte)'c'; icc[38] = (byte)'s'; icc[39] = (byte)'p';
        src.Metadata.IccProfile = new IccProfile(icc);
        using var back = Encoded(src, new PictEncoder { BitsPerPixel = 32 }, preserveAlpha: true);
        Assert.Equal(src[0, 0], back[0, 0]);
        Assert.Equal(src[2, 0], back[2, 0]);
        Assert.Equal(300, back.Metadata.HorizontalResolution, 3);
        Assert.Equal(src.Metadata.IccProfile.ToByteArray(), back.Metadata.IccProfile!.ToByteArray());
    }
}
