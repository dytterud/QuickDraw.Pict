using System;
using System.Collections.Generic;

namespace QuickDraw.Pict
{
    // The QuickDraw drawing state of a picture being played back, and its drawing verbs. Coordinates arrive in picture
    // space and are mapped to canvas pixels when an opcode is read (the canvas covers the picture's bounds; the Origin
    // opcode shifts the mapping cumulatively), so pen locations and "same shape" memory are in canvas space, as
    // Executor's DrawPicture keeps them in destination space. Shapes are rasterized as regions (RegionShapes) and
    // painted through a pattern and transfer mode (Painter). verb: 0 frame, 1 paint, 2 erase, 3 invert, 4 fill.
    internal sealed class GrafPort
    {
        private readonly PictBitmap canvas;
        private readonly PictDecodeOptions options;
        private readonly int frameLeft, frameTop;
        private int originH, originV;

        // Port state as DrawPicture initializes it (Executor C_DrawPicture): black on white, pen 1x1 patCopy with a
        // black pen and fill pattern and a white background pattern, text mode srcOr, OpColor black.
        public PictColor ForeColor = new PictColor(0, 0, 0);
        public PictColor BackColor = new PictColor(255, 255, 255);
        public (ushort r, ushort g, ushort b) OpColor;
        public PictColor HiliteColor;
        private bool hilitePending;

        public Pattern BkPat = Pattern.White;
        public Pattern PnPat = Pattern.Black;
        public Pattern FillPat = Pattern.Black;
        public int PenH = 1, PenV = 1;
        public int PenMode = TransferModes.PatCopy;
        public int OvalW, OvalH;
        private int penX, penY;                                   // canvas space

        private Region? clip;                                     // canvas space; null = no clip
        private PictRect lastRect;                                // canvas space, for the "same shape" opcodes
        private (int h, int v)[]? lastPoly;
        private Region lastRegion = Region.Empty;

        public int TextFontId, TextFace, TextSize, TextMode = TransferModes.SrcOr;
        private int textX, textY;                                 // text origin, canvas space
        private readonly Dictionary<int, string> fontNames = new Dictionary<int, string>();

        public GrafPort(PictBitmap canvas, PictRect bounds, PictDecodeOptions options)
        {
            this.canvas = canvas;
            this.options = options;
            frameLeft = bounds.Left;
            frameTop = bounds.Top;
            HiliteColor = options.HiliteColor;
        }

        // DrawPicture starts the pattern alignment at (0, 0) and the Origin opcode adds its dh, dv to it.
        private (int h, int v) PatternAlign => (originH, originV);

        private PortColors Colors => new PortColors(ForeColor, BackColor, OpColor, HiliteColor);

        // ---- coordinate mapping ----

        public int MapH(int h) => h - frameLeft - originH;
        public int MapV(int v) => v - frameTop - originV;
        public PictRect Map(PictRect r) => new PictRect(MapV(r.Top), MapH(r.Left), MapV(r.Bottom), MapH(r.Right));
        private Region Map(Region r) => r.Offset(-(frameLeft + originH), -(frameTop + originV));

        // Origin opcode: Executor origin() offsets the source picture frame, so later coordinates shift by -dh, -dv.
        public void Origin(int dh, int dv)
        {
            originH += dh;
            originV += dv;
            textX -= dh;
            textY -= dv;
        }

        // ---- state ----

        public void SetClip(Region pictureRegion) => clip = Map(pictureRegion);
        public void HiliteMode() => hilitePending = true;
        public void DefaultHilite() => HiliteColor = options.HiliteColor;
        public void FontName(int fontId, string name) => fontNames[fontId] = name;

        // ---- shapes ----

        public void Rect(PictRect? pictureRect, int verb)
        {
            if (pictureRect is { } pr) lastRect = Map(pr);
            var r = lastRect;
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.Rect(r), () => RegionShapes.FrameRect(r, PenH, PenV));
        }

        public void RoundRect(PictRect? pictureRect, int verb)
        {
            if (pictureRect is { } pr) lastRect = Map(pr);
            var r = lastRect;
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.RoundRect(r, OvalW, OvalH),
                () => RegionShapes.FrameRoundRect(r, OvalW, OvalH, PenH, PenV));
        }

        public void Oval(PictRect? pictureRect, int verb)
        {
            if (pictureRect is { } pr) lastRect = Map(pr);
            var r = lastRect;
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.Oval(r), () => RegionShapes.FrameOval(r, PenH, PenV));
        }

        public void Arc(PictRect? pictureRect, int startAngle, int arcAngle, int verb)
        {
            if (pictureRect is { } pr) lastRect = Map(pr);
            var r = lastRect;
            if (r.IsEmpty) { Done(); return; }
            Shape(verb, () => RegionShapes.Arc(r, startAngle, arcAngle),
                () => RegionShapes.FrameArc(r, startAngle, arcAngle, PenH, PenV));
        }

        // Polygons (picture-space points). Framing draws each edge as a line and does not close the polygon.
        public void Polygon((int h, int v)[]? picturePoints, int verb)
        {
            if (picturePoints != null)
            {
                lastPoly = new (int h, int v)[picturePoints.Length];
                for (int i = 0; i < picturePoints.Length; i++)
                    lastPoly[i] = (MapH(picturePoints[i].h), MapV(picturePoints[i].v));
            }
            var pts = lastPoly;
            if (pts == null || pts.Length < 2) { Done(); return; }
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
            if (pictureRegion != null) lastRegion = Map(pictureRegion);
            var rgn = lastRegion;
            Shape(verb, () => rgn, () => RegionShapes.FrameRegion(rgn, PenH, PenV));
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
            penX = MapH(h1);
            penY = MapV(v1);
            LineTo(h2, v2);
        }

        public void LineTo(int h, int v) => LineToCanvas(MapH(h), MapV(v));

        public void LineBy(int dh, int dv) => LineToCanvas(penX + dh, penY + dv);

        private void LineToCanvas(int x, int y)
        {
            PaintLine(penX, penY, x, y);
            penX = x;
            penY = y;
            Done();
        }

        // C_StdLine paints the pen-swept region with the pen pattern; Boolean pen modes act as pattern modes.
        private void PaintLine(int x1, int y1, int x2, int y2)
        {
            var region = RegionShapes.Line(x1, y1, x2, y2, PenH, PenV);
            int mode = PenMode < TransferModes.Blend ? (PenMode % 0x40) | 8 : PenMode;
            Painter.FillRegion(canvas, region, clip, PnPat, PatternAlign, mode, hilitePending, Colors);
        }

        // ---- text ----

        // LongText sets the text origin; DH/DV/DHDV text offset it from the previous origin (Executor longtext /
        // dhtext / dvtext / dhdvtext). Drawing does not move the text origin.
        public void LongText(int h, int v, string s)
        {
            textX = MapH(h);
            textY = MapV(v);
            DrawText(s);
        }

        public void OffsetText(int dh, int dv, string s)
        {
            textX += dh;
            textY += dv;
            DrawText(s);
        }

        private void DrawText(string s)
        {
            var fallback = options.TextFallback;
            if (string.IsNullOrEmpty(s) || fallback == null) { Done(); return; }
            fontNames.TryGetValue(TextFontId, out var name);
            var mask = fallback.Render(s, new PictTextStyle(TextFontId, TextFace, TextSize, name));
            if (mask != null && mask.Width > 0 && mask.Height > 0)
                Painter.FillMask(canvas, textX - mask.OriginX, textY - mask.OriginY, mask.Width, mask.Height, mask.Bits,
                    clip, TextMode, hilitePending, Colors);
            Done();
        }
    }
}
