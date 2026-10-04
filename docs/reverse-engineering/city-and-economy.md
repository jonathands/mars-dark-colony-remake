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
  multiplayer maps, the team's commander (entity 69) is placed on line 1. What
  this point does during play has not been traced.
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

SCN placements are then created from actor `0x98` onward by `0x41AF14`. That
routine writes its cell into the ground grid without testing it, so a
placement inside a footprint takes that cell. The port appends city actors
after the placements, which keeps placement instance IDs stable. It claims
only the footprint cells no placement took, and seeds critter groups after
the cities.

## Income gates

- **Passive income.** The world update `0x419B2E` runs every 16 ticks
  (`world + 0x94C & 0xF`). Each player whose slot-0 health (`+0xBD4`) is
  nonzero gains `+0x19B4` (default 3) P7. Script action 12 (in `0x43D814`)
  changes the rate; the port does not have it until the mission script engine
  exists. The 1500 → 1506 step in the native War capture matches two pulses.
- **Vent income.** The vent producer also requires `+0xBD4 != 0` for the
  harvester's owner (`0x413B31`).
- The port applies both gates only when the SCN declares cities, so synthetic
  engine scenarios keep their configured `PetraFlowRules`. Both pulses
  currently share the port's `simulationTicks & 15` phase. The native passive
  counter is `world + 0x94C` and the vent counter is `world + 0x530`; their
  relative phase is provisional.

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
| Passive vs vent pulse phase | provisional (shared port phase) |
| Script rate changes (action 12) | not implemented (mission scripts) |
| Network-session slot gating, placement race substitution | not implemented |
| Role of `%AISlots` line 1 | unknown |
| Building sprite anchor | open: the HQ draws about two tiles lower and left of the original captures |
