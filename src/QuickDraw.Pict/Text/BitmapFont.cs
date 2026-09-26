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

        public int RowBytes => RowWords * 2;
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
            int nDescent = Word(10);
            long owTLoc = (ushort)Word(16);
            if (nDescent > 0) owTLoc |= (long)nDescent << 16;

            int strikeBytes = f.RowBytes * f.RectHeight;
            f.Strike = data.AsSpan(26, Math.Min(strikeBytes, data.Length - 26)).ToArray();
            int entries = f.LastChar - f.FirstChar + 3;
            f.Locations = Words(data, 26 + strikeBytes, entries, unsigned: true);
            long ow = 16 + owTLoc * 2;
            f.OffsetWidths = Words(data, (int)ow, entries, unsigned: false);
            long after = ow + entries * 2L;
            if ((f.FontType & 2) != 0) after += entries * 2L;           // width table
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

        public bool StrikeBit(int row, int column)
        {
            if (row < 0 || row >= RectHeight || column < 0 || column >= RowBytes * 8) return false;
            int i = row * RowBytes + (column >> 3);
            return i < Strike.Length && ((Strike[i] >> (7 - (column & 7))) & 1) != 0;
        }
    }

    // A 'FOND' resource: a font family's association table (size, style, font resource id), after the family record
    // header (ffFlags, ffFamID, ffFirstChar, ffLastChar, ffAscent, ffDescent, ffLeading, ffWidMax, ffWTabOff,
    // ffKernOff, ffStylOff, ffProperty[9], ffIntl[2], ffVersion) at offset 52.
    internal sealed class FontFamilyRecord
    {
        public readonly record struct Association(int Size, int Style, int FontId);

        public int FamilyId;
        public Association[] Associations = Array.Empty<Association>();

        public static FontFamilyRecord Parse(int familyId, byte[] data)
        {
            var f = new FontFamilyRecord { FamilyId = familyId };
            if (data.Length < 54) return f;
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
    }
}
