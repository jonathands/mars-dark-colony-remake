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

## Vent rates

An SCN vent record has five fields, `x z 40 a b`. The loader reads it with
the six-field placement format, so `a` lands in the team column and `b` in
the health column. The vent becomes a team-8 actor with:

- reservoir (`+0x0C`) = `b` x stat (2,0) >> 8;
- per-pulse rate (`+0x32`) = `a` x stat (1,0) >> 8;

and the map's live-mine bit is set. Both session stats are 256, so the SCN
values apply unchanged. The corpus rates are 0 (437 vents, idle until a
script's `newrate`), 15, 20, 25, and a few others.

The producer `0x413A26` pays the rate when the strict `remaining - rate > 0`
test passes. It multiplies the rate by the owner's `+0x19B8` (8.8) when
`+0xBBC` marks a computer player. That multiplier is `session[0x14C4 + p *
4] << 8 / 100` (`0x401799`) and is not decoded, so the port uses x1. This
explains the original's +23 per pulse observed with one Exploiter on a
rate-20 vent (j4play01): 20 from the vent plus the passive 3.

## Income gates

- **Passive income.** The world update `0x419B2E` runs every 16 ticks
  (`world + 0x94C & 0xF`). Each player whose slot-0 health (`+0xBD4`) is
  nonzero gains `+0x19B4` (default 3) P7. Script action 12 (in `0x43D814`)
  changes the rate; the port does not have it until the mission script engine
  exists. The 1500 → 1506 step in the native War capture matches two pulses.
- **Vent income.** The vent producer also requires `+0xBD4 != 0` for the
  harvester's owner (`0x413B31`).
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
  world update. Script action `0x43D9ED` (ally) writes the matrix.

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
| Network-session slot gating | deferred to goal 8 (networking): single-player sessions build every slot |
| Role of `%AISlots` line 1 | confirmed: starting view center (`0x41EBD8`) and AI home fallback (`0x457210`) |
| Building purchase builds the slot at once (command 9) | confirmed; see [production-flow.md](production-flow.md) |
| Building prerequisite = live slot building of at least that variant (`0x438220`) | confirmed; completed building items follow each slot |
| Building sprite anchor | open: the HQ draws about two tiles lower and left of the original captures |
