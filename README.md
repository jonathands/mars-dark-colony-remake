# Dark Colony .NET port

A clean-room reimplementation of **Dark Colony**, the 1997 real-time
strategy game published by Strategic Simulations, Inc., in C# on .NET 8 and
Direct3D 11. Its rules come from reading the original `dc.exe`. The
original's files stay on the player's disk: the port reads the installation
at runtime, and no file from it is in this repository.

It plays the Human and Gray campaigns, the training missions, and Single and
Multi Player War. The computer player, mission scripts, day and night, the
fog of war, music, videos and the encyclopedia are ported too.

How it was made: [docs/HOW_THE_PORT_WAS_MADE.md](docs/HOW_THE_PORT_WAS_MADE.md).

![A Single Player War in the port, in a 1280x720 view: the fog of war fades into the unexplored map](docs/images/port-expanded-view.webp)

## Screenshots

The pictures in [docs/images](docs/images) show the port, and the original
for comparison, running on the author's own copy of the game. They are
screenshots only; the game's files are not in the repository.

### The port

| | |
| --- | --- |
| ![Main menu](docs/images/port-main-menu.webp) | ![New campaign: choosing a race](docs/images/port-new-campaign.webp) |
| Main menu | New campaign: the race screen and its portraits |
| ![Single Player War lobby](docs/images/port-war-lobby.webp) | ![Encyclopedia](docs/images/port-encyclopedia.webp) |
| Single Player War lobby | Encyclopedia |
| ![A War by day](docs/images/port-war-day.webp) | ![The same War at night](docs/images/port-war-night.webp) |
| A War by day | The same base at night: the ground greys, the units keep their colours |
| ![A Gray Atril's ground attack at night](docs/images/port-night-explosion.webp) | |
| A Gray Atril's ground attack at night: the blast lights the ground around it | |

### The original and the port

The original pictures are captures of `dc16.exe`, Take 2's 16-bit-colour
build, in a window. The scenes are alike, not identical.

| Original | Port |
| --- | --- |
| ![The original's base at night](docs/images/original-night.webp) | ![The port's base at night](docs/images/port-war-night.webp) |
| Night: grey ground, coloured units | The same rule, from `dc.exe`'s terrain pass ([day-night.md](docs/reverse-engineering/day-night.md)) |
| ![The original's fog fading into black](docs/images/original-fog-edge.webp) | ![The port's fog fading into black](docs/images/port-war-day.webp) |
| The explored ground fades into black over about a tile | The same fade, rebuilt from the corner ramps of `0x453B94` ([vision.md](docs/reverse-engineering/vision.md)) |

### How it got there: before and after

Each fix came from a play-testing report, was traced in `dc.exe`, and went
in with a check ([docs/GAMEPLAY_FIXES_PLAN.md](docs/GAMEPLAY_FIXES_PLAN.md)).
These pairs come from the same saved game or the same animation frames,
before and after the fix.

**Fog of war and night** (items 26 and 29). Before, unexplored ground was cut
out in black squares and the night did not show. After, the ground fades
into black, and the night greys it.

| Before | After |
| --- | --- |
| ![Before: hard black squares, full colour at night](docs/images/fix-fog-night-before.webp) | ![After: a soft edge and the night tint](docs/images/fix-fog-night-after.webp) |

**Ground out of sight** (item 26). Before, ground the player had explored
stayed fully lit. After, ground outside every unit's sight darkens to 10/16,
as the original shades it.

| Before | After |
| --- | --- |
| ![Before: explored ground fully lit](docs/images/fix-explored-before.webp) | ![After: ground out of sight darkened](docs/images/fix-explored-after.webp) |

**Explosions** (item 23). Two FIN draw types read blend tables from the
tileset's `.rmp` instead of covering the ground. They were drawn as grey and
white blobs (top); now they glow over the ground (bottom).

![Explosions before (top) and after (bottom)](docs/images/fix-explosions.webp)

**A VTOL leaving the factory** (item 25). The last frames of its build
animation hold the craft and its shadow, which the port drew as a second
VTOL (top). Draw type 2 is a shadow that darkens the ground (bottom).

![The VTOL before (top) and after (bottom)](docs/images/fix-vtol.webp)

**The Sarge's walk** (item 27). A table that turns a unit's facing into an
animation had been misread, so a Sarge walking west showed a one-frame
turning pose and slid (top). With the executable's values it walks (bottom).

![The Sarge walking west, before (top) and after (bottom)](docs/images/fix-sarge-walk.webp)

## Requirements

- Windows 10 or later, and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- An original installation (the folder with `dc.exe`, `gamestat`, `sprites`,
  `scenario`). The port looks in `--data <folder>`, then `DARKCOLONY_DATA`,
  then `..\Dark Colony` next to this repository.
- Optional: the CD image (`DCUK.cue`/`.bin`) next to the installation, for
  the soundtrack and the videos.

## Run

| How | What it does |
| --- | --- |
| `run-debug.cmd [flags]` | Build and run in Debug |
| `run-display\*.cmd` | The same, with a display preset (fullscreen 1080p, 2x window, ...) |
| `run-war-lobby-debug.cmd` | Debug, straight to the Single Player War lobby |
| `run-release.cmd` | Run the Release build (make it first, below) |

To make the Release build, a self-contained folder that runs without the SDK:

```powershell
dotnet publish src\DarkColony.App -c Release -r win-x64 --self-contained true -o artifacts\DarkColony-win-x64
```

## Playing

The original controls are the default: the left button selects and gives
orders, and the right button deselects. The MOUSE option switches orders to
the right button.

| Key | Action |
| --- | --- |
| Space | BUILD (sends the counted troops, buildings and research) |
| S, M, A, W | Stop, Move, Move & Attack, Waypoint |
| Enter | The selected unit's special |
| F1-F10 | Select the on-screen units of one type (Shift toggles, Ctrl ground only, Alt air only) |
| Arrows, screen edges, minimap | Scroll |
| Esc, Q | Cancel, then "REALLY QUIT?" |
| Alt+Enter | Window ↔ fullscreen |
| F10 (main menu), VIDEO (in-game OPTIONS) | Video panel |

## Beyond the original

None of these change the simulation:

- Display: window or fullscreen, native DPI, and larger gameplay views
  (network games stay 640x480). See [docs/DISPLAY.md](docs/DISPLAY.md).
- Saves and replays as command journals: [docs/SAVE_GAMES.md](docs/SAVE_GAMES.md).
- Multi Player War over TCP/IP with lockstep: [docs/NETWORK_AND_REPLAY.md](docs/NETWORK_AND_REPLAY.md).
- Diagnostics:
  - F12 labels the actors.
  - Ctrl+F12 (`--reveal-map`) lifts the fog.
  - Shift+F12 shows the path regions.
  - DEBUG GUIDES draws the P7 markers and paths.
  - `--perf` logs frame timings ([docs/PERFORMANCE.md](docs/PERFORMANCE.md)).
- A session log in `%LOCALAPPDATA%\DarkColony.Port\logs\latest.log`.

## Verify

```powershell
dotnet build DarkColony.Port.sln                        # zero warnings
dotnet run --project tests/DarkColony.Engine.Checks     # all checks, about 35 s
dotnet run --project tests/DarkColony.Engine.Checks -- --tag fast   # no installation needed
```

The checks include determinism goldens: scripted games hashed every update.
See [docs/TESTING.md](docs/TESTING.md). `tools/Run-Port.ps1` runs the app
unattended, posts input and captures the window.

## Layout

```text
src/DarkColony.Engine         simulation and original-data readers (no UI, deterministic)
src/DarkColony.Presentation   display layout, settings, HUD anchoring (platform-free)
src/DarkColony.App            Windows host: Direct3D 11, input, audio, video, screens
tests/DarkColony.Engine.Checks   the check runner (dependency-free)
tools/                        PowerShell: unattended runs, posted input, live tests
run-display/                  one-click display presets
docs/                         design, testing, reverse-engineering notes (docs/README.md)
```

Rules for contributors (and coding agents) are in [AGENTS.md](AGENTS.md) and
[docs/PORT_CONTRACT.md](docs/PORT_CONTRACT.md).

## Known gaps

- Fog of war: the original's remembered overlays (grid bits 10-17) are not
  drawn, and a city building seen earlier is drawn as it stands now, not as
  the player last saw it ([vision.md](docs/reverse-engineering/vision.md)).
- A War is lost with the last building in the port. The original counts any
  live unit; the change is the author's call ([war-session.md](docs/reverse-engineering/war-session.md)).
- The original's own save files are not read.
- Smaller items are open in [docs/GAMEPLAY_FIXES_PLAN.md](docs/GAMEPLAY_FIXES_PLAN.md)
  and in each reverse-engineering note's status table.
