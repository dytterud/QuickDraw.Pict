using System.Buffers.Binary;
using System.IO;

namespace QuickDraw.Pict
{
    // PICT data is big-endian; BinaryReader always reads little-endian.
    internal static class BigEndianReader
    {
        public static short ReadI16BE(this BinaryReader b) => BinaryPrimitives.ReverseEndianness(b.ReadInt16());
        public static ushort ReadU16BE(this BinaryReader b) => BinaryPrimitives.ReverseEndianness(b.ReadUInt16());
        public static int ReadI32BE(this BinaryReader b) => BinaryPrimitives.ReverseEndianness(b.ReadInt32());
        public static uint ReadU32BE(this BinaryReader b) => BinaryPrimitives.ReverseEndianness(b.ReadUInt32());

        public static PictRect ReadRectBE(this BinaryReader b)
        {
            int top = b.ReadI16BE(), left = b.ReadI16BE(), bottom = b.ReadI16BE(), right = b.ReadI16BE();
            return new PictRect(top, left, bottom, right);
        }

        public static void Skip(this BinaryReader b, long n) => b.BaseStream.Seek(n, SeekOrigin.Current);

        // Reads exactly n bytes or throws, so truncated data surfaces as EndOfStreamException.
        public static byte[] ReadExactly(this BinaryReader b, int n)
        {
            var bytes = b.ReadBytes(n);
            if (bytes.Length != n) throw new EndOfStreamException();
            return bytes;
        }
    }
}
