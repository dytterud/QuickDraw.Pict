using System.Text;

namespace QuickDraw.Pict.Tests;

// Hand-assembles QuickDraw pictures (big-endian) for opcode-level tests.
internal sealed class PictBuilder
{
    private readonly List<byte> bytes = new();

    // Bare v2 picture: picSize, picFrame, version opcode 0x0011 0x02FF, then the 0x0C00 header op + 24 bytes.
    public static PictBuilder V2(int top, int left, int bottom, int right) =>
        new PictBuilder().U16(0).Rect(top, left, bottom, right).U16(0x0011).U16(0x02FF).U16(0x0C00).Zeros(24);

    // Bare v1 picture: picSize, picFrame, then the 1-byte version opcode 0x11 + version 0x01.
    public static PictBuilder V1(int top, int left, int bottom, int right) =>
        new PictBuilder().U16(0).Rect(top, left, bottom, right).U8(0x11).U8(0x01);

    public PictBuilder U8(int v) { bytes.Add((byte)v); return this; }
    public PictBuilder U16(int v) { bytes.Add((byte)(v >> 8)); bytes.Add((byte)v); return this; }
    public PictBuilder Rect(int top, int left, int bottom, int right) => U16(top).U16(left).U16(bottom).U16(right);
    public PictBuilder Point(int v, int h) => U16(v).U16(h);

    // The Origin opcode: dh first, then dv (unlike a Point).
    public PictBuilder Origin(int dh, int dv) => U16(0x000C).U16(dh).U16(dv);
    public PictBuilder Rgb(int r, int g, int b) => U16(r).U16(g).U16(b);
    public PictBuilder Text(string s) { U8(s.Length); bytes.AddRange(Encoding.ASCII.GetBytes(s)); return this; }
    public PictBuilder Bytes(params byte[] data) { bytes.AddRange(data); return this; }
    public PictBuilder Zeros(int n) { bytes.AddRange(new byte[n]); return this; }
    public PictBuilder Align() => bytes.Count % 2 == 1 ? U8(0) : this;   // v2 opcodes are word-aligned

    public byte[] ToArray() => bytes.ToArray();
}
