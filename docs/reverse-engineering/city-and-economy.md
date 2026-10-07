# Player cities and passive income

How the SCN loader builds each player's city and how the city gates Petra-7
income. Recovered from `dc.exe` on 2026-10-04 and implemented in
`Simulation/ScenarioSimulation.City.cs`, `Scenario/ScenarioDefinition.cs`
(`ScenarioTeam.CityOrigin`, `CitySlots`), and
`World/BuildingFootprintCatalog.cs`.

## SCN team block

The loader `0x41B920` reads these lines for each of the 8 players. `edi`
points at the player record `world + 0xB98 + player * 0xE30`:

| SCN | Runtime | Evidence |
| --- | --- | --- |
| `%AISlots` line 1 `x z` | player `+0xBCC/+0xBD0` | `sscanf` at `0x41C04F` into `edi+0x34/+0x38` |
| `%AISlots` line 2 `x z` | player `+0xBC4/+0xBC8` (city origin) | `sscanf` at `0x41C07E` into `edi+0x2C/+0x30` |
| `%City` line 1 | five `level health` pairs for slots 0-4 | loop before `0x41C3F0` |

- If line 1 is `0 0`, the loader copies the city origin into it. In the
  multiplayer maps, the team's commander (entity 69) is placed on line 1.
  Line 1 has two readers. `0x41EBD8` centers the local player's starting
  view on it (`0x499068`/`0x499070` = `x << 8`, `z << 8`; the view is clamped
  to 8 cells from the left/right edges and 7 from the top/bottom). The AI's
  region search `0x4571AC` uses the city origin as home and falls back to
  line 1 when the origin is zero (`0x457210`).
- A slot is built only when the origin X (`+0xBC4`) is nonzero, the level is
  positive, and (in network sessions) the player is in the session. Level `n`
  selects build variant `n - 1`. Health `-1` means the entity's gamestat
  health (`0x444C3C`). The slot health is stored at `+0xBD4 + slot * 4`.
- The loader also sets the passive income rate (`+0x19B4`) to 3 and the
  visibility mask (`+0x19C0`) to the player's own bit.

## Slot buildings `0x444F14`

The loader calls `0x444F14` for all 8 players × 15 slots, before any SCN
placement. Each built slot becomes actor `player * 15 + slot` (actors 0-119):

- Entity: build table `0x47AFA8`, indexed by `race * 0x78 + variant * 0x3C +
  slot * 4`.
- Position (8.8 raw): `origin * 0x100 + offset * 8`. The per-slot offsets
  come from `0x47AB70` (x, z dwords in 1/32 cell). For slot 0 (the HQ) the
  offset is `(-64, 15)`.
- Footprint: up to eight `(dx, dz)` cell offsets from the origin
  (`0x47ABE8 + slot * 0x40`). Each cell is written into the ground grid
  (`map + 0x804`) with the actor index. The loader reports an error if a
  cell is already owned by another actor, or if its terrain cell lacks
  `load` bit 31.
- `load` is the terrain array the MAP loader `0x453320` builds as
  `(foreground << 11 | background) | attribute << 22`. Bit 31 is therefore
  MAP attribute bit 9. Its rows are in file order, and the city check indexes
  them as `[ysize - 1 - z][x]`, the same mirror the port uses for world Z.
  The check "every built SCN city slot lies on MAP pedestal cells" confirms
  this for all 1960 built-slot cells in the corpus. Attribute bit 9 is the
  hexagonal pedestal painted into the MAP.

SCN placements are then created from actor `0x98` onward by `0x41AF14`.
The actor constructor takes the value column as the initial health unless it
is -1 (`0x41B339`; the corpus uses -1 for 2327 placements and 20-600 for the
rest), and the flag column as byte `+0xCB` (`0x41B321`). Units created during
play (`0x41B634`) get -1 and 0. The constructor (`0x41B3D1`) then writes
every actor outside team 8 into one grid cell: the mine grid for a mine
(runtime `+0x68`), otherwise the ground or alternate grid by movement class.
Speed plays no part, so static objects such as alien01's Salad shooters,
beacons, and crates block their cell and can be hit; the port had left
speed-0 placements off the grids.
Actors 120-151 are not used by the loader. Before creating a player
placement, the loader (`0x41C4E3`) compares the entity's race (gamestat
value 1, runtime `+4`) with the player's race (`+0xBB8`). If they differ and
the entity has a counterpart (value 31, runtime `+0x114`), it places the
counterpart instead. Example: a Marine placed for a Gray player becomes a
Gray trooper. In the corpus this changes 147 placements: 142 in the desert
multiplayer maps (`d*play*`), 4 in `test/htrain7` and 1 in `human/human01`. That
routine writes its cell into the ground grid without testing it, so a
placement inside a footprint takes that cell. The port appends city actors
after the placements, which keeps placement instance IDs stable. It claims
only the footprint cells no placement took, and seeds critter groups after
the cities.

## The tower in the centre of the city

The SCN loader reads the first `%City` line as (level, health) for slots 0-4.
While it does so (`0x41C1AA`), it gives slot 5 health 1 and variant 0 to
every player whose city origin has both values nonzero and who takes part in
the game (any game that is not a War, or an occupied War position). The same
loader then calls `0x444F14` for all 15 slots of all 8 players, and a slot
with nonzero health gets its building.

Slot 5 is `TOWR` (entity 81), the pylon in the middle of the city's pad,
with `TOWRSTAND0` in `towr.fin`. Its armour class 8 has a zero damage-matrix
column, so no weapon harms it despite its health of 1. The War end test skips
it (`0x40DE5C`).

The port seeds it after the team's slots 0-4. An empty War position has
`ScenarioTeam.InWarSession` false and gets none.

## Vent rates

An SCN vent record has five fields, `x z 40 a b`. The loader reads it with
the six-field placement format, so `a` lands in the team column and `b` in
the health column. The vent becomes a team-8 actor with:

- reservoir (`+0x0C`) = `b` x stat (2,0) >> 8;
- per-pulse rate (`+0x32`) = `a` x stat (1,0) >> 8;

and the map's live-mine bit is set. The two session stats are the War
lobby's P7 Flow and P7 Quantity, in steps of 25% shifted left by 6, so 100%
is 256 and the SCN values apply unchanged (see single-player-war-lobby.md).
Outside a War they stay 256. The corpus rates are 0 (437 vents, idle until a
script's `newrate`), 15, 20, 25, and a few others.

The producer `0x413A26` pays the rate when the strict `remaining - rate > 0`
test passes. It multiplies the rate by the owner's `+0x19B8` (8.8) when
`+0xBBC` marks a computer player. That multiplier is `session[0x14C4 + p *
4] << 8 / 100` (`0x401799`) and is not decoded, so the port uses x1. This
explains the original's +23 per pulse observed with one Exploiter on a
rate-20 vent (j4play01): 20 from the vent plus the passive 3.

## Erupting and stopped vents

The vent's own update (`0x413490`) runs every update. It drives the vent's
animation state (actor `+0x14`: animation, frame `+4`, delay `+5`, mode
`+6`) through `0x42630C`, which restarts the animation at frame 0 when the
animation or the mode changes. Mode 0 loops, mode 1 plays once and then
becomes 2, and mode 2 is stopped.

- A vent with no rate (`+0x32 == 0`) sets mode 2 and returns (`0x4134DD`).
  It runs no countdown, so a harvester standing on it never deploys.
- A stopped vent with a rate plays again from frame 0 when its cell is
  empty or holds an `EXPL` or `SLUG` (`0x41361E`). It then stores 50 in
  the countdown and returns, so a waiting harvester starts over.
- Anything else on the cell resets the countdown to 50. An `EXPL` or `SLUG`
  counts it down; at 0 it deploys (see unit-special-commands.md), and the
  vent stops (`0x4136F9`).

The sprite pass draws nothing for an actor whose animation is stopped:
`0x426334` returns 0 in mode 2, and `0x4397A7` skips the actor. An
erupting vent plays `VENTSTAND0` from `vent.fin`. That is the `vent2` hole,
the `puff` steam and the `glit` glow (draw type 5), and the `smsp` light
(type 3). A stopped vent shows only the map's dark crater.

The port keeps the mode as `PetraVent.Erupting`, set by
`ScenarioSimulation.UpdateVents`, and draws erupting vents in the actors'
painter's order (`MainForm.DrawGameplayVent`). Before 2026-10-07 it drew no
vents at all, only the P7 debugging markers, and let harvesters deploy on
vents with no rate.

The sound the original plays when a vent erupts has not been found. It is
not a FIN hotspot, and the vent's update plays only the harvester's deploy
sound (`0x431DA8`).

## Income gates

- **Passive income.** The world update `0x419B2E` runs every 16 ticks
  (`world + 0x94C & 0xF`). Each player whose slot-0 health (`+0xBD4`) is
  nonzero gains `+0x19B4` (default 3) P7. Script action 12 (in `0x43D814`)
  changes the rate; the port does not have it until the mission script engine
  exists. The 1500 → 1506 step in the native War capture matches two pulses.
- **Vent income.** The vent producer pays the harvester's owner only while
  its `+0xBD4` is nonzero (`0x413B6A`), and then adds 1 to the owner's stat 5
  (`0x413B9C`, harvest pulses). The vent's reservoir loses the amount even
  when nobody is paid (`0x413BA9`). A linked stealing stance takes half the
  amount (`0x413A30`, gated on the thief's own headquarters at `0x413B31`);
  see [unit-special-commands.md](unit-special-commands.md).
- The port applies both gates only when the SCN declares cities, so
  synthetic engine scenarios keep their configured `PetraFlowRules`. The
  passive pulse follows `world + 0x94C` and the vent pulse the day/night phase
  counter `world + 0x530`; see "World update order" below.

## World update order

`0x4196F4` runs once per world update, and the port's `Step` follows its
order. Its caller (`0x41E1F9`) increments the update counter `world + 0x94C`
first. The update increments the clock `+0x52C` and the day/night phase
counter `+0x530` before anything reads them, so the update counter and the
clock equal `TickCount + 1` during a step. The order is:

1. Statistics recount.
2. Troop cap (`0x41E6AC`).
3. Relation rows from alliance bits.
4. Day/night phase change.
5. Every 8 updates: critter groups (`0x43FEAC`), then norm triggers.
6. Every 16 updates: passive income.
7. Actors, including vents (`+0x530 & 15`), production, Inspire countdown
   (`+0x530 & 15`), and ability charge (`+0x530 & 31`).
8. Projectiles.

## Team relations

The loader zeroes the 10×10 relation matrix at `world + 0x46F34`. It then
sets the diagonal through `0x41E7D8` and writes `[player][9] = [9][player] =
1` for players 0-7 (`0x41C00E`). In this matrix, 1 means cooperative and 0
means hostile. The matrix has these readers:

- `0x435570` (target selector) and `0x41707B` (hostile collector) accept
  only candidates whose relation is 0.
- `0x4423F8` (projectiles) ignores collisions with non-hostile actors.
- `0x41AEB4` (interface) treats every actor above team 7 as friendly to the
  local player.
- `0x4196F4` recomputes rows and columns 0-7 from the alliance bits on every
  world update (`0x4198D3`, after the troop cap and before day/night).

**Alliance and vision bits.** Two 8×8 bit matrices hold one byte per player:
alliance at `world + 0x471A0`, vision at `+0x471A4`. `0x41E7D8` sets or
clears one bit (set only when the value is 1). `0x41E820` answers true only
when both players have set each other's bit. Each update writes relation
[p][q] = mutual alliance for p, q < 8. It also sets the player's vision mask
(`player + 0x19C0`) to its own bit plus every mutual vision bit. Writers:

- the SCN loader (`0x41BF63`): a team's first eight `%TeamAllies` values set
  its alliance bits, and its own bit is set in both matrices. Every shipped
  SCN has all eight TEAM blocks and no alliance flags;
- `ally a b v` (`0x43D9ED`): relation [a][b] = v, then both alliance bits;
- `vision a b v`: both vision bits;
- the Allies panel packet (`0x41D7B3`): one bit, in one direction, of either
  matrix.

`ScenarioSimulation.Alliances.cs` implements these rules. Visibility queries
OR the stamps of every player in the viewer's mask (see
[vision.md](vision.md)).

`TeamRelationMatrix.CreateDefault` reproduces the loader's initial state.
Because critters are cooperative, team 9 now runs the idle command (its
scans find nothing but still consume the shared random stream). Players
cannot direct-attack critters, and their projectiles pass through critters.

## Status

| Rule | Status |
| --- | --- |
| `%AISlots` line order, origin fallback | confirmed (disassembly) |
| Slot entity, position, footprint, health | confirmed (`0x444F14`, pedestal corpus check) |
| Placement overwrites footprint cell | confirmed (`0x41AF14`) |
| Passive +3 per 16 ticks while the HQ stands | confirmed (`0x419B2E`, War capture) |
| Vent income requires the HQ | confirmed (`0x413B31`) |
| Players ↔ team 9 cooperative | confirmed (`0x41C00E`) |
| Passive vs vent pulse phase | confirmed: passive on `world+0x94C & 15` (the update counter, incremented by `0x41E1F9` before each update); vents on `+0x530 & 15` (the day/night phase counter, reset at each phase change) |
| Script rate changes (action 12) | confirmed; see [mission-triggers.md](mission-triggers.md) |
| Placement race substitution (`0x41C4E3`) | confirmed; check "the SCN loader swaps a placement of the other race for its counterpart" |
| Session slot gating | implemented (`WarSession.Apply`): in War, the loader (`0x41C155`) zeroes the city slots of every team outside the session (no occupied lobby row after the shuffle); see [war-session.md](war-session.md) |
| Role of `%AISlots` line 1 | confirmed: starting view center (`0x41EBD8`) and AI home fallback (`0x457210`) |
| Team without a city | decided from the executable: every building is a city slot at the origin, and the footprint routine `0x444C80` does nothing for a zero origin. In a scenario that declares cities, such a team's purchases return `NoCity`, and it cannot drop buildings or train troops. Scenarios without any `%AISlots` (engine fixtures) keep the port's free drop and immediate-spawn adapters (`UsesPortConstructionAdapters`) |
| Building purchase builds the slot at once (command 9) | confirmed; see [production-flow.md](production-flow.md) |
| Building prerequisite = live slot building of at least that variant (`0x438220`) | confirmed; completed building items follow each slot |
| Building sprite anchor | fixed: the actor renderer (`0x4398AB`) subtracts a city slot actor's slot offset (`0x444C58`, table `0x47AB70`) before queueing its layers, so building art hangs from the city origin's corner, not from the slot position. Separately, the world blit (`0x454751` culls a queued sprite to `[y - height, y]`) puts each layer's bottom row on its FIN Y; a sprite frame's own Y is only its place on the artist's canvas. Queued positions are `x >> 3` and `(height * 256 - z - 1) >> 3` (`0x436051`). The port drew from the slot position and added the frame Y, which put the HQ about two tiles low and left and every unit about three rows low. Verified on 2026-10-04 against the native capture `Dark Colony/screenshots/pedestal-exploiter-stationary.png`, with a jungle War HQ in the port on the same kind of pedestal. In both, the HQ stands on the upper-left plate of its hexagon cluster. The door-to-plate-corner offset is about (13, 87) px native and (17, 87) px in the port, measured by eye |
| Vent animation (`0x413490`): stopped without a rate or under a deployed harvester, not drawn when stopped, no countdown on a dry vent | implemented (`PetraVent.Erupting`, `UpdateVents`, `DrawGameplayVent`); the eruption sound is not found |

## Rescue and pickup placements

Recovered on 2026-10-04 and implemented in
`Simulation/ScenarioSimulation.Contact.cs`. The idle command (`0x4148B0`)
hands an actor whose `+0xCB` is 1 or 2 to `0x4140DC` and does nothing else,
so such an actor neither scans for targets nor takes orders, and vision skips
it. Every fourth phase tick (`world + 0x530 & 3`) it walks the 5 x 5 square
around its cell, x outer and z inner, and probes the ground grid and then the
alternate grid of each cell, skipping occupants whose own `+0xCB` is nonzero.

- **1, rescue** (81 corpus placements, captives and loose artifacts): the
  first player-0 occupant clears the byte, makes the actor a player-0 unit,
  adds 1 to the script word u(0) (`0x43FC24`), and plays sound (3, 7).
- **2, pickup** (163 placements, such as FUEL crates worth 20): the first
  occupant of players 0-7 adds the actor's health to that player's P7
  (`+0xBAC`), and the actor is killed (`0x416308`). No kill statistic changes.
