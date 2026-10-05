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

## Fullscreen

**Alt+Enter** switches between windowed and the last fullscreen kind used,
borderless unless exclusive was chosen. The change is saved like a Video panel
change.

- **Borderless** (default fullscreen): the window loses its frame and covers
  the monitor that holds it, at the desktop resolution. The picture is scaled
  onto the whole monitor.
- **Exclusive**: the monitor switches to a chosen display mode, then the
  borderless window covers it.
  - The modes come from `EnumDisplaySettings`, one per size with its highest
    refresh rate. With none chosen, it uses the desktop's mode.
  - The switch uses `ChangeDisplaySettingsEx` with `CDS_FULLSCREEN`, so it is
    temporary and never written to the registry.
  - DXGI's own exclusive state is not used, because it needs a top-level
    swap chain window and the port's surface is a child control
    (`SetFullscreenState` returned `DXGI_ERROR_INVALID_CALL`).
  - When the window loses focus, the desktop mode comes back and the window
    minimizes. When it returns, the chosen mode is set again. Closing the
    game restores the desktop mode too.
  - A mode the monitor does not have falls back to windowed, with a log line.

During play, the pointer is kept on the picture (`ClipCursor`) while the game
has the focus. This makes the scroll edges easy to reach. By default this
applies in fullscreen only. `--confine-cursor on` extends it to windowed;
`off` disables it everywhere, and the tools pass `off`.

`--display-cycle N` is a diagnostic for resource leaks. It switches windowed
→ borderless → exclusive N times, logs live textures, handles, GDI/USER
objects and private memory at every step, then closes. It never saves
settings.

## Video panel

The Video panel is the port's own. It is drawn with the OPTIONS popup's art
(`popp.spr`, the `mfonto7` font), and it opens:

- with **F10** on the main menu;
- with the **VIDEO** button the port adds to the in-game OPTIONS popup.

| Row | Values |
| --- | --- |
| MODE | WINDOW, FULLSCREEN (borderless), EXCLUSIVE |
| SIZE | Window: AUTO, 1X-8X. Exclusive: DESKTOP, then the monitor's modes. Fullscreen: the desktop |
| SCALING | INTEGER, FIT, STRETCH |
| VIEW | CLASSIC, AUTO, 800X600 ... 1920X1080 (network games always play classic) |
| VSYNC | ON, OFF |
| POINTER LOCK | FULLSCREEN (default), ALWAYS, NEVER |

The arrows step through each row. OK applies and saves; Cancel and Esc
discard. A new exclusive display mode asks "KEEP THIS DISPLAY MODE?": OK
keeps it, and Cancel, Esc or 15 seconds without an answer revert to the
previous settings. While the panel is open in a single-player game, the world
waits, as it does for OPTIONS. The row rules (labels, values, stepping, when
to confirm) are `DisplaySettingsEditor` in the presentation library.

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
| `--confine-cursor on\|off` | Keep the pointer on the picture during play, in every mode (unset: fullscreen only) |
| `--display-settings <file>` | Use another settings file (the tests use a throwaway one) |

The Video panel and Alt+Enter save the settings in effect, flags included.
The log records those settings (`Display:`). It also writes a
`Presentation:` line with the output, logical size and destination rectangle
every time they change.

## Automation

- `tools/Run-Port.ps1` and `tools/Run-NetworkPair.ps1` pass
  `--windowed --window-scale 1 --view classic`, so their coordinates are
  window coordinates whatever `display.json` says.
- `tools/Send-PortInput.ps1 -LogPath <log>` maps logical coordinates through
  the latest `Presentation:` line before posting them. `Run-Port.ps1` passes
  its log.
- `tools/Test-LiveConstruction.ps1` takes `-WindowScale N`,
  `-ScaleMode integer|fit|stretch` and `-Fullscreen`.
- `tools/Test-VideoPanel.ps1` drives the Video panel with posted input,
  using a throwaway settings file:
  - FIT and VSYNC OFF are applied and saved;
  - an exclusive mode is left unanswered and reverts after 15 s;
  - borderless fullscreen, with the panel reopened over the scaled picture.

  `-NoExclusive` skips the step that switches the monitor's mode.
- Alt+Enter cannot be posted: WinForms reads Alt from the real keyboard
  state.

## Verified

- **Scale 1:** the classic picture at window scale 1 is byte-identical to the
  previous renderer in the War lobby and in gameplay. The only differing
  pixels are the title bar's maximize button, which is now enabled.
- **Scale 2:** at window scale 2, the gameplay capture reduced 2:1 equals
  the scale-1 capture. `Test-LiveConstruction -WindowScale 2` passes.
- **Fit:** a 1000x700 client gives a 933x700 picture between bars, and the
  mapped clicks reach the War lobby's READY and the Research tab.

- **Mode switches:** 20 cycles (60 switches, 20 display-mode restores)
  leave the process steady at about 415-420 handles, 25 GDI / 28-30 USER
  objects and 40-44 MB. The first frame alone loads 123 cached textures.
- **Exclusive:** `--exclusive 1280x720` switched the monitor to
  1280x720@60. The picture sat at 1x between bars (Integer), mapped clicks
  worked, and the desktop mode came back on close. `--exclusive 1234x567`
  fell back to windowed.
- **Borderless:** 1920x1080 shows the 2x picture centred.
  `Test-LiveConstruction -Fullscreen` passes, and a pointer held at the
  right edge scrolls the view.

Not verified yet:

- a DPI change between two monitors;
- a 150% monitor;
- window scale 3, which this 1080p monitor cannot fit;
- a second monitor: borderless and exclusive pick the monitor that holds
  the window;
- alt-tab with exclusive fullscreen by hand (posted input cannot take the
  focus away).
