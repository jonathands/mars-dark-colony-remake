# Display modes

The original only ran at 640x480. The port can also run larger, sharp at the
monitor's native DPI, fullscreen, and with a larger gameplay view. Everything
here is a port enhancement (plan: [DISPLAY_MODES_PLAN.md](DISPLAY_MODES_PLAN.md)).
At window scale 1 with the classic view the port draws exactly the original
640x480 picture. None of it touches the simulation.

## How a frame reaches the screen

1. The app draws the frame at its **logical size** into an offscreen target.
   The logical size is 640x480, or the gameplay view on the gameplay screen.
2. `DisplayLayout` (in `src/DarkColony.Presentation`) places that picture on
   the **output**, which is the window's client area in physical pixels:
   - **Integer** (default): the largest whole scale that fits, centred
     between black bars, point sampled. Pixel-exact.
   - **Fit**: the largest scale that keeps the picture's shape. Its
     sharp-bilinear filter (`PSSharpBilinear` in `Sprite.hlsl`) enlarges
     each pixel by the whole part of the scale and blends only the seams, so
     pixels stay even.
   - **Stretch**: fills the output and distorts. Sharp-bilinear too.
   - An Integer output smaller than the picture is fitted instead.
3. The flip-model swap chain (`FlipDiscard`) always matches the client area,
   and is resized with it. Windows never stretches the window, because the
   app declares `PerMonitorV2` DPI awareness.

`Direct3DSurface` raises mouse events in logical coordinates
(`DisplayLayout.ToLogical`: the logical pixel under the output pixel's
centre). A point on a bar maps to the nearest picture edge, so the scroll
edges work from the bars too. The rest of the app never sees physical pixels.

When the picture is not shown 1:1, menus draw the game's own cursor
(`curs.fin` DEFAULT) so it scales with the picture. Gameplay always draws its
own cursor.

## Windowed

The window shows the classic picture times the **window scale**. Scale 0
(automatic) follows the monitor's DPI, so 150% gives 2x. Every scale is
limited to what fits the monitor's work area. The window can be resized or
maximized, and the picture follows the scaling mode.

When the window moves to a monitor with another DPI, it keeps its physical
size. With the automatic scale, it instead takes the new monitor's scale.

## Settings

`%LOCALAPPDATA%\DarkColonyPort\display.json` holds the port's display
settings (`DisplaySettings`). A missing file gives the defaults; a broken one
gives the defaults and a log line.

Command-line flags override the file for one run, without saving:

| Flag | Meaning |
| --- | --- |
| `--windowed` | Windowed |
| `--fullscreen` | Borderless fullscreen |
| `--exclusive [WxH[@Hz]]` | Exclusive fullscreen, optionally in a display mode |
| `--window-scale N` | Window scale, 0 (automatic) to 8 |
| `--scale-mode integer\|fit\|stretch` | How the picture is fitted |
| `--view classic\|auto\|WxH` | Gameplay view |
| `--vsync on\|off` | Wait for the vertical blank |
| `--confine-cursor on\|off` | Keep the pointer in the window during play |

The log records the settings in effect (`Display:`). It also writes a
`Presentation:` line with the output, logical size and destination rectangle
every time they change.

## Automation

- `tools/Run-Port.ps1` and `tools/Run-NetworkPair.ps1` pass
  `--windowed --window-scale 1 --view classic`, so their coordinates are
  window coordinates whatever `display.json` says.
- `tools/Send-PortInput.ps1 -LogPath <log>` maps logical coordinates through
  the latest `Presentation:` line before posting them. `Run-Port.ps1` passes
  its log.
- `tools/Test-LiveConstruction.ps1` takes `-WindowScale N` and
  `-ScaleMode integer|fit|stretch`.

## Verified

- **Scale 1:** the classic picture at window scale 1 is byte-identical to the
  previous renderer in the War lobby and in gameplay. The only differing
  pixels are the title bar's maximize button, which is now enabled.
- **Scale 2:** at window scale 2, the gameplay capture reduced 2:1 equals
  the scale-1 capture. `Test-LiveConstruction -WindowScale 2` passes.
- **Fit:** a 1000x700 client gives a 933x700 picture between bars, and the
  mapped clicks reach the War lobby's READY and the Research tab.

Not verified yet:

- a DPI change between two monitors;
- a 150% monitor;
- window scale 3, which this 1080p monitor cannot fit.
