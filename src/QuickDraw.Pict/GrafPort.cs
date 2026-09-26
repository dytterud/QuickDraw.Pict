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

        public int TextFontId, TextFace, TextSize, TextMode = TransferModes.SrcOr;
        public int SpaceExtra;                                    // Fixed
        private int textH, textV;                                 // text origin, picture space
        private (int h, int v) textNumer, textDenom;              // text scaling, as DrawPicture's play state
        private readonly Dictionary<int, string> fontNames = new Dictionary<int, string>();

        public GrafPort(PictBitmap canvas, PictRect pictureFrame, PictDecodeOptions options)
        {
            this.canvas = canvas;
            this.options = options;
            fromRect = pictureFrame;
            toRect = new PictRect(0, 0, canvas.Height, canvas.Width);
            HiliteColor = options.HiliteColor;
            textNumer = (toRect.Width, toRect.Height);
            textDenom = (fromRect.Width, fromRect.Height);
        }

        private PortColors Colors => new PortColors(ForeColor, BackColor, OpColor, HiliteColor);

        // DrawPicture starts the pattern alignment at (0, 0) and the Origin opcode adds its dh, dv to it.
        private (int h, int v) PatternAlign => (patAlignH, patAlignV);

        // ---- coordinate mapping ----

        private (int h, int v) MapPoint(int h, int v) => PictureMapping.MapPoint(h, v, fromRect, toRect);
        private PictRect MapRect(PictRect r) => PictureMapping.MapRect(r, fromRect, toRect);
        private Region MapRegion(Region r) => PictureMapping.MapRegion(r, fromRect, toRect);

        // Origin opcode: moves the picture frame by (dh, dv) (so later coordinates land dh, dv further up-left),
        // shifts the pattern alignment by the same amount, and re-maps the clip.
        public void Origin(int dh, int dv)
        {
            fromRect = new PictRect(fromRect.Top + dv, fromRect.Left + dh, fromRect.Bottom + dv, fromRect.Right + dh);
            patAlignH += dh;
            patAlignV += dv;
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

        // TxRatio: the text scale numerator (scaled like a pen size) and denominator.
        public void TextRatio(int numerH, int numerV, int denomH, int denomV)
        {
            textNumer = PictureMapping.ScaleSize(numerH, numerV, fromRect, toRect);
            textDenom = (denomH, denomV);
        }
        public void DefaultHilite() => HiliteColor = options.HiliteColor;
        public void FontName(int fontId, string name) => fontNames[fontId] = name;

        // ---- shapes ----

        public void Rect(PictRect? pictureRect, int verb)
        {
            if (pictureRect is { } pr) lastRect = pr;
            var r = MapRect(lastRect);
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.Rect(r), () => RegionShapes.FrameRect(r, penWidth, penHeight));
        }

        public void RoundRect(PictRect? pictureRect, int verb)
        {
            if (pictureRect is { } pr) lastRect = pr;
            var r = MapRect(lastRect);
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.RoundRect(r, ovalWidth, ovalHeight),
                () => RegionShapes.FrameRoundRect(r, ovalWidth, ovalHeight, penWidth, penHeight));
        }

        public void Oval(PictRect? pictureRect, int verb)
        {
            if (pictureRect is { } pr) lastRect = pr;
            var r = MapRect(lastRect);
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.Oval(r), () => RegionShapes.FrameOval(r, penWidth, penHeight));
        }

        public void Arc(PictRect? pictureRect, int startAngle, int arcAngle, int verb)
        {
            if (pictureRect is { } pr) lastRect = pr;
            var r = MapRect(lastRect);
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.Arc(r, startAngle, arcAngle),
                () => RegionShapes.FrameArc(r, startAngle, arcAngle, penWidth, penHeight));
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
            Shape(verb, () => RegionShapes.Polygon(pts), () => Region.Empty);
        }

        public void Rgn(Region? pictureRegion, int verb)
        {
            if (pictureRegion != null) lastRegion = pictureRegion;
            var rgn = MapRegion(lastRegion);
            Shape(verb, () => rgn, () => RegionShapes.FrameRegion(rgn, penWidth, penHeight));
        }

        // StdRgn: frame paints the frame with the pen, paint uses the pen pattern and mode, erase the background
        // pattern (patCopy), invert XORs with black (hilite when pending), fill the fill pattern (patCopy).
        private void Shape(int verb, Func<Region> interior, Func<Region> frame)
        {
            var colors = Colors;
            switch (verb)
            {
                case 0: Painter.FillRegion(canvas, frame(), clip, PnPat, PatternAlign, PenMode, hilitePending, colors); break;
                case 1: Painter.FillRegion(canvas, interior(), clip, PnPat, PatternAlign, PenMode, hilitePending, colors); break;
                case 2: Painter.FillRegion(canvas, interior(), clip, BkPat, PatternAlign, TransferModes.PatCopy, false, colors); break;
                case 3: Painter.FillRegion(canvas, interior(), clip, Pattern.Black, PatternAlign, TransferModes.PatXor, hilitePending, colors); break;
                case 4: Painter.FillRegion(canvas, interior(), clip, FillPat, PatternAlign, TransferModes.PatCopy, false, colors); break;
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
            Painter.FillRegion(canvas, region, clip, PnPat, PatternAlign, mode, hilitePending, Colors);
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

        // ---- text ----

        // LongText sets the text origin; DH/DV/DHDV text move it from the previous origin. Drawing does not move it.
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
            if (text.Length == 0) { Done(); return; }
            var (x, y) = MapPoint(textH, textV);
            fontNames.TryGetValue(TextFontId, out var name);
            if (options.Fonts is { } library)
            {
                int family = name != null && library.TryGetFamilyByName(name, out int byName) ? byName : TextFontId;
                var font = FontManager.Swap(library, family, TextSize, TextFace, textNumer, textDenom, SpaceExtra);
                if (font != null)
                {
                    TextDrawer.Draw(canvas, font, text, x, y, TextMode, clip, hilitePending, Colors);
                    Done();
                    return;
                }
            }
            var fallback = options.TextFallback;
            if (fallback == null) { Done(); return; }
            var mask = fallback.Render(PictReader.MacRomanString(text), new PictTextStyle(TextFontId, TextFace, TextSize, name));
            if (mask != null && mask.Width > 0 && mask.Height > 0)
                Painter.FillMask(canvas, x - mask.OriginX, y - mask.OriginY, mask.Width, mask.Height, mask.Bits,
                    clip, TextMode, hilitePending, Colors);
            Done();
        }
    }
}
