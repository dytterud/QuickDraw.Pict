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

    // A resource fork holding the given resources (one type list entry per type, names in the name list).
    public static byte[] ResourceFork(params (string type, int id, string? name, byte[] data)[] resources)
    {
        var data = new List<byte>();
        var offsets = new List<int>();
        foreach (var r in resources)
        {
            offsets.Add(data.Count);
            data.AddRange(BE32(r.data.Length));
            data.AddRange(r.data);
        }
        var names = new List<byte>();
        var types = resources.Select(r => r.type).Distinct().ToList();
        var typeList = new List<byte>();
        var refLists = new List<byte>();
        typeList.AddRange(BE16(types.Count - 1));
        int refStart = 2 + 8 * types.Count;
        foreach (var t in types)
        {
            var group = resources.Select((r, i) => (r, i)).Where(x => x.r.type == t).ToList();
            typeList.AddRange(System.Text.Encoding.ASCII.GetBytes(t));
            typeList.AddRange(BE16(group.Count - 1));
            typeList.AddRange(BE16(refStart + refLists.Count));
            foreach (var (r, i) in group)
            {
                refLists.AddRange(BE16(r.id));
                if (r.name == null) refLists.AddRange(BE16(0xFFFF));
                else
                {
                    refLists.AddRange(BE16(names.Count));
                    names.Add((byte)r.name.Length);
                    names.AddRange(System.Text.Encoding.ASCII.GetBytes(r.name));
                }
                refLists.Add(0);
                refLists.Add((byte)(offsets[i] >> 16)); refLists.Add((byte)(offsets[i] >> 8)); refLists.Add((byte)offsets[i]);
                refLists.AddRange(BE32(0));
            }
        }
        var map = new List<byte>(new byte[24]);
        int typeListOffset = 28;
        map.AddRange(BE16(typeListOffset));
        map.AddRange(BE16(typeListOffset + typeList.Count + refLists.Count));
        map.AddRange(typeList);
        map.AddRange(refLists);
        map.AddRange(names);
        int dataOffset = 256, mapOffset = dataOffset + data.Count;
        var fork = new List<byte>();
        fork.AddRange(BE32(dataOffset)); fork.AddRange(BE32(mapOffset)); fork.AddRange(BE32(data.Count)); fork.AddRange(BE32(map.Count));
        fork.AddRange(new byte[dataOffset - 16]);
        fork.AddRange(data);
        fork.AddRange(map);
        return fork.ToArray();
    }

    private static byte[] BE16(int v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] BE32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    // 'FOND' with an association table.
    public static byte[] Family(int familyId, params (int size, int style, int fontId)[] entries) =>
        Family(familyId, 0, 0, 0, Array.Empty<(int, int[])>(), entries);

    // 'FOND' with flags, a character range and fractional width tables (style, 4.12 words) after the associations.
    public static byte[] Family(int familyId, int flags, int firstChar, int lastChar, (int style, int[] words)[] widthTables,
        params (int size, int style, int fontId)[] entries)
    {
        var b = new List<byte>();
        void W(int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        W(flags); W(familyId); W(firstChar); W(lastChar);
        for (int i = 0; i < 4; i++) W(0);                                  // ascent, descent, leading, widMax
        int wTabOff = widthTables.Length == 0 ? 0 : 54 + 6 * entries.Length;
        W(wTabOff >> 16); W(wTabOff);                                       // ffWTabOff at 16
        for (int i = 0; i < 15; i++) W(0);                                 // kern/style offsets, properties, intl
        W(0);                                                              // ffVersion at 50
        W(entries.Length - 1);
        foreach (var (size, style, id) in entries) { W(size); W(style); W(id); }
        if (widthTables.Length > 0)
        {
            W(widthTables.Length - 1);
            foreach (var (style, words) in widthTables) { W(style); foreach (var w in words) W(w); }
        }
        return b.ToArray();
    }
}
