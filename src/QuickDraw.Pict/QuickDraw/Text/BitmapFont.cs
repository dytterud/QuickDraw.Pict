using System;
using System.Buffers.Binary;

namespace QuickDraw.Pict
{
    // A 'FONT' or 'NFNT' resource: a bitmap strike (Inside Macintosh: Text, "The Bitmapped Font ('NFNT') Resource").
    // Header words: fontType, firstChar, lastChar, widMax, kernMax, nDescent (high word of owTLoc when positive),
    // fRectWidth, fRectHeight, owTLoc (offset in words from itself to the offset/width table), ascent, descent,
    // leading, rowWords; then the strike (rowWords * 2 bytes x fRectHeight rows), the location table and the
    // offset/width table (lastChar - firstChar + 3 words each: the characters, the missing symbol, a sentinel), an
    // optional fixed-point width table (fontType bit 1) and an optional height table (fontType bit 0).
    internal sealed class BitmapFont
    {
        public int FontType, FirstChar, LastChar, WidMax, KernMax, RectWidth, RectHeight, Ascent, Descent, Leading, RowWords;
        public byte[] Strike = Array.Empty<byte>();
        public int[] Locations = Array.Empty<int>();
        public int[] OffsetWidths = Array.Empty<int>();   // -1 = missing; else offset << 8 | width
        public int[]? Heights;                           // top << 8 | height, when the font has a height table
        public int[]? FractionalWidths;                  // 8.8 glyph widths, when the font has a width table (bit 1)
        public byte[] Data = Array.Empty<byte>();
        public int OffsetWidthTable;                     // byte offset of the offset/width table in Data

        // A color font's strike (fontType bits 2-4: log2 depth) holds rowWords x 2 x depth bytes per row.
        public int Depth => 1 << ((FontType >> 2) & 7);
        public int RowBytes => RowWords * 2 * Depth;
        public bool HasHeightTable => (FontType & 1) != 0;

        // The character's slot in the tables, or the missing symbol's (lastChar - firstChar + 1).
        public int MissingIndex => LastChar - FirstChar + 1;

        public static BitmapFont Parse(byte[] data)
        {
            if (data.Length < 26) throw new ArgumentException("Font resource too short.", nameof(data));
            short Word(int offset) => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset));
            var f = new BitmapFont
            {
                FontType = (ushort)Word(0),
                FirstChar = Word(2),
                LastChar = Word(4),
                WidMax = Word(6),
                KernMax = Word(8),
                RectWidth = Word(12),
                RectHeight = Word(14),
                Ascent = Word(18),
                Descent = Word(20),
                Leading = Word(22),
                RowWords = Word(24),
            };
            f.Data = data;
            int nDescent = Word(10);
            long owTLoc = (ushort)Word(16);
            if (nDescent > 0) owTLoc |= (long)nDescent << 16;    // nDescent >= 0 is owTLoc's high word

            int strikeBytes = f.RowBytes * f.RectHeight;
            f.Strike = data.AsSpan(26, Math.Min(strikeBytes, data.Length - 26)).ToArray();
            int entries = f.LastChar - f.FirstChar + 3;
            f.Locations = Words(data, 26 + strikeBytes, entries, unsigned: true);
            long ow = 16 + owTLoc * 2;
            f.OffsetWidthTable = (int)Math.Min(int.MaxValue, ow);
            f.OffsetWidths = Words(data, (int)ow, entries, unsigned: false);
            long after = ow + entries * 2L;
            if ((f.FontType & 2) != 0)
            {
                f.FractionalWidths = Words(data, (int)after, entries, unsigned: true);
                after += entries * 2L;
            }
            if (f.HasHeightTable) f.Heights = Words(data, (int)after, entries, unsigned: true);
            return f;
        }

        private static int[] Words(byte[] data, int offset, int count, bool unsigned)
        {
            var result = new int[Math.Max(0, count)];
            for (int i = 0; i < result.Length; i++)
            {
                int o = offset + 2 * i;
                if (o < 0 || o + 2 > data.Length) { result[i] = unsigned ? 0 : -1; continue; }
                short w = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(o));
                result[i] = unsigned ? (ushort)w : w;
            }
            return result;
        }

        // An offset/width table word by raw index, read from wherever it lands in the resource (0 past its end), as
        // DrText's first-character kerning reads it.
        public int RawOffsetWidth(int index)
        {
            long o = OffsetWidthTable + 2L * index;
            return o >= 0 && o + 2 <= Data.Length ? BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan((int)o)) : 0;
        }

        // The strike as a pixel map of its depth (for color fonts), with the given palette.
        public PixMap StrikeMap(PictColor[] palette) => new PixMap
        {
            Bounds = new PictRect(0, 0, RectHeight, RowWords * 16), RowBytes = RowBytes, PixelSize = Depth,
            IsPixMap = true, Palette = palette, Data = Strike,
        };

        public bool StrikeBit(int row, int column)
        {
            if (row < 0 || row >= RectHeight || column < 0 || column >= RowBytes * 8) return false;
            int i = row * RowBytes + (column >> 3);
            return i < Strike.Length && ((Strike[i] >> (7 - (column & 7))) & 1) != 0;
        }
    }

    // A 'FOND' resource: the family record header (ffFlags, ffFamID, ffFirstChar, ffLastChar, ffAscent, ffDescent,
    // ffLeading, ffWidMax, ffWTabOff, ffKernOff, ffStylOff, ffProperty[9], ffIntl[2], ffVersion), then the association
    // table (size, style, font resource id) at offset 52. ffProperty holds the style extra widths (4.12 per point:
    // plain, then one per style bit); ffWTabOff locates the family's fractional width tables (a count - 1, then per
    // table a style word and 4.12 widths for ffFirstChar..ffLastChar + 2).
    internal sealed class FontFamilyRecord
    {
        public readonly record struct Association(int Size, int Style, int FontId);
        public sealed record WidthTable(int Style, int Start);   // Start: byte offset of its first width

        public int FamilyId, Flags, FirstChar, LastChar;
        public int[] Property = new int[9];
        public Association[] Associations = Array.Empty<Association>();
        public WidthTable[] WidthTables = Array.Empty<WidthTable>();
        public byte[] Data = Array.Empty<byte>();

        // Width word i of a width table, read on from its start wherever that lands (0 past the resource).
        public int WidthWord(WidthTable table, int i)
        {
            long o = table.Start + 2L * i;
            return i >= 0 && o + 2 <= Data.Length ? BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan((int)o)) : 0;
        }

        public static FontFamilyRecord Parse(int familyId, byte[] data)
        {
            var f = new FontFamilyRecord { FamilyId = familyId, Data = data };
            if (data.Length < 54) return f;
            f.Flags = BinaryPrimitives.ReadUInt16BigEndian(data);
            f.FirstChar = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(4));
            f.LastChar = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(6));
            for (int i = 0; i < 9; i++) f.Property[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(28 + 2 * i));
            f.WidthTables = ReadWidthTables(data, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)), f.FirstChar, f.LastChar);
            int count = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(52)) + 1;
            var list = new Association[Math.Max(0, Math.Min(count, (data.Length - 54) / 6))];
            for (int i = 0; i < list.Length; i++)
            {
                var e = data.AsSpan(54 + 6 * i);
                list[i] = new Association(BinaryPrimitives.ReadInt16BigEndian(e), BinaryPrimitives.ReadInt16BigEndian(e.Slice(2)),
                    BinaryPrimitives.ReadInt16BigEndian(e.Slice(4)));
            }
            f.Associations = list;
            return f;
        }

        private static WidthTable[] ReadWidthTables(byte[] data, int offset, int firstChar, int lastChar)
        {
            if (offset <= 0 || lastChar == 0 || offset + 2 > data.Length) return Array.Empty<WidthTable>();
            int count = BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset)) + 1;
            int entries = lastChar - firstChar + 3;
            if (count <= 0 || entries <= 0) return Array.Empty<WidthTable>();
            var tables = new System.Collections.Generic.List<WidthTable>();
            int o = offset + 2;
            for (int t = 0; t < count && o + 2 <= data.Length; t++)
            {
                tables.Add(new WidthTable(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o)), o + 2));
                o += 2 + 2 * entries;                                   // stepped with the family's own range
            }
            return tables.ToArray();
        }
    }
}
