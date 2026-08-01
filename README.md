# Dark Colony .NET port

This is the compiled reconstruction of Dark Colony. It is separate from the
web-based research viewer in `../dc-port-26` and reads the original installed
game data at runtime.

The central architecture, gameplay-shape, and implementation-priority contract
is [`docs/PORT_CONTRACT.md`](docs/PORT_CONTRACT.md). New subsystems should follow
that document before relying on milestone notes or viewer-era approximations.

## Run

For a one-click Debug build and run, double-click `run-debug.cmd` or execute:

```powershell
.\run-debug.cmd
```

The equivalent explicit command is:

```powershell
dotnet run --project src/DarkColony.App -- --data "..\Dark Colony"
```

If `--data` is omitted, the app checks `DARKCOLONY_DATA` and then the adjacent
`../Dark Colony` directory. The original files are never copied into this port.

## Verify

```powershell
dotnet build DarkColony.Port.sln
dotnet run --project tests/DarkColony.Engine.Checks
```

The first milestone establishes a native 640×480 compiled menu host using the
original external GIF backgrounds and recovered control coordinates. The
opening `DCSS` sequence and `LARGEBUTTON`/`MEDBUTTON` controls are now composed
from the original FIN and SPR layers at runtime. Campaign race portraits and
the single-player commander portrait are likewise composed from their original
`hcar`/`acar` and `acom` animation data. Menu labels use the original
`mfonto5.spr` bitmap glyphs, declared offset 31, and shipped per-glyph metrics. Main-menu
buttons navigate to campaign/training race selection, load game, single-player,
encyclopedia, and network-option screens; unfinished actions report their next
engine dependency instead of pretending to work. The exact 66 ms deterministic
scheduler, installation boundary, and base weapon/armor matrix reader are also
active. See `docs/RECONSTRUCTION_CONTEXT.md` for the next implementation stages.

Presentation now runs through a Direct3D 11 device and double-buffered swap
chain. A shader samples the transitional frame texture into a fixed 640x480 GPU
render target, then a second point-sampled shader pass presents that surface.
The composed legacy-frame upload remains only while individual draw calls are
being migrated to the GPU sprite command path; this is intentionally documented
as transitional rather than a completed renderer. The encyclopedia reads the original `encyclo.txt` identity catalog,
supports its three categories and list navigation, and previews confirmed FIN
animations where their native identity is mapped.

The first in-game harness is available through **Single Player War** (choose a
map with **Previous**/**Next**, then **To Battle**) or campaign start. Single
Player War discovers complete original `scenario/mplayer` SCN/MAP/PTH triplets
at runtime and loads the selected scenario before gameplay is entered. It
renders a 516x458 native terrain viewport and composites the original gameplay
HUD above it. The engine now parses the complete SCN corpus, seeds ordinary
placements as deterministic world entities, and applies executable-confirmed
building footprints to static occupancy when structures are introduced. The
shipped scenarios contain no such ordinary building placements. Special vent
records and unresolved entity kinds stay separate so the port does not invent
blockers. Selection, camera movement, and simulation commands are the next
engine slice.

Campaign starts now load `human01` or `alien01` and draw their ordinary SCN
actors using positional `gamestat.txt` identity plus exact `<CODE>STAND…` FIN
animation matches. World composites retain their FIN logical origins and sort
by cell Z then X before the HUD is applied. Press **F3** during gameplay to
toggle `entity ID · code · FIN` labels. Training starts retain the first
training maps, whose SCNs contain only special vent records.

During gameplay, the arrow keys pan the camera in 16-pixel increments within
the decoded MAP bounds. Left-dragging the terrain by more than four pixels pans
the map and captures the pointer until release. A short left-click on visible
nontransparent actor art selects
the topmost depth-sorted actor and draws a provisional cyan ground marker. This
alpha hit test and marker are presentation diagnostics pending recovery of the
original selection masks; they do not mutate simulation state.

Right-clicking a map cell with an actor selected submits a deterministic
next-tick `MoveIntent` and draws a cyan target marker. This is deliberately an
ingress diagnostic only: it does not move the actor until PTH routing,
passability, native actor commands, and occupancy playback are connected.

Press **F4** during gameplay to overlay PTH diagnostics. Region-zero cells are
shaded red and region boundaries are cyan. This deliberately says “region
zero,” not “blocked”: only the bottom-up row conversion and 256×256 next-region
table are proven. Right-click diagnostics now report source/target regions and
whether their coarse chain reached the target, hit zero, cycled, or exhausted
its limit.

The engine also implements the recovered autonomous spawn-cell search: expand
around the SCN origin, scanning X then Z, until the movement-class-specific
grid accepts a cell. Ground class zero requires a nonzero PTH region and empty
ground occupancy; nonzero classes use alternate occupancy. This is not yet the
local movement pathfinder.

Right-click diagnostics now also draw a yellow local cell chain. The search
uses eight neighbors, permits executable-confirmed diagonal corner-cutting,
checks the appropriate occupancy grid, follows the coarse-region set for
ground actors, and emits at most 32 packed steps. It remains diagnostic because
the native priority costs and tie-breaking order are not fully recovered; no
actor position is changed.

The engine now contains the recovered per-step playback boundary as well. It
uses the executable's 2,048-scale cardinal vectors and 1,448 diagonal vectors,
integer speed/duration math, and authentic no-snap completion residue. A path
step atomically transfers occupancy to its destination before visual
interpolation. Contention produces an explicit blocked state; repair, yielding,
and jittered replanning are not implemented yet. The UI does not own or mutate
this authoritative movement state.

Gameplay now renders positions from an engine-owned `ScenarioSimulation`.
Right-click intents are consumed on deterministic ticks and mobile actors play
their diagnostic packed path with native occupancy/interpolation behavior.
Across all 101 missions the aggregate seeds 4,450 actors. Ordinary SCN
construction preserves the executable's overwrite behavior for deliberately
stacked records (for example LUNA formations), while autonomous groups still
search for distinct empty cells. MOVE-facing animation and blockage recovery
remain incomplete.

Moving actors now carry persistent 8-bit facing. Each tick turns along the
shorter wrapped arc by the entity's gamestat turn speed, and rendering applies
the recovered `((facing + 8) & 255) >> 4` 16-sector quantization. Active path
playback selects exact `<CODE>MOVE<sector>` animations when shipped. Even-only
families use a visibly labelled nearest-sector fallback pending recovery of the
native doubled animation-selector/mirroring rule; press F3 to audit the chosen
animation.

## Current unit controls

In a campaign mission, team-0 units are locally controllable:

- **Left-click** a unit to select it.
- **Right-click** terrain to issue a deterministic move order.
- **Left-drag** or use the arrow keys to pan.
- **F3** shows resolved entity/animation identity; **F4** shows PTH regions.

Move orders chain through any number of 32-step packed segments. A temporarily
blocked unit waits four simulation ticks and replans around the current
occupancy grid. The compact top-left gameplay readout shows the selected unit,
cell, facing sector, target, segment count, and wait state.

Neutral SCN team `-1` rows are now treated as autonomous spawn groups rather
than single placed sprites. The installed corpus contains 561 such groups and
requests 1,767 nature actors, exclusively Salamander, Bat, Renat, Spider, and
Grub entity IDs. Initial members receive internal team 9 and occupy distinct
movement-class-specific cells near the group origin. Wandering and population
maintenance are not active yet.
