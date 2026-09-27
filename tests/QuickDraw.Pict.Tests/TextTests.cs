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
        PictFontLibrary? fonts = null, Action<PictBuilder>? before = null, Action<PictBuilder>? beforeFont = null,
        PictQuickDraw quickDraw = PictQuickDraw.MacOS9)
    {
        var b = PictBuilder.V2(0, 0, height, width);
        beforeFont?.Invoke(b);
        b.Align().U16(0x0003).U16(Family).U16(0x000D).U16(9).U16(0x0004).U8(face).Align();
        if (spExtra != 0) b.U16(0x0006).U16(spExtra >> 16).U16(spExtra & 0xFFFF);
        before?.Invoke(b);
        b.Align().U16(0x0028).Point(4, 2).Text(s).Align().U16(0x00FF);
        var bmp = PictReader.Decode(b.ToArray(), new PictDecodeOptions { Fonts = fonts ?? Library(), QuickDraw = quickDraw });
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
        var rows = Text("A", face: 2, quickDraw: PictQuickDraw.MacRom);
        Assert.Equal(new[] { "....##....", "...##.....", "...##....." }, rows[1..4]);
    }

    [Fact]
    public void Text_Italic_OnMacOS9_PivotsOnTheRowBelowTheBaseline()
    {
        // Rows above pnLoc.v shift right (rows above x 8) / 16: 3 -> 1, 2 -> 1, 1 -> 0; row pnLoc.v and below shift left.
        var rows = Text("A", face: 2);
        Assert.Equal(new[] { "...##.....", "...##.....", "..##......" }, rows[1..4]);
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
    public void Text_Outline_OnAColorPort_DrawsOnlyTheRing()
    {
        // The shadow buffer with the glyph XOR-ed out, drawn one pixel up-left; the inside is left untouched.
        Assert.Equal(new[] { ".####.....", ".#..#.....", ".#..#.....", ".#..#.....", ".####....." }, Text("A", face: 8)[0..5]);
    }

    [Fact]
    public void Text_InkPastTheFinalPenPosition_IsClipped()
    {
        // 'W' advances 2 but its image is 4 wide: textRect ends at the pen + width (no slop), so columns 4-5 are cut.
        var lib = new PictFontLibrary();
        lib.AddFont(Family * 128 + 9, Build(3, 2, 0, 1, new[] { new Glyph('W', 2, 0, "####", "####", "####") }));
        Assert.Equal("..##......", Text("W", fonts: lib, quickDraw: PictQuickDraw.MacRom)[1]);
    }

    [Fact]
    public void Text_InkPastTheFinalPenPosition_IsDrawnOnMacOS9()
    {
        var lib = new PictFontLibrary();
        lib.AddFont(Family * 128 + 9, Build(3, 2, 0, 1, new[] { new Glyph('W', 2, 0, "####", "####", "####") }));
        Assert.Equal("..####....", Text("W", fonts: lib)[1]);
    }

    [Fact]
    public void Text_LoneCarriageReturn_DrawsNothingOnMacOS9()
    {
        var lib = new PictFontLibrary();
        lib.AddFont(Family * 128 + 9, Build(3, 2, 0, 1, new[] { new Glyph('\r', 2, 0, "##", "##", "##") }));
        Assert.All(Text("\r", face: 2, fonts: lib), row => Assert.Equal("..........", row));
    }

    [Fact]
    public void Text_ChExtra_WidensEveryCharacterButSpaces()
    {
        // ChExtra 0x1C7 (4.12 per point) x 9 pt = 0.9998 pixel: the second 'A' starts at 2.5 + 3 + 0.9998 -> 6.
        Assert.Equal("..##..##..", Text("AA", before: b => b.Align().U16(0x0016).U16(0x01C7))[1]);
        Assert.Equal("..##....##", Text("A A", before: b => b.Align().U16(0x0016).U16(0x01C7))[1]);   // space: none
    }

    [Fact]
    public void Text_PnLocHFrac_SetsThePenFractionForTheNextTextOnly()
    {
        // ChExtra 0xE4 x 9 = 0.501 pixel per character. From the default half pixel the second 'A' lands at
        // 2.5 + 3.501 -> 6; from PnLocHFrac 0x7F00 (0.496) at 5.997 -> 5.
        Action<PictBuilder> extra = b => b.Align().U16(0x0016).U16(0x00E4);
        Assert.Equal("..##..##..", Text("AA", before: extra, quickDraw: PictQuickDraw.MacRom)[1]);
        Assert.Equal("..##.##...", Text("AA", before: b => { extra(b); b.Align().U16(0x0015).U16(0x7F00); },
            quickDraw: PictQuickDraw.MacRom)[1]);
        // Mac OS 9 places glyphs from 1/2 whatever the pen's fraction: PnLocHFrac changes nothing here.
        Assert.Equal("..##..##..", Text("AA", before: b => { extra(b); b.Align().U16(0x0015).U16(0x7F00); })[1]);
    }

    [Fact]
    public void Text_TxRatio_ScalesTheSearchSize()
    {
        // numer 2/1 (x frame 10 / 10): 9 pt asks for 18, the 9 pt strike stretched x2 about the pen (2, 4).
        var rows = Text("A", width: 10, height: 12, before: b => b.Align().U16(0x0010).Point(2, 2).Point(1, 1),
            quickDraw: PictQuickDraw.MacRom);
        Assert.Equal("..####....", rows[0]);
        Assert.Equal("..####....", rows[3]);
        Assert.Equal("..........", rows[4]);
    }

    [Fact]
    public void Text_MacOS9_StretchedSrcOrUsesTheInkRowsAsItsRect()
    {
        // Vertical 3/2 about the pen (v 4), glyph rows ".." "#." ".#" "##" "#." (ascent 3, descent 2). srcOr's rect is
        // the ink rows (-2..2 about the baseline: rows 2..5), mapped to 1..7, and the ordinary DDA picks source rows
        // 0, 0, 1, 2, 2, 3 of it; a copy mode's rect would be the whole font rect (-3..2 -> -1..7).
        var lib = new PictFontLibrary();
        lib.AddFont(Family * 128 + 9, Build(3, 2, 0, 1, new[] { new Glyph('B', 3, 0, "..", "#.", ".#", "##", "#.") }));
        var rows = Text("B", fonts: lib, height: 9, before: b => b.Align().U16(0x0010).Point(3, 1).Point(2, 1));
        Assert.Equal(new[] { "....", "..#.", "..#.", "...#", "..##", "..##", "..#.", "...." },
            rows.Take(8).Select(r => r[..4]).ToArray());
    }

    [Fact]
    public void Text_SizeWithoutAStrike_StretchesTheNearestOneAboutThePen()
    {
        // 18 pt from the 9 pt strike: doubled about the pen (2, 8): 'A' (x 2-3, rows 5-7 at 9 pt) covers x 2-5, rows 2-7.
        var pict = PictBuilder.V2(0, 0, 12, 10).U16(0x0003).U16(Family).U16(0x000D).U16(18)
            .U16(0x0028).Point(8, 2).Text("A").Align().U16(0x00FF).ToArray();
        var bmp = PictReader.Decode(pict, new PictDecodeOptions { Fonts = Library(), QuickDraw = PictQuickDraw.MacRom });
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
        // The picture names its font 400 "Test Font"; the library knows that family as 7. The map applies to TxFont
        // opcodes after the fontName, as the recorder writes them.
        var rows = Text("A", fonts: lib, beforeFont: b => b.Align().U16(0x002C).U16(12).U16(Family).Text("Test Font").Align());
        Assert.Equal("..##......", rows[1]);
        rows = Text("A", fonts: lib, before: b => b.Align().U16(0x002C).U16(12).U16(Family).Text("Test Font").Align(),
            quickDraw: PictQuickDraw.MacRom);
        Assert.Equal("..........", rows[1]);
    }

    [Fact]
    public void FontManager_OnMacOS9_FallsBackToTheLowestNumberedFamily()
    {
        // Family 400 is missing; Mac OS 9 tries the application font (absent here), then the lowest family, 7.
        var lib = new PictFontLibrary();
        lib.AddFamily(7, "Test Font", Family(7, (9, 0, 1234)));
        lib.AddNfnt(1234, Font9);
        Assert.NotNull(FontManager.Swap(lib, Family, 9, 0, (1, 1), (1, 1), 0, false, false, macOS9: true));
        Assert.Null(FontManager.Swap(lib, Family, 9, 0, (1, 1), (1, 1), 0, false, false, macOS9: false));
    }

    [Fact]
    public void FontManager_OnMacOS9_MatchesStyleVariantsWithoutUnderlineCondenseExtend()
    {
        // Underline asked, plain and underline strikes: Mac OS 9 matches face & $9B = plain and synthesizes it.
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, (9, 0, 1), (9, 4, 2)));
        lib.AddNfnt(1, Font9);
        lib.AddNfnt(2, Font9);
        Assert.Equal(1, FontManager.Swap(lib, Family, 9, 4, (1, 1), (1, 1), 0, false, false, macOS9: true)!.UlThick);
        Assert.Equal(0, FontManager.Swap(lib, Family, 9, 4, (1, 1), (1, 1), 0, false, false, macOS9: false)!.UlThick);
    }

    [Fact]
    public void FontManager_OnMacOS9_WalksFamilyWidthTablesWithTheFamilysRange()
    {
        // FOND range 31..103, strike 32..103: 'A' takes FOND word 'A' - 31 = 34 (value 35).
        var words = Enumerable.Range(1, 75).ToArray();
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, 0, 31, 103, new[] { (0, words) }, (9, 0, 1)));
        lib.AddNfnt(1, Font9);
        var s = FontManager.Swap(lib, Family, 9, 0, (1, 1), (1, 1), 0, fractEnable: true, fScaleDisable: false, macOS9: true)!;
        Assert.Equal(35 * 9 << 4, s.Widths['A']);
    }

    [Fact]
    public void FontLibrary_LoadsFontsFromAResourceFork()
    {
        var fork = ResourceFork(("FOND", 7, "Suitcase Font", Family(7, (9, 0, 5000))), ("NFNT", 5000, null, Font9),
            ("STR ", 1, null, new byte[] { 0 }));
        var lib = new PictFontLibrary();
        Assert.Equal(2, lib.AddResourceFork(fork));
        var rows = Text("A", fonts: lib, beforeFont: b => b.Align().U16(0x002C).U16(16).U16(Family).Text("Suitcase Font").Align());
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
        FontSelection Swap(int size) => FontManager.Swap(lib, Family, size, 0, (1, 1), (1, 1), 0, false, false, macOS9: false)!;

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
        var bold = FontManager.Swap(lib, Family, 9, 1, (1, 1), (1, 1), 0, false, false, macOS9: false)!;
        var boldItalic = FontManager.Swap(lib, Family, 9, 3, (1, 1), (1, 1), 0, false, false, macOS9: false)!;

        Assert.Equal((0, 0), (bold.Bold, bold.Extra));
        Assert.Equal((0, 8), (boldItalic.Bold, boldItalic.Italic));
    }

    [Fact]
    public void FontManager_StyleVariant_ScoresSubsetsItalicOverBold()
    {
        // Bold + italic asked, plain / bold / italic strikes: italic scores 8, bold 4, so bold is synthesized.
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, (9, 0, 1), (9, 1, 2), (9, 2, 3)));
        lib.AddNfnt(1, Font9);
        lib.AddNfnt(2, Font9);
        lib.AddNfnt(3, Font9);
        var s = FontManager.Swap(lib, Family, 9, 3, (1, 1), (1, 1), 0, false, false, macOS9: false)!;
        Assert.Equal((1, 0, 1), (s.Bold, s.Italic, s.CurStyle));
    }

    [Fact]
    public void FontManager_ScalingDisabled_TakesTheNearestSmallerSizeAndScalesWidthsByTheRest()
    {
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, (9, 0, 1), (12, 0, 2)));
        lib.AddNfnt(1, Font9);
        lib.AddNfnt(2, Build(4, 2, 0, 0, new[] { new Glyph('A', 4, 0, "###") }));
        var s = FontManager.Swap(lib, Family, 11, 0, (1, 1), (1, 1), 0, false, fScaleDisable: true, macOS9: false)!;
        Assert.Equal(3, s.Font.Ascent);                       // 9, not the nearer 12
        Assert.Equal((0x100, 0x100), s.Numer);                // 11/9 = $139 stays a width factor, not a stretch
        Assert.Equal(3 * 0x13900, s.Widths['A']);
    }

    [Fact]
    public void FontManager_ScalingDisabled_CutsTheStretchToAPowerOfTwoOrThreeQuarters()
    {
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, (9, 0, 1)));
        lib.AddNfnt(1, Font9);
        // 7 pt: no smaller size, so 9; 7/9 = $C7 becomes 3/4 ($C0) with the factor $C7 * 4 / 3 = $109.
        var small = FontManager.Swap(lib, Family, 7, 0, (1, 1), (1, 1), 0, false, fScaleDisable: true, macOS9: false)!;
        Assert.Equal((0xC0, 0xC0), small.Numer);
        Assert.Equal(3 * 0x10900, small.Widths['A']);
        // 36 pt: no double/half search; 9 stretched x4 exactly, widths untouched.
        var large = FontManager.Swap(lib, Family, 36, 0, (1, 1), (1, 1), 0, false, fScaleDisable: true, macOS9: false)!;
        Assert.Equal((0x400, 0x400), large.Numer);
        Assert.Equal(3 << 16, large.Widths['A']);
    }

    [Fact]
    public void FontManager_ScalingDisabled_ScansOldStyleSizesDownwardFirst()
    {
        var lib = new PictFontLibrary();
        lib.AddFont(Family * 128 + 9, Font9);
        lib.AddFont(Family * 128 + 12, Build(4, 2, 0, 0, new[] { new Glyph('A', 4, 0, "###") }));
        Assert.Equal(3, FontManager.Swap(lib, Family, 10, 0, (1, 1), (1, 1), 0, false, fScaleDisable: true, macOS9: false)!.Font.Ascent);
    }

    [Fact]
    public void Text_GlyphStateScalingDisabled_SpacesUnstretchedGlyphsByScaledWidths()
    {
        // 12 pt from the 9 pt strike: glyphs stay 9 pt, advancing 3 x 12/9 = 3.996: the second 'A' at 2.5 + 3.996 -> 6.
        var rows = Text("AA", before: b => b.Align().U16(0x000D).U16(12).U16(0x002E).U16(4).U8(0).U8(0).U8(0).U8(1));
        Assert.Equal("..##..##..", rows[1]);
        Assert.Equal("..........", rows[4]);
    }

    [Fact]
    public void FontManager_OnMacOS9_FoldsTheHorizontalRatioIntoTheSize()
    {
        Assert.Equal((12, (256, 192), (256, 256)), FontManager.Fold(9, (4, 1), (3, 1)));
        Assert.Equal((18, (256, 171), (256, 256)), FontManager.Fold(12, (3, 1), (2, 1)));
        Assert.Equal((11, (262, 209), (256, 256)), FontManager.Fold(9, (5, 1), (4, 1)));
        Assert.Equal(12, FontManager.Fold(12, (1, 1), (3, 1)).size);                  // under 4 points: no fold
        // 9 pt at 4/3 wide: the 12 pt strike, squeezed vertically to 3/4.
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, (9, 0, 1), (12, 0, 2)));
        lib.AddNfnt(1, Font9);
        lib.AddNfnt(2, Build(4, 2, 0, 0, new[] { new Glyph('A', 4, 0, "###") }));
        var s = FontManager.Swap(lib, Family, 9, 0, (4, 1), (3, 1), 0, false, false, macOS9: true)!;
        Assert.Equal(4, s.Font.Ascent);
        Assert.Equal((256, 192), s.Numer);
    }

    // Expands a 1-bit test font to 8 bits (ink = inkIndex, background 0), marking it a color font with an fctb.
    private static byte[] Deep8(byte[] font, byte inkIndex)
    {
        int rowWords = (font[24] << 8) | font[25], height = (font[14] << 8) | font[15];
        int rb1 = rowWords * 2, rb8 = rb1 * 8;
        var img = new byte[rb8 * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < rb1 * 8; x++)
                if ((font[26 + y * rb1 + (x >> 3)] & (0x80 >> (x & 7))) != 0) img[y * rb8 + x] = inkIndex;
        var tail = font.AsSpan(26 + rb1 * height).ToArray();
        int owTLoc = ((font[16] << 8) | font[17]) + (img.Length - rb1 * height) / 2;
        var header = font.AsSpan(0, 26).ToArray();
        int fontType = ((header[0] << 8) | header[1]) & ~0x1C | (3 << 2) | 0x280;
        header[0] = (byte)(fontType >> 8); header[1] = (byte)fontType;
        header[10] = (byte)(owTLoc >> 24); header[11] = (byte)(owTLoc >> 16);             // nDescent = high word
        header[16] = (byte)(owTLoc >> 8); header[17] = (byte)owTLoc;
        return header.Concat(img).Concat(tail).ToArray();
    }

    [Fact]
    public void ColorFont_DrawsEachGlyphBoxOpaqueThroughItsColorTable()
    {
        // An 8-bit color variant (FOND style $0300) with an fctb: 0 = white box, 7 = green ink; drawn srcCopy whatever
        // the text mode (srcOr here), the whole glyph box opaque.
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, (9, 0x0300, 500)));
        lib.AddNfnt(500, Deep8(Font9, 7));
        var fctb = new PictBuilder().U16(0).U16(0).U16(0).U16(7);
        for (int i = 0; i < 8; i++) fctb.U16(i).Rgb(i == 7 ? 0 : 0xFFFF, 0xFFFF, i == 7 ? 0 : 0xFFFF);
        lib.AddFontColorTable(500, fctb.ToArray());
        var b = PictBuilder.V2(0, 0, 7, 10).U16(0x0003).U16(Family).U16(0x000D).U16(9).U16(0x0005).U16(1)
            .U16(0x0028).Point(4, 2).Text("A").Align().U16(0x00FF);
        var bmp = PictReader.Decode(b.ToArray(), new PictDecodeOptions { Fonts = lib });
        Assert.Equal(new PictColor(0, 255, 0), bmp[2, 1]);                // ink
        Assert.Equal(new PictColor(255, 255, 255), bmp[2, 4]);            // the box's background, drawn opaque
        Assert.Equal(0, bmp.Pixels[(1 * 10 + 5) * 4 + 3]);               // outside the box: untouched
    }

    [Fact]
    public void FixRound_RoundsHalvesAwayFromZeroAndSaturates()
    {
        Assert.Equal(new[] { 1, -1, -2, -1, 0, 32767 },
            new[] { 0x8000, -0x8000, -0x18000, -0x14000, 0x7FFF, 0x7FFFFFFF }.Select(FixedMath.FixRound).ToArray());
    }

    [Fact]
    public void FontManager_OutlineEntry_TakesOverWhenNoExactBitmapSize()
    {
        // A size-0 (TrueType) entry anywhere: the exact bitmap still wins; otherwise TrueType, never double/half.
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, (9, 0, 1), (0, 0, 2)));
        lib.AddNfnt(1, Font9);
        Assert.NotNull(FontManager.Swap(lib, Family, 9, 0, (1, 1), (1, 1), 0, false, false, macOS9: false));
        Assert.Null(FontManager.Swap(lib, Family, 18, 0, (1, 1), (1, 1), 0, false, false, macOS9: false));
    }

    [Fact]
    public void FontManager_NearestSizeWithoutItsResource_FallsBackToOldStyleFonts()
    {
        // 11 pt: nearest listed 12 has no NFNT; the 9 pt NFNT is not tried - the FONT-id path finds 400/9 instead.
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, (9, 0, 1), (12, 0, 2)));
        lib.AddNfnt(1, Font9);
        lib.AddFont(Family * 128 + 9, Build(4, 2, 0, 0, new[] { new Glyph('A', 4, 0, "###") }));
        Assert.Equal(4, FontManager.Swap(lib, Family, 11, 0, (1, 1), (1, 1), 0, false, false, macOS9: false)!.Font.Ascent);
    }

    [Fact]
    public void FontManager_FamilyWidthTable_IsReadWithTheStrikesCharacterRange()
    {
        // FOND range 31..103, strike range 32..103: the strike's char c takes FOND word c - 32 (shifted by one), the
        // missing symbol word 72; 0xFFFF means missing. Width = word x 9 pt << 4.
        var words = Enumerable.Range(1, 75).ToArray();
        words['g' - ' '] = 0xFFFF;
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, 0, 31, 103, new[] { (0, words) }, (9, 0, 1)));
        lib.AddNfnt(1, Font9);
        var s = FontManager.Swap(lib, Family, 9, 0, (1, 1), (1, 1), 0, fractEnable: true, fScaleDisable: false, macOS9: false)!;
        Assert.Equal(('A' - ' ' + 1) * 9 << 4, s.Widths['A']);
        Assert.Equal(73 * 9 << 4, s.Widths['g']);
        Assert.Equal(73 * 9 << 4, s.Widths[200]);
    }

    [Fact]
    public void Text_GrayishTextOr_DrawsTheMidGray()
    {
        var b = PictBuilder.V2(0, 0, 7, 10).U16(0x0003).U16(Family).U16(0x000D).U16(9).U16(0x0005).U16(49)
            .U16(0x0028).Point(4, 2).Text("A").Align().U16(0x00FF);
        var bmp = PictReader.Decode(b.ToArray(), new PictDecodeOptions { Fonts = Library(), QuickDraw = PictQuickDraw.MacRom });
        Assert.Equal(new PictColor(0x80, 0x80, 0x80), bmp[2, 1]);
        // Mac OS 9 too (srcOr in the gray GetGray finds).
        Assert.Equal(new PictColor(0x80, 0x80, 0x80), PictReader.Decode(b.ToArray(), new PictDecodeOptions { Fonts = Library() })[2, 1]);
    }

    [Fact]
    public void FontManager_NearestSize_TiesGoToTheLarger()
    {
        var lib = new PictFontLibrary();
        lib.AddFamily(Family, null, Family(Family, (9, 0, 1), (13, 0, 2)));
        lib.AddNfnt(1, Font9);
        lib.AddNfnt(2, Build(4, 2, 0, 0, new[] { new Glyph('A', 4, 0, "###") }));
        Assert.Equal(4, FontManager.Swap(lib, Family, 11, 0, (1, 1), (1, 1), 0, false, false, macOS9: false)!.Font.Ascent);
    }

    [Fact]
    public void FontManager_OldStyleFonts_ScanUpwardBeforeDownward()
    {
        // No FOND: 10 pt is not 9 pt's neighbour first - the scan goes 11, 12 before 9.
        var lib = new PictFontLibrary();
        lib.AddFont(Family * 128 + 9, Font9);
        lib.AddFont(Family * 128 + 12, Build(4, 2, 0, 0, new[] { new Glyph('A', 4, 0, "###") }));
        var s = FontManager.Swap(lib, Family, 10, 0, (1, 1), (1, 1), 0, false, false, macOS9: false)!;
        Assert.Equal(4, s.Font.Ascent);
        Assert.Equal((213, 213), s.Numer);                 // 10 / 12 as 8.8, rounded
    }

    [Fact]
    public void FontManager_Widths_ExtraOnNonZeroWidthsAndCarriageReturnZero()
    {
        var lib = new PictFontLibrary();
        lib.AddFont(Family * 128 + 9, Build(3, 2, 0, 1, new[]
        {
            new Glyph('\r', 5, 0, "#"), new Glyph(' ', 2, 0), new Glyph('A', 3, 0, "##"), new Glyph('B', 0, 0, "#"),
        }, missing: new Glyph('\0', 2, 0, "#")));
        var s = FontManager.Swap(lib, Family, 9, 1, (1, 1), (1, 1), 0, false, false, macOS9: false)!;   // bold: extra 1
        Assert.Equal(4 << 16, s.Widths['A']);
        Assert.Equal(0, s.Widths['B']);                    // zero widths get no extra
        Assert.Equal(3 << 16, s.Widths['Z']);              // the missing symbol's width does
        Assert.Equal(0, s.Widths['\r']);
    }
}
