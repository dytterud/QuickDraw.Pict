using System.Text;
using QuickDraw.Pict.ImageSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace QuickDraw.Pict.Tests;

// Standalone QuickTime image files (QTIF) and MacPaint documents (PNTG), in the core and through ImageSharp.
public class MacImageFileTests
{
    private static readonly Configuration Config = CreateConfiguration();

    private static Configuration CreateConfiguration()
    {
        var configuration = Configuration.Default.Clone();
        configuration.Configure(new PictConfigurationModule());
        return configuration;
    }

    private static byte[] Atom(string type, byte[] content)
    {
        var b = new List<byte>();
        int size = content.Length + 8;
        b.AddRange(new[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size });
        b.AddRange(Encoding.ASCII.GetBytes(type));
        b.AddRange(content);
        return b.ToArray();
    }

    // An 86-byte ImageDescription.
    private static byte[] Description(string codec, int width, int height, int depth, int dataSize) =>
        new PictBuilder().U16(0).U16(86).Bytes(Encoding.ASCII.GetBytes(codec)).Zeros(8).U16(0).U16(0).Zeros(4)
            .Zeros(8).U16(width).U16(height).U16(144).U16(0).U16(144).U16(0)          // 144 dpi
            .U16(dataSize >> 16).U16(dataSize).U16(1).U8(0).Zeros(31).U16(depth).U16(0xFFFF).ToArray();

    private static byte[] RawQtif(byte[]? icc = null)
    {
        var pixels = new byte[] { 0, 255, 0, 0, 0, 0, 0, 255 };                         // 2 x 1 ARGB: red, blue
        var file = new List<byte>(Atom("idsc", Description("raw ", 2, 1, 32, pixels.Length)));
        file.AddRange(Atom("idat", pixels));
        if (icc != null) file.AddRange(Atom("iicc", icc));
        return file.ToArray();
    }

    [Fact]
    public void QuickTimeImageFile_DecodesItsImageWithTheBuiltInCodecs()
    {
        var data = RawQtif(new byte[] { 1, 2, 3 });
        Assert.True(QuickTimeImageFile.IsQuickTimeImageFile(data));
        var bmp = QuickTimeImageFile.Decode(data);
        Assert.Equal((2, 1), (bmp.Width, bmp.Height));
        Assert.Equal(new PictColor(255, 0, 0), bmp[0, 0]);
        Assert.Equal(new PictColor(0, 0, 255), bmp[1, 0]);
        Assert.Equal(144, QuickTimeImageFile.ReadDescription(data).HorizontalResolution);
        Assert.Equal(new byte[] { 1, 2, 3 }, QuickTimeImageFile.ReadIccProfile(data));
    }

    [Fact]
    public void QuickTimeImageFile_WithAnUnsupportedCodec_Throws()
    {
        var file = Atom("idsc", Description("xxxx", 1, 1, 24, 1)).Concat(Atom("idat", new byte[] { 0 })).ToArray();
        Assert.Throws<NotSupportedException>(() => QuickTimeImageFile.Decode(file));
        Assert.False(QuickTimeImageFile.IsQuickTimeImageFile(new byte[] { 0, 0, 0, 20, (byte)'m', (byte)'o', (byte)'o', (byte)'v' }));
    }

    [Fact]
    public void QuickTimeImageFile_LoadsThroughImageSharpWithEmbeddedPng()
    {
        using var png = new Image<Rgba32>(Config, 3, 2, new Rgba32(10, 20, 30, 255));
        using var ms = new MemoryStream();
        png.SaveAsPng(ms);
        var data = ms.ToArray();
        var file = Atom("idsc", Description("png ", 3, 2, 32, data.Length)).Concat(Atom("idat", data)).ToArray();
        using var image = Image.Load<Rgba32>(new DecoderOptions { Configuration = Config }, file);
        Assert.Equal(QuickTimeImageFormat.Instance, image.Metadata.DecodedImageFormat);
        Assert.Equal(new Rgba32(10, 20, 30, 255), image[2, 1]);
        Assert.Equal(144, image.Metadata.HorizontalResolution);
    }

    // A MacPaint document: version 2 header, row 0 = 8 black bytes + 64 white, every other row white.
    private static byte[] MacPaint()
    {
        var b = new List<byte>(new byte[512]);
        b[3] = 2;
        b.AddRange(new byte[] { 0xF9, 0xFF, 0xC1, 0x00 });                              // 8 x $FF, 64 x $00
        for (int row = 1; row < 720; row++) b.AddRange(new byte[] { 0xB9, 0x00 });      // 72 x $00
        return b.ToArray();
    }

    [Fact]
    public void MacPaintFile_DecodesBlackOnWhite()
    {
        var data = MacPaint();
        Assert.True(MacPaintFile.IsMacPaintFile(data));
        var bmp = MacPaintFile.Decode(data);
        Assert.Equal((576, 720), (bmp.Width, bmp.Height));
        Assert.Equal(new PictColor(0, 0, 0), bmp[63, 0]);
        Assert.Equal(new PictColor(255, 255, 255), bmp[64, 0]);
        Assert.Equal(new PictColor(255, 255, 255), bmp[0, 1]);
    }

    [Fact]
    public void MacPaintFile_SkipsAMacBinaryWrapper()
    {
        var paint = MacPaint();
        var header = new byte[128];
        header[1] = 5;
        Encoding.ASCII.GetBytes("Paint").CopyTo(header, 2);
        Encoding.ASCII.GetBytes("PNTGMPNT").CopyTo(header, 65);
        header[83] = (byte)(paint.Length >> 24); header[84] = (byte)(paint.Length >> 16);
        header[85] = (byte)(paint.Length >> 8); header[86] = (byte)paint.Length;
        var data = header.Concat(paint).ToArray();
        Assert.True(MacPaintFile.IsMacPaintFile(data));
        Assert.Equal(new PictColor(0, 0, 0), MacPaintFile.Decode(data)[0, 0]);
    }

    [Fact]
    public void MacPaintFile_LoadsThroughImageSharp_AndPictDataIsNotMistakenForIt()
    {
        using var image = Image.Load<Rgba32>(new DecoderOptions { Configuration = Config }, MacPaint());
        Assert.Equal(MacPaintFormat.Instance, image.Metadata.DecodedImageFormat);
        Assert.Equal(new Rgba32(0, 0, 0, 255), image[0, 0]);
        var pict = PictBuilder.V2(0, 0, 1, 1).U16(0x00FF).ToArray();
        Assert.False(MacPaintFile.IsMacPaintFile(pict));
        Assert.False(QuickTimeImageFile.IsQuickTimeImageFile(pict));
    }
}
