using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace QuickDraw.Pict.Tests;

// Emulator goldens (tests/golden/README.md): each screenshot in tests/golden/screens/<name>.png shows
// tests/golden/pict/<name>.pict drawn by a Macintosh at 100%. The picture's 1-pixel black frame is located in the
// screenshot and everything inside it must match QuickDraw.Pict's rendering (at the picture frame, over white) exactly.
// Bitmap fonts for the text picture are loaded from resource forks in tests/golden/fonts (not committed).
public class GoldenTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "QuickDraw.Pict.slnx"))) return Path.Combine(dir.FullName, "tests", "golden");
        return Path.Combine(AppContext.BaseDirectory, "golden");
    }

    public static TheoryData<string> Screens()
    {
        var data = new TheoryData<string>();
        var dir = Path.Combine(Root, "screens");
        if (Directory.Exists(dir))
            foreach (var png in Directory.GetFiles(dir, "*.png").OrderBy(p => p)) data.Add(Path.GetFileNameWithoutExtension(png));
        if (!data.Any()) data.Add("");                                   // no screenshots captured yet
        return data;
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Screenshot_MatchesTheDecodedPicture(string name)
    {
        if (name.Length == 0) return;
        var picture = File.ReadAllBytes(Path.Combine(Root, "pict", name + ".pict"));
        var expected = PictReader.Decode(picture, new PictDecodeOptions { Resolution = PictResolution.PictureFrame, Fonts = Fonts() });
        using var screen = Image.Load<Rgb24>(Path.Combine(Root, "screens", name + ".png"));

        var origin = Locate(screen, expected.Width, expected.Height);
        Assert.True(origin.HasValue, $"{name}: no {expected.Width}x{expected.Height} black frame found in the screenshot.");
        var (ox, oy) = origin!.Value;

        int wrong = 0;
        using var diff = new Image<Rgb24>(expected.Width, expected.Height);
        for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
            {
                var e = expected[x, y];
                var ours = e.A == 0 ? new Rgb24(255, 255, 255) : new Rgb24(e.R, e.G, e.B);
                var mac = screen[ox + x, oy + y];
                bool same = ours == mac;
                if (!same) wrong++;
                diff[x, y] = same ? new Rgb24((byte)(ours.R / 4 + 190), (byte)(ours.G / 4 + 190), (byte)(ours.B / 4 + 190)) : new Rgb24(255, 0, 255);
            }
        if (wrong > 0) diff.SaveAsPng(Path.Combine(AppContext.BaseDirectory, $"golden-diff-{name}.png"));
        Assert.True(wrong == 0, $"{name}: {wrong} pixels differ from the Macintosh (diff: golden-diff-{name}.png in the test output).");
    }

    // The top-left of a width x height rectangle whose 1-pixel border is entirely black.
    internal static (int x, int y)? Locate(Image<Rgb24> screen, int width, int height)
    {
        var black = new Rgb24(0, 0, 0);
        for (int y = 0; y + height <= screen.Height; y++)
            for (int x = 0; x + width <= screen.Width; x++)
            {
                if (screen[x, y] != black || screen[x + width - 1, y] != black || screen[x, y + height - 1] != black) continue;
                bool frame = true;
                for (int i = 0; i < width && frame; i++) frame = screen[x + i, y] == black && screen[x + i, y + height - 1] == black;
                for (int i = 0; i < height && frame; i++) frame = screen[x, y + i] == black && screen[x + width - 1, y + i] == black;
                if (frame) return (x, y);
            }
        return null;
    }

    private static PictFontLibrary? Fonts()
    {
        var dir = Path.Combine(Root, "fonts");
        if (!Directory.Exists(dir)) return null;
        var library = new PictFontLibrary();
        foreach (var file in Directory.GetFiles(dir))
        {
            try { library.AddResourceFork(File.ReadAllBytes(file)); }
            catch (ArgumentException) { }
        }
        return library;
    }

    [Fact]
    public void Locate_FindsTheFrameInsideALargerScreenshot()
    {
        using var screen = new Image<Rgb24>(40, 30, new Rgb24(200, 200, 200));
        for (int i = 0; i < 12; i++) { screen[7 + i, 5] = default; screen[7 + i, 14] = default; }
        for (int i = 0; i < 10; i++) { screen[7, 5 + i] = default; screen[18, 5 + i] = default; }
        Assert.Equal((7, 5), Locate(screen, 12, 10));
        Assert.Null(Locate(screen, 13, 10));
    }
}
