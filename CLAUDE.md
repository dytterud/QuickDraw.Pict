# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

QuickDraw.Pict decodes and encodes Apple QuickDraw PICT pictures. Its goal is to draw **exactly the pixels a Macintosh
draws**, so every rendering rule is ground-truthed rather than approximated. The core (`src/QuickDraw.Pict`, .NET 8)
has no dependencies. `src/QuickDraw.Pict.ImageSharp` is the ImageSharp format plugin on top of it, and
`src/QuickDraw.Pict.SkiaSharp` the SkiaSharp adapter (Skia has no managed-codec registry, so it offers
`PictSkia.Decode`/`Encode` instead of a format plugin).

## Commands

- Build and test everything: `dotnet test QuickDraw.Pict.slnx`
- One test or class: `dotnet test QuickDraw.Pict.slnx --filter "FullyQualifiedName~TextTests.Text_TxRatio"`
- Regenerate the golden feature pictures: `dotnet run --project tools/GoldenPictures`
  - They are written to `tests/golden/pict`.
  - Capturing screenshots is described in `tests/golden/README.md`.
  - `GoldenTests` skip when there are no screenshots. The text golden needs user-supplied Apple fonts in
    `tests/golden/fonts/` (gitignored).

Tests use `PictBuilder` (tests project) to write pictures opcode by opcode, and `TestFont` builds small NFNT/FOND fonts.
Internals are visible to the test project.

## Architecture

**Layers.** The core is one package whose code lives in layer folders under `src/QuickDraw.Pict/`, in the order they
will become separate ClassicMac packages. A layer uses only the layers below it; `LayeringTests` checks this, with one
listed exception (`PictBitmap.Info`, public API until the merge). Public types keep the `QuickDraw.Pict` namespace.

| Folder | Contains | May use |
| --- | --- | --- |
| `Graphics/` | `PictBitmap`, `PictColor`/`PictRect`, `PixMap` records, standard colour tables, PackBits | nothing |
| `MacPaint/` | `MacPaintFile` (also QuickTime's `PNTG` codec) | Graphics |
| `QuickTime/` | Image descriptions, the codecs, `IPictImageCodec`, QTIF files | Graphics, MacPaint |
| `QuickDraw/` | The renderer: `Engine/`, `Regions/`, `Text/`, `Pattern`, `PictFontLibrary` | Graphics |
| `Pict/` | The PICT format: `PictReader`, `GrafPort`, `PictWriter`, `PictHeader`/`PictInfo`, options, the `$8200` opcode | all of the above |
| `Resources/` | `QuickDrawResources` (icons, cursors, patterns) | Graphics, QuickDraw |

**Decoding pipeline**
- `PictReader` parses the opcode stream and drives a `GrafPort`. The port holds the play state: pen, patterns,
  fore/back/op/hilite colours, clip, text state, and picture-to-canvas mapping.
- The port draws onto a `PictBitmap` (RGBA canvas) through the renderer in `QuickDraw/Engine/`:
  - `Painter`: region + pattern fills.
  - `Bits`: CopyBits/StretchBits, including the row and column DDAs and colorizing.
  - `TransferModes`: Boolean, arithmetic and hilite modes.
  - `PictureMapping`: MapPt/MapRect.
- Shapes become `QuickDraw/Regions/Region` via `RegionShapes`, which scan-converts the way QuickDraw does.
- Fixed-point maths lives in `QuickDraw/Regions/FixedMath`.

**Two QuickDraws.** `PictDecodeOptions.QuickDraw` selects `MacOS9` (the default) or `MacRom` (the 68k ROM $077D).
The difference flows through `PortColors.MacOS9` / `FontSelection.MacOS9`. The difference is scattered through every
layer: DDAs, rounding, colorizing, text placement and the Font Manager. Any behaviour change must keep **both** modes
correct.

**Screen depth.** `PictDecodeOptions.ScreenDepth` (1/2/4/8/16) routes every pixel write through
`QuickDraw/Engine/ScreenDevice.cs`:
- `ScreenDevice`: default clut, inverse table, Color2Index.
- `DeviceModes`: index-level transfer modes, PatDither, ditherCopy.
- The device rides on `PortColors.Device`. Painter and Bits branch to it. The only canvas write sites are
  `Painter.FillRegion`/`FillMask` and `Bits.CopyBits`.

**Text** (`QuickDraw/Text/`)
- `PictFontLibrary` holds caller-supplied `FOND`/`NFNT`/`FONT`/`fctb` resources, parsed from resource forks by
  `ResourceFork`.
- `FontManager.Swap` reproduces the Font Manager's font choice and outputs a `FontSelection`: widths, style extras
  and stretch.
- `TextDrawer` is the character generator. It draws into a 1-bit buffer, then does one StretchBits. It also has a
  colour-NFNT path.
- Text without a usable bitmap strike, including TrueType-only families, goes to `IPictTextFallback`.

**Other formats**
- `QuickTime/`: the built-in codecs and QTIF files; `Pict/QuickTimeImage` parses the 0x8200/0x8201 opcodes. Other
  codecs go through `IPictImageCodec`.
- `Resources/QuickDrawResources`: icons, cursors and patterns from resource bytes.
- `MacPaint/MacPaintFile`: MacPaint documents.
- `Pict/PictWriter`: the encoder.

## Ground truth and the spec

- `docs/PICT-FORMAT.md` is the full format and rendering spec. It is precise enough to reimplement the library, and
  it is kept in sync with the code.
  - Update the matching section in the same change as any behaviour change.
  - §16 lists what isn't covered; §17 the Mac OS 9 differences; §19 screen depths.
- Behaviour is verified against real QuickDraw (disassembly and emulator renders kept outside this repo), not
  reasoned out.
- When a rule is fitted to renders instead of read from code, say so in the code comment and the spec.
- **Licensing:**
  - The repo is MIT.
  - Never copy Apple source code, private test harnesses or Apple fonts into it.
  - Third-party ports keep their notices in `THIRD-PARTY-NOTICES.md`.

## Conventions

- Match QuickDraw's integer behaviour exactly: signed 16-bit wraps, truncating versus rounding divides, Fixed maths.
  Comments cite the routine or rule being reproduced.
- Work on `main` (no feature branches).
