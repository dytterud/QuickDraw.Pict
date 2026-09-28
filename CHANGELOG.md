# Changelog

## Unreleased

**QuickDraw.Pict**
- Fixed: the Origin opcode ($000C) reads dh before dv, as Mac OS 9.0's DrawPicture does; pictures that move their
  origin (DOCMaker's, for one) drew mostly outside their frame.

**QuickDraw.Pict.SkiaSharp** (new)
- Decode pictures, QTIF files and MacPaint documents to `SKBitmap`/`SKImage` (`PictSkia.Decode`, `DecodeImage`,
  `DecodeAny`), with the same options as the ImageSharp decoder.
- Encode `SKBitmap`/`SKPixmap` as PICT (`PictSkia.Encode`, `SaveAsPict`).
- JPEG, PNG, GIF, WebP and BMP QuickTime images through Skia's codecs; outline-font text fallback through Skia.

## 0.1.0 — 2026-09-27

First release.

**QuickDraw.Pict**
- Reads PICT version 1, version 2 and extended version 2 pictures, bare or with a 512-byte file header. It draws them
  with a software QuickDraw engine to an RGBA `PictBitmap`. The engine covers:
  - shapes, regions, patterns, pen modes and transfer modes;
  - CopyBits scaling and masks;
  - bitmap-font and color-bitmap-font text through a Font Manager model;
  - QuickTime images: raw, Animation, Road Pizza, Graphics, Cinepak, 8BPS, YUV2, YVU9, Targa and MacPaint.
- Two QuickDraw models: Mac OS 9 (default) and the 68k ROM.
- `ScreenDepth` draws a picture as it looks on a 1, 2, 4, 8 or 16-bit screen.
- `PictWriter` writes 1/2/4/8-bit indexed, 16-bit and 32-bit pictures with resolution and an ICC profile.
- `PictInfo` gives the header, resolution, comments and ICC profile.
- `QuickTimeImageFile` (QTIF) and `MacPaintFile` (PNTG).
- `QuickDrawResources` decodes icons, cursors and patterns from resource data.

**QuickDraw.Pict.ImageSharp**
- ImageSharp format plugin: PICT detection, decoding and encoding (`SaveAsPict`).
- It also loads QTIF and MacPaint files.
- JPEG, PNG, GIF, TIFF, WebP and BMP images inside QuickTime pictures are decoded through ImageSharp.
- Outline-font fallback for text without bitmap fonts.
