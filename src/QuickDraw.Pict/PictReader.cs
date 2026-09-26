using System;
using System.IO;
using System.Text;
using System.Threading;

namespace QuickDraw.Pict
{
    /// <summary>
    /// Decodes a QuickDraw PICT (v1 / v2 / extended v2) to a <see cref="PictBitmap"/>. Bitmap opcodes (BitsRect /
    /// PackBitsRect / DirectBitsRect and their Rgn variants) are decoded here; vector/shape/text opcodes are resolved to
    /// geometry and passed to an optional <see cref="IPictRenderer"/>. Every opcode in Inside Macintosh: Imaging With
    /// QuickDraw, Appendix A, Table A-2 is parsed or skipped by its specified operand size; only malformed data and
    /// unsupported pixel depths throw.
    /// </summary>
    public static class PictReader
    {
        private static readonly Encoding MacRoman = GetMacRoman();
        private static Encoding GetMacRoman()
        {
            try { return CodePagesEncodingProvider.Instance.GetEncoding(10000) ?? Encoding.Latin1; }
            catch { return Encoding.Latin1; }
        }
        private static string ReadMacString(BinaryReader b, int n) => MacRoman.GetString(b.ReadExactly(n));

        /// <summary>Decodes the picture read from the current position to the end of <paramref name="stream"/>.</summary>
        /// <inheritdoc cref="Decode(byte[], Func{PictBitmap, IPictRenderer}?, CancellationToken)"/>
        public static PictBitmap Decode(Stream stream, Func<PictBitmap, IPictRenderer>? rendererFactory = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Decode(ms.ToArray(), rendererFactory, cancellationToken);
        }

        /// <summary>
        /// Decodes a picture, either bare (as stored in a <c>PICT</c> resource) or as a <c>.pict</c> file with its
        /// 512-byte application header. The canvas covers <see cref="PictInfo.Bounds"/>.
        /// </summary>
        /// <param name="data">The picture bytes.</param>
        /// <param name="rendererFactory">
        /// Creates the renderer for the vector/text opcodes, given the canvas being decoded into. Null skips those
        /// opcodes (their operands are still consumed), so only bitmap content is decoded.
        /// </param>
        /// <param name="cancellationToken">Cancels decoding between opcodes.</param>
        /// <exception cref="NotSupportedException">The picture uses an unsupported pixel format.</exception>
        /// <exception cref="EndOfStreamException">The picture data is truncated.</exception>
        public static PictBitmap Decode(byte[] data, Func<PictBitmap, IPictRenderer>? rendererFactory = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            using var ms = new MemoryStream(data);
            using var b = new BinaryReader(ms);

            var info = PictHeader.Parse(b, data.Length, out bool v1);
            var bounds = info.Bounds;
            var canvas = new PictBitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height)) { Info = info };
            var renderer = rendererFactory?.Invoke(canvas);
            try
            {
                var port = new GrafPort(canvas, renderer, bounds.Left, bounds.Top);
                while (b.BaseStream.Position < b.BaseStream.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!v1 && (b.BaseStream.Position & 1) == 1) // v2 opcodes are word-aligned
                        b.BaseStream.Seek(1, SeekOrigin.Current);
                    if (b.BaseStream.Position >= b.BaseStream.Length) break;

                    int op = v1 ? b.ReadByte() : b.ReadU16BE();
                    switch (op)
                    {
                        case 0x0011:                        // VersionOp mid-stream: 1 = byte opcodes, 2 = word opcodes
                            v1 = b.ReadByte() == 1;          // (v2's trailing 0xFF is eaten by the word alignment)
                            break;
                        case 0x0090:                        // BitsRect
                        case 0x0091:                        // BitsRgn
                        case 0x0098:                        // PackBitsRect
                        case 0x0099:                        // PackBitsRgn
                        {
                            var pm = PixMap.ReadIndexedHeader(b);
                            var (src, dst) = ReadCopyBitsTail(b, hasRegion: (op & 0x01) != 0);
                            pm.ReadPixData(b, packedOpcode: (op & 0x08) != 0);
                            Blit(pm, src, dst, canvas, bounds);
                            break;
                        }
                        case 0x009A:                        // DirectBitsRect
                        case 0x009B:                        // DirectBitsRgn
                        {
                            var pm = PixMap.ReadDirectHeader(b);
                            var (src, dst) = ReadCopyBitsTail(b, hasRegion: op == 0x009B);
                            pm.ReadPixData(b, packedOpcode: true);
                            Blit(pm, src, dst, canvas, bounds);
                            break;
                        }
                        case 0x00A0:                        // ShortComment
                            info.AddComment(b.ReadU16BE(), Array.Empty<byte>());
                            break;
                        case 0x00A1:                        // LongComment
                        {
                            int kind = b.ReadU16BE();
                            int size = b.ReadU16BE();
                            info.AddComment(kind, b.ReadExactly(size));
                            break;
                        }
                        case 0x00FF:                        // end of picture
                            return canvas;
                        default:
                            if (!HandleDrawingOpcode(port, b, op))
                                SkipOperands(b, op);
                            break;
                    }
                }
                return canvas;
            }
            finally
            {
                (renderer as IDisposable)?.Dispose();
            }
        }

        // srcRect, dstRect, mode and (Rgn variants) maskRgn, which sit between a CopyBits PixMap and its PixData.
        private static (PictRect src, PictRect dst) ReadCopyBitsTail(BinaryReader b, bool hasRegion)
        {
            var src = b.ReadRectBE();
            var dst = b.ReadRectBE();
            b.ReadU16BE();                                  // transfer mode
            if (hasRegion) SkipRegion(b);                   // mask region
            return (src, dst);
        }

        // Applies the vector/shape/graphics-state opcodes to the GrafPort. Returns false if the
        // opcode isn't one we handle (caller then consumes its operands via SkipOperands).
        private static bool HandleDrawingOpcode(GrafPort port, BinaryReader b, int op)
        {
            // shape blocks: rect 0x30, round-rect 0x40, oval 0x50, arc 0x60; +verb within the block,
            // "same" variants at base+8.
            switch (op)
            {
                case 0x0001: port.Clip = ReadRegionBBox(b); return true;           // clip region
                case 0x0002: port.BkPat = Pattern.FromMono(b.ReadExactly(8)); return true;    // BkPat
                case 0x0009: port.PnPat = Pattern.FromMono(b.ReadExactly(8)); return true;    // PnPat
                case 0x000A: port.FillPat = Pattern.FromMono(b.ReadExactly(8)); return true;  // FillPat
                case 0x0012: port.BkPat = Pattern.Read(b); return true;            // BkPixPat
                case 0x0013: port.PnPat = Pattern.Read(b); return true;            // PnPixPat
                case 0x0014: port.FillPat = Pattern.Read(b); return true;          // FillPixPat
                case 0x0007: { var p = ReadPoint(b); port.PenWidth = p.h; return true; }  // PnSize
                case 0x000B: { var p = ReadPoint(b); port.OvalW = p.h; port.OvalH = p.v; return true; }  // OvSize
                case 0x000C: { var p = ReadPoint(b); port.OriginH = p.h; port.OriginV = p.v; return true; }  // Origin
                case 0x000E: port.ForeColor = ClassicColor((int)b.ReadU32BE(), true); return true;   // FgColor
                case 0x000F: port.BackColor = ClassicColor((int)b.ReadU32BE(), false); return true;  // BkColor
                case 0x001A: port.ForeColor = ReadRgb(b); return true;             // RGBFgCol
                case 0x001B: port.BackColor = ReadRgb(b); return true;             // RGBBkCol
                case 0x001F: ReadRgb(b); return true;                             // OpColor (parsed, unused)
                case 0x0020: { var a = ReadPoint(b); var c = ReadPoint(b); port.Pen = port.P(a.h, a.v); port.Line(port.Pen, port.P(c.h, c.v)); return true; }
                case 0x0021: { var c = ReadPoint(b); port.Line(port.Pen, port.P(c.h, c.v)); return true; }
                case 0x0022: { var a = ReadPoint(b); sbyte dh = (sbyte)b.ReadByte(), dv = (sbyte)b.ReadByte(); port.Pen = port.P(a.h, a.v); port.Line(port.Pen, port.P(a.h + dh, a.v + dv)); return true; }
                case 0x0023: { sbyte dh = (sbyte)b.ReadByte(), dv = (sbyte)b.ReadByte(); var to = new PictPoint(port.Pen.X + dh, port.Pen.Y + dv); port.Line(port.Pen, to); return true; }
                case 0x0003: port.TextFontId = b.ReadU16BE(); return true;         // TxFont
                case 0x0004: port.TextFace = b.ReadByte(); return true;            // TxFace (1 byte)
                case 0x0005: b.ReadU16BE(); return true;                          // TxMode (parsed, unused)
                case 0x000D: port.TextSize = b.ReadU16BE(); return true;          // TxSize
                case 0x0028: { var p = ReadPoint(b); int n = b.ReadByte(); var s = ReadMacString(b, n); port.SetTextLoc(p.h, p.v); port.DrawText(s); return true; }     // LongText
                case 0x0029: { int dh = b.ReadByte(); int n = b.ReadByte(); var s = ReadMacString(b, n); port.OffsetText(dh, 0); port.DrawText(s); return true; }       // DHText
                case 0x002A: { int dv = b.ReadByte(); int n = b.ReadByte(); var s = ReadMacString(b, n); port.OffsetText(0, dv); port.DrawText(s); return true; }       // DVText
                case 0x002B: { int dh = b.ReadByte(); int dv = b.ReadByte(); int n = b.ReadByte(); var s = ReadMacString(b, n); port.OffsetText(dh, dv); port.DrawText(s); return true; }  // DHDVText
            }

            if (op >= 0x0030 && op <= 0x0034) { port.Rect(ReadRect(b), op - 0x0030); return true; }
            if (op >= 0x0038 && op <= 0x003C) { port.SameRect(op - 0x0038); return true; }
            if (op >= 0x0040 && op <= 0x0044) { port.RoundRect(ReadRect(b), op - 0x0040); return true; }
            if (op >= 0x0048 && op <= 0x004C) { port.SameRoundRect(op - 0x0048); return true; }
            if (op >= 0x0050 && op <= 0x0054) { port.Oval(ReadRect(b), op - 0x0050); return true; }
            if (op >= 0x0058 && op <= 0x005C) { port.SameOval(op - 0x0058); return true; }
            if (op >= 0x0060 && op <= 0x0064) { var r = ReadRect(b); int sa = b.ReadI16BE(), aa = b.ReadI16BE(); port.Arc(r, sa, aa, op - 0x0060); return true; }
            if (op >= 0x0068 && op <= 0x006C) { int sa = b.ReadI16BE(), aa = b.ReadI16BE(); port.SameArc(sa, aa, op - 0x0068); return true; }
            if (op >= 0x0070 && op <= 0x0074) { port.LastPoly = ReadPolyPoints(b, port); port.Polygon(port.LastPoly, op - 0x0070); return true; }
            if (op >= 0x0078 && op <= 0x007C) { if (port.LastPoly != null) port.Polygon(port.LastPoly, op - 0x0078); return true; }
            if (op >= 0x0080 && op <= 0x0084) { port.LastRegion = ReadRegionBBox(b); port.RegionRect(port.LastRegion, op - 0x0080); return true; }
            if (op >= 0x0088 && op <= 0x008C) { port.RegionRect(port.LastRegion, op - 0x0088); return true; }

            return false;
        }

        // QuickDraw Point is (v, h) - vertical first.
        private static (int v, int h) ReadPoint(BinaryReader b)
        {
            int v = b.ReadI16BE();
            int h = b.ReadI16BE();
            return (v, h);
        }

        // RGBColor: three 16-bit channels (use the high byte).
        internal static PictColor ReadRgb(BinaryReader b)
        {
            int r = b.ReadU16BE(), g = b.ReadU16BE(), bl = b.ReadU16BE();
            return new PictColor((byte)(r >> 8), (byte)(g >> 8), (byte)(bl >> 8), 255);
        }

        // Classic 1-bit-era color constants (FgColor/BkColor longs).
        private static PictColor ClassicColor(int value, bool fore)
        {
            switch (value)
            {
                case 30: return new PictColor(255, 255, 255, 255);  // whiteColor
                case 33: return new PictColor(0, 0, 0, 255);        // blackColor
                case 69: return new PictColor(252, 243, 5, 255);    // yellowColor
                case 137: return new PictColor(241, 0, 144, 255);   // magentaColor
                case 205: return new PictColor(221, 8, 6, 255);     // redColor
                case 273: return new PictColor(2, 171, 234, 255);   // cyanColor
                case 341: return new PictColor(0, 100, 18, 255);    // greenColor
                case 409: return new PictColor(0, 0, 212, 255);     // blueColor
                default: return fore ? new PictColor(0, 0, 0, 255) : new PictColor(255, 255, 255, 255);
            }
        }

        private static (int top, int left, int bottom, int right) ReadRect(BinaryReader b)
        {
            var r = b.ReadRectBE();
            return (r.Top, r.Left, r.Bottom, r.Right);
        }

        // A Region/Polygon header: u16 size (incl. itself) + bounding Rect; skip any run data.
        private static (int top, int left, int bottom, int right) ReadRegionBBox(BinaryReader b)
        {
            int size = b.ReadU16BE();
            var bbox = ReadRect(b);
            int consumed = 2 + 8;
            if (size > consumed) b.Skip(size - consumed);
            return bbox;
        }

        // A Polygon: u16 size + bounding Rect + Point[] ((size-10)/4 points). Returns canvas-space points.
        private static PictPoint[] ReadPolyPoints(BinaryReader b, GrafPort port)
        {
            int size = b.ReadU16BE();
            b.ReadRectBE();                      // bbox (unused)
            int count = Math.Max(0, (size - 10) / 4);
            var pts = new PictPoint[count];
            for (int i = 0; i < count; i++)
            {
                var p = ReadPoint(b);
                pts[i] = port.P(p.h, p.v);
            }
            return pts;
        }

        // Copy the srcRect part of a decoded PixMap to its dstRect on the canvas (canvas = picture bounds).
        private static void Blit(PixMap pm, PictRect src, PictRect dst, PictBitmap canvas, PictRect bounds)
        {
            for (int y = 0; y < src.Height; y++)
            {
                int sy = (src.Top - pm.Bounds.Top) + y;
                int dy = (dst.Top - bounds.Top) + y;
                if (sy < 0 || sy >= pm.Height || dy < 0 || dy >= canvas.Height) continue;
                for (int x = 0; x < src.Width; x++)
                {
                    int sx = (src.Left - pm.Bounds.Left) + x;
                    int dx = (dst.Left - bounds.Left) + x;
                    if (sx < 0 || sx >= pm.Width || dx < 0 || dx >= canvas.Width) continue;
                    canvas[dx, dy] = pm.GetPixel(sx, sy);
                }
            }
        }

        // A QuickDraw Region or Polygon: u16 total size (including itself) + bounding Rect +
        // optional run data. We only need to skip past it.
        private static void SkipRegion(BinaryReader b)
        {
            int size = b.ReadU16BE();
            if (size >= 2) b.Skip(size - 2);
        }

        // var16/var32: a u16/u32 byte-length prefix followed by that many data bytes.
        private static void SkipVar16(BinaryReader b) => b.Skip(b.ReadU16BE());
        private static void SkipVar32(BinaryReader b) => b.Skip(b.ReadU32BE());

        // Text opcodes: positioning bytes, then a u8 char count, then that many chars.
        private static void SkipText(BinaryReader b, int positionBytes)
        {
            b.Skip(positionBytes);
            int count = b.ReadByte();
            b.Skip(count);
        }

        // Consumes the operands of an opcode that is not interpreted, by its size in Inside Macintosh: Imaging With
        // QuickDraw, Appendix A, Table A-2 (cross-checked with Executor's wparray, qPicstuff.cpp). Reserved opcodes
        // "for Apple use" are skipped, as QuickDraw does.
        private static void SkipOperands(BinaryReader b, int op)
        {
            switch (op)
            {
                case 0x0000: return;                                    // NOP
                case 0x0001: SkipRegion(b); return;                    // clip region
                case 0x0002: case 0x0009: case 0x000A: case 0x0010:    // BkPat/PnPat/FillPat/TxRatio
                    b.Skip(8); return;
                case 0x0003: case 0x0005: case 0x0008: case 0x000D:    // TxFont/TxMode/PnMode/TxSize
                case 0x0015: case 0x0016: case 0x0023:                 // PnLocHFrac/ChExtra/ShortLineFrom
                    b.Skip(2); return;
                case 0x0004: b.Skip(1); return;                        // TxFace
                case 0x0006: case 0x0007: case 0x000B: case 0x000C:    // SpExtra/PnSize/OvSize/Origin
                case 0x000E: case 0x000F: case 0x0021:                 // Fg/BkColor/LineFrom
                    b.Skip(4); return;
                case 0x0017: case 0x0018: case 0x0019: return;         // reserved (no data)
                case 0x001A: case 0x001B: case 0x001D: case 0x001F:    // RGB Fg/Bk/Hilite/OpColor
                    b.Skip(6); return;
                case 0x001C: case 0x001E: return;                      // HiliteMode/DefHilite
                case 0x0020: b.Skip(8); return;                        // Line
                case 0x0022: b.Skip(6); return;                        // ShortLine
                case 0x0028: SkipText(b, 4); return;                   // LongText
                case 0x0029: case 0x002A: SkipText(b, 1); return;      // DH/DV Text
                case 0x002B: SkipText(b, 2); return;                   // DHDV Text
                case 0x02FF: b.Skip(2); return;                        // Version (mid-stream)
            }

            if (op >= 0x0024 && op <= 0x0027) { SkipVar16(b); return; }     // reserved: length + data
            if (op >= 0x002C && op <= 0x002F) { SkipVar16(b); return; }     // fontName/lineJustify/glyphState/reserved
            if (op >= 0x0030 && op <= 0x0037) { b.Skip(8); return; }        // rect (incl. reserved)
            if (op >= 0x0038 && op <= 0x003F) return;                       // same rect (no data)
            if (op >= 0x0040 && op <= 0x0047) { b.Skip(8); return; }        // round rect
            if (op >= 0x0048 && op <= 0x004F) return;
            if (op >= 0x0050 && op <= 0x0057) { b.Skip(8); return; }        // oval
            if (op >= 0x0058 && op <= 0x005F) return;
            if (op >= 0x0060 && op <= 0x0067) { b.Skip(12); return; }       // arc (rect + angles)
            if (op >= 0x0068 && op <= 0x006F) { b.Skip(4); return; }        // same arc (angles)
            if (op >= 0x0070 && op <= 0x0077) { SkipRegion(b); return; }    // poly (size includes itself)
            if (op >= 0x0078 && op <= 0x007F) return;                       // same poly (no data)
            if (op >= 0x0080 && op <= 0x0087) { SkipRegion(b); return; }    // region
            if (op >= 0x0088 && op <= 0x008F) return;                       // same region (no data)
            if (op >= 0x0092 && op <= 0x0097) { SkipVar16(b); return; }     // reserved
            if (op >= 0x009C && op <= 0x009F) { SkipVar16(b); return; }     // reserved
            if (op >= 0x00A2 && op <= 0x00AF) { SkipVar16(b); return; }     // reserved
            if (op >= 0x00B0 && op <= 0x00CF) return;                       // reserved (no data)
            if (op >= 0x00D0 && op <= 0x00FE) { SkipVar32(b); return; }     // reserved (u32 length + data)
            if (op >= 0x0100 && op <= 0x7FFF) { b.Skip(2 * (op >> 8)); return; }   // $nnXX: 2 * nn bytes
            if (op >= 0x8000 && op <= 0x80FF) return;                       // reserved (no data)
            // 0x8100-0xFFFF: u32 length + data, including 0x8200/0x8201 QuickTime and 0xFFFF.
            SkipVar32(b);
        }
    }
}
