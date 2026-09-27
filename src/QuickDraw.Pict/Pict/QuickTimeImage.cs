using System;
using System.Buffers.Binary;
using System.Text;

namespace QuickDraw.Pict
{
    // The CompressedQuickTime opcode (0x8200): u32 data length, then version, a 3x3 matrix (a b u / c d v / h v w;
    // u, v, w are 2.30 fixed, the rest 16.16), matte size and rect, transfer mode, source rect, accuracy, mask size,
    // the matte (image description + data), the mask region, and the image description (86 bytes, plus a color table
    // when clutID is 0, plus atoms) followed by the compressed data.
    internal sealed class QuickTimeImage
    {
        public int[] Matrix = new int[9];
        public int Mode;
        public PictRect SourceRect;
        public Region? Mask;
        public PictImageDescription Description = null!;
        public byte[] Data = Array.Empty<byte>();

        public static QuickTimeImage? Parse(byte[] block)
        {
            if (block.Length < 2 + 36 + 4 + 8 + 2 + 8 + 4 + 4) return null;
            var q = new QuickTimeImage();
            int p = 2;                                                // version
            int I32() { int v = BinaryPrimitives.ReadInt32BigEndian(block.AsSpan(p)); p += 4; return v; }
            short I16() { short v = BinaryPrimitives.ReadInt16BigEndian(block.AsSpan(p)); p += 2; return v; }
            PictRect Rect() { var r = new PictRect(I16(), I16(), I16(), I16()); return r; }
            for (int i = 0; i < 9; i++) q.Matrix[i] = I32();
            int matteSize = I32();
            Rect();                                                   // matte rect
            q.Mode = (ushort)I16();
            q.SourceRect = Rect();
            I32();                                                    // accuracy
            int maskSize = I32();
            if (matteSize < 0 || maskSize < 0 || p + (long)matteSize + maskSize > block.Length) return null;
            p += matteSize;                                           // the matte is not applied
            if (maskSize > 0)
            {
                using var r = new System.IO.BinaryReader(new System.IO.MemoryStream(block, p, maskSize));
                try { q.Mask = Region.Read(r); } catch (System.IO.EndOfStreamException) { }
                p += maskSize;
            }
            var description = ImageDescriptionReader.Read(block, p, out int idSize);
            if (description == null) return null;
            q.Description = description;
            int dataStart = p + Math.Max(idSize, 86);
            if (dataStart > block.Length) return null;
            q.Data = block.AsSpan(dataStart).ToArray();
            return q;
        }

        // Where the matrix puts the source rect, in picture coordinates (scale and translation; a rotated or skewed
        // image is placed in its bounding box).
        public PictRect DestinationRect() => Place(Matrix, SourceRect);

        // The bounding box of a rect's corners mapped through a QuickTime matrix (h' = x a + y c + h, v' = x b + y d +
        // v, rounded).
        public static PictRect Place(int[] matrix, PictRect r)
        {
            (int h, int v) Map(int x, int y)
            {
                long h = (long)x * matrix[0] + (long)y * matrix[3] + matrix[6];
                long v = (long)x * matrix[1] + (long)y * matrix[4] + matrix[7];
                return ((int)((h + 0x8000) >> 16), (int)((v + 0x8000) >> 16));
            }
            var a = Map(r.Left, r.Top);
            var b = Map(r.Right, r.Bottom);
            var c = Map(r.Right, r.Top);
            var d = Map(r.Left, r.Bottom);
            int left = Math.Min(Math.Min(a.h, b.h), Math.Min(c.h, d.h)), right = Math.Max(Math.Max(a.h, b.h), Math.Max(c.h, d.h));
            int top = Math.Min(Math.Min(a.v, b.v), Math.Min(c.v, d.v)), bottom = Math.Max(Math.Max(a.v, b.v), Math.Max(c.v, d.v));
            return new PictRect(top, left, bottom, right);
        }

        // The decoded image as a 32-bit pixel map for CopyBits (alpha kept in the pad byte).
        public static PixMap ToPixMap(PictBitmap image)
        {
            var data = new byte[image.Width * image.Height * 4];
            var px = image.Pixels;
            for (int i = 0; i < image.Width * image.Height; i++)
            {
                data[4 * i] = px[4 * i + 3];
                data[4 * i + 1] = px[4 * i];
                data[4 * i + 2] = px[4 * i + 1];
                data[4 * i + 3] = px[4 * i + 2];
            }
            return new PixMap
            {
                Bounds = new PictRect(0, 0, image.Height, image.Width),
                RowBytes = image.Width * 4,
                PixelSize = 32,
                CmpCount = 4,
                IsPixMap = true,
                Data = data,
            };
        }
    }
}
