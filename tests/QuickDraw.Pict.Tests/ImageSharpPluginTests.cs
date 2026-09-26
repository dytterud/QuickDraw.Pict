using QuickDraw.Pict.ImageSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace QuickDraw.Pict.Tests;

// The ImageSharp plugin: format registration/detection, decode/identify/encode through ImageSharp's API.
public class ImageSharpPluginTests
{
    private static readonly Configuration Config = CreateConfiguration();
    private static readonly DecoderOptions Options = new() { Configuration = Config };

    private static Configuration CreateConfiguration()
    {
        var configuration = Configuration.Default.Clone();
        configuration.Configure(new PictConfigurationModule());
        return configuration;
    }

    private static Image<Rgba32> TestCard(int width, int height)
    {
        var card = PictReaderTests.TestCard(width, height);
        return Image.LoadPixelData<Rgba32>(Config, card.Pixels, width, height);
    }

    private static byte[] SaveAsPict(Image image)
    {
        using var ms = new MemoryStream();
        image.SaveAsPict(ms);
        return ms.ToArray();
    }

    private static void AssertSamePixels(Image<Rgba32> expected, Image<Rgba32> actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                Assert.Equal(expected[x, y], actual[x, y]);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(84, 4)]
    [InlineData(300, 5)]
    public void SavedPict_LoadsBackThroughImageLoad(int w, int h)
    {
        using var src = TestCard(w, h);
        using var loaded = Image.Load<Rgba32>(Options, new MemoryStream(SaveAsPict(src)));

        AssertSamePixels(src, loaded);
        Assert.Same(PictFormat.Instance, loaded.Metadata.DecodedImageFormat);
        Assert.Equal(src.Metadata.HorizontalResolution, loaded.Metadata.HorizontalResolution, 3);   // resolution round-trips
    }

    [Theory]
    [InlineData(1, 1)]     // well under 526 bytes: only the bare-picture detector can see it
    [InlineData(40, 20)]
    public void ResourceFormPicture_IsDetectedAndDecoded(int w, int h)
    {
        using var src = TestCard(w, h);
        var resource = SaveAsPict(src)[512..];   // bare picture, as stored in a 'PICT' resource

        Assert.Same(PictFormat.Instance, Image.DetectFormat(Options, new MemoryStream(resource)));
        using var loaded = Image.Load<Rgba32>(Options, new MemoryStream(resource));
        AssertSamePixels(src, loaded);
    }

    public static TheoryData<string> OtherFormats => new() { "bmp", "gif", "jpg", "png", "qoi", "tga", "tiff", "webp" };

    [Theory]
    [MemberData(nameof(OtherFormats))]
    public void BuiltInFormats_AreNotClaimedByThePictDetector(string extension)
    {
        using var src = TestCard(64, 64);
        Assert.True(Config.ImageFormatsManager.TryFindFormatByFileExtension(extension, out var format));
        using var ms = new MemoryStream();
        src.Save(ms, Config.ImageFormatsManager.GetEncoder(format));

        var detected = Image.DetectFormat(Options, new MemoryStream(ms.ToArray()));
        Assert.NotSame(PictFormat.Instance, detected);
        Assert.Contains(extension, detected.FileExtensions);
    }

    [Fact]
    public void Identify_ReportsFrameSizeWithoutDecoding()
    {
        using var src = TestCard(40, 20);
        var info = Image.Identify(Options, new MemoryStream(SaveAsPict(src)));

        Assert.Equal(40, info.Width);
        Assert.Equal(20, info.Height);
        Assert.Same(PictFormat.Instance, info.Metadata.DecodedImageFormat);
    }

    [Fact]
    public void Decode_ConvertsToTheRequestedPixelType()
    {
        using var src = TestCard(10, 3);
        using var loaded = Image.Load<Rgb24>(Options, new MemoryStream(SaveAsPict(src)));

        var p = src[7, 2];
        Assert.Equal(new Rgb24(p.R, p.G, p.B), loaded[7, 2]);
    }

    [Fact]
    public void Decode_HonoursTargetSize()
    {
        using var src = TestCard(40, 20);
        var options = new DecoderOptions { Configuration = Config, TargetSize = new Size(30, 30) };
        using var loaded = Image.Load<Rgba32>(options, new MemoryStream(SaveAsPict(src)));

        Assert.Equal(new Size(30, 15), loaded.Size);   // ResizeMode.Max keeps the aspect ratio
    }

    [Fact]
    public void Decoder_PictureFrameResolution_DecodesAtTheFrameSizeAnd72Dpi()
    {
        // Extended v2: frame 10x10 at 72 dpi, drawn in a 20x20 source rect at 144 dpi.
        var pict = new PictBuilder().U16(0).Rect(0, 0, 10, 10).U16(0x0011).U16(0x02FF)
            .U16(0x0C00).U16(0xFFFE).U16(0).U16(0x0090).U16(0).U16(0x0090).U16(0).Rect(0, 0, 20, 20).U16(0).U16(0)
            .U16(0x0031).Rect(0, 0, 20, 20).U16(0x00FF).ToArray();
        var decoder = PictDecoder.Instance;

        using var native = decoder.Decode<Rgba32>(new PictDecoderOptions { GeneralOptions = Options }, new MemoryStream(pict));
        using var frame = decoder.Decode<Rgba32>(
            new PictDecoderOptions { GeneralOptions = Options, Resolution = PictResolution.PictureFrame }, new MemoryStream(pict));

        Assert.Equal((new Size(20, 20), 144.0), (native.Size, native.Metadata.HorizontalResolution));
        Assert.Equal((new Size(10, 10), 72.0), (frame.Size, frame.Metadata.HorizontalResolution));
    }

    [Fact]
    public void TruncatedPict_ThrowsInvalidImageContent()
    {
        using var src = TestCard(40, 20);
        var truncated = SaveAsPict(src)[..600];

        Assert.Throws<InvalidImageContentException>(() => Image.Load<Rgba32>(Options, new MemoryStream(truncated)));
    }

    [Fact]
    public void FontResolver_IsAskedForTheTxFontId()
    {
        var pict = PictBuilder.V2(0, 0, 20, 40)
            .U16(0x0003).U16(21)
            .U16(0x0028).Point(15, 2).Text("Hi").Align()
            .U16(0x00FF).ToArray();
        var asked = new List<int>();
        var options = new PictDecoderOptions
        {
            GeneralOptions = Options,
            FontResolver = id => { asked.Add(id); return null; },   // null: fall back to a system font
        };

        using var _ = PictDecoder.Instance.Decode<Rgba32>(options, new MemoryStream(pict));

        Assert.Equal(new[] { 21 }, asked);
    }

    [Fact]
    public async Task SaveAsPict_ToPathAndByExtension_RoundTrip()
    {
        using var src = TestCard(12, 6);
        string dir = Path.Combine(Path.GetTempPath(), "quickdraw-pict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string explicitPath = Path.Combine(dir, "a.bin");
            string byExtension = Path.Combine(dir, "b.pict");
            src.SaveAsPict(explicitPath);
            await src.SaveAsync(byExtension);           // encoder chosen from the registered .pict extension

            using var a = Image.Load<Rgba32>(Options, explicitPath);
            using var b = await Image.LoadAsync<Rgba32>(Options, byExtension);
            AssertSamePixels(src, a);
            AssertSamePixels(src, b);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
