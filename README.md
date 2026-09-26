# QuickDraw.Pict

Reader and writer for Apple QuickDraw PICT (v1/v2) images.

| Package | What it is |
|---|---|
| `QuickDraw.Pict` | Dependency-free core. `PictReader` decodes to an RGBA `PictBitmap` with a software QuickDraw engine (regions, patterns, pen and transfer modes); `PictWriter` writes PICT v2; `PictHeader` detects pictures and reads their header. Text uses the bitmap fonts you supply (`PictFontLibrary`), else an `IPictTextFallback` (skipped if none). |
| `QuickDraw.Pict.ImageSharp` | [ImageSharp](https://github.com/SixLabors/ImageSharp) format plugin built on the core: detection, decode (text via SixLabors.Fonts), encode, `SaveAsPict`. |

## ImageSharp

```csharp
Configuration.Default.Configure(new PictConfigurationModule());

using var image = Image.Load("picture.pict");   // .pict file or bare PICT resource data
image.SaveAsPict("copy.pict");
```

Text opcodes use a system font approximating the classic Mac font; supply your own per QuickDraw font id:

```csharp
var options = new PictDecoderOptions { FontResolver = fontId => myFamily };
using var image = PictDecoder.Instance.Decode<Rgba32>(options, stream);
```

## Core only

```csharp
PictBitmap bitmap = PictReader.Decode(bytes);            // everything but text
PictBitmap drawn  = PictReader.Decode(bytes, new PictDecodeOptions { TextFallback = myTextRasterizer });
PictWriter.Write(stream, bitmap);
```

Text is drawn exactly as QuickDraw does when you supply the classic Mac bitmap fonts it uses: add `FOND`, `NFNT` and
`FONT` resources (from a font suitcase, the System file or an application) to a `PictFontLibrary` and pass it as
`PictDecodeOptions.Fonts` (ImageSharp: `PictDecoderOptions.BitmapFonts`). No Apple fonts are included; text in fonts
the library lacks goes to the outline fallback.

High-resolution (extended v2) pictures decode at their native resolution by default. `Resolution =
PictResolution.PictureFrame` draws them at their 72 dpi picture frame instead, scaled the way `DrawPicture` does it. That
includes pen and oval sizes and CopyBits stretching. `PreserveAlpha = true` keeps the alpha channel of 32-bit pixel maps
that carry one. Both options exist on `PictDecodeOptions` and on the ImageSharp `PictDecoderOptions`.

## Build

```
dotnet test QuickDraw.Pict.slnx
```
