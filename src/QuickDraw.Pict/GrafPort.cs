using System;
using System.Collections.Generic;

namespace QuickDraw.Pict
{
    // QuickDraw drawing state for the PICT reader. Keeps pen/colors/origin/clip/"same shape" memory and
    // resolves each shape opcode to canvas-space geometry, which an IPictRenderer (if any) rasterizes.
    // Coordinates arrive in picFrame space; we translate to canvas space (minus frame origin and the
    // QuickDraw drawing origin). verb: 0 frame, 1 paint, 2 erase, 3 invert, 4 fill.
    internal sealed class GrafPort
    {
        public readonly PictBitmap Canvas;
        private readonly IPictRenderer? renderer;
        private readonly int frameLeft, frameTop;

        public PictColor ForeColor = new PictColor(0, 0, 0, 255);
        public PictColor BackColor = new PictColor(255, 255, 255, 255);
        public int PenWidth = 1;
        public int OvalW, OvalH;                 // round-rect corner diameters
        public int OriginH, OriginV;             // QuickDraw SetOrigin offset
        public (int top, int left, int bottom, int right)? Clip;  // null = whole canvas

        // Patterns as DrawPicture initializes them (Executor C_DrawPicture): background white, pen and fill black.
        public Pattern BkPat = Pattern.White;
        public Pattern PnPat = Pattern.Black;
        public Pattern FillPat = Pattern.Black;

        public PictPoint Pen;                    // current pen position (canvas space)
        public (int top, int left, int bottom, int right) LastRect;  // for the "same shape" opcodes
        public PictPoint[]? LastPoly;            // for the "same poly" opcodes
        public (int top, int left, int bottom, int right) LastRegion;  // for the "same region" opcodes

        public int TextFontId, TextFace, TextSize = 12;   // text state
        public int TextH, TextV;                          // current text pen (QuickDraw coords)

        public GrafPort(PictBitmap canvas, IPictRenderer? renderer, int frameLeft, int frameTop)
        {
            Canvas = canvas;
            this.renderer = renderer;
            this.frameLeft = frameLeft;
            this.frameTop = frameTop;
        }

        // Translate a QuickDraw (h,v) coordinate to canvas pixel space.
        public float Cx(int h) => h - frameLeft - OriginH;
        public float Cy(int v) => v - frameTop - OriginV;
        public PictPoint P(int h, int v) => new PictPoint(Cx(h), Cy(v));

        // The clip rect in canvas space, or null when it covers the whole canvas.
        private PictRectangleF? CanvasClip()
        {
            if (Clip is { } c && !(c.left <= frameLeft && c.top <= frameTop &&
                                   c.right - frameLeft >= Canvas.Width && c.bottom - frameTop >= Canvas.Height))
                return new PictRectangleF(Cx(c.left), Cy(c.top),
                    Math.Max(0, c.right - c.left), Math.Max(0, c.bottom - c.top));
            return null;
        }

        private int PenPixels => Math.Max(1, PenWidth);

        // Fill/stroke a shape according to the verb.
        private void RenderShape(in PictShape shape, int verb)
        {
            if (renderer == null) return;
            switch (verb)
            {
                case 0: renderer.Frame(shape, ForeColor, PenPixels, CanvasClip()); break;
                case 1: renderer.Fill(shape, ForeColor, CanvasClip()); break;     // paint
                case 4: renderer.Fill(shape, ForeColor, CanvasClip()); break;     // fill
                case 2: renderer.Fill(shape, BackColor, CanvasClip()); break;     // erase
                case 3: renderer.Invert(shape); break;                            // invert
            }
        }

        // ---- shapes (all take QuickDraw rect coords; verb selects frame/paint/erase/invert/fill) ----

        public void Rect((int top, int left, int bottom, int right) r, int verb)
        {
            LastRect = r;
            RenderShape(RectShape(r), verb);
        }

        public void SameRect(int verb) => RenderShape(RectShape(LastRect), verb);

        public void RoundRect((int top, int left, int bottom, int right) r, int verb)
        {
            LastRect = r;
            RenderShape(RoundRectShape(r), verb);
        }

        public void SameRoundRect(int verb) => RenderShape(RoundRectShape(LastRect), verb);

        public void Oval((int top, int left, int bottom, int right) r, int verb)
        {
            LastRect = r;
            RenderShape(OvalShape(r), verb);
        }

        public void SameOval(int verb) => RenderShape(OvalShape(LastRect), verb);

        public void Arc((int top, int left, int bottom, int right) r, int startAngle, int arcAngle, int verb)
        {
            LastRect = r;
            RenderShape(ArcShape(r, startAngle, arcAngle, verb == 0), verb);
        }

        public void SameArc(int startAngle, int arcAngle, int verb) =>
            RenderShape(ArcShape(LastRect, startAngle, arcAngle, verb == 0), verb);

        public void Polygon(PictPoint[] pts, int verb)
        {
            if (pts.Length < 2) return;
            if (verb == 0)
                renderer?.DrawPolyline(pts, ForeColor, PenPixels, CanvasClip());
            else
                RenderShape(PictShape.Polygon(pts), verb);
        }

        public void Line(PictPoint from, PictPoint to)
        {
            renderer?.DrawPolyline(new[] { from, to }, ForeColor, PenPixels, CanvasClip());
            Pen = to;
        }

        public void RegionRect((int top, int left, int bottom, int right) bbox, int verb)
        {
            // We don't rasterize region run data; approximate a region as its bounding rect.
            RenderShape(RectShape(bbox), verb);
        }

        // ---- text (the renderer picks a font for the QuickDraw font id/face/size) ----

        public void SetTextLoc(int h, int v) { TextH = h; TextV = v; }
        public void OffsetText(int dh, int dv) { TextH += dh; TextV += dv; }

        public void DrawText(string s)
        {
            if (string.IsNullOrEmpty(s) || renderer == null) return;
            float advance = renderer.DrawText(s, new PictPoint(Cx(TextH), Cy(TextV)),
                new PictTextStyle(TextFontId, TextFace, TextSize), ForeColor, CanvasClip());
            TextH += (int)Math.Round(advance);
        }

        // ---- shape builders ----

        private PictRectangleF Bounds((int top, int left, int bottom, int right) r) =>
            new PictRectangleF(Cx(r.left), Cy(r.top), Math.Max(0, r.right - r.left), Math.Max(0, r.bottom - r.top));

        private PictShape RectShape((int top, int left, int bottom, int right) r) => PictShape.Rectangle(Bounds(r));

        private PictShape OvalShape((int top, int left, int bottom, int right) r) => PictShape.Oval(Bounds(r));

        private PictShape RoundRectShape((int top, int left, int bottom, int right) r)
        {
            if (OvalW <= 0 && OvalH <= 0) return RectShape(r);
            var b = Bounds(r);
            return PictShape.RoundRectangle(b, Math.Min(OvalW / 2f, b.Width / 2f), Math.Min(OvalH / 2f, b.Height / 2f));
        }

        private PictShape ArcShape((int top, int left, int bottom, int right) r, int startAngle, int arcAngle, bool frame)
        {
            float w = Math.Max(0, r.right - r.left), h = Math.Max(0, r.bottom - r.top);
            float cx = Cx(r.left) + w / 2f, cy = Cy(r.top) + h / 2f, rx = w / 2f, ry = h / 2f;
            // QuickDraw angles: 0 = up (12 o'clock), clockwise, in degrees.
            var pts = new List<PictPoint>();
            int steps = Math.Max(2, Math.Abs(arcAngle));
            if (!frame) pts.Add(new PictPoint(cx, cy));   // wedge for fills
            for (int i = 0; i <= steps; i++)
            {
                double deg = startAngle + arcAngle * (i / (double)steps);
                double rad = (90 - deg) * Math.PI / 180.0;     // convert QD (cw from up) to math (ccw from +x)
                pts.Add(new PictPoint(cx + (float)(rx * Math.Cos(rad)), cy - (float)(ry * Math.Sin(rad))));
            }
            return PictShape.Polygon(pts.ToArray());
        }
    }
}
