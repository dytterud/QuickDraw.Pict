using Xunit;
using static QuickDraw.Pict.Tests.TestFont;

namespace QuickDraw.Pict.Tests;

// Bitmap-font text: FONT/NFNT/FOND parsing, Font Manager selection and QuickDraw's character generator (glyph
// placement, space extra, missing symbol, bold, italic, underline, outline).
public class TextTests
{
    private const int Family = 400;

    // Ascent 3, descent 2: 'A' a 2x3 block, 'g' a 2-wide descender, space 2 wide, missing symbol a 1-wide bar.
    private static readonly byte[] Font9 = Build(3, 2, 0, 1, new[]
    {
        new Glyph(' ', 2, 0),
        new Glyph('A', 3, 0, "##", "##", "##", "..", ".."),
        new Glyph('g', 3, 0, "..", "##", "##", "##", "##"),
    }, missing: new Glyph('\0', 2, 0, "#", "#", "#"));

    private static PictFontLibrary Library()
    {
        var lib = new PictFontLibrary();
        lib.AddFont(Family * 128 + 9, Font9);
        return lib;
    }

    private static string[] Text(string s, int face = 0, int spExtra = 0, int width = 10, int height = 7,
        PictFontLibrary? fonts = null, Action<PictBuilder>? before = null)
    {
        var b = PictBuilder.V2(0, 0, height, width).U16(0x0003).U16(Family).U16(0x000D).U16(9)
            .U16(0x0004).U8(face).Align();
        if (spExtra != 0) b.U16(0x0006).U16(spExtra >> 16).U16(spExtra & 0xFFFF);
        before?.Invoke(b);
        b.Align().U16(0x0028).Point(4, 2).Text(s).Align().U16(0x00FF);
        var bmp = PictReader.Decode(b.ToArray(), new PictDecodeOptions { Fonts = fonts ?? Library() });
        return Enumerable.Range(0, bmp.Height).Select(y => new string(Enumerable.Range(0, bmp.Width).Select(x =>
        {
            var c = bmp[x, y];
            return c.A == 0 ? '.' : c == new PictColor(0, 0, 0) ? '#' : c == new PictColor(255, 255, 255) ? 'w' : '?';
        }).ToArray())).ToArray();
    }

    [Fact]
    public void BitmapFont_ParsesTheStrikeAndTables()
    {
        var f = BitmapFont.Parse(Font9);
        Assert.Equal((' ', 'g', 3, 2, 5), ((char)f.FirstChar, (char)f.LastChar, f.Ascent, f.Descent, f.RectHeight));
        int a = 'A' - ' ';
        Assert.Equal(3, f.OffsetWidths[a] & 0xFF);
        Assert.Equal(2, f.Locations[a + 1] - f.Locations[a]);
        Assert.True(f.StrikeBit(0, f.Locations[a]));
        Assert.Equal(-1, f.OffsetWidths['B' - ' ']);
    }

    [Fact]
    public void Text_PlacesGlyphsOnTheBaselineAndAdvancesByTheirWidths()
    {
        Assert.Equal(new[] { "..........", "..##.##...", "..##.##...", "..##.##...", "..........", "..........", ".........." },
            Text("AA"));
    }

    [Fact]
    public void Text_SpaceExtraWidensSpaces()
    {
        Assert.Equal("..##....##", Text("A A", spExtra: 0x10000)[1]);
    }

    [Fact]
    public void Text_MissingCharacters_DrawTheMissingSymbol()
    {
        Assert.Equal("..#.##....", Text("ZA")[1]);
    }

    [Fact]
    public void Text_Bold_SmearsOnePixelRightAndWidens()
    {
        Assert.Equal("..###.###.", Text("AA", face: 1)[1]);
    }

    [Fact]
    public void Text_Italic_SlantsHalfAPixelPerRowAboveTheBottom()
    {
        var rows = Text("A", face: 2);
        Assert.Equal(new[] { "....##....", "...##.....", "...##....." }, rows[1..4]);
    }

    [Fact]
    public void Text_Underline_RunsBelowTheBaselineToThePen()
    {
        Assert.Equal("..######..", Text("AA", face: 4)[5]);
    }

    [Fact]
    public void Text_Underline_BreaksAroundDescenders()
    {
        Assert.Equal("..##.###..", Text("gA", face: 4)[5]);
    }

    [Fact]
    public void Text_Outline_IsTheDilatedGlyphWithItsInsideInverted()
    {
        Assert.Equal(new[] { ".####.....", ".#ww#.....", ".#ww#.....", ".#ww#.....", ".####....." }, Text("A", face: 8)[0..5]);
    }

    [Fact]
    public void Text_SizeWithoutAStrike_StretchesTheNearestOneAboutThePen()
    {
        // 18 pt from the 9 pt strike: doubled about the pen (2, 8): 'A' (x 2-3, rows 5-7 at 9 pt) covers x 2-5, rows 2-7.
        var pict = PictBuilder.V2(0, 0, 12, 10).U16(0x0003).U16(Family).U16(0x000D).U16(18)
            .U16(0x0028).Point(8, 2).Text("A").Align().U16(0x00FF).ToArray();
        var bmp = PictReader.Decode(pict, new PictDecodeOptions { Fonts = Library() });
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 10; x++)
                Assert.Equal(y >= 2 && y <= 7 && x >= 2 && x <= 5, bmp[x, y] == new PictColor(0, 0, 0));
    }

    [Fact]
    public void Text_FontNameOpcode_MapsTheFontNumberByFamilyName()
    {
        var lib = new PictFontLibrary();
        lib.AddFamily(7, "Test Font", Family(7, (9, 0, 1234)));
        lib.AddNfnt(1234, Font9);
        // The picture names its font 400 "Test Font"; the library knows that family as 7.
        var rows = Text("A", fonts: lib, before: b => b.Align().U16(0x002C).U16(12).U16(Family).Text("Test Font").Align());
        Assert.Equal("..##......", rows[1]);
    }

    [Fact]
    public void FontLibrary_LoadsFontsFromAResourceFork()
    {
        var fork = ResourceFork(("FOND", 7, "Suitcase Font", Family(7, (9, 0, 5000))), ("NFNT", 5000, null, Font9),
            ("STR ", 1, null, new byte[] { 0 }));
        var lib = new PictFontLibrary();
        Assert.Equal(2, lib.AddResourceFork(fork));
        var rows = Text("A", fonts: lib, before: b => b.Align().U16(0x002C).U16(16).U16(Family).Text("Suitcase Font").Align());
        Assert.Equal("..##......", rows[1]);
    }

    [Fact]
    public void FontLibrary_RejectsDataThatIsNotAResourceFork()
    {
        Assert.Throws<ArgumentException>(() => new PictFontLibrary().AddResourceFork(new byte[8]));
    }

    [Fact]
    public void Text_FamilyMissingFromTheLibrary_UsesTheTextFallback()
    {
        var fallback = new RecordingFallback();
        var pict = PictBuilder.V2(0, 0, 4, 4).U16(0x0003).U16(99).U16(0x0028).Point(2, 0).Text("x").Align().U16(0x00FF).ToArray();
        PictReader.Decode(pict, new PictDecodeOptions { Fonts = new PictFontLibrary(), TextFallback = fallback });
        Assert.Equal("x", fallback.Last);
    }

    private sealed class RecordingFallback : IPictTextFallback
    {
        public string? Last;
        public PictTextMask? Render(string text, PictTextStyle style) { Last = text; return null; }
    }

    [Fact]
    public void FontManager_PicksTheExactSizeElseDoubleOrHalfElseTheNearest()
    {
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, (9, 0, 1), (12, 0, 2)));
        lib.AddNfnt(1, Font9);
        lib.AddNfnt(2, Build(4, 2, 0, 0, new[] { new Glyph('A', 4, 0, "###") }));
        FontSelection Swap(int size) => FontManager.Swap(lib, Family, size, 0, (1, 1), (1, 1), 0)!;

        Assert.Equal(3, Swap(9).Font.Ascent);
        Assert.Equal(4, Swap(12).Font.Ascent);
        Assert.Equal((512, 512), Swap(18).Numer);           // 18 = 2 x 9: the 9 point strike, stretched x2
        Assert.Equal(3, Swap(10).Font.Ascent);              // 10: nearer to 9 than to 12
        Assert.Equal((284, 284), Swap(10).Numer);           // stretched 10/9
        Assert.Equal(4, Swap(11).Font.Ascent);
    }

    [Fact]
    public void FontManager_UsesAStyledStrikeInsteadOfSynthesizingTheStyle()
    {
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, (9, 0, 1), (9, 1, 2)));
        lib.AddNfnt(1, Font9);
        lib.AddNfnt(2, Font9);
        var bold = FontManager.Swap(lib, Family, 9, 1, (1, 1), (1, 1), 0)!;
        var boldItalic = FontManager.Swap(lib, Family, 9, 3, (1, 1), (1, 1), 0)!;

        Assert.Equal((0, 0), (bold.Bold, bold.Extra));
        Assert.Equal((0, 8), (boldItalic.Bold, boldItalic.Italic));
    }
}
