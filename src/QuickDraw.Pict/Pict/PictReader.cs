using System;
using System.IO;
using System.Text;
using System.Threading;

namespace QuickDraw.Pict
{
    /// <summary>
    /// Decodes a QuickDraw PICT (v1 / v2 / extended v2) to a <see cref="PictBitmap"/>, drawing it the way QuickDraw's
    /// DrawPicture does: shapes are rasterized as QuickDraw regions and transferred through the port's patterns and
    /// transfer modes, bitmaps are decoded from every PixMap layout, and text is rasterized by an optional
    /// <see cref="IPictTextFallback"/>. Every opcode in Inside Macintosh: Imaging With QuickDraw, Appendix A,
    /// Table A-2 is parsed or skipped by its specified operand size; only malformed data and unsupported pixel depths
    /// throw.
    /// </summary>
    public static class PictReader
    {
        private static readonly Encoding MacRoman = GetMacRoman();
        private static Encoding GetMacRoman()
        {
            try { return CodePagesEncodingProvider.Instance.GetEncoding(10000) ?? Encoding.Latin1; }
            catch { return Encoding.Latin1; }
        }
        internal static string MacRomanString(byte[] bytes) => MacRoman.GetString(bytes);

        /// <summary>Decodes the picture read from the current position to the end of <paramref name="stream"/>.</summary>
        /// <inheritdoc cref="Decode(byte[], PictDecodeOptions?, CancellationToken)"/>
        public static PictBitmap Decode(Stream stream, PictDecodeOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Decode(ms.ToArray(), options, cancellationToken);
        }

        /// <summary>
        /// Decodes a picture, either bare (as stored in a <c>PICT</c> resource) or as a <c>.pict</c> file with its
        /// 512-byte application header. The canvas covers <see cref="PictInfo.Bounds"/>, or <see cref="PictInfo.PictureFrame"/>
        /// with <see cref="PictResolution.PictureFrame"/>; pixels the picture never
        /// draws stay transparent.
        /// </summary>
        /// <param name="data">The picture bytes.</param>
        /// <param name="options">Text rasterizer and highlight color; null for defaults.</param>
        /// <param name="cancellationToken">Cancels decoding between opcodes.</param>
        /// <exception cref="NotSupportedException">The picture uses an unsupported pixel format.</exception>
        /// <exception cref="EndOfStreamException">The picture data is truncated.</exception>
        public static PictBitmap Decode(byte[] data, PictDecodeOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            using var ms = new MemoryStream(data);
            using var b = new BinaryReader(ms);

            options ??= PictDecodeOptions.Default;
            var info = PictHeader.Parse(b, data.Length, out bool v1);
            var bounds = info.Bounds;
            var canvasRect = options.Resolution == PictResolution.PictureFrame ? info.PictureFrame : bounds;
            var canvas = new PictBitmap(Math.Max(1, canvasRect.Width), Math.Max(1, canvasRect.Height)) { Info = info };
            var port = new GrafPort(canvas, bounds, options);
            bool macOS9 = options.QuickDraw == PictQuickDraw.MacOS9;
            {
                PictRect? quickTimeRect = null;           // destination of a QuickTime image drawn by the last opcode
                while (b.BaseStream.Position < b.BaseStream.Length)
                {
                    var justDrawnQuickTime = quickTimeRect;
                    quickTimeRect = null;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!v1 && (b.BaseStream.Position & 1) == 1) // v2 opcodes are word-aligned
                        b.BaseStream.Seek(1, SeekOrigin.Current);
                    if (b.BaseStream.Position >= b.BaseStream.Length) break;

                    int op = v1 ? b.ReadByte() : b.ReadU16BE();
                    port.Version1 = v1;
                    if (macOS9 && (op == 0x0092 || op == 0x0093)) op = 0x0094;   // Mac OS 9: reserved
                    switch (op)
                    {
                        case 0x0011:                        // VersionOp mid-stream: 1 = byte opcodes, 2 = word opcodes
                            v1 = b.ReadByte() == 1;          // (v2's trailing 0xFF is eaten by the word alignment)
                            break;
                        case 0x0090:                        // BitsRect (read like PackBitsRect: the ROM ignores bit 3)
                        case 0x0091:                        // BitsRgn
                        case 0x0098:                        // PackBitsRect
                        case 0x0099:                        // PackBitsRgn
                        case 0x0092:                        // the ROM treats 0x92/0x93 as 0x9A/0x9B
                        case 0x0093:
                        case 0x009A:                        // DirectBitsRect
                        case 0x009B:                        // DirectBitsRgn
                        {
                            var (pm, src, dst, mode, mask) = ReadBits(b, op, macOS9);
                            if (dst != justDrawnQuickTime) port.CopyBits(pm, src, dst, mode, mask);
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
                        case 0x8200:                        // CompressedQuickTime
                        {
                            // A decoded image skips the picture's fallback for systems without QuickTime: the drawing
                            // after a PnSize marker (SkipQuickTimeFallback), or else a bitmap that immediately follows
                            // into the same rectangle (Photoshop's "QuickTime PICT" placeholder).
                            var block = b.ReadExactly((int)b.ReadU32BE());
                            var drawn = port.QuickTime(block);
                            if (drawn != null)
                            {
                                quickTimeRect = drawn;
                                SkipQuickTimeFallback(b);
                            }
                            break;
                        }
                        case 0x8201:                        // UncompressedQuickTime
                        {
                            var drawn = UncompressedQuickTime(port, b.ReadExactly((int)b.ReadU32BE()), macOS9);
                            if (drawn != null)
                            {
                                quickTimeRect = drawn;
                                SkipQuickTimeFallback(b);
                            }
                            break;
                        }
                        default:
                            if (!HandleDrawingOpcode(port, b, op, macOS9))
                                SkipOperands(b, op);
                            break;
                    }
                }
                return canvas;
            }
        }

        // QuickTime writes pictures whose compressed image is followed by drawing for systems without QuickTime
        // ("QuickTime and a ... decompressor are needed"), introduced by a PnSize opcode with v = 0x00AE whose h is the
        // byte count QuickTime skips once it has drawn the image.
        private static void SkipQuickTimeFallback(BinaryReader b)
        {
            var s = b.BaseStream;
            long start = s.Position + (s.Position & 1);
            if (start + 6 > s.Length) return;
            s.Position = start;
            if (b.ReadU16BE() == 0x0007 && b.ReadU16BE() == 0x00AE)
            {
                int skip = b.ReadU16BE();
                s.Position = Math.Min(s.Length, s.Position + skip);
                return;
            }
            s.Position = start;
        }

        // A bitmap opcode's operands (0x90-0x93, 0x98-0x9B): the BitMap/PixMap, srcRect, dstRect, mode, the mask
        // region of the Rgn variants (odd opcodes), and the pixel data.
        private static (PixMap pm, PictRect src, PictRect dst, int mode, Region? mask) ReadBits(BinaryReader b, int op,
            bool macOS9)
        {
            bool direct = (op & 0x0A) == 0x0A || op == 0x0092 || op == 0x0093;
            var pm = direct ? PixMap.ReadDirectHeader(b, macOS9) : PixMap.ReadIndexedHeader(b, macOS9);
            var (src, dst, mode, mask) = ReadCopyBitsTail(b, hasRegion: (op & 1) != 0);
            pm.ReadPixData(b);
            return (pm, src, dst, mode, mask);
        }

        // UncompressedQuickTime (0x8201): version, the 3x3 matrix, matte size and rect, the matte (skipped, then
        // word-aligned), then a bitmap opcode with its operands, drawn as QuickTime draws it. Returns where it drew
        // (picture space), or null when the block holds no bitmap opcode.
        private static PictRect? UncompressedQuickTime(GrafPort port, byte[] block, bool macOS9)
        {
            using var b = new BinaryReader(new MemoryStream(block));
            try
            {
                b.ReadU16BE();                                        // version
                var matrix = new int[9];
                for (int i = 0; i < 9; i++) matrix[i] = b.ReadI32BE();
                long matteSize = b.ReadU32BE();
                b.ReadRectBE();                                       // matte rect
                if (matteSize > block.Length - b.BaseStream.Position) return null;
                b.BaseStream.Position = (b.BaseStream.Position + matteSize + 1) & ~1L;
                int op = b.ReadU16BE();
                if (op < 0x0090 || op > 0x009B || (op > 0x0093 && op < 0x0098)) return null;
                if (macOS9 && (op == 0x0092 || op == 0x0093)) return null;
                var (pm, src, dst, mode, mask) = ReadBits(b, op, macOS9);
                return port.UncompressedQuickTime(pm, src, dst, mode, mask, matrix);
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }

        // srcRect, dstRect, mode and (Rgn variants) maskRgn, which sit between a CopyBits PixMap and its PixData.
        private static (PictRect src, PictRect dst, int mode, Region? mask) ReadCopyBitsTail(BinaryReader b, bool hasRegion)
        {
            var src = b.ReadRectBE();
            var dst = b.ReadRectBE();
            int mode = b.ReadU16BE();
            var mask = hasRegion ? Region.Read(b) : null;
            return (src, dst, mode, mask);
        }

        // Applies the drawing and graphics-state opcodes to the port. Returns false if the opcode isn't one we
        // interpret (the caller then consumes its operands via SkipOperands). Shape blocks: rect 0x30, round rect
        // 0x40, oval 0x50, arc 0x60, poly 0x70, region 0x80; + verb, "same" variants at base + 8.
        private static bool HandleDrawingOpcode(GrafPort port, BinaryReader b, int op, bool macOS9)
        {
            switch (op)
            {
                case 0x0001: port.SetClip(Region.Read(b)); return true;                  // ClipRgn
                case 0x0002: port.BkPat = Pattern.FromMono(b.ReadExactly(8)); return true;    // BkPat
                case 0x0009: port.PnPat = Pattern.FromMono(b.ReadExactly(8)); return true;    // PnPat
                case 0x000A: port.FillPat = Pattern.FromMono(b.ReadExactly(8)); return true;  // FillPat
                case 0x0012: port.BkPat = Pattern.Read(b, macOS9); return true;          // BkPixPat
                case 0x0013: port.PnPat = Pattern.Read(b, macOS9); return true;          // PnPixPat
                case 0x0014: port.FillPat = Pattern.Read(b, macOS9); return true;        // FillPixPat
                case 0x0003: port.TextFont(b.ReadU16BE()); return true;                  // TxFont
                case 0x0004: port.TextFace = b.ReadByte(); return true;                  // TxFace
                case 0x0005: port.TextMode = b.ReadU16BE(); return true;                 // TxMode
                case 0x0007: { var p = ReadPoint(b); port.PenSize(p.h, p.v); return true; }              // PnSize
                case 0x0008: port.PenMode = b.ReadU16BE(); return true;                  // PnMode
                case 0x000B: { var p = ReadPoint(b); port.OvalSize(p.h, p.v); return true; }             // OvSize
                case 0x000C: { int dh = b.ReadI16BE(), dv = b.ReadI16BE(); port.Origin(dh, dv); return true; } // Origin: dh, dv
                case 0x000D: port.TextSize = b.ReadU16BE(); return true;                 // TxSize
                case 0x0006: port.SpaceExtra = b.ReadI32BE(); return true;               // SpExtra (Fixed)
                case 0x0015: port.PnLocHFrac(b.ReadU16BE()); return true;                // PnLocHFrac
                case 0x0016: port.ChExtra = (short)b.ReadU16BE(); return true;           // ChExtra (4.12 per point)
                case 0x0010: { var n = ReadPoint(b); var d = ReadPoint(b); port.TextRatio(n.h, n.v, d.h, d.v); return true; }   // TxRatio
                case 0x000E: (port.ForeColor, port.Fore16) = ClassicColor((int)b.ReadU32BE(), true); return true;  // FgColor
                case 0x000F: (port.BackColor, port.Back16) = ClassicColor((int)b.ReadU32BE(), false); return true; // BkColor
                case 0x001A: (port.ForeColor, port.Fore16) = ReadRgbExact(b); return true;                          // RGBFgCol
                case 0x001B: (port.BackColor, port.Back16) = ReadRgbExact(b); return true;                          // RGBBkCol
                case 0x001C: port.HiliteMode(); return true;                             // HiliteMode
                case 0x001D: port.HiliteColor = ReadRgb(b); return true;                 // HiliteColor
                case 0x001E: port.DefaultHilite(); return true;                          // DefHilite
                case 0x001F: port.OpColor = (b.ReadU16BE(), b.ReadU16BE(), b.ReadU16BE()); return true;   // OpColor
                case 0x0020: { var a = ReadPoint(b); var c = ReadPoint(b); port.Line(a.h, a.v, c.h, c.v); return true; }   // Line
                case 0x0021: { var c = ReadPoint(b); port.LineTo(c.h, c.v); return true; }               // LineFrom
                case 0x0022: { var a = ReadPoint(b); sbyte dh = (sbyte)b.ReadByte(), dv = (sbyte)b.ReadByte(); port.Line(a.h, a.v, a.h + dh, a.v + dv); return true; }   // ShortLine
                case 0x0023: { sbyte dh = (sbyte)b.ReadByte(), dv = (sbyte)b.ReadByte(); port.LineBy(dh, dv); return true; }   // ShortLineFrom
                case 0x0028: { var p = ReadPoint(b); port.LongText(p.h, p.v, ReadText(b)); return true; }            // LongText
                case 0x0029: { int dh = b.ReadByte(); port.OffsetText(dh, 0, ReadText(b)); return true; }            // DHText
                case 0x002A: { int dv = b.ReadByte(); port.OffsetText(0, dv, ReadText(b)); return true; }            // DVText
                case 0x002B: { int dh = b.ReadByte(), dv = b.ReadByte(); port.OffsetText(dh, dv, ReadText(b)); return true; }   // DHDVText
                case 0x002C:                                                              // fontName
                {
                    int length = b.ReadU16BE();
                    var data = b.ReadExactly(length);
                    if (length >= 3 && data[2] <= length - 3)
                        port.FontName((data[0] << 8) | data[1], MacRoman.GetString(data, 3, data[2]));
                    return true;
                }
                case 0x002D:                                                              // LineJustify
                {
                    var data = b.ReadExactly(b.ReadU16BE());                              // interCharSpacing, textExtra
                    if (data.Length >= 4) port.LineJustify((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
                    return true;
                }
                case 0x002E:                                                              // glyphState
                {
                    var data = b.ReadExactly(b.ReadU16BE());                              // outline preferred, preserve
                    if (data.Length >= 3)                                                 // glyph, fractional widths,
                        port.GlyphState(data[2] != 0, data.Length >= 4 && data[3] != 0);  // scaling disabled
                    return true;
                }
            }

            if (op >= 0x0030 && op <= 0x0034) { port.Rect(b.ReadRectBE(), op - 0x0030); return true; }
            if (op >= 0x0038 && op <= 0x003C) { port.Rect(null, op - 0x0038); return true; }
            if (op >= 0x0040 && op <= 0x0044) { port.RoundRect(b.ReadRectBE(), op - 0x0040); return true; }
            if (op >= 0x0048 && op <= 0x004C) { port.RoundRect(null, op - 0x0048); return true; }
            if (op >= 0x0050 && op <= 0x0054) { port.Oval(b.ReadRectBE(), op - 0x0050); return true; }
            if (op >= 0x0058 && op <= 0x005C) { port.Oval(null, op - 0x0058); return true; }
            if (op >= 0x0060 && op <= 0x0064) { var r = b.ReadRectBE(); int sa = b.ReadI16BE(), aa = b.ReadI16BE(); port.Arc(r, sa, aa, op - 0x0060); return true; }
            if (op >= 0x0068 && op <= 0x006C) { int sa = b.ReadI16BE(), aa = b.ReadI16BE(); port.Arc(null, sa, aa, op - 0x0068); return true; }
            if (op >= 0x0070 && op <= 0x0074) { port.Polygon(ReadPolygon(b), op - 0x0070); return true; }
            if (op >= 0x0078 && op <= 0x007C) { port.Polygon(null, op - 0x0078); return true; }
            if (op >= 0x0080 && op <= 0x0084) { port.Rgn(Region.Read(b), op - 0x0080); return true; }
            if (op >= 0x0088 && op <= 0x008C) { port.Rgn(null, op - 0x0088); return true; }

            return false;
        }

        private static byte[] ReadText(BinaryReader b) => b.ReadExactly(b.ReadByte());

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

        private static (PictColor, (ushort, ushort, ushort)) ReadRgbExact(BinaryReader b)
        {
            int r = b.ReadU16BE(), g = b.ReadU16BE(), bl = b.ReadU16BE();
            return (new PictColor((byte)(r >> 8), (byte)(g >> 8), (byte)(bl >> 8), 255), ((ushort)r, (ushort)g, (ushort)bl));
        }

        // Classic 1-bit-era color constants (FgColor/BkColor longs): on a color port, the QDColors table (clut 127).
        private static (PictColor, (ushort, ushort, ushort)) ClassicColor(int value, bool fore)
        {
            (int r, int g, int b) c = value switch
            {
                30 => (0xFFFF, 0xFFFF, 0xFFFF),     // whiteColor
                33 => (0, 0, 0),                    // blackColor
                69 => (0xFC00, 0xF37D, 0x052F),     // yellowColor
                137 => (0xF2D7, 0x0856, 0x84EC),    // magentaColor
                205 => (0xDD6B, 0x08C2, 0x06A2),    // redColor
                273 => (0x0241, 0xAB54, 0xEAFF),    // cyanColor
                341 => (0x0000, 0x8000, 0x11B0),    // greenColor
                409 => (0x0000, 0x0000, 0xD400),    // blueColor
                _ => fore ? (0, 0, 0) : (0xFFFF, 0xFFFF, 0xFFFF),
            };
            return (new PictColor((byte)(c.r >> 8), (byte)(c.g >> 8), (byte)(c.b >> 8), 255), ((ushort)c.r, (ushort)c.g, (ushort)c.b));
        }

        // A Polygon: u16 polySize + bounding Rect + (polySize - 10) / 4 Points, returned as (h, v) picture points.
        // An empty polygon (no points or an empty bounding box) draws nothing (Executor C_StdPoly).
        private static (int h, int v)[] ReadPolygon(BinaryReader b)
        {
            int size = b.ReadU16BE();
            var bbox = b.ReadRectBE();
            int count = Math.Max(0, (size - 10) / 4);
            var pts = new (int h, int v)[count];
            for (int i = 0; i < count; i++)
            {
                var p = ReadPoint(b);
                pts[i] = (p.h, p.v);
            }
            if (size > 10 && (size - 10) % 4 != 0) b.Skip((size - 10) % 4);
            return bbox.IsEmpty ? Array.Empty<(int h, int v)>() : pts;
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
            if (op >= 0x0094 && op <= 0x0097) { SkipVar16(b); return; }     // reserved
            if (op >= 0x009C && op <= 0x009F) { SkipVar16(b); return; }     // reserved
            if (op >= 0x00A2 && op <= 0x00AF) { SkipVar16(b); return; }     // reserved
            if (op >= 0x00B0 && op <= 0x00CF) return;                       // reserved (no data)
            if (op >= 0x00D0 && op <= 0x00DF) { SkipVar16(b); return; }     // reserved: the ROM reads a u16 length
            if (op >= 0x00E0 && op <= 0x00FE) { SkipVar32(b); return; }     // reserved (u32 length + data)
            if (op >= 0x0100 && op <= 0x7FFF) { b.Skip(2 * (op >> 8)); return; }   // $nnXX: 2 * nn bytes
            if (op >= 0x8000 && op <= 0x80FF) return;                       // reserved (no data)
            // 0x8100-0xFFFF: u32 length + data, including 0x8200/0x8201 QuickTime and 0xFFFF.
            SkipVar32(b);
        }
    }
}
