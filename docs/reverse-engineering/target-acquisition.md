# Automatic target acquisition

How units pick targets without an explicit attack order, recovered from
`dc.exe` on 2026-10-04 and implemented in
`Simulation/ScenarioSimulation.Acquisition.cs`.

## Command stack

Actor state 1 (`0x412654`) pushes command type 3, the idle command. Command
handlers are dispatched through the table at `0x4792B8`, index `20 + type`:
type 3 is `0x4148B0` (idle) and type 8 is `0x4157EC` (one path step). A
command record for type 3 holds the selected target (word +0), the health at
the last scan (word +2), and a miss counter (word +4).

## Idle handler `0x4148B0`

For an armed actor (weapon slot != -1), every time its wait has elapsed:

1. Scan with the ring selector up to the weapon's range (weapon +0x14). A hit
   starts the attack routine (`0x41481C` → `0x412D00`) and clears the miss
   counter.
2. Otherwise compare health with word +2. Lower health means the actor was
   damaged since the last scan: the miss counter resets. Word +2 takes the
   current health.
3. If the entity moves (runtime +0x0C, gamestat value 3, is nonzero), run a
   second scan with a radius chosen at `0x414B79`:

   | Scanner | Radius (rings) |
   | --- | --- |
   | Team 8 or 9 | 16 |
   | Player flag +0xBBC set (computer; the P7 multiplier path uses the same flag) | 16 |
   | Human player, entity +0x60 nonzero (flier) | no second scan |
   | Human player, damaged | 9 |
   | Human player, otherwise | 4 |

   A hit stores the hostile's position at actor +0x2E/+0x30 and pushes a move
   in mode 2 (`0x414CE4`).
4. Otherwise (`0x414C29`), draw once from the shared random stream; if its low
   nibble is zero and entity +0xDC (gamestat value 20) is zero, draw again and
   push the fidget, command type 4 with that byte (`0x412338`). Then push the
   wait, command type 3 (`0x412274`): 15 while the miss counter is below 3
   (incrementing it), otherwise 45. The handler returns 0, so the wait starts
   on the next update.

Unarmed actors take type-specific paths instead: vents (`0x413490`), mines
(`0x4131BC`), stealing stances (`0x413BC0`), healers (`0x413C20`). City
buildings (actors 0-119) run troop production (`0x414314`) from the same
handler.

The dispatcher (`0x4194FA`) calls the top command's handler from the table at
`0x479310` (type t at `0x4792B8 + (22 + t) * 4`) and dispatches again while
the handler returns nonzero.

- **Wait** (type 3, `0x4122C8`). Its record holds the counter and the health
  at the push. If the health changed (or a state is pending, `+0x36`), it
  pops and returns 1, so the idle record scans in the same update. Otherwise a
  zero counter pops and returns 0, and a nonzero counter is decremented. A
  wait of 15 therefore gives scans 17 updates apart, and 45 gives 47.
- **Fidget** (type 4, `0x412358`). It turns the actor toward the drawn bearing
  through `0x4120FC` (turn rate = gamestat value 2). The update in which the
  facing arrives pops the record and returns 1, so the idle record scans in
  that update. It sits below the wait, so the unit turns after waiting. The
  state handler `0x419238` mentioned in older notes is the state table's
  entry, not this command's.

Nothing in the idle handler runs while either lasts. Moves and
attacks the handler starts itself (approach, yield, acquired target) are
pushed above the idle record too. When they end, the record resumes with its
health snapshot and miss counter intact. A player order replaces the whole
stack.

## Yielding to a blocked ally

A path step that finds an allied actor in its next cell stores its own travel
direction in that actor's byte `+0x35` (`0x415795`, only when the byte is
`0xFF`). The blocker reacts the next time its idle handler runs. Movers are
checked after the first (weapon-range) scan: `0x414A99` for armed actors and
`0x4149C0` for unarmed ones. `0x412BC8` clears the byte and picks a
neighbor:

- Directions use the `0x479208` delta order (the port's `PathDirection`).
  `0x479268` and `0x479248` convert between it and compass rotation.
- Mode 1 (no hostile in range) tries rotation offsets `2, -2, 1, -1, 3, -3,
  0` (`0x479288`): sideways first, never back toward the mover
  (`0x4126A8`). The cell must have no occupant in the actor's grid, a
  nonzero PTH region (cell record `+0x0C`, filled by the PTH loader
  `0x442B7C`), and for a diagonal step one passable orthogonal neighbor. If
  every candidate fails, `0x412820` shuffles the same seven offsets with the
  shared random stream. In that pass it also accepts reserved cells and cells
  held by a live actor.
- Mode 2 (a hostile was found) tries `0, 1, -1, 2, -2` (`0x4792A4`,
  `0x412A50`), with no random fallback. The hostile is not attacked on that
  pass.
- A chosen cell becomes a plain move (mode 0) pushed by `0x414CE4`.

## Ring selector `0x435570`

- **Rings:** the table at `0x434090` holds `(dx, dz)` short pairs; `dx == 99`
  ends a ring. Ring r holds the cells with `r <= sqrt(dx² + dz²) < r + 1`, and
  ring 16 holds only the four axis cells. Rings 0 through the radius are
  walked in table order around the scanner's current cell.
- **Visibility:** only cells whose ground-grid word has the scanner team's
  visibility bit are considered (team 9 uses the union of teams 0-7). Another
  team's mine also needs the scanner team's revealed bit (`+0xCA`,
  `0x435829`). See [vision.md](vision.md).
- **Occupants:** each cell's ground, alternate, and mine occupants are
  candidates, in that order.
- **Rejected candidates:**
  - a mine-layer entity (value 15) of another team, unless its per-team
    revealed bit (actor +0xCA) is set;
  - an entity with runtime byte +0x00 set (gamestat value 32: trees, beacons,
    fuel, watchtowers, cameras);
  - team 8 or 9;
  - the scanner itself;
  - a non-hostile candidate (relation byte nonzero), unless the scanner's
    attack-anything byte (+0xD0) is set;
  - a candidate the weapon cannot damage (its `mbullet` cell is zero).
- **Score:** 50, or 150 if the candidate's first weapon slot is populated,
  plus 200 if its +0x60 byte is set (fliers and buildings).
  - Area weapons (weapon +0x1C nonzero) add +10 per hostile and -15 per other
    ground occupant in the candidate's 3×3 neighborhood.
  - Other weapons multiply by `(max(0x400 - health, 0 when below 0x400) +
    0x400) >> 11`, which is zero for every living target. In practice the
    first candidate in ring order wins.
- **Result:** the strictly highest score wins, so ties keep the earlier
  candidate.

## Path-step modes `0x4157EC`

Before each step of a move, the word at command +8 selects a mode:

| Mode | Behavior |
| --- | --- |
| 0 | Plain move (Move Only). |
| 1 | Scan weapon range; a hit starts the attack (Move & Attack). |
| 2 | Scan weapon range; a hit ends the move, returning to the idle command. |
| 3 | Chase a specific target; attack once it is inside weapon range. |
| 4 | Move until inside weapon range of a point. |

## Port implementation and adapters

- Idle acquisition runs for teams 0-7 and critter team 9. The SCN loader
  makes every player and team 9 mutually cooperative (see
  [city-and-economy.md](city-and-economy.md)), so critter scans find nothing
  but still draw from the shared stream. Players never auto-target teams 8/9,
  which matches the native filter.
- Computer teams are those with a nonzero SCN `%AI` profile; Single Player War
  marks every enabled non-local team as computer.
- Allied vision bits (`player + 0x19C0`) are not modeled yet: a scanner sees
  only its own team's bits.
- The mode-2 approach targets the closest free cell within weapon range of the
  hostile (the pursuit helper), because the port's local search has no partial
  routes to an occupied cell.
- Move & Attack scans weapon range with the ring selector at step boundaries.
  Its former observation-range nearest-hostile rule remains only for checks
  built without the ring table.
- Without the executable's ring table (synthetic checks), automatic acquisition
  is disabled.
