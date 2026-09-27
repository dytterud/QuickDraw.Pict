using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace QuickDraw.Pict
{
    // A Macintosh resource fork (Inside Macintosh: More Macintosh Toolbox, "Resource File Format"): a header with the
    // data and map offsets and lengths, resource data as length-prefixed blocks, and a map with the type list, each
    // type's reference list (id, name offset, attributes, 24-bit data offset) and the name list (Pascal strings).
    internal static class ResourceFork
    {
        public static IEnumerable<(string type, int id, string? name, byte[] data)> Read(byte[] fork)
        {
            if (fork.Length < 16) throw new ArgumentException("Not a resource fork.", nameof(fork));
            var s = fork.AsSpan();
            int dataOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(s);
            int mapOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(s.Slice(4));
            if (dataOffset < 16 || mapOffset < 16 || mapOffset + 30 > fork.Length || dataOffset > fork.Length)
                throw new ArgumentException("Not a resource fork.", nameof(fork));
            int typeList = mapOffset + BinaryPrimitives.ReadUInt16BigEndian(s.Slice(mapOffset + 24));
            int nameList = mapOffset + BinaryPrimitives.ReadUInt16BigEndian(s.Slice(mapOffset + 26));
            if (typeList + 2 > fork.Length) throw new ArgumentException("Not a resource fork.", nameof(fork));
            int types = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(typeList)) + 1;
            var result = new List<(string, int, string?, byte[])>();
            for (int t = 0; t < types && typeList + 2 + 8 * (t + 1) <= fork.Length; t++)
            {
                int entry = typeList + 2 + 8 * t;
                string type = Encoding.Latin1.GetString(fork, entry, 4);
                int count = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(entry + 4)) + 1;
                int refs = typeList + BinaryPrimitives.ReadUInt16BigEndian(s.Slice(entry + 6));
                for (int r = 0; r < count && refs + 12 * (r + 1) <= fork.Length; r++)
                {
                    int re = refs + 12 * r;
                    int id = BinaryPrimitives.ReadInt16BigEndian(s.Slice(re));
                    int nameOffset = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(re + 2));
                    int dataAt = dataOffset + ((s[re + 5] << 16) | (s[re + 6] << 8) | s[re + 7]);
                    if (dataAt + 4 > fork.Length) continue;
                    int length = (int)BinaryPrimitives.ReadUInt32BigEndian(s.Slice(dataAt));
                    if (length < 0 || dataAt + 4 + length > fork.Length) continue;
                    string? name = null;
                    if (nameOffset != 0xFFFF && nameList + nameOffset < fork.Length)
                    {
                        int at = nameList + nameOffset;
                        int n = Math.Min(fork[at], fork.Length - at - 1);
                        name = Encoding.Latin1.GetString(fork, at + 1, n);
                    }
                    result.Add((type, id, name, s.Slice(dataAt + 4, length).ToArray()));
                }
            }
            return result;
        }
    }
}
