using System;
using System.Collections.Generic;

namespace QuickDraw.Pict
{
    /// <summary>
    /// Classic Macintosh bitmap fonts for drawing a picture's text exactly as QuickDraw does: font families
    /// (<c>FOND</c> resources) and their bitmap strikes (<c>NFNT</c> and <c>FONT</c> resources), as found in the
    /// resource forks of font suitcases, the System file or an application. No fonts are built in.
    /// </summary>
    /// <remarks>
    /// Families are found by number (the picture's TxFont) or, when the picture names its fonts (the fontName
    /// opcode), by name. Old-style <c>FONT</c> resources without a family record are found by their resource id
    /// (family × 128 + size). Text in a family the library lacks falls back to
    /// <see cref="PictDecodeOptions.TextFallback"/>.
    /// </remarks>
    public sealed class PictFontLibrary
    {
        private readonly Dictionary<int, FontFamilyRecord> families = new Dictionary<int, FontFamilyRecord>();
        private readonly Dictionary<string, int> familyNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, byte[]> nfnt = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, byte[]> font = new Dictionary<int, byte[]>();
        private readonly Dictionary<(bool nfnt, int id), BitmapFont?> parsed = new Dictionary<(bool, int), BitmapFont?>();
        private readonly Dictionary<int, byte[]> colorTables = new Dictionary<int, byte[]>();

        /// <summary>The family used for font number 0 (the system font). Defaults to 0 (Chicago).</summary>
        public int SystemFontId { get; init; }

        /// <summary>The family used for font number 1 (the application font). Defaults to 3 (Geneva).</summary>
        public int ApplicationFontId { get; init; } = 3;

        // A library is filled before decoding starts; decoding only reads it (strikes parse lazily, under a lock).

        /// <summary>Adds a font family: a <c>FOND</c> resource with its resource id (the family number) and name.</summary>
        public void AddFamily(int familyId, string? name, byte[] fond)
        {
            ArgumentNullException.ThrowIfNull(fond);
            families[familyId] = FontFamilyRecord.Parse(familyId, fond);
            if (!string.IsNullOrEmpty(name)) familyNames[name] = familyId;
        }

        /// <summary>Adds an <c>NFNT</c> resource (a bitmap strike referenced by a family's association table).</summary>
        public void AddNfnt(int resourceId, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            nfnt[resourceId] = data;
        }

        /// <summary>
        /// Adds a <c>FONT</c> resource. Old-style fonts are numbered family × 128 + size; size 0 carries only the
        /// family's name, which may be given here.
        /// </summary>
        public void AddFont(int resourceId, byte[] data, string? familyName = null)
        {
            ArgumentNullException.ThrowIfNull(data);
            font[resourceId] = data;
            if (!string.IsNullOrEmpty(familyName) && (resourceId & 127) == 0) familyNames.TryAdd(familyName, resourceId >> 7);
        }

        /// <summary>
        /// Adds an <c>fctb</c> resource: the color table of the color <c>NFNT</c> with the same resource id.
        /// </summary>
        public void AddFontColorTable(int resourceId, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            colorTables[resourceId] = data;
        }

        /// <summary>
        /// Adds every <c>FOND</c>, <c>NFNT</c>, <c>FONT</c> and <c>fctb</c> resource of a Macintosh resource fork (a font
        /// suitcase, the System file, an application), with the families' names. Other resources are ignored.
        /// </summary>
        /// <returns>The number of font resources added.</returns>
        /// <exception cref="ArgumentException">The data is not a resource fork.</exception>
        public int AddResourceFork(byte[] resourceFork)
        {
            ArgumentNullException.ThrowIfNull(resourceFork);
            int added = 0;
            foreach (var (type, id, name, data) in ResourceFork.Read(resourceFork))
            {
                switch (type)
                {
                    case "FOND": AddFamily(id, name, data); added++; break;
                    case "NFNT": AddNfnt(id, data); added++; break;
                    case "FONT": AddFont(id, data, name); added++; break;
                    case "fctb": AddFontColorTable(id, data); added++; break;
                }
            }
            return added;
        }

        internal bool TryGetFamilyByName(string name, out int familyId) => familyNames.TryGetValue(name, out familyId);

        internal FontFamilyRecord? Family(int familyId) => families.TryGetValue(familyId, out var f) ? f : null;

        // The lowest-numbered Roman family (under 0x4000) with a family record, or null.
        internal int? LowestFamily()
        {
            int? lowest = null;
            foreach (var (id, fond) in families)
                if (id < 0x4000 && fond.Associations.Length > 0 && (lowest == null || id < lowest)) lowest = id;
            return lowest;
        }

        internal bool HasFamily(int familyId)
        {
            if (families.TryGetValue(familyId, out var fond) && fond.Associations.Length > 0) return true;
            foreach (var id in font.Keys)
                if ((id >> 7) == familyId && (id & 127) != 0) return true;
            return false;
        }

        // Old-style FONT sizes of a family (resource ids family * 128 + size).
        internal IEnumerable<int> OldStyleSizes(int familyId)
        {
            foreach (var id in font.Keys)
                if ((id >> 7) == familyId && (id & 127) != 0) yield return id & 127;
        }

        // A color font's palette: its fctb (same id), else the standard table of its depth.
        internal PictColor[] ColorFontPalette(int resourceId, int depth)
        {
            if (colorTables.TryGetValue(resourceId, out var fctb) && fctb.Length >= 8)
            {
                try
                {
                    using var b = new System.IO.BinaryReader(new System.IO.MemoryStream(fctb));
                    return PixMap.ReadColorTable(b, depth);
                }
                catch (System.IO.EndOfStreamException) { }
            }
            return StandardColorTables.ForId(depth) ?? StandardColorTables.ForId(8)!;
        }

        // A strike by resource id: NFNT first, then FONT, as the Font Manager looks them up.
        internal BitmapFont? Strike(int resourceId) => Load(true, resourceId) ?? Load(false, resourceId);

        // An old-style FONT by family and size; the Font Manager treats a FONT under 0x24 bytes as missing.
        internal BitmapFont? OldStyleStrike(int familyId, int size)
        {
            int id = familyId * 128 + size;
            return font.TryGetValue(id, out var data) && data.Length >= 0x24 ? Load(false, id) : null;
        }

        private BitmapFont? Load(bool isNfnt, int id)
        {
            lock (parsed)
                return LoadLocked(isNfnt, id);
        }

        private BitmapFont? LoadLocked(bool isNfnt, int id)
        {
            if (parsed.TryGetValue((isNfnt, id), out var cached)) return cached;
            BitmapFont? result = null;
            if ((isNfnt ? nfnt : font).TryGetValue(id, out var data))
            {
                try { result = BitmapFont.Parse(data); }
                catch (ArgumentException) { }
            }
            parsed[(isNfnt, id)] = result;
            return result;
        }
    }
}
