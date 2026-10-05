# Dark Colony .NET port

A clean-room reimplementation of **Dark Colony**, the 1997 real-time
strategy game published by Strategic Simulations, Inc., in C# on .NET 8 and
Direct3D 11. Its rules come from reading the original `dc.exe`. The
original's files stay on the player's disk: the port reads the installation
at runtime, and nothing from it is in this repository.

It plays the Human and Gray campaigns, the training missions, and Single and
Multi Player War. The computer player, mission scripts, music, videos and the
encyclopedia are ported too.

How it was made: [docs/HOW_THE_PORT_WAS_MADE.md](docs/HOW_THE_PORT_WAS_MADE.md).

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

- Fog of war:
  - enemy structures stay drawn on explored ground (the original's remembered
    overlays are not decoded);
  - the soft edge of unexplored ground is not reproduced.
- FIN draw type 4 (glow layers such as GASY's `spon`) draws as an opaque blob.
- The original's own save files are not read.
- Smaller items are open in [docs/GAMEPLAY_FIXES_PLAN.md](docs/GAMEPLAY_FIXES_PLAN.md)
  and in each reverse-engineering note's status table.
