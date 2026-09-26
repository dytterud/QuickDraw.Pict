# QuickDraw.Pict

Reader and writer for Apple QuickDraw PICT (v1/v2) images.

| Package | What it is |
|---|---|
| `QuickDraw.Pict` | Dependency-free core. `PictReader` decodes to an RGBA `PictBitmap`; `PictWriter` writes PICT v2; `PictHeader` detects pictures. Vector and text opcodes are handed to an `IPictRenderer` you supply (skipped if none). |
| `QuickDraw.Pict.ImageSharp` | [ImageSharp](https://github.com/SixLabors/ImageSharp) format plugin built on the core: detection, decode (shapes/text via ImageSharp.Drawing), encode, `SaveAsPict`. |

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
PictBitmap bitmap = PictReader.Decode(bytes);            // bitmap opcodes only
PictBitmap drawn  = PictReader.Decode(bytes, canvas => new MyRenderer(canvas));
PictWriter.Write(stream, bitmap);
```

## Build

```
dotnet test QuickDraw.Pict.slnx
```
