using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace QuickDraw.Pict
{
    /// <summary>
    /// Decodes a QuickDraw PICT (v1 / v2) to a <see cref="PictBitmap"/>. Bitmap opcodes (BitsRect / PackBitsRect /
    /// DirectBitsRect and their Rgn variants) are decoded here; vector/shape/text opcodes are resolved to geometry and
    /// passed to an optional <see cref="IPictRenderer"/>. Unsupported opcodes (QuickTime, pixel patterns) throw
    /// <see cref="NotSupportedException"/>. Opcode table follows the Apple QuickDraw/TI spec and resource_dasm QuickDrawEngine.
    /// </summary>
    public static class PictReader
    {
        private static readonly Encoding MacRoman = GetMacRoman();
        private static Encoding GetMacRoman()
        {
            try { return CodePagesEncodingProvider.Instance.GetEncoding(10000) ?? Encoding.Latin1; }
            catch { return Encoding.Latin1; }
        }
        private static string ReadMacString(BinaryReader b, int n) => MacRoman.GetString(b.ReadBytes(n));

        // A decoded bitmap plus the rects needed to composite it onto the picture frame.
        private sealed class Bits
        {
            public Bits(PictBitmap image, (int top, int left, int bottom, int right) bounds,
                (int top, int left, int bottom, int right) src, (int top, int left, int bottom, int right) dst)
            {
                Image = image; Bounds = bounds; Src = src; Dst = dst;
            }
            public readonly PictBitmap Image;
            public readonly (int top, int left, int bottom, int right) Bounds, Src, Dst;
        }

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
        /// 512-byte application header.
        /// </summary>
        /// <param name="data">The picture bytes.</param>
        /// <param name="rendererFactory">
        /// Creates the renderer for the vector/text opcodes, given the canvas being decoded into. Null skips those
        /// opcodes (their operands are still consumed), so only bitmap content is decoded.
        /// </param>
        /// <param name="cancellationToken">Cancels decoding between opcodes.</param>
        /// <exception cref="NotSupportedException">The picture uses an unsupported opcode or pixel format.</exception>
        /// <exception cref="EndOfStreamException">The picture data is truncated.</exception>
        public static PictBitmap Decode(byte[] data, Func<PictBitmap, IPictRenderer>? rendererFactory = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(data);
            using var ms = new MemoryStream(data);
            using var b = new BinaryReader(ms);

            ushort picSize = ReadU16(b);    // unreliable in v2
            var frame = ReadRect(b);
            // A .pict file may be preceded by a 512-byte null header; if the picture header reads as
            // all zeroes, skip it and re-read. (In-memory resources have no such pad.)
            if (picSize == 0 && frame == (0, 0, 0, 0) && data.Length > PictHeader.FileHeaderSize)
            {
                b.BaseStream.Seek(PictHeader.FileHeaderSize, SeekOrigin.Begin);
                ReadU16(b);
                frame = ReadRect(b);
            }
            int frameW = frame.right - frame.left;
            int frameH = frame.bottom - frame.top;

            bool v1;
            ushort versionOp = ReadU16(b);
            if (versionOp == 0x1101)
            {
                v1 = true;  // 0x11 = version opcode, 0x01 = version 1; 1-byte opcodes follow
            }
            else if (versionOp == 0x0011)
            {
                v1 = false;
                ReadU16(b);                 // version (0x02FF)
                ushort headerOp = ReadU16(b);
                if (headerOp == 0x0C00)
                    b.BaseStream.Seek(24, SeekOrigin.Current);
            }
            else
            {
                throw new NotSupportedException($"Unexpected PICT version opcode 0x{versionOp:X4}");
            }

            var canvas = new PictBitmap(Math.Max(1, frameW), Math.Max(1, frameH));
            var renderer = rendererFactory?.Invoke(canvas);
            try
            {
                var port = new GrafPort(canvas, renderer, frame.left, frame.top);
                while (b.BaseStream.Position < b.BaseStream.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!v1 && (b.BaseStream.Position & 1) == 1) // v2 opcodes are word-aligned
                        b.BaseStream.Seek(1, SeekOrigin.Current);
                    if (b.BaseStream.Position >= b.BaseStream.Length) break;

                    int op = v1 ? b.ReadByte() : ReadU16(b);
                    switch (op)
                    {
                        case 0x0090:                        // BitsRect (unpacked)
                        case 0x0098:                        // PackBitsRect
                        case 0x0091:                        // BitsRgn (with mask region)
                        case 0x0099:                        // PackBitsRgn (with mask region)
                            Blit(DecodeBitsRect(b, packed: (op & 0x08) != 0, hasRegion: (op & 0x01) != 0),
                                 canvas, frame.top, frame.left);
                            break;
                        case 0x009A:                        // DirectBitsRect (truecolor)
                        case 0x009B:                        // DirectBitsRgn (with mask region)
                            Blit(DecodeDirectBits(b, hasRegion: (op & 0x01) != 0),
                                 canvas, frame.top, frame.left);
                            break;
                        case 0x00FF:                        // end of picture
                            return canvas;
                        default:
                            // Render vector/shape/state opcodes; anything else has its operands consumed
                            // (SkipOperands) so the stream stays aligned.
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

        // Applies the vector/shape/graphics-state opcodes to the GrafPort. Returns false if the
        // opcode isn't one we handle (caller then consumes its operands via SkipOperands).
        private static bool HandleDrawingOpcode(GrafPort port, BinaryReader b, int op)
        {
            // shape blocks: rect 0x30, round-rect 0x40, oval 0x50, arc 0x60; +verb within the block,
            // "same" variants at base+8.
            switch (op)
            {
                case 0x0001: port.Clip = ReadRegionBBox(b); return true;           // clip region
                case 0x0007: { var p = ReadPoint(b); port.PenWidth = p.h; return true; }  // PnSize
                case 0x000B: { var p = ReadPoint(b); port.OvalW = p.h; port.OvalH = p.v; return true; }  // OvSize
                case 0x000C: { var p = ReadPoint(b); port.OriginH = p.h; port.OriginV = p.v; return true; }  // Origin
                case 0x000E: port.ForeColor = ClassicColor((int)ReadU32(b), true); return true;   // FgColor
                case 0x000F: port.BackColor = ClassicColor((int)ReadU32(b), false); return true;  // BkColor
                case 0x001A: port.ForeColor = ReadRgb(b); return true;             // RGBFgCol
                case 0x001B: port.BackColor = ReadRgb(b); return true;             // RGBBkCol
                case 0x001F: ReadRgb(b); return true;                             // OpColor (parsed, unused)
                case 0x0020: { var a = ReadPoint(b); var c = ReadPoint(b); port.Pen = port.P(a.h, a.v); port.Line(port.Pen, port.P(c.h, c.v)); return true; }
                case 0x0021: { var c = ReadPoint(b); port.Line(port.Pen, port.P(c.h, c.v)); return true; }
                case 0x0022: { var a = ReadPoint(b); sbyte dh = (sbyte)b.ReadByte(), dv = (sbyte)b.ReadByte(); port.Pen = port.P(a.h, a.v); port.Line(port.Pen, port.P(a.h + dh, a.v + dv)); return true; }
                case 0x0023: { sbyte dh = (sbyte)b.ReadByte(), dv = (sbyte)b.ReadByte(); var to = new PictPoint(port.Pen.X + dh, port.Pen.Y + dv); port.Line(port.Pen, to); return true; }
                case 0x0003: port.TextFontId = ReadU16(b); return true;            // TxFont
                case 0x0004: port.TextFace = b.ReadByte(); return true;            // TxFace (1 byte)
                case 0x0005: ReadU16(b); return true;                             // TxMode (parsed, unused)
                case 0x000D: port.TextSize = ReadU16(b); return true;             // TxSize
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
            if (op >= 0x0060 && op <= 0x0064) { var r = ReadRect(b); int sa = (short)ReadU16(b), aa = (short)ReadU16(b); port.Arc(r, sa, aa, op - 0x0060); return true; }
            if (op >= 0x0068 && op <= 0x006C) { int sa = (short)ReadU16(b), aa = (short)ReadU16(b); port.SameArc(sa, aa, op - 0x0068); return true; }
            if (op >= 0x0070 && op <= 0x0074) { port.LastPoly = ReadPolyPoints(b, port); port.Polygon(port.LastPoly, op - 0x0070); return true; }
            if (op >= 0x0078 && op <= 0x007C) { if (port.LastPoly != null) port.Polygon(port.LastPoly, op - 0x0078); return true; }
            if (op >= 0x0080 && op <= 0x0084) { port.LastRegion = ReadRegionBBox(b); port.RegionRect(port.LastRegion, op - 0x0080); return true; }
            if (op >= 0x0088 && op <= 0x008C) { port.RegionRect(port.LastRegion, op - 0x0088); return true; }

            return false;
        }

        // QuickDraw Point is (v, h) - vertical first.
        private static (int v, int h) ReadPoint(BinaryReader b)
        {
            int v = ReadI16(b);
            int h = ReadI16(b);
            return (v, h);
        }

        // RGBColor: three 16-bit channels (use the high byte).
        private static PictColor ReadRgb(BinaryReader b)
        {
            int r = ReadU16(b), g = ReadU16(b), bl = ReadU16(b);
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

        // A Region/Polygon header: u16 size (incl. itself) + bounding Rect; skip any run data.
        private static (int top, int left, int bottom, int right) ReadRegionBBox(BinaryReader b)
        {
            int size = ReadU16(b);
            var bbox = ReadRect(b);
            int consumed = 2 + 8;
            if (size > consumed) b.BaseStream.Seek(size - consumed, SeekOrigin.Current);
            return bbox;
        }

        // A Polygon: u16 size + bounding Rect + Point[] ((size-10)/4 points). Returns canvas-space points.
        private static PictPoint[] ReadPolyPoints(BinaryReader b, GrafPort port)
        {
            int size = ReadU16(b);
            ReadRect(b);                         // bbox (unused)
            int count = Math.Max(0, (size - 10) / 4);
            var pts = new PictPoint[count];
            for (int i = 0; i < count; i++)
            {
                var p = ReadPoint(b);
                pts[i] = port.P(p.h, p.v);
            }
            return pts;
        }

        // Copy the source_rect region of a decoded bitmap to its dest_rect on the frame canvas.
        private static void Blit(Bits bits, PictBitmap canvas, int frameTop, int frameLeft)
        {
            int w = bits.Src.right - bits.Src.left;
            int h = bits.Src.bottom - bits.Src.top;
            for (int y = 0; y < h; y++)
            {
                int sy = (bits.Src.top - bits.Bounds.top) + y;
                int dy = (bits.Dst.top - frameTop) + y;
                if (sy < 0 || sy >= bits.Image.Height || dy < 0 || dy >= canvas.Height) continue;
                for (int x = 0; x < w; x++)
                {
                    int sx = (bits.Src.left - bits.Bounds.left) + x;
                    int dx = (bits.Dst.left - frameLeft) + x;
                    if (sx < 0 || sx >= bits.Image.Width || dx < 0 || dx >= canvas.Width) continue;
                    canvas[dx, dy] = bits.Image[sx, sy];
                }
            }
        }

        // BitsRect (0x90) / PackBitsRect (0x98): indexed-color pixmap or monochrome bitmap.
        // The Rgn variants (0x91/0x99) add a mask region after the transfer mode (hasRegion).
        private static Bits DecodeBitsRect(BinaryReader b, bool packed, bool hasRegion)
        {
            int rawRowBytes = ReadU16(b);
            bool isPixMap = (rawRowBytes & 0x8000) != 0;
            int rowBytes = rawRowBytes & 0x7FFF;
            var bounds = ReadRect(b);
            int width = bounds.right - bounds.left;
            int height = bounds.bottom - bounds.top;

            int pixelSize = 1;
            var palette = new Dictionary<int, PictColor>();
            if (isPixMap)
            {
                // remainder of PixelMapHeader (we already consumed flags_row_bytes + bounds = 10 bytes)
                ReadU16(b);             // version
                ReadU16(b);             // pack_format
                ReadU32(b);             // pack_size
                ReadU32(b);             // h_res
                ReadU32(b);             // v_res
                ReadU16(b);             // pixel_type
                pixelSize = ReadU16(b);
                ReadU16(b);             // component_count
                ReadU16(b);             // component_size
                ReadU32(b);             // plane_offset
                ReadU32(b);             // color_table_offset
                ReadU32(b);             // reserved

                ReadU32(b);             // ctSeed
                int ctFlags = ReadU16(b);
                int ctSize = ReadU16(b);
                // A device color table (flags & 0x8000) ignores color_num and is
                // indexed positionally; otherwise color_num is the pixel index.
                bool positional = (ctFlags & 0x8000) != 0;
                for (int i = 0; i <= ctSize; i++)
                {
                    int value = ReadU16(b);
                    int r = ReadU16(b), g = ReadU16(b), bl = ReadU16(b);
                    palette[positional ? i : value] = new PictColor((byte)(r >> 8), (byte)(g >> 8), (byte)(bl >> 8), 255);
                }
            }
            else
            {
                palette[0] = new PictColor(255, 255, 255, 255);
                palette[1] = new PictColor(0, 0, 0, 255);
            }

            var srcRect = ReadRect(b);
            var dstRect = ReadRect(b);
            ReadU16(b);   // transfer mode
            if (hasRegion) SkipRegion(b);

            var img = new PictBitmap(width, height);
            byte[] row = new byte[rowBytes];
            bool sizesAreWords = rowBytes > 250;
            for (int y = 0; y < height; y++)
            {
                if (packed && rowBytes >= 8)
                    UnpackRow(b, row, sizesAreWords, chunksAreWords: false);
                else
                    b.Read(row, 0, rowBytes);

                for (int x = 0; x < width; x++)
                {
                    int index = ReadBits(row, x, pixelSize);
                    img[x, y] = palette.TryGetValue(index, out var c) ? c : new PictColor(0, 0, 0, 255);
                }
            }
            return new Bits(img, bounds, srcRect, dstRect);
        }

        // DirectBitsRect (0x9A): truecolor - planar 8-bit RGB/ARGB or packed xrgb1555.
        // DirectBitsRgn (0x9B) adds a mask region after the transfer mode (hasRegion).
        private static Bits DecodeDirectBits(BinaryReader b, bool hasRegion)
        {
            ReadU32(b);                 // base_address (unused)
            int rawRowBytes = ReadU16(b);
            var bounds = ReadRect(b);
            ReadU16(b);                 // version
            int packType = ReadU16(b);  // 0 default, 1 unpacked, 2 drop-pad, 3 RLE-16, 4 RLE-component
            ReadU32(b);                 // pack_size
            ReadU32(b);                 // h_res
            ReadU32(b);                 // v_res
            ReadU16(b);                 // pixel_type
            int pixelSize = ReadU16(b);
            int componentCount = ReadU16(b);
            int componentSize = ReadU16(b);
            ReadU32(b);                 // plane_offset
            ReadU32(b);                 // color_table_offset
            ReadU32(b);                 // reserved

            var srcRect = ReadRect(b);
            var dstRect = ReadRect(b);
            ReadU16(b);                 // mode
            if (hasRegion) SkipRegion(b);

            int width = bounds.right - bounds.left;
            int height = bounds.bottom - bounds.top;

            int bytesPerPixel;
            if (componentSize == 8)
            {
                if (componentCount != 3 && componentCount != 4)
                    throw new NotSupportedException($"DirectBitsRect 8-bit channels need 3 or 4 components (got {componentCount})");
                bytesPerPixel = componentCount;
            }
            else if (componentSize == 5)
            {
                if (componentCount != 3)
                    throw new NotSupportedException("DirectBitsRect 5-bit channels need 3 components");
                bytesPerPixel = 2; // xrgb1555
            }
            else
            {
                throw new NotSupportedException($"DirectBitsRect component size {componentSize} not supported");
            }

            int rowBytes = width * bytesPerPixel;
            // packType: 1 = unpacked, 2 = drop-pad (unpacked), 3 = RLE-16, 4 = RLE-component.
            // 0 = default (depends on depth): fall back to the row-length heuristic.
            // Honoring explicit 1/2 fixes truecolor PICTs that store rows raw.
            bool packed = packType == 1 || packType == 2 ? false
                        : packType >= 3 ? true
                        : (rawRowBytes & 0x8000) != 0 ? (rawRowBytes & 0x7FFF) >= 8 : rowBytes >= 8;
            bool sizesAreWords = rowBytes > 250;
            bool chunksAreWords = pixelSize == 0x10;

            var img = new PictBitmap(width, height);
            byte[] row = new byte[rowBytes];
            for (int y = 0; y < height; y++)
            {
                if (packed)
                    UnpackRow(b, row, sizesAreWords, chunksAreWords);
                else
                    b.Read(row, 0, rowBytes);

                for (int x = 0; x < width; x++)
                {
                    PictColor color;
                    if (componentSize == 8 && componentCount == 3)
                    {
                        int plane = rowBytes / 3;
                        color = new PictColor(row[x], row[plane + x], row[2 * plane + x], 255);
                    }
                    else if (componentSize == 8 && componentCount == 4)
                    {
                        int plane = rowBytes / 4; // first plane (alpha/pad) ignored
                        color = new PictColor(row[plane + x], row[2 * plane + x], row[3 * plane + x], 255);
                    }
                    else // xrgb1555
                    {
                        int p = (row[2 * x] << 8) | row[2 * x + 1];
                        int r5 = (p >> 10) & 0x1F, g5 = (p >> 5) & 0x1F, b5 = p & 0x1F;
                        color = new PictColor((byte)((r5 << 3) | (r5 >> 2)), (byte)((g5 << 3) | (g5 >> 2)), (byte)((b5 << 3) | (b5 >> 2)), 255);
                    }
                    img[x, y] = color;
                }
            }
            return new Bits(img, bounds, srcRect, dstRect);
        }

        // A QuickDraw Region or Polygon: u16 total size (including itself) + bounding Rect +
        // optional run data. We only need to skip past it.
        private static void SkipRegion(BinaryReader b)
        {
            int size = ReadU16(b);
            if (size >= 2)
                b.BaseStream.Seek(size - 2, SeekOrigin.Current);
        }

        private static void Skip(BinaryReader b, int n) => b.BaseStream.Seek(n, SeekOrigin.Current);

        // var16/var32: a u16/u32 byte-length prefix followed by that many data bytes.
        private static void SkipVar16(BinaryReader b) => Skip(b, ReadU16(b));
        private static void SkipVar32(BinaryReader b) => Skip(b, (int)ReadU32(b));

        // Text opcodes: positioning bytes, then a u8 char count, then that many chars.
        private static void SkipText(BinaryReader b, int positionBytes)
        {
            Skip(b, positionBytes);
            int count = b.ReadByte();
            Skip(b, count);
        }

        // Consume the operands of any opcode we don't handle so the opcode loop stays aligned and
        // can still reach the picture's bitmap. Operand sizes per the QuickDraw opcode table
        // (resource_dasm QuickDrawEngine / PictFormat.md). Truly unsupported opcodes (QuickTime,
        // pixel patterns, indeterminate reserved) throw so we never silently desync.
        private static void SkipOperands(BinaryReader b, int op)
        {
            switch (op)
            {
                case 0x0000: return;                                    // NOP
                case 0x0001: SkipRegion(b); return;                    // clip region
                case 0x0002: case 0x0009: case 0x000A: case 0x0010:    // BkPat/PnPat/FillPat/TxRatio
                    Skip(b, 8); return;
                case 0x0003: case 0x0005: case 0x0008: case 0x000D:    // TxFont/TxMode/PnMode/TxSize
                case 0x0015: case 0x0016: case 0x0023:                 // PnLocHFrac/ChExtra/ShortLineFrom
                    Skip(b, 2); return;
                case 0x0004: Skip(b, 1); return;                       // TxFace
                case 0x0006: case 0x0007: case 0x000B: case 0x000C:    // SpExtra/PnSize/OvSize/Origin
                case 0x000E: case 0x000F: case 0x0021:                 // Fg/BkColor/LineFrom
                    Skip(b, 4); return;
                case 0x001A: case 0x001B: case 0x001D: case 0x001F:    // RGB Fg/Bk/Hilite/OpColor
                    Skip(b, 6); return;
                case 0x001C: case 0x001E: return;                      // HiliteMode/DefHilite
                case 0x0020: Skip(b, 8); return;                       // Line
                case 0x0022: Skip(b, 6); return;                       // ShortLine
                case 0x0028: SkipText(b, 4); return;                   // LongText
                case 0x0029: case 0x002A: SkipText(b, 1); return;      // DH/DV Text
                case 0x002B: SkipText(b, 2); return;                   // DHDV Text
                case 0x00A0: Skip(b, 2); return;                       // short comment (kind)
                case 0x00A1: Skip(b, 2); SkipVar16(b); return;         // long comment (kind + var data)
            }

            if (op >= 0x0024 && op <= 0x0027) { SkipVar16(b); return; }     // reserved text
            if (op >= 0x002C && op <= 0x002F) { SkipVar16(b); return; }     // font name / justify / glyph
            if (op >= 0x0030 && op <= 0x0037) { Skip(b, 8); return; }       // rect (with data)
            if (op >= 0x0040 && op <= 0x0047) { Skip(b, 8); return; }       // round rect
            if (op >= 0x0050 && op <= 0x0057) { Skip(b, 8); return; }       // oval
            if (op >= 0x0060 && op <= 0x0067) { Skip(b, 12); return; }      // arc (rect + angles)
            if (op >= 0x0068 && op <= 0x006F) { Skip(b, 4); return; }       // same arc (angles)
            if (op >= 0x0038 && op <= 0x005F) return;                       // same rect/rrect/oval (no data)
            if (op >= 0x0070 && op <= 0x0087)                               // poly / region
            {
                if (op <= 0x0077 || (op >= 0x0080 && op <= 0x0087)) SkipRegion(b);
                return;                                                     // same poly/region (0x78-0x7F/0x88-0x8F) = no data
            }
            if (op >= 0x0088 && op <= 0x008F) return;                       // same region (no data)
            if (op >= 0x0092 && op <= 0x0097) { SkipVar16(b); return; }     // reserved
            if (op >= 0x009C && op <= 0x009F) { SkipVar16(b); return; }     // reserved
            if (op >= 0x00A2 && op <= 0x00AF) { SkipVar16(b); return; }     // reserved
            if (op >= 0x00B0 && op <= 0x00CF) return;                       // reserved (no data)
            if (op >= 0x00D0 && op <= 0x00FE) { SkipVar32(b); return; }     // reserved (u32 data)

            // v2 long opcodes. For 0x0100-0x7FFF the data size is 2 x (high byte) bytes (Apple
            // QuickDraw / TI PICT spec); this covers the 0x0C00 header (24) and 0x7Fxx (254) too.
            if (op == 0x02FF) return;                                       // version (consumed in header)
            if (op >= 0x0100 && op <= 0x7FFF) { Skip(b, 2 * (op >> 8)); return; }
            if (op >= 0x8000 && op <= 0x80FF) return;
            if (op >= 0x8100 && op <= 0x81FF) { SkipVar32(b); return; }
            if (op == 0x8200 || op == 0x8201)
                throw new NotSupportedException("QuickTime-compressed PICT not supported");

            throw new NotSupportedException($"Unsupported PICT opcode 0x{op:X4}");
        }

        // PackBits (RLE) decompression of one row. count<0 => repeat (1-count); count>=0 => copy (count+1).
        private static void UnpackRow(BinaryReader b, byte[] outRow, bool sizesAreWords, bool chunksAreWords)
        {
            int packedBytes = sizesAreWords ? ReadU16(b) : b.ReadByte();
            long end = b.BaseStream.Position + packedBytes;
            int pos = 0;
            while (b.BaseStream.Position < end && pos < outRow.Length)
            {
                sbyte count = (sbyte)b.ReadByte();
                if (count == -128)
                    continue;               // no-op (Apple PackBits)
                if (count < 0)
                {
                    int n = 1 - count;
                    if (chunksAreWords)
                    {
                        byte hi = b.ReadByte(), lo = b.ReadByte();
                        for (int i = 0; i < n && pos + 1 < outRow.Length; i++) { outRow[pos++] = hi; outRow[pos++] = lo; }
                    }
                    else
                    {
                        byte v = b.ReadByte();
                        for (int i = 0; i < n && pos < outRow.Length; i++) outRow[pos++] = v;
                    }
                }
                else
                {
                    int n = (count + 1) * (chunksAreWords ? 2 : 1);
                    for (int i = 0; i < n && pos < outRow.Length; i++) outRow[pos++] = b.ReadByte();
                }
            }
        }

        private static (int top, int left, int bottom, int right) ReadRect(BinaryReader b)
        {
            int top = ReadI16(b);
            int left = ReadI16(b);
            int bottom = ReadI16(b);
            int right = ReadI16(b);
            return (top, left, bottom, right);
        }

        // Read `bpp` bits for pixel x in a row, MSB-first.
        private static int ReadBits(byte[] buf, int x, int bpp)
        {
            int bitPos = x * bpp;
            int value = 0;
            for (int i = 0; i < bpp; i++)
            {
                int bit = bitPos + i;
                int b = (buf[bit >> 3] >> (7 - (bit & 7))) & 1;
                value = (value << 1) | b;
            }
            return value;
        }

        // PICT data is big-endian; BinaryReader always reads little-endian.
        private static short ReadI16(BinaryReader b) => BinaryPrimitives.ReverseEndianness(b.ReadInt16());
        private static ushort ReadU16(BinaryReader b) => BinaryPrimitives.ReverseEndianness(b.ReadUInt16());
        private static uint ReadU32(BinaryReader b) => BinaryPrimitives.ReverseEndianness(b.ReadUInt32());
    }
}
