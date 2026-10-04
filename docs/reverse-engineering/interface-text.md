# Interface text: colour remap and the credits teletype

Addresses are dc.exe unless marked dc16 (the Take 2 16-bit build that
`Dark Colony/launch.ps1` runs). The two builds share this code apart from
addresses.

## Colour/brightness remap

Interface draws name a **colour** (0-7) and a **brightness** (0-31, 16 is
unchanged). The glyph blitters index one remap table per pair:

| Build | Builder | Table | Index |
| --- | --- | --- | --- |
| dc.exe (8-bit) | `0x44F200` (cached in `<palette>.rmp`, 3 x 64 KB) | `[0x47C06C]` bytes | `colour * 0x100 + brightness * 0x800 + pixel` |
| dc16 (16-bit) | `0x44F34C` | `[0x48C0B4]` words | `colour * 0x200 + brightness * 0x1000 + pixel * 2` |

Colour 8 bypasses the table (`[surface + 0x14] + 0x601`, dc16 `+0x602`).

dc16 builds each entry in RGB:

- **Font ramp** (palette 138-143, the cyan mfonto5 glyph shades):
  - colour c reads entry `index - 6 * (7 - c)`, so colour 7 stays cyan, 2 is yellow and 0 is red (96-101);
  - colour 5 reads a byte table instead, dc.exe `0x47BF86[index]` (dc16 `0x48BFCE`): 47, 61, 65, 66, 67, 254.
- **Any other index** keeps its colour, blended toward its luminance `L = 3r + 6g + b` by `c / 11`:
  `r' = (10 r (11 - c) + L c) / 110`.
- Each channel is then multiplied by `brightness / 16`, capped at 255, and packed into the 16-bit surface.

The port does this in `NativeColourRemap`, which reads the colour-5 table
from the user's executable, and skips the 16-bit packing.

Evidence (`Dark Colony/screenshots/menu-130543.png` and a dc16 capture of
2026-10-04):

| Item | Remap | Port RGB | Capture (RGB565) |
| --- | --- | --- | --- |
| Main-menu button labels | colour 0, brightness 11 | (139,15,15) | (140,12,8) |
| Credits names (`~0`, settled) | | (203,23,23) | (206,20,16) |
| Credits headers (`~2`, settled) | | (195,203,0) | (198,203,0) |

In every case the glyph pixel counts are identical.

The labels' brightness 11 is observed rather than decoded. It is the same
11/16 that the LARGEBUTTON frames show.

## Teletype ("TTY") window

The main menu (`0x404B1C`) opens it at `0x404BAD`: `0x428514(x 178, y 200,
w 280, h 100, "intrface/credits.txt", font mfonto5, mode 2, interval 5)`.
The menu loop calls the runner `0x427C34` once per iteration.

### Layout

The cell is font frame 0 (7 x 14) plus one pixel of spacing. The grid gets:

- `columns = (w - w/7) / 7 = 34`;
- last row index `(h - h/14) / 14 - 1 = 5`, so six rows are visible.

The cell at (column, row) draws at `x + column * 8, y + row * 15`, plus the
glyph anchor (`0x428A10`).

### Parsing and wrapping

The file opens in text mode (`"rt"`), so CR-LF becomes LF. `~N` sets the
colour of the bytes that follow; N must be 0-7 or the game asserts. Text
starts in colour 0.

Wrapping works line by line:

- **Scan.** Each scan reads up to `columns + 1` bytes. It remembers the last space, and stops at a newline.
- **Copy.** The bytes before that break are copied into the grid line, which is padded with spaces.
- **Stale break.** A scan that finds no break keeps the previous one and never finishes. The port rejects such a file.
- **Trailing lines.** Lines keep coming from the blank padding after the text, until the copied byte count exceeds the bytes read.

`credits.txt` makes 72 lines (2448 cells). The last 8 are blank.

### Drawing

A glyph is the byte minus 0x1F. Anything past 0x7A, including control bytes, becomes frame 1, the blank.

### Stepping

A step runs when more than 5 ms (`timeGetTime`) have passed since the last one.

1. **Typing.** Five cursors k = 0..4 sit on cells `C - k`. Each draws its cell at brightness 0x1F, 0x1C, 0x18, 0x14 and 0x10. Visible cells request sound 0x84, BEEP.WAV.
2. **Scrolling.** When the lead cursor leaves row 5, the window clears to black (`0x42C044`). Rows 0-4 are redrawn one line lower, all at 0x10. The rest of the trail then redraws, so on that step the newest cell shows 0x10.
3. **Restarting.** After the step where `C - 4` reaches the cell count, mode 2 clears the window (state 0) and typing restarts at C = 4.

A pass is therefore cell count + 1 steps.

The original's menu loop is unthrottled under cnc-ddraw (vsync off). A dc16
burst capture measured about 148 characters per second, which is the
5 ms rule plus loop overhead. The port takes one step per 6 ms of wall time
from when the menu opens.

The port's model is `TeletypeText`, drawn by `MainForm.Credits.cs`. The
checks pin the window to two captures:

- the 2026-10-04 dc16 capture, with lines 37-42 on screen;
- `menu-130543.png`, with lines 55-59 over a blank line 60.
