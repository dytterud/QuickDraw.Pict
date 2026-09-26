# QuickDraw.Pict

Reader and writer for Apple QuickDraw PICT (v1/v2) images.

| Package | What it is |
|---|---|
| `QuickDraw.Pict` | Dependency-free core. `PictReader` decodes to an RGBA `PictBitmap` with a software QuickDraw engine (regions, patterns, pen and transfer modes); `PictWriter` writes PICT v2; `PictHeader` detects pictures and reads their header. Text is rasterized by an `IPictTextFallback` you supply (skipped if none). |
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

## Build

```
dotnet test QuickDraw.Pict.slnx
```
