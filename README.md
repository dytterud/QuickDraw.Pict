# QuickDraw.Pict

Reader and writer for Apple QuickDraw PICT pictures (version 1, version 2 and extended version 2), aiming to draw
exactly the pixels a Macintosh draws.

| Package | What it is |
|---|---|
| `QuickDraw.Pict` | Dependency-free core (.NET 8). `PictReader` decodes to an RGBA `PictBitmap`; `PictWriter` writes pictures; `PictHeader` detects pictures and reads their header. |
| `QuickDraw.Pict.ImageSharp` | [ImageSharp](https://github.com/SixLabors/ImageSharp) format plugin on top of the core: detection, decoding, encoding, `SaveAsPict`. |
| `QuickDraw.Pict.SkiaSharp` | [SkiaSharp](https://github.com/mono/SkiaSharp) integration: decode to `SKBitmap`/`SKImage`, encode `SKBitmap`/`SKPixmap`, `SaveAsPict`. |

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
| `QuickDraw` | `MacOS9` (default) or `MacRom`: which Macintosh QuickDraw to reproduce. |
| `ScreenDepth` | 32 (default), or 1, 2, 4, 8 or 16 to draw as on that screen (default color table, index-level transfer modes, ditherCopy dithering). |

JPEG, PNG, GIF, TIFF, WebP and BMP QuickTime images inside pictures are decoded with ImageSharp's own decoders.

ImageSharp has its own licence (the Six Labors Split License); check that it fits your use.

## SkiaSharp

```csharp
using SKBitmap bitmap = PictSkia.Decode(File.ReadAllBytes("picture.pict"));
using SKImage image   = PictSkia.DecodeImage(bytes, new PictSkiaOptions { ScreenDepth = 8 });
SKBitmap? any         = PictSkia.DecodeAny(bytes);          // PICT, QTIF or MacPaint
bitmap.SaveAsPict("copy.pict");
```

SkiaSharp has no registry for managed codecs, so this package is an adapter rather than a format plugin: `SKCodec` and
`SKBitmap.Decode` do not see PICT. `PictSkiaOptions` has the same options as the ImageSharp decoder, with a
`TypefaceResolver` for the outline-text fallback. JPEG, PNG, GIF, WebP and BMP QuickTime images are decoded with Skia's
own codecs (Skia has no TIFF decoder). On Linux, add a SkiaSharp native-assets package, such as
`SkiaSharp.NativeAssets.Linux`.

## Core

```csharp
PictBitmap bitmap = PictReader.Decode(bytes);
PictBitmap exact  = PictReader.Decode(bytes, new PictDecodeOptions { Fonts = library, ImageCodec = myJpegCodec });
PictBitmap screen = PictReader.Decode(bytes, new PictDecodeOptions { ScreenDepth = 8 });  // as on an 8-bit screen
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
- **Text**: with a `PictFontLibrary` of `FOND` / `NFNT` / `FONT` / `fctb` resources (from a font suitcase, the System
  file or an application), text is drawn by the Font Manager and character generator: font and size substitution,
  bold, italic, underline, outline, shadow, condense, extend, space and character extra, fractional widths, text
  ratios, font-name mapping, pen fractions, and color bitmap fonts. No Apple fonts are included; without a matching font (or for
  TrueType-only families), text goes to an `IPictTextFallback` (the ImageSharp plugin renders it with SixLabors.Fonts).
- **QuickTime images**: `raw `, `rle ` (Animation), `rpza` (Road Pizza), `smc ` (Graphics), `cvid` (Cinepak), `8BPS`,
  `yuv2`, `YVU9`, `tga ` and `PNTG` are decoded by the core; others go to an `IPictImageCodec`. A decoded image skips
  the picture's "QuickTime is required" fallback.
- **QuickTime image files and MacPaint documents**: `QuickTimeImageFile` decodes standalone QTIF files (with their
  resolution and ICC profile) and `MacPaintFile` decodes MacPaint (PNTG) documents, MacBinary-wrapped or not. The
  ImageSharp plugin registers both formats for loading.
- **Icons, cursors and patterns**: `QuickDrawResources` decodes the QuickDraw image resources of classic Mac OS resource
  forks (`ICON`, `ICN#`, `ics#`, `icm#`, `SICN`, `icl4/8`, `ics4/8`, `icm4/8`, `cicn`, `CURS`, `crsr`, `PAT `,
  `PAT#`, `ppat`, `ppt#`) from each resource's bytes, with the system's loading rules (computed masks for icon lists
  without one, cursor XOR masks, pixel-pattern types).
- **Screen depth**: `ScreenDepth` = 1, 2, 4, 8 or 16 draws the picture as it looks on that screen: the default color
  table, QuickDraw's color matching (inverse tables), transfer modes on pixel values and ditherCopy error diffusion.
- **Resolution**: extended version 2 pictures decode at their native resolution, or with
  `Resolution = PictResolution.PictureFrame` at their 72 dpi frame, scaled the way `DrawPicture` scales.
- **Metadata**: `PictInfo` has the version, frame, bounds, resolution, all picture comments and the embedded ICC profile.

The writer stores 1/2/4/8-bit indexed, 16-bit and 32-bit (with or without alpha) pictures with their resolution and an
ICC profile, as a `.pict` file or a bare picture, splitting images too wide for one bitmap opcode into strips.

## Accuracy

Pictures are drawn as Mac OS 9's QuickDraw draws them (the default), or with
`QuickDraw = PictQuickDraw.MacRom` as the classic 68k QuickDraw of the Macintosh ROM (Mac OS ROM $077D). Picture
playback, shape rasterization, regions, CopyBits scaling and transfer modes, pixel data, the Font Manager, the text
character generator and drawing on indexed and 16-bit screens are reproduced from the system's own code and checked
pixel for pixel against it, in both modes. The QuickTime codecs match ffmpeg's decoders on real and generated samples.
Not modelled: TrueType text (it goes to the outline fallback), and QuickTime codecs' own dithering on indexed
screens.

The format and every rendering rule the library follows are specified in [docs/PICT-FORMAT.md](docs/PICT-FORMAT.md),
in enough detail to write a compatible decoder and encoder without reading the source.

## Build

```
dotnet test QuickDraw.Pict.slnx
```

## Licence

MIT. See `LICENSE` and `THIRD-PARTY-NOTICES.md`.
