namespace QuickDraw.Pict.Tests;

// Builds 'FONT'/'NFNT' resources for tests: glyphs given as rows of '#'/'.' (top row = ascent rows above the
// baseline), each with an advance width and an image offset (relative to kernMax).
internal static class TestFont
{
    public sealed record Glyph(char Char, int Width, int Offset, params string[] Rows);

    public static byte[] Build(int ascent, int descent, int kernMax, int leading, IReadOnlyList<Glyph> glyphs,
        Glyph? missing = null, bool heightTable = false)
    {
        int first = glyphs.Min(g => g.Char), last = glyphs.Max(g => g.Char);
        int height = ascent + descent;
        var all = new List<Glyph?>();
        for (int c = first; c <= last; c++) all.Add(glyphs.FirstOrDefault(g => g.Char == c));
        all.Add(missing);                                               // the missing symbol
        int stripWidth = all.Sum(g => g == null ? 0 : ImageWidth(g));
        int rowWords = Math.Max(1, (stripWidth + 15) / 16);
        var strike = new byte[rowWords * 2 * height];
        var locs = new List<int>();
        var ows = new List<int>();
        int x = 0;
        foreach (var g in all)
        {
            locs.Add(x);
            if (g == null) { ows.Add(-1); continue; }
            int w = ImageWidth(g);
            for (int r = 0; r < g.Rows.Length && r < height; r++)
                for (int i = 0; i < g.Rows[r].Length; i++)
                    if (g.Rows[r][i] == '#') strike[r * rowWords * 2 + ((x + i) >> 3)] |= (byte)(0x80 >> ((x + i) & 7));
            ows.Add((g.Offset << 8) | g.Width);
            x += w;
        }
        locs.Add(x);                                                     // sentinel
        ows.Add(-1);

        var b = new List<byte>();
        void W(int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        int entries = last - first + 3;
        int owTLoc = (26 - 16 + strike.Length + entries * 2) / 2;       // words from offset 16 to the OW table
        W(0x9000 | (heightTable ? 1 : 0)); W(first); W(last); W(glyphs.Max(g => g.Width)); W(kernMax); W(-descent);
        W(stripWidth); W(height); W(owTLoc); W(ascent); W(descent); W(leading); W(rowWords);
        b.AddRange(strike);
        foreach (var l in locs) W(l);
        foreach (var o in ows) W(o);
        if (heightTable)
            foreach (var g in all.Append(null))
                W(g == null ? 0 : height);                               // top 0, full height
        return b.ToArray();
    }

    private static int ImageWidth(Glyph g) => g.Rows.Length == 0 ? 0 : g.Rows.Max(r => r.Length);

    // 'FOND' with an association table.
    public static byte[] Family(int familyId, params (int size, int style, int fontId)[] entries)
    {
        var b = new List<byte>();
        void W(int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        W(0); W(familyId);
        for (int i = 0; i < 23; i++) W(0);                                 // header up to ffVersion (offset 50)
        W(0);                                                              // ffVersion at 50
        W(entries.Length - 1);
        foreach (var (size, style, id) in entries) { W(size); W(style); W(id); }
        return b.ToArray();
    }
}
