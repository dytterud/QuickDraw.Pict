using System.Text;

// Writes the golden feature pictures (tests/golden/pict/*.pict): each is 240 x 160 with a 1-pixel black frame on its
// picture frame, so the golden test can find it in a screenshot of a Macintosh showing it at 100% (SimpleText).
// Usage: dotnet run --project tools/GoldenPictures [output directory]
string outDir = args.Length > 0 ? args[0] : Path.Combine("tests", "golden", "pict");
Directory.CreateDirectory(outDir);
const int W = 240, H = 160;

// `scale`: the picture's coordinates per 72 dpi pixel (2 for the 144 dpi picture).
void Save(string name, Action<Pict> draw, Pict? start = null, int scale = 1)
{
    var p = start ?? Pict.Standard(W, H);
    p.Clip(0, 0, H * scale, W * scale);
    draw(p);
    p.Clip(0, 0, H * scale, W * scale).Op(0x000C).Point(0, 0);
    p.Op(0x0007).Point(scale, scale).Op(0x0009).Pattern(0xFF).Op(0x0008).U16(8);   // a 1-pixel black patCopy pen
    p.Op(0x001A).Rgb(0, 0, 0).Op(0x0030).Rect(0, 0, H * scale, W * scale);         // the registration frame
    File.WriteAllBytes(Path.Combine(outDir, name + ".pict"), p.End());
    Console.WriteLine(name);
}

// 1. Shapes: every verb on rect, round rect, oval, arc, polygon and region, with several pen sizes.
Save("shapes", p =>
{
    int x = 6;
    foreach (var (ph, pv) in new[] { (1, 1), (2, 2), (3, 1) })
    {
        p.Op(0x0007).Point(pv, ph).Op(0x000B).Point(10, 14);
        p.Op(0x0030).Rect(6, x, 30, x + 30);                                    // frame rect
        p.Op(0x0040).Rect(36, x, 60, x + 30);                                   // frame round rect
        p.Op(0x0050).Rect(66, x, 90, x + 30);                                   // frame oval
        p.Op(0x0060).Rect(96, x, 126, x + 30).U16(30).U16(250);                 // frame arc
        p.Op(0x0070).Poly((x, 150), (x + 15, 130), (x + 30, 152), (x + 10, 140));
        x += 36;
    }
    p.Op(0x0031).Rect(6, 118, 30, 150);                                         // paint rect
    p.Op(0x0041).Rect(36, 118, 60, 150);                                        // paint round rect
    p.Op(0x0051).Rect(66, 118, 91, 149);                                        // paint oval (odd size)
    p.Op(0x0061).Rect(96, 118, 126, 148).U16(-45).U16(135);                     // paint arc
    p.Op(0x0071).Poly((118, 130), (150, 132), (120, 154), (140, 140));
    p.Op(0x0033).Rect(10, 160, 40, 230);                                        // invert
    p.Op(0x000A).Pattern(0x88, 0x22).Op(0x0054).Rect(46, 160, 80, 230);         // fill oval with a pattern
    p.Op(0x0002).Pattern(0xAA, 0x55).Op(0x0052).Rect(84, 160, 100, 230);        // erase with a pattern
    p.Op(0x0081).Region(104, 160, 150, 230,
        new[] { (104, 160, 120, 230), (120, 160, 150, 180), (130, 200, 150, 230) });   // paint region
    p.Op(0x0007).Point(2, 3).Op(0x0080).Region(104, 160, 150, 230,
        new[] { (104, 160, 120, 230), (120, 160, 150, 180), (130, 200, 150, 230) });   // frame region
});

// 2. Pen modes with patterns over stripes.
Save("modes", p =>
{
    for (int i = 0; i < 12; i++) p.Op(0x0031).Rect(4 + 12 * i, 4, 10 + 12 * i, 236);
    for (int m = 0; m < 8; m++)
    {
        int x = 6 + 29 * m;
        p.Op(0x0009).Pattern(0x81, 0x42, 0x24, 0x18).Op(0x0008).U16(8 + m).Op(0x0031).Rect(8, x, 150, x + 26);
    }
});

// 3. Lines: a star of slopes at three pen sizes.
Save("lines", p =>
{
    int cx = 40;
    foreach (var (ph, pv) in new[] { (1, 1), (2, 2), (3, 1) })
    {
        p.Op(0x0007).Point(pv, ph);
        for (int a = 0; a < 24; a++)
        {
            double t = a * Math.PI / 12;
            p.Op(0x0020).Point(80, cx).Point(80 + (int)Math.Round(70 * Math.Sin(t)), cx + (int)Math.Round(34 * Math.Cos(t)));
        }
        cx += 80;
    }
});

// A 16 x 16 1-bit image (rows of a fixed pseudo-random pattern) and a 24 x 24 color gradient.
var bits = new byte[32];
uint seed = 1984;
for (int i = 0; i < bits.Length; i++) { seed = seed * 1103515245 + 12345; bits[i] = (byte)(seed >> 16); }

// 4. CopyBits of the 1-bit image: every Boolean source mode at 1:1 and every StretchBits ratio case.
Save("copybits1", p =>
{
    p.Op(0x0031).Rect(2, 120, 158, 238);                                        // black right half
    int[] modes = { 0, 1, 2, 3, 4, 5, 6, 7 };
    for (int i = 0; i < modes.Length; i++)
        p.Bits1(bits, 16, 16, (6 + 18 * i, 104), 16, 16, modes[i]);
    p.Bits1(bits, 16, 16, (6, 4), 32, 32, 0);                                   // x2
    p.Bits1(bits, 16, 16, (40, 4), 24, 24, 0);                                  // x1.5
    p.Bits1(bits, 16, 16, (66, 4), 12, 12, 0);                                  // x3/4
    p.Bits1(bits, 16, 16, (80, 4), 8, 8, 0);                                    // x1/2
    p.Bits1(bits, 16, 16, (90, 4), 11, 23, 0);                                  // 11/16 x 23/16
    p.Bits1(bits, 16, 16, (6, 40), 48, 48, 0);                                  // x3
    p.Bits1(bits, 16, 16, (6, 124), 48, 32, 1);                                 // srcOr stretched onto black
    p.BitsRgn1(bits, 16, 16, (60, 124), (60, 124, 76, 132), (68, 132, 76, 140)); // mask region
});

// 5. CopyBits of color images: 8-bit (standard palette), 16-bit and 32-bit, at 1:1, shrunk and stretched.
Save("copybitsColor", p =>
{
    int y = 6;
    foreach (int depth in new[] { 8, 16, 32 })
    {
        p.Pixels(depth, 24, 24, (y, 6), 24, 24);
        p.Pixels(depth, 24, 24, (y, 36), 18, 18);                               // 3/4
        p.Pixels(depth, 24, 24, (y, 60), 12, 12);                               // 1/2
        p.Pixels(depth, 24, 24, (y, 78), 36, 36);                               // 3/2
        p.Pixels(depth, 24, 24, (y, 120), 17, 31);                              // arbitrary
        y += 50;
    }
});

// 6. Color: fore/back colors on shapes and patterns, colorized 1-bit CopyBits, arithmetic modes, hilite.
Save("color", p =>
{
    p.Op(0x001A).Rgb(0xFFFF, 0, 0).Op(0x001B).Rgb(0, 0, 0xFFFF);
    p.Op(0x0009).Pattern(0xCC, 0x33).Op(0x0031).Rect(4, 4, 30, 60);             // pattern in fore/back colors
    p.Bits1(bits, 16, 16, (4, 66), 26, 26, 0);                                  // colorized
    p.Op(0x001A).Rgb(0, 0, 0).Op(0x001B).Rgb(0xFFFF, 0xFFFF, 0xFFFF).Op(0x0009).Pattern(0xFF);
    for (int i = 0; i < 8; i++)                                                 // a gray ramp background
    {
        int v = i * 0x2000;
        p.Op(0x001A).Rgb(v, 0xFFFF - v, v / 2).Op(0x0031).Rect(40, 4 + 29 * i, 150, 33 + 29 * i);
    }
    p.Op(0x001F).Rgb(0x8000, 0x4000, 0xC000);                                   // OpColor
    int[] arithmetic = { 32, 33, 34, 35, 36, 37, 38, 39 };
    for (int i = 0; i < arithmetic.Length; i++)
        p.Op(0x001A).Rgb(0x6000, 0xA000, 0x2000).Op(0x0008).U16(arithmetic[i]).Op(0x0031).Rect(50 + 12 * i, 10, 58 + 12 * i, 230);
    p.Op(0x0008).U16(8).Op(0x001D).Rgb(0xFFFF, 0xCCCC, 0).Op(0x001C).Op(0x0033).Rect(4, 120, 30, 230);   // hilite
});

// 7. Text: the standard fonts and every style (needs the same bitmap fonts in tests/golden/fonts to compare).
Save("text", p =>
{
    const string sample = "Sphinx of black quartz 0123";
    (int id, string name)[] fonts = { (0, "Chicago"), (2, "New York"), (3, "Geneva"), (4, "Monaco") };
    foreach (var (id, name) in fonts) p.FontName(id, name);
    int y = 14;
    foreach (var (id, size) in new[] { (0, 12), (3, 9), (3, 12), (2, 12), (4, 9), (3, 10), (2, 18) })
    {
        p.Op(0x0003).U16(id).Op(0x000D).U16(size).Op(0x0004).U8(0).Align();
        p.Op(0x0028).Point(y, 6).Text(sample).Align();
        y += size + 4;
    }
    int x = 6;
    foreach (int face in new[] { 1, 2, 4, 8, 16, 32, 64 })
    {
        p.Op(0x0003).U16(3).Op(0x000D).U16(12).Op(0x0004).U8(face).Align();
        p.Op(0x0028).Point(y + 8, x).Text("Style").Align();
        x += 34;
    }
    p.Op(0x0031).Rect(y + 14, 4, y + 30, 120).Op(0x0005).U16(2).Op(0x0004).U8(0).Align();   // srcXor over black
    p.Op(0x0028).Point(y + 26, 8).Text("Inverted text").Align();
});

// 8. High resolution: an extended version 2 picture drawn at 144 dpi into its 72 dpi frame (scaled by DrawPicture).
Save("highres", p =>
{
    p.Op(0x0007).Point(2, 2).Op(0x0050).Rect(20, 20, 280, 460);
    p.Op(0x0061).Rect(40, 40, 260, 260).U16(0).U16(120);
    p.Bits1(bits, 16, 16, (100, 300), 64, 64, 0);
    p.Op(0x0003).U16(3).Op(0x000D).U16(24).Op(0x0028).Point(300, 40).Text("High resolution").Align();
}, Pict.Extended(W, H, 144), scale: 2);

// 9. Origin and clipping: shifted coordinates, pattern alignment and a non-rectangular clip.
Save("originClip", p =>
{
    p.Op(0x000A).Pattern(0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01);
    p.Op(0x0034).Rect(4, 4, 40, 116);
    p.Op(0x000C).Point(-3, -5);                                                 // Origin dh -5, dv -3
    p.Op(0x0034).Rect(4, 124, 40, 236);                                         // same pattern, shifted phase
    p.Op(0x000C).Point(3, 5);
    p.Op(0x0001).Region(50, 4, 156, 236, new[] { (50, 4, 100, 120), (80, 60, 156, 236) });
    p.Op(0x0051).Rect(50, 4, 156, 236);
});

// A picture under construction, big-endian.
sealed class Pict
{
    private readonly List<byte> b = new();

    public static Pict Standard(int w, int h) =>
        new Pict().Zeros(512).U16(0).Rect(0, 0, h, w).U16(0x0011).U16(0x02FF).U16(0x0C00)
            .U16(0xFFFE).U16(0).U32(72 << 16).U32(72 << 16).Rect(0, 0, h, w).U32(0);

    // A picture whose opcodes draw at `dpi` in a (w, h) 72 dpi frame.
    public static Pict Extended(int w, int h, int dpi) =>
        new Pict().Zeros(512).U16(0).Rect(0, 0, h, w).U16(0x0011).U16(0x02FF).U16(0x0C00)
            .U16(0xFFFE).U16(0).U32((uint)dpi << 16).U32((uint)dpi << 16).Rect(0, 0, h * dpi / 72, w * dpi / 72).U32(0);

    public Pict U8(int v) { b.Add((byte)v); return this; }
    public Pict U16(int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); return this; }
    public Pict U32(uint v) => U16((int)(v >> 16)).U16((int)(v & 0xFFFF));
    public Pict Zeros(int n) { b.AddRange(new byte[n]); return this; }
    public Pict Align() => b.Count % 2 == 1 ? U8(0) : this;
    public Pict Op(int op) => Align().U16(op);
    public Pict Rect(int t, int l, int bt, int r) => U16(t).U16(l).U16(bt).U16(r);
    public Pict Point(int v, int h) => U16(v).U16(h);
    public Pict Rgb(int r, int g, int bl) => U16(r).U16(g).U16(bl);
    public Pict Text(string s) { U8(s.Length); b.AddRange(Encoding.ASCII.GetBytes(s)); return this; }
    public Pict Clip(int t, int l, int bt, int r) => Op(0x0001).U16(10).Rect(t, l, bt, r);

    // An 8-byte pattern from repeated rows.
    public Pict Pattern(params int[] rows) { for (int i = 0; i < 8; i++) U8(rows[i % rows.Length]); return this; }

    public Pict FontName(int id, string name) =>
        Op(0x002C).U16(3 + name.Length).U16(id).Text(name);

    // A polygon from (h, v) points.
    public Pict Poly(params (int h, int v)[] pts)
    {
        U16(10 + 4 * pts.Length).Rect(pts.Min(p => p.v), pts.Min(p => p.h), pts.Max(p => p.v), pts.Max(p => p.h));
        foreach (var (h, v) in pts) Point(v, h);
        return this;
    }

    // A region: the union of rects, written as inversion points.
    public Pict Region(int t, int l, int bt, int r, (int t, int l, int b, int r)[] rects)
    {
        var inside = new bool[bt - t + 1, r - l + 1];
        foreach (var x in rects)
            for (int y = x.t; y < x.b; y++)
                for (int h = x.l; h < x.r; h++) inside[y - t, h - l] = true;
        bool At(int y, int h) => y >= t && h >= l && y < bt && h < r && inside[y - t, h - l];
        var data = new List<int>();
        for (int y = t; y <= bt; y++)
        {
            var xs = new List<int>();
            for (int h = l; h <= r; h++)
                if (At(y, h) ^ At(y, h - 1) ^ At(y - 1, h) ^ At(y - 1, h - 1)) xs.Add(h);
            if (xs.Count == 0) continue;
            data.Add(y); data.AddRange(xs); data.Add(0x7FFF);
        }
        data.Add(0x7FFF);
        U16(10 + 2 * data.Count).Rect(t, l, bt, r);
        foreach (var v in data) U16(v);
        return this;
    }

    // BitsRect of a 1-bit image (rowBytes 2) at `at` (v, h) scaled to (dw, dh) with `mode`.
    public Pict Bits1(byte[] rows, int w, int h, (int v, int h) at, int dw, int dh, int mode)
    {
        Op(0x0090).U16(2).Rect(0, 0, h, w).Rect(0, 0, h, w).Rect(at.v, at.h, at.v + dh, at.h + dw).U16(mode);
        b.AddRange(rows);
        return this;
    }

    // BitsRgn of the 1-bit image at 1:1 through a mask region of two rects.
    public Pict BitsRgn1(byte[] rows, int w, int h, (int v, int h) at, (int t, int l, int b, int r) a, (int t, int l, int b, int r) c)
    {
        Op(0x0091).U16(2).Rect(0, 0, h, w).Rect(0, 0, h, w).Rect(at.v, at.h, at.v + h, at.h + w).U16(0);
        Region(Math.Min(a.t, c.t), Math.Min(a.l, c.l), Math.Max(a.b, c.b), Math.Max(a.r, c.r), new[] { a, c });
        b.AddRange(rows);
        return this;
    }

    // A w x h gradient at depth 8 (standard 8-bit palette indices), 16 or 32, unpacked, drawn at (v, h) as dw x dh.
    public Pict Pixels(int depth, int w, int h, (int v, int h) at, int dw, int dh)
    {
        int rowBytes = depth == 8 ? (w + 1) & ~1 : w * depth / 8;
        var dst = (at.v, at.h, at.v + dh, at.h + dw);
        if (depth == 8)
        {
            Op(0x0098).U16(rowBytes | 0x8000).Rect(0, 0, h, w);
            PixMapFields(packType: 0, pixelType: 0, depth: 8, cmpCount: 1, cmpSize: 8);
            U32(0).U16(0x8000).U16(255);                                        // ctSeed, device ctFlags, 256 entries
            for (int i = 0; i < 256; i++) { var (r, g, bl) = Standard8(i); U16(i).Rgb(r * 257, g * 257, bl * 257); }
        }
        else
        {
            Op(0x009A).U32(0xFF).U16(rowBytes | 0x8000).Rect(0, 0, h, w);
            PixMapFields(packType: 1, pixelType: 16, depth: depth, cmpCount: 3, cmpSize: depth == 16 ? 5 : 8);
        }
        Rect(0, 0, h, w).Rect(dst.Item1, dst.Item2, dst.Item3, dst.Item4).U16(0);
        for (int y = 0; y < h; y++)
        {
            var row = new List<byte>();
            for (int x = 0; x < w; x++)
            {
                int r = x * 255 / (w - 1), g = y * 255 / (h - 1), bl = (x + y) * 255 / (w + h - 2);
                switch (depth)
                {
                    case 8:
                    {
                        // Nearest color of the 6x6x6 cube (levels from white); the cube's black is entry 255.
                        int ri = 5 - (r + 25) / 51, gi = 5 - (g + 25) / 51, bi = 5 - (bl + 25) / 51;
                        int index = ri * 36 + gi * 6 + bi;
                        row.Add((byte)(index == 215 ? 255 : index));
                        break;
                    }
                    case 16: { int v = ((r >> 3) << 10) | ((g >> 3) << 5) | (bl >> 3); row.Add((byte)(v >> 8)); row.Add((byte)v); break; }
                    default: row.Add(0); row.Add((byte)r); row.Add((byte)g); row.Add((byte)bl); break;
                }
            }
            while (row.Count < rowBytes) row.Add(0);
            if (depth == 8 && rowBytes >= 8) { U8(rowBytes + 1); U8(rowBytes - 1); }   // one PackBits literal run
            b.AddRange(row);
        }
        return this;
    }

    private Pict PixMapFields(int packType, int pixelType, int depth, int cmpCount, int cmpSize) =>
        U16(0).U16(packType).U32(0).U32(72 << 16).U32(72 << 16).U16(pixelType).U16(depth).U16(cmpCount).U16(cmpSize)
            .U32(0).U32(0).U32(0);

    // The standard Macintosh 8-bit palette.
    private static (int r, int g, int b) Standard8(int i)
    {
        if (i < 215) return ((5 - i / 36) * 51, (5 - i / 6 % 6) * 51, (5 - i % 6) * 51);
        int[] ramp = { 0xEE, 0xDD, 0xBB, 0xAA, 0x88, 0x77, 0x55, 0x44, 0x22, 0x11 };
        if (i == 255) return (0, 0, 0);
        int k = (i - 215) % 10, v = ramp[k];
        return ((i - 215) / 10) switch { 0 => (v, 0, 0), 1 => (0, v, 0), 2 => (0, 0, v), _ => (v, v, v) };
    }

    public byte[] End()
    {
        Op(0x00FF);
        return b.ToArray();
    }
}
