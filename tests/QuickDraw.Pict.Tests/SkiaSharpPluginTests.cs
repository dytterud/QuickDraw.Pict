using QuickDraw.Pict.SkiaSharp;
using SkiaSharp;
using Xunit;

namespace QuickDraw.Pict.Tests;

// The SkiaSharp adapter: decode to SKBitmap, encode from SKBitmap, Skia's codecs for QuickTime images.
public class SkiaSharpPluginTests
{
    [Fact]
    public void Decode_GivesAnUnpremultipliedRgbaBitmap()
    {
        // A 4 x 2 picture painted red.
        var pict = PictBuilder.V2(0, 0, 2, 4).U16(0x001A).Rgb(0xFFFF, 0, 0).U16(0x0031).Rect(0, 0, 2, 4).U16(0x00FF).ToArray();
        using var bitmap = PictSkia.Decode(pict);
        Assert.Equal((4, 2), (bitmap.Width, bitmap.Height));
        Assert.Equal(SKColorType.Rgba8888, bitmap.ColorType);
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(3, 1));
    }

    [Fact]
    public void SaveAsPict_RoundTripsThePixels()
    {
        using var source = new SKBitmap(new SKImageInfo(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        source.SetPixel(0, 0, new SKColor(10, 20, 30));
        source.SetPixel(2, 1, new SKColor(200, 100, 50));
        using var stream = new MemoryStream();
        source.SaveAsPict(stream);
        using var decoded = PictSkia.Decode(stream.ToArray());
        Assert.Equal(new SKColor(10, 20, 30), decoded.GetPixel(0, 0));
        Assert.Equal(new SKColor(200, 100, 50), decoded.GetPixel(2, 1));
    }

    [Fact]
    public void ImageCodec_DecodesPngWithSkia_AndLeavesTiffToTheFallback()
    {
        using var source = new SKBitmap(new SKImageInfo(2, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        source.SetPixel(1, 0, new SKColor(1, 2, 3));
        byte[] png;
        using (var data = source.Encode(SKEncodedImageFormat.Png, 100)) png = data.ToArray();
        var codec = new SkiaImageCodec();
        var decoded = codec.Decode(new PictImageDescription("png ", 2, 1, 32, -1, 72, 72, "PNG"), png);
        Assert.NotNull(decoded);
        Assert.Equal(new PictColor(1, 2, 3), decoded![1, 0]);
        Assert.Null(codec.Decode(new PictImageDescription("tiff", 2, 1, 32, -1, 72, 72, "TIFF"), png));
    }
}
