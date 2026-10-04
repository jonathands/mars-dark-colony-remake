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

For a repeatable Single Player War lobby check, add `--single-player-war`:

```powershell
dotnet run --project src/DarkColony.App -- --data "..\Dark Colony" --single-player-war
```

Or double-click [`run-war-lobby-debug.cmd`](run-war-lobby-debug.cmd).

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
chain. Ordered GPU sprite, tile, bitmap-glyph, and primitive commands compose
the fixed 640x480 target before a point-sampled presentation pass. The legacy
GDI scratch surface is non-presented fallback infrastructure only; it is not a
full-frame upload path. The encyclopedia reads the original `encyclo.txt` identity catalog,
supports its three categories and list navigation, and previews confirmed FIN
animations where their native identity is mapped.

The first in-game harness is available through **Single Player War** or a
campaign start. The War setup is the recovered 640×480 `multie` screen over
`tcpwait`, not `shumane`: it presents eight player rows, an alphabetical list
of complete original `scenario/mplayer` SCN/MAP/PTH triplets, and the native
Storage/Artifacts/Vents/P7/Rank controls. Press **READY** to enter the selected
map. It renders a 516×458 native terrain viewport and composites the original
gameplay HUD above it. The engine parses the complete SCN corpus, seeds
ordinary placements as deterministic world entities, and applies
executable-confirmed building footprints to static occupancy when structures
are introduced. Special vent records and unresolved entity kinds remain
separate so the port does not invent blockers. Gameplay supports direct unit
clicks, box selection, orders, drag panning, keyboard navigation, and edge
scrolling.

War maps retain their original per-team roster data. The faction selected on
the lobby's Human player row selects the matching enabled SCN team; a missing
faction is rejected rather than remapped. The launcher carries validated lobby
settings with that map/team selection. SCN starting P7 and team/race state feed
the local deterministic simulation; the native scenario-start handoff for
power-ups and rank remains unrecovered.
The SCN team format labels fields *after* their values; the port decodes that
layout so race, starting resource, AI profile, and colour are not shifted by
one field. The New Game leader-name field accepts up to 17 letters, numbers,
and spaces; that persistent value appears in the lobby's player-name field.

Campaign starts now load `human01` or `alien01` and draw their ordinary SCN
actors using positional `gamestat.txt` identity plus exact `<CODE>STAND…` FIN
animation matches. World composites retain their FIN logical origins and sort
by cell Z then X before the HUD is applied. Press **F3** during gameplay to
toggle `entity ID · code · FIN` labels. Training starts retain the first
training maps, whose SCNs contain only special vent records.

Campaign launch first presents the recovered native `storye` narrative layout:
its original `story` background, scroll arrows, and Back/Next geometry remain
at the 640×480 source coordinates. The briefing comes from the mission's
original `.txt` file; **Next** enters the map and **Back** returns to race
selection. Trigger message and outcome texts are loaded but mission scripts
are not yet executed.

During gameplay, the arrow keys pan the camera in 16-pixel increments within
the decoded MAP bounds; holding the pointer in the eight-pixel viewport border
also scrolls at the fixed simulation cadence. Left-dragging terrain by more
than eight pixels pans the map and captures the pointer until release. A short
left-click on visible actor art selects the topmost depth-sorted local actor;
dragging a box selects local actors within it. The cyan ground marker remains a
presentation diagnostic while original selection masks are still unresolved.

Right-clicking a map cell with an actor selected submits a deterministic
next-tick `MoveIntent`; the route uses decoded PTH regions plus local
passability, reservations, interpolation, occupancy updates, and blocked-path
repair. Hold **Shift** while right-clicking to append a waypoint. Press **S**
to stop the selected actors and discard their active and queued movement.

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
Across all 101 missions the authoritative simulation seeds 4,608 actors. Ordinary SCN
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
- **Shift+left-click** adds/removes a unit from the selection.
- **Shift+drag** makes an additive selection box; ordinary left-drag remains map panning.
- **Right-click** terrain to issue deterministic move orders; selected units receive distinct nearby formation cells.
- **Shift+right-click** appends a waypoint instead of replacing the active order.
- The recovered right-hand HUD controls provide **Stop**, **Move**, **Move &
  Attack**, and persistent **Waypoint** mode. Waypoint mode makes each
  right-click append; each actor has the native maximum of eight queued
  destinations, and consecutive duplicates are ignored.
- The top HUD tabs follow the recovered exclusive groups: **Build** maps the
  faction troop/building catalog, **Research** maps upgrades, and **Options**
  maps pause/menu and local alliance controls. Build switches back to unit
  commands when a mobile unit is selected. Paid building drops, troop
  production, research completion, P7 reservations, and faction-matched
  footprint validation run through deterministic engine intents.
- **Move & Attack** directly targets a hostile actor, or attack-moves toward
  terrain while acquiring visible hostiles. It turns the actor, launches a
  simulation-owned projectile, applies original weapon-class/armor-class
  matrix damage, plays recovered effects/sounds, and destroys actors at zero
  HP. Projectile lifetime and autonomous target reacquisition remain
  provisional.
- Contextual fifth-slot commands are live where their rule is recovered:
  Exploiter/Slug deploy to Petra-7 vents, Engineer/Sloom deploy faction mines,
  Turret/Xenowort deploy into their armed static forms, and BEON/ZISP use
  **Heal** against a damaged cooperative unit. Heals use the original class-7
  `mbullet` formula, recovered firing animation, cursor, and sound; their
  native cadence and exact target range are still untraced.
- **Steal Money** converts Cyborg/Psy-raider into the recovered static
  SARGSTL/PSYCSTL stance with its original deployment animation and sound.
  Campaign-authored rules now intercept 50% of a nearby hostile miner's vent
  income without requiring visibility. Exact native range and tie-breaking
  between multiple stealing units remain explicit provisional policies.
- A deployed Turret/Xenowort is a static combat unit: its selected HUD exposes
  **Stop** (clear an explicit target) and **Attack** only. It can directly
  target a hostile actor but cannot receive move, waypoint, or Build orders.
- Selected-unit and structure panels show live HP, effective weapon, movement,
  current day/night sight, and completed weapon/armor research level.
- **S** cancels active and queued movement for the selected units. **M** selects
  Move mode and **W** selects Waypoint mode, matching the source button
  dictionary annotations.
- **Esc** first cancels the active map-target mode while retaining selection;
  the next press clears selection. A paid pending building drop is kept active
  because no native refund/cancel rule has been recovered.
- **Left-drag** or use the arrow keys to pan.
- **F3** shows resolved entity/animation identity; **F4** shows PTH regions.

Move orders chain through any number of 32-step packed segments. A temporarily
blocked unit first rebuilds its local route around the current occupancy grid;
if no route is available it waits four simulation ticks before retrying. The
compact top-left gameplay readout shows the selected unit, live
health/movement/sight fields, research level, and queued-waypoint count.

Neutral SCN team `-1` rows are now treated as autonomous spawn groups rather
than single placed sprites. The installed corpus contains 561 such groups and
requests 1,767 nature actors, exclusively Salamander, Bat, Renat, Spider, and
Grub entity IDs. Initial members receive internal team 9 and occupy distinct
movement-class-specific cells near the group origin. Members issue deterministic
eight-tick wander orders; native population-maintenance rules remain untraced.
