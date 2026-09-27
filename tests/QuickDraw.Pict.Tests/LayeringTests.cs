using System.Text.RegularExpressions;
using Xunit;

namespace QuickDraw.Pict.Tests;

// The core is one package split into layer folders (src/QuickDraw.Pict/<Layer>/), in the order they will become
// separate ClassicMac packages. A layer may use only the layers below it; this test reads the sources and fails on any
// type a file uses from a layer it may not depend on.
public class LayeringTests
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["Graphics"] = Array.Empty<string>(),
        ["MacPaint"] = new[] { "Graphics" },
        ["QuickTime"] = new[] { "Graphics", "MacPaint" },
        ["QuickDraw"] = new[] { "Graphics" },
        ["Pict"] = new[] { "Graphics", "MacPaint", "QuickTime", "QuickDraw" },
        ["Resources"] = new[] { "Graphics", "QuickDraw" },
    };

    // Kept on purpose until the merge (see the ClassicMac plan): PictBitmap.Info is public API; at the merge the PICT
    // decoder returns the info separately.
    private static readonly (string File, string Type)[] Exceptions = { ("PictBitmap.cs", "PictInfo") };

    [Fact]
    public void EveryLayerUsesOnlyTheLayersBelowIt()
    {
        var src = Path.Combine(FindRoot(), "src", "QuickDraw.Pict");
        var files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path: f, Layer: Path.GetRelativePath(src, f).Split(Path.DirectorySeparatorChar)[0],
                          Code: Strip(File.ReadAllText(f))))
            .ToList();

        var unplaced = files.Where(f => !Allowed.ContainsKey(f.Layer)).Select(f => Path.GetRelativePath(src, f.Path)).ToList();
        Assert.True(unplaced.Count == 0, "Files outside a layer folder: " + string.Join(", ", unplaced));

        var declared = new Dictionary<string, string>();
        foreach (var f in files)
            foreach (Match m in Regex.Matches(f.Code, @"\b(?:class|struct|record(?:\s+struct)?|interface|enum)\s+([A-Z]\w*)"))
                declared[m.Groups[1].Value] = f.Layer;

        var violations = new List<string>();
        foreach (var f in files)
            foreach (var (type, layer) in declared)
            {
                if (layer == f.Layer || Allowed[f.Layer].Contains(layer)) continue;
                if (Exceptions.Contains((Path.GetFileName(f.Path), type))) continue;
                if (Regex.IsMatch(f.Code, $@"\b{type}\b"))
                    violations.Add($"{Path.GetRelativePath(src, f.Path)} ({f.Layer}) uses {type} ({layer})");
            }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    // Comments and string literals removed, so words in them are not taken for type references.
    private static string Strip(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        code = Regex.Replace(code, @"//.*", "");
        return Regex.Replace(code, @"""(\\.|[^""\\])*""", "\"\"");
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "QuickDraw.Pict.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("QuickDraw.Pict.slnx not found above the test output.");
    }
}
