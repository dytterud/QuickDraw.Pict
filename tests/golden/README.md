# Emulator goldens

`pict/` holds feature pictures written by `tools/GoldenPictures`:

| Picture | Covers |
|---|---|
| `shapes` | Every verb on rects, round rects, ovals, arcs, polygons and regions; pen sizes 1x1, 2x2, 3x1. |
| `modes` | The eight pattern pen modes over stripes. |
| `lines` | Line slopes at three pen sizes. |
| `copybits1` | 1-bit CopyBits in every Boolean mode, StretchBits ratios, and a mask region. |
| `copybitsColor` | 8-, 16- and 32-bit CopyBits at 1:1, shrunk and stretched. |
| `color` | Fore/back colors, colorized CopyBits, arithmetic modes with OpColor, hilite. |
| `text` | Chicago, Geneva, New York and Monaco at several sizes, every style, srcXor text. |
| `highres` | A 144 dpi extended version 2 picture drawn into its 72 dpi frame. |
| `originClip` | The Origin opcode, pattern alignment and a non-rectangular clip. |

Each picture is 240 x 160 with a 1-pixel black frame around it, so the test can find it in a screenshot.

## Capturing

1. In Basilisk II or SheepShaver, run System 7.5 to Mac OS 9 with the monitor set to **millions of colors**, and copy
   `pict/` into the emulated Mac (through the shared folder). If the files have no type, set them to `PICT`, e.g. with
   ResEdit's Get Info, so SimpleText opens them.
2. Open each picture in SimpleText. It draws the picture at 100%.
3. Take a screenshot at the emulator's native size (no window scaling): Cmd-Shift-3 inside the Mac, or the emulator's
   own screenshot, or the host's if the window is 1:1. Crop it or not; the test finds the frame.
4. Save it as PNG in `screens/<picture name>.png`, e.g. `screens/shapes.png`.

For `text`, copy the Mac's font suitcases or System file resource forks (Chicago, Geneva, New York, Monaco) into
`fonts/` (any file names; resource-fork data, e.g. from `..namedfork/rsrc` or a `.rsrc` export). They are not committed.

## Checking

`dotnet test` runs `GoldenTests`: every screenshot must match QuickDraw.Pict's rendering exactly. On a mismatch it
writes `golden-diff-<name>.png` next to the test binaries (differences in magenta).

Regenerate the pictures with `dotnet run --project tools/GoldenPictures -- tests/golden/pict` (only needed when the
generator changes; existing screenshots then need recapturing).
