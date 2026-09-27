using System;
using System.Collections.Generic;

namespace QuickDraw.Pict
{
    // The QuickDraw drawing state of a picture being played back, and its drawing verbs. As DrawPicture does, the
    // play state keeps picture-space coordinates (pen and text locations, the "same shape" rect/poly/region, the clip)
    // and maps them to the canvas when drawing: from fromRect (the picture's frame, moved by the Origin opcode) to
    // toRect (the canvas), scaling when the two differ in size. Shapes are rasterized as regions (RegionShapes) and
    // painted through a pattern and transfer mode (Painter); bitmaps go through CopyBits. verb: 0 frame, 1 paint,
    // 2 erase, 3 invert, 4 fill.
    internal sealed class GrafPort
    {
        private readonly PictBitmap canvas;
        private readonly PictDecodeOptions options;
        private PictRect fromRect;
        private readonly PictRect toRect;
        private int patAlignH, patAlignV;

        // Port state as DrawPicture initializes it: black on white, pen 1x1 patCopy with a black pen and fill pattern
        // and a white background pattern, text mode srcOr, OpColor black.
        public PictColor ForeColor = new PictColor(0, 0, 0);
        public PictColor BackColor = new PictColor(255, 255, 255);
        public (ushort r, ushort g, ushort b) Fore16, Back16 = (0xFFFF, 0xFFFF, 0xFFFF);   // their exact components
        public (ushort r, ushort g, ushort b) OpColor;
        public PictColor HiliteColor;
        private bool hilitePending;

        public Pattern BkPat = Pattern.White;
        public Pattern PnPat = Pattern.Black;
        public Pattern FillPat = Pattern.Black;
        public int PenMode = TransferModes.PatCopy;
        private int penWidth = 1, penHeight = 1;                  // canvas pixels (scaled when set)
        private int ovalWidth, ovalHeight;                        // canvas pixels (scaled when set)
        private int penH, penV;                                   // picture space

        private Region? pictureClip;                              // picture space; null = no clip
        private Region? clip;                                     // canvas space
        private PictRect lastRect;                                // picture space, for the "same shape" opcodes
        private (int h, int v)[]? lastPoly;
        private Region lastRegion = Region.Empty;

        public int TextFace, TextSize, TextMode = TransferModes.SrcOr;
        public int SpaceExtra;                                    // Fixed
        public int ChExtra;                                       // the color port's chExtra (4.12 per point)
        public bool Version1;                                     // the picture's opcodes are version 1
        private int textFontId, pictureFontId;                    // the port's txFont, and the picture's number for it
        private int interCharSpacing;                             // LineJustify (Fixed per point)
        private bool fractEnable, fScaleDisable;                  // glyphState: fractional widths, scaling disabled
        private int penFrac = 0x8000, pendingFrac = 0x8000;       // the pen's h fraction; PnLocHFrac for the next text
        private int textH, textV;                                 // text origin, picture space
        private (int h, int v) textNumer, textDenom;              // text scaling, as DrawPicture's play state
        private readonly Dictionary<int, string> fontNames = new Dictionary<int, string>();
        private readonly Dictionary<int, int> fontMap = new Dictionary<int, int>();

        public GrafPort(PictBitmap canvas, PictRect pictureFrame, PictDecodeOptions options)
        {
            this.canvas = canvas;
            this.options = options;
            fromRect = pictureFrame;
            toRect = new PictRect(0, 0, canvas.Height, canvas.Width);
            macOS9 = options.QuickDraw == PictQuickDraw.MacOS9;
            device = ScreenDevice.For(options.ScreenDepth, macOS9);
            HiliteColor = SystemHilite;
            textNumer = (toRect.Width, toRect.Height);
            textDenom = (fromRect.Width, fromRect.Height);
        }

        private PortColors Colors => new PortColors(ForeColor, BackColor, OpColor, HiliteColor, macOS9, device, Fore16, Back16);
        private readonly bool macOS9;
        private readonly ScreenDevice? device;

        // DrawPicture starts the pattern alignment at (0, 0) and the Origin opcode adds its dh, dv to it.
        private (int h, int v) PatternAlign => (patAlignH, patAlignV);

        // ---- coordinate mapping ----

        private (int h, int v) MapPoint(int h, int v) => PictureMapping.MapPoint(h, v, fromRect, toRect);
        private PictRect MapRect(PictRect r) => PictureMapping.MapRect(r, fromRect, toRect);
        private Region MapRegion(Region r) => PictureMapping.MapRegion(r, fromRect, toRect);

        // Origin opcode: moves the picture frame by (dh, dv) (so later coordinates land dh, dv further up-left),
        // shifts the pattern alignment by the same amount (ROM only; Mac OS 9 leaves it), and re-maps the clip.
        public void Origin(int dh, int dv)
        {
            fromRect = new PictRect(fromRect.Top + dv, fromRect.Left + dh, fromRect.Bottom + dv, fromRect.Right + dh);
            if (!macOS9)
            {
                patAlignH += dh;
                patAlignV += dv;
            }
            if (pictureClip != null) clip = MapRegion(pictureClip);
        }

        // ---- state ----

        public void SetClip(Region pictureRegion)
        {
            pictureClip = pictureRegion;
            clip = MapRegion(pictureRegion);
        }

        public void PenSize(int h, int v) => (penWidth, penHeight) = PictureMapping.ScaleSize(h, v, fromRect, toRect);
        public void OvalSize(int h, int v) => (ovalWidth, ovalHeight) = PictureMapping.ScaleSize(h, v, fromRect, toRect);
        public void HiliteMode() => hilitePending = true;

        // TxRatio: numer x the destination size over denom x the picture frame size, per axis, both halved together
        // until they fit in 15 bits.
        public void TextRatio(int numerH, int numerV, int denomH, int denomV)
        {
            var (nh, dh) = Reduce(numerH, toRect.Width, denomH, fromRect.Width);
            var (nv, dv) = Reduce(numerV, toRect.Height, denomV, fromRect.Height);
            textNumer = (nh, nv);
            textDenom = (dh, dv);
        }

        private static (int n, int d) Reduce(int numer, int toSize, int denom, int fromSize)
        {
            uint n = (uint)(ushort)numer * (ushort)toSize, d = (uint)(ushort)denom * (ushort)fromSize;
            while (((n | d) & 0xFFFF8000) != 0)
            {
                n >>= 1;
                d >>= 1;
            }
            return ((int)n, (int)d);
        }

        public void DefaultHilite() => HiliteColor = SystemHilite;

        private PictColor SystemHilite =>
            options.HiliteColor ?? (macOS9 ? new PictColor(0xCC, 0xCC, 0xFF) : new PictColor(0x99, 0xCC, 0xCC));

        // TxFont goes through the font map the fontName opcodes build.
        public void TextFont(int fontId)
        {
            pictureFontId = fontId;
            textFontId = fontMap.TryGetValue(fontId, out int mapped) ? mapped : fontId;
        }

        // fontName: when the library has a family of that name (GetFNum, which never maps to 0) numbered differently,
        // later TxFont opcodes for the picture's number select it. The name also goes to the outline fallback.
        public void FontName(int fontId, string name)
        {
            fontNames[fontId] = name;
            if (options.Fonts is { } library && library.TryGetFamilyByName(name, out int family) && family != 0
                && family != fontId)
                fontMap.TryAdd(fontId, family);
        }

        public void PnLocHFrac(int fraction) => pendingFrac = fraction & 0xFFFF;
        // (Mac OS 9 skips LineJustify: its character extra stays 0.)
        public void LineJustify(int interCharacterSpacing)
        {
            if (!macOS9) interCharSpacing = interCharacterSpacing;
        }
        public void GlyphState(bool fractionalWidths, bool scalingDisabled) =>
            (fractEnable, fScaleDisable) = (fractionalWidths, scalingDisabled);

        // ---- shapes ----

        public void Rect(PictRect? pictureRect, int verb)
        {
            if (pictureRect is { } pr) lastRect = pr;
            var r = MapRect(lastRect);
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.Rect(r), () => new[] { RegionShapes.FrameRect(r, penWidth, penHeight) }, true);
        }

        public void RoundRect(PictRect? pictureRect, int verb)
        {
            if (pictureRect is { } pr) lastRect = pr;
            var r = MapRect(lastRect);
            if (r.IsEmpty) { Done(); return; }
            int ow = ovalWidth, oh = ovalHeight;
            if (macOS9)
            {
                // Mac OS 9 clamps the corner to the rect; a zero corner is a plain rect.
                ow = Math.Clamp(ow, 0, r.Width);
                oh = Math.Clamp(oh, 0, r.Height);
                if (ow == 0 || oh == 0)
                {
                    Shape(verb, () => RegionShapes.Rect(r), () => new[] { RegionShapes.FrameRect(r, penWidth, penHeight) }, false);
                    return;
                }
            }
            Shape(verb, () => RegionShapes.RoundRect(r, ow, oh),
                () => RegionShapes.FrameRoundRectParts(r, ow, oh, penWidth, penHeight, macOS9), false);
        }

        public void Oval(PictRect? pictureRect, int verb)
        {
            if (pictureRect is { } pr) lastRect = pr;
            var r = MapRect(lastRect);
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.Oval(r), () => RegionShapes.FrameOvalParts(r, penWidth, penHeight, macOS9), false);
        }

        public void Arc(PictRect? pictureRect, int startAngle, int arcAngle, int verb)
        {
            if (pictureRect is { } pr) lastRect = pr;
            var r = MapRect(lastRect);
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.Arc(r, startAngle, arcAngle, macOS9),
                () => RegionShapes.FrameArcParts(r, startAngle, arcAngle, penWidth, penHeight, macOS9), false);
        }

        // Polygons (picture-space points). Framing draws each edge as a line and does not close the polygon.
        public void Polygon((int h, int v)[]? picturePoints, int verb)
        {
            if (picturePoints != null) lastPoly = picturePoints;
            if (lastPoly == null || lastPoly.Length < 2) { Done(); return; }
            var pts = new (int h, int v)[lastPoly.Length];
            for (int i = 0; i < pts.Length; i++) pts[i] = MapPoint(lastPoly[i].h, lastPoly[i].v);
            if (verb == 0)
            {
                for (int i = 1; i < pts.Length; i++)
                    PaintLine(pts[i - 1].h, pts[i - 1].v, pts[i].h, pts[i].v);
                Done();
                return;
            }
            Shape(verb, () => RegionShapes.Polygon(pts), () => new[] { Region.Empty }, true);
        }

        public void Rgn(Region? pictureRegion, int verb)
        {
            if (pictureRegion != null) lastRegion = pictureRegion;
            var rgn = MapRegion(lastRegion);
            Shape(verb, () => rgn, () => new[] { RegionShapes.FrameRegion(rgn, penWidth, penHeight) }, true);
        }

        // StdRgn: frame paints the frame with the pen, paint uses the pen pattern and mode, erase the background
        // pattern (patCopy), invert XORs with black (hilite when pending), fill the fill pattern (patCopy).
        // viaStretchBits: rects, regions and polygons (not ovals, round rects and arcs, which DrawArc draws itself).
        private void Shape(int verb, Func<Region> interior, Func<Region[]> frame, bool viaStretchBits)
        {
            var colors = Colors;
            // DrawArc (ovals, round rects, arcs) takes the pen mode with bit 3 forced and draws only pattern modes
            // 8-15, arithmetic modes 40-47 and hilite 58; any other mode (16-31, 49, 64 and up, ...) draws nothing
            // (ROM $FFC93E8C; Mac OS 9 the same after dropping bit 6).
            if (!viaStretchBits && verb <= 1)
            {
                int m = (macOS9 ? PenMode & ~TransferModes.DitherCopy : PenMode) | 8;
                if (!(m <= 15 || (m >= 40 && m <= 47) || m == 58)) { Done(); return; }
            }
            switch (verb)
            {
                case 0:
                    // (Mac OS 9 paints a crossed frame's two slabs one after the other.)
                    foreach (var part in frame())
                        Painter.FillRegion(canvas, part, clip, PnPat, PatternAlign, PenMode, hilitePending, colors, viaStretchBits);
                    break;
                case 1: Painter.FillRegion(canvas, interior(), clip, PnPat, PatternAlign, PenMode, hilitePending, colors, viaStretchBits); break;
                case 2: Painter.FillRegion(canvas, interior(), clip, BkPat, PatternAlign, TransferModes.PatCopy, false, colors, viaStretchBits); break;
                case 3: Painter.FillRegion(canvas, interior(), clip, Pattern.Black, PatternAlign, TransferModes.PatXor, hilitePending, colors, viaStretchBits); break;
                case 4: Painter.FillRegion(canvas, interior(), clip, FillPat, PatternAlign, TransferModes.PatCopy, false, colors, viaStretchBits); break;
            }
            Done();
        }

        // Color QuickDraw resets the highlight bit after every drawing operation.
        private void Done() => hilitePending = false;

        // ---- lines ----

        public void Line(int h1, int v1, int h2, int v2)
        {
            penH = h1;
            penV = v1;
            LineTo(h2, v2);
        }

        public void LineTo(int h, int v)
        {
            var (x1, y1) = MapPoint(penH, penV);
            var (x2, y2) = MapPoint(h, v);
            PaintLine(x1, y1, x2, y2);
            penH = h;
            penV = v;
            Done();
        }

        public void LineBy(int dh, int dv) => LineTo(penH + dh, penV + dv);

        // StdLine paints the pen-swept region with the pen pattern; Boolean pen modes act as pattern modes.
        private void PaintLine(int x1, int y1, int x2, int y2)
        {
            var region = RegionShapes.Line(x1, y1, x2, y2, penWidth, penHeight);
            int mode = PenMode < TransferModes.Blend ? (PenMode % 0x40) | 8 : PenMode;
            Painter.FillRegion(canvas, region, clip, PnPat, PatternAlign, mode, hilitePending, Colors, x1 == x2 || y1 == y2);
        }

        // ---- bitmaps ----

        // BitsRect / BitsRgn / PackBitsRect / PackBitsRgn / DirectBitsRect / DirectBitsRgn: the destination rect and
        // mask region are mapped like any other picture coordinates.
        public void CopyBits(PixMap source, PictRect srcRect, PictRect pictureDstRect, int mode, Region? pictureMask)
        {
            var mask = pictureMask == null ? null : MapRegion(pictureMask);
            if (clip != null) mask = mask == null ? clip : mask.Intersect(clip);
            Bits.CopyBits(canvas, source, srcRect, MapRect(pictureDstRect), mode, mask, hilitePending, Colors,
                options.PreserveAlpha);
            Done();
        }

        // ---- QuickTime ----

        // CompressedQuickTime: decompress (built-in codecs, then the caller's) and draw the image where its matrix
        // puts the source rect, through its transfer mode and mask. Returns that destination (picture space), or null
        // when the image could not be decoded.
        public PictRect? QuickTime(byte[] block)
        {
            var q = QuickTimeImage.Parse(block);
            if (q == null) return null;
            var image = QuickTimeCodecs.Decode(q.Description, q.Data) ?? options.ImageCodec?.Decode(q.Description, q.Data);
            if (image == null) return null;
            var mask = q.Mask == null ? null : MapRegion(q.Mask);
            if (clip != null) mask = mask == null ? clip : mask.Intersect(clip);
            var source = q.SourceRect.IsEmpty ? new PictRect(0, 0, image.Height, image.Width) : q.SourceRect;
            var destination = q.DestinationRect();
            Bits.CopyBits(canvas, QuickTimeImage.ToPixMap(image), source, MapRect(destination), q.Mode, mask,
                hilitePending, Colors, options.PreserveAlpha);
            Done();
            return destination;
        }

        // UncompressedQuickTime: the embedded bitmap opcode's image drawn to its dstRect, or, when the matrix is not
        // the identity, to where the matrix puts its srcRect (bounding box; the same placement as a compressed image).
        public PictRect UncompressedQuickTime(PixMap source, PictRect srcRect, PictRect dstRect, int mode, Region? pictureMask,
            int[] matrix)
        {
            bool identity = matrix[0] == 0x10000 && matrix[1] == 0 && matrix[3] == 0 && matrix[4] == 0x10000 &&
                matrix[6] == 0 && matrix[7] == 0;
            var destination = identity ? dstRect : QuickTimeImage.Place(matrix, srcRect);
            CopyBits(source, srcRect, destination, mode, pictureMask);
            return destination;
        }

        // ---- text ----

        // LongText sets the text origin; DH/DV/DHDV text move it from the previous origin. Drawing does not move it.
        // The pen goes to the mapped origin: in a version 2 picture through MapFixPt, from (v + 1/2, h + the pending
        // PnLocHFrac fraction), keeping h's fraction as the pen's; in a version 1 picture through MapPt, the pen
        // keeping the fraction the last text left.
        public void LongText(int h, int v, byte[] text)
        {
            textH = h;
            textV = v;
            DrawText(text);
        }

        public void OffsetText(int dh, int dv, byte[] text)
        {
            textH += dh;
            textV += dv;
            DrawText(text);
        }

        // StdText: bitmap fonts from the font library when it has the family (or a stand-in the Font Manager would
        // use), else the outline text fallback. A fontName opcode maps the picture's font number to a family by name.
        private void DrawText(byte[] text)
        {
            int fraction = pendingFrac;
            pendingFrac = 0x8000;
            int x, y;
            if (Version1) (x, y) = MapPoint(textH, textV);
            else
            {
                int fh = MapFixed(unchecked((textH << 16) | fraction), fromRect.Left, fromRect.Right, toRect.Left, toRect.Right, macOS9);
                int fv = MapFixed(unchecked((textV << 16) | 0x8000), fromRect.Top, fromRect.Bottom, toRect.Top, toRect.Bottom, macOS9);
                (x, y, penFrac) = (fh >> 16, fv >> 16, fh & 0xFFFF);
            }
            if (text.Length == 0) { Done(); return; }

            int mode = TextMode;
            // Mac OS 9 draws transparent and ditherCopy text as srcOr (grayishTextOr stays the gray srcOr below).
            if (macOS9 && (mode == TransferModes.Transparent || mode == TransferModes.DitherCopy))
                mode = TransferModes.SrcOr;
            var colors = Colors;
            fontNames.TryGetValue(pictureFontId, out var name);
            if (options.Fonts is { } library)
            {
                var font = FontManager.Swap(library, textFontId, TextSize, TextFace, textNumer, textDenom, SpaceExtra, fractEnable,
                    fScaleDisable, macOS9);
                if (font != null)
                {
                    int charExtra = unchecked(((short)ChExtra << 4) + interCharSpacing);
                    if (mode == TransferModes.GrayishTextOr)
                        GrayishText(font, text, x, y, charExtra);
                    else
                        penFrac = TextDrawer.Draw(canvas, font, text, x, y, penFrac, charExtra, mode, clip, hilitePending, colors);
                    Done();
                    return;
                }
            }
            var fallback = options.TextFallback;
            if (fallback == null) { Done(); return; }
            var mask = fallback.Render(PictReader.MacRomanString(text), new PictTextStyle(pictureFontId, TextFace, TextSize, name));
            if (mask != null && mask.Width > 0 && mask.Height > 0)
                Painter.FillMask(canvas, x - mask.OriginX, y - mask.OriginY, mask.Width, mask.Height, mask.Bits,
                    clip, mode, hilitePending, colors);
            Done();
        }

        // MapFixPt on one axis: (c - from) x toSize / fromSize (a truncating 64/32-bit divide) + to, in Fixed; an
        // axis of equal sizes only moves. Mac OS 9 multiplies by the truncated ratio (toSize << 16) / fromSize instead,
        // rounding half up (so 3 -> 1 maps 3.0 to 0.99998).
        private static int MapFixed(int c, int fromLo, int fromHi, int toLo, int toHi, bool macOS9)
        {
            int fromSize = unchecked(((short)fromHi << 16) - ((short)fromLo << 16));
            int toSize = unchecked(((short)toHi << 16) - ((short)toLo << 16));
            int d = unchecked(c - ((short)fromLo << 16));
            if (fromSize != toSize && fromSize != 0)
            {
                if (macOS9)
                {
                    long ratio = ((long)(short)(toHi - toLo) << 16) / (short)(fromHi - fromLo);
                    d = FixedMath.FixMulHalfUp(d, (int)Math.Clamp(ratio, int.MinValue, int.MaxValue));
                }
                else d = unchecked((int)((long)d * toSize / fromSize));
            }
            return unchecked(d + ((short)toLo << 16));
        }

        // grayishTextOr on a color port (GetGray): the realized midpoint of the fore and back colors (16-bit
        // components averaged, + 2 below 0x8000) drawn srcOr when it is nearer the midpoint than half its distance to
        // either color; else the text srcOr with the gray pattern patBic over pen.h .. + the StdTxMeas width (integer,
        // unscaled), pen.v - ascent .. + descent - GetFontInfo's metrics: the strike's, + 1 / + shadow for shadowed
        // text, scaled by the Font Manager's stretch (rounded half up) but not by the text ratio.
        private void GrayishText(FontSelection font, byte[] text, int x, int y, int charExtra)
        {
            (int r, int g, int b) Wide(PictColor c) => (c.R * 257, c.G * 257, c.B * 257);
            var fg = Wide(ForeColor);
            var bk = Wide(BackColor);
            int Mid(int a, int b) { int m = (a + b) >> 1; return m < 0x8000 ? m + 2 : m; }
            (int r, int g, int b) mid = (Mid(fg.r, bk.r), Mid(fg.g, bk.g), Mid(fg.b, bk.b));
            var gray = new PictColor((byte)(mid.r >> 8), (byte)(mid.g >> 8), (byte)(mid.b >> 8));
            var grayWide = Wide(gray);
            int Distance((int r, int g, int b) a, (int r, int g, int b) b) =>
                Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
            if (Distance(grayWide, mid) < Distance(grayWide, bk) / 2 && Distance(grayWide, mid) < Distance(grayWide, fg) / 2)
            {
                penFrac = TextDrawer.Draw(canvas, font, text, x, y, penFrac, charExtra, TransferModes.SrcOr, clip,
                    hilitePending, new PortColors(gray, BackColor, OpColor, HiliteColor, macOS9, device));
                return;
            }
            int width = (short)(TextDrawer.Measure(font, text, charExtra) >> 16);
            int ascent = font.Ascent, descent = font.Descent;
            if (font.Shadow != 0) (ascent, descent) = (ascent + 1, descent + (byte)font.Shadow);
            if (font.Numer != font.Denom)
            {
                uint n = (ushort)font.Numer.v, d = (ushort)font.Denom.v;
                ascent = (int)(((uint)ascent * n + d / 2) / d);
                descent = (int)(((uint)descent * n + d / 2) / d);
            }
            penFrac = TextDrawer.Draw(canvas, font, text, x, y, penFrac, charExtra, TransferModes.SrcOr, clip,
                hilitePending, Colors);
            var box = new PictRect(y - ascent, x, y + descent, x + width);
            Painter.FillRegion(canvas, RegionShapes.Rect(box), clip, Gray, PatternAlign, TransferModes.PatBic, false, Colors, true);
        }

        private static readonly Pattern Gray = Pattern.FromMono(new byte[] { 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55 });
    }
}
