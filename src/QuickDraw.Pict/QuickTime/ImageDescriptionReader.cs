using System;
using System.Buffers.Binary;
using System.Text;

namespace QuickDraw.Pict
{
    // QuickTime image descriptions, shared by the PICT opcode and QTIF files.
    internal static class ImageDescriptionReader
    {
        // An ImageDescription at p (86 bytes: idSize, cType, reserved, version, revision, vendor, temporal and spatial
        // quality, width, height, hRes, vRes, dataSize, frameCount, name[32], depth, clutID), with the color table that
        // follows it when clutID is 0 (else the standard table for the id or depth). Null if it does not fit.
        public static PictImageDescription? Read(byte[] block, int p, out int idSize)
        {
            idSize = 0;
            if (p < 0 || p + 86 > block.Length) return null;
            int idStart = p;
            int I32() { int v = BinaryPrimitives.ReadInt32BigEndian(block.AsSpan(p)); p += 4; return v; }
            short I16() { short v = BinaryPrimitives.ReadInt16BigEndian(block.AsSpan(p)); p += 2; return v; }
            idSize = I32();
            string codec = Encoding.Latin1.GetString(block, p, 4);
            p += 4 + 8 + 2 + 2 + 4 + 4 + 4;                           // cType, reserved, version, revision, vendor, qualities
            int width = (ushort)I16(), height = (ushort)I16();
            double hRes = I32() / 65536.0, vRes = I32() / 65536.0;
            I32();                                                    // dataSize
            I16();                                                    // frameCount
            int nameLength = Math.Min(block[p], (byte)31);
            string name = Encoding.Latin1.GetString(block, p + 1, nameLength);
            p += 32;
            int depth = I16(), clutId = I16();
            PictColor[]? table = StandardColorTables.ForId(clutId);
            if (clutId == 0 && idSize > 86 && p + 8 <= block.Length)
                table = ReadColorTable(block, p, idStart + idSize);
            table ??= StandardColorTables.ForDepth(depth);
            return new PictImageDescription(codec, width, height, depth, clutId, hRes, vRes, name) { ColorTable = table };
        }

        // A ColorTable stored after the image description: ctSeed, ctFlags, ctSize, then (value, r, g, b) entries.
        private static PictColor[]? ReadColorTable(byte[] block, int p, int end)
        {
            int size = BinaryPrimitives.ReadInt16BigEndian(block.AsSpan(p + 6)) + 1;
            if (size <= 0 || size > 256) return null;
            var table = new PictColor[size];
            p += 8;
            for (int i = 0; i < size && p + 8 <= Math.Min(end, block.Length); i++, p += 8)
                table[i] = new PictColor(block[p + 2], block[p + 4], block[p + 6]);
            return table;
        }
    }
}
