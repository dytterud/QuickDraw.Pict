# QuickDraw.Pict

Reader and writer for Apple QuickDraw PICT pictures (version 1, version 2 and extended version 2), aiming to draw
exactly the pixels a Macintosh draws.

| Package | What it is |
|---|---|
| `QuickDraw.Pict` | Dependency-free core (.NET 8). `PictReader` decodes to an RGBA `PictBitmap`; `PictWriter` writes pictures; `PictHeader` detects pictures and reads their header. |
| `QuickDraw.Pict.ImageSharp` | [ImageSharp](https://github.com/SixLabors/ImageSharp) format plugin on top of the core: detection, decoding, encoding, `SaveAsPict`. |

## ImageSharp

```csharp
Configuration.Default.Configure(new PictConfigurationModule());

using var image = Image.Load("picture.pict");   // a .pict file or bare PICT resource data
image.SaveAsPict("copy.pict");
image.SaveAsPict("indexed.pict", new PictEncoder { BitsPerPixel = 8 });
```

Options go through `PictDecoderOptions` with `PictDecoder.Instance.Decode(...)`:

| Option | Effect |
|---|---|
| `BitmapFonts` | Classic Mac bitmap fonts (`PictFontLibrary`) for exact text. |
| `FontResolver` | Outline font family per QuickDraw font number, for text no bitmap font covers (default: an installed system font). |
| `Resolution` | `Native` (default) or `PictureFrame` (72 dpi). |
| `PreserveAlpha` | Keep the alpha channel of 32-bit pixel maps that have one. |

JPEG, PNG, GIF, TIFF, WebP and BMP QuickTime images inside pictures are decoded with ImageSharp's own decoders.

ImageSharp has its own licence (the Six Labors Split License); check that it fits your use.

## Core

```csharp
PictBitmap bitmap = PictReader.Decode(bytes);
PictBitmap exact  = PictReader.Decode(bytes, new PictDecodeOptions { Fonts = library, ImageCodec = myJpegCodec });
PictWriter.Write(stream, bitmap);                                       // 24-bit, 72 dpi
PictWriter.Write(stream, bitmap, new PictWriteOptions { Format = PictPixelFormat.Indexed8, Palette = colors,
    HorizontalResolution = 144, VerticalResolution = 144, IccProfile = icc });
```

## What is drawn

- **Shapes**: rects, round rects, ovals, arcs and wedges, lines, polygons and regions, framed, painted, erased,
  inverted and filled, with pen size and patterns (1-bit and pixel patterns), clip regions and the Origin opcode.
  Shapes are scan-converted the way QuickDraw does, pixel for pixel.
- **Transfer modes**: every Boolean pattern and source mode, the arithmetic modes (blend, addPin, addOver, subPin,
  subOver, addMax, adMin), transparent and hilite.
- **Bitmaps** (CopyBits): 1/2/4/8-bit indexed and 16/32-bit direct pixel maps in every packing, stretched or shrunk from
  the source to the destination rect like QuickDraw's StretchBits, with mask regions and fore/back colorizing.
- **Text**: with a `PictFontLibrary` of `FOND` / `NFNT` / `FONT` resources (from a font suitcase, the System file or an
  application), text is drawn by QuickDraw's character generator: bold, italic, underline, outline, shadow, condense,
  extend, space extra, substituted sizes stretched. No Apple fonts are included; without a matching font, text goes to
  an `IPictTextFallback` (the ImageSharp plugin renders it with SixLabors.Fonts).
- **QuickTime images**: `raw `, `rle ` (Animation), `rpza` (Road Pizza), `smc ` (Graphics), `cvid` (Cinepak), `8BPS`,
  `yuv2`, `YVU9`, `tga ` and `PNTG` are decoded by the core; others go to an `IPictImageCodec`. A decoded image skips
  the picture's "QuickTime is required" fallback.
- **Resolution**: extended version 2 pictures decode at their native resolution, or with
  `Resolution = PictResolution.PictureFrame` at their 72 dpi frame, scaled the way `DrawPicture` scales.
- **Metadata**: `PictInfo` has the version, frame, bounds, resolution, all picture comments and the embedded ICC profile.

The writer stores 1/2/4/8-bit indexed, 16-bit and 32-bit (with or without alpha) pictures with their resolution and an
ICC profile, as a `.pict` file or a bare picture, splitting images too wide for one bitmap opcode into strips.

## Accuracy

Shape rasterization, regions, 1-bit CopyBits scaling and the text character generator match reference
implementations of the corresponding QuickDraw routines pixel for pixel. The QuickTime codecs match ffmpeg's
decoders on real and generated samples. Behaviour documented as provisional in the source (font selection details,
scaling of deeper pixels, color Boolean modes) is still being checked against the Macintosh ROM.

## Build

```
dotnet test QuickDraw.Pict.slnx
```

## Licence

MIT. See `LICENSE` and `THIRD-PARTY-NOTICES.md`.
