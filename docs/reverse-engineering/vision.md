# Team visibility

How `dc.exe` decides which cells each player sees. Recovered on 2026-10-04,
implemented in `Simulation/ScenarioSimulation.Vision.cs` and
`Data/NativeVisionTrees.cs`.

## Storage and cadence

Each ground-grid word (`map + 0x804`) holds the actor index in bits 0-9 and
one "seen" bit per player: player p uses `0x40000000 >> p` (bits 30 down to
23). Bit 31 is set for the local player's display and is never cleared
(explored-terrain memory). Bits 10-17 hold a per-cell display value that
the local player's stamps clear (`and 0xFFFC03FF`). `0x454DB8` writes it: for
every live actor (not dying) on a cell the local player sees, whose runtime
record has a class of 1-15 at `+0x78`, the cell gets `class << 3 | team`.
Only two shipped entities have such a class. The minimap pass (`0x438A22`,
also `0x439C0D`) hands any non-zero value on a cell outside the local
player's current sight to `0x4387B0`, which queues a minimap point.
`value >> 3` goes through the class table (`0x454DAC`, dwords at
`0x516A50`) to an entity, and `value & 7` names the player whose colour
(`+0xC98`) the point takes; entities 37 (`POOP`) and 40 (`VENT`) take
colour 8 instead. `0x43A318` turns the colour into a palette index through
the byte table at `0x4F01CC`. So the value is the minimap's memory of vents
seen, not a terrain overlay. The writer skips a vent whose rate (`+0x32`)
is 0 (`0x454E25`).

The world update (`0x4196F4`) rebuilds the player bits in two places:

- when the clock `world + 0x52C` is still zero (`0x41988C`), that is on the
  first update;
- after the day/night update, every 16 updates (`0x419A30`:
  `world + 0x94C & 15`).

Each rebuild calls `0x4456F0`, which clears bits 23-30 of the whole grid,
then `0x44A6D4`, which stamps every actor. Between rebuilds every query
(the target selector `0x435570`, the steal search `0x417944`, the mission
condition `v(x,z,p)`) reads the last picture.

## Stamping (`0x44A6D4`)

1. Every live actor's revealed byte `+0xCA` is cleared.
2. An actor stamps if its state is not 0 (alive or dying), its team is 0-7,
   and it is not a placement waiting for contact (`+0xCB` 1 or 2, see
   city-and-economy.md).
3. **Radius.** `r = (w × night + (256 − w) × day) >> 8`, where day and night
   are gamestat values 4 and 5 and `w` is the lighting level
   `world + 0x540`. `w` ramps over the first `world + 0x538` ticks of each
   phase (`0x4199C1`): from 256 to 0 at dawn, from 0 to 256 at dusk. A dying
   actor (state 10) shrinks it to `(150 − t) × r / 150` (at least 1), with
   `t` its death timer. Only radii 1-12 stamp. The shipped sight values stop
   at 10.
4. **Tree.** `0x488FC8` holds one sight tree per radius. Nodes are
   `{ dx, dz, (children − 1) × 4, child[8] }`. For radii 1-10 the tree visits
   exactly the cells with dx² + dz² ≤ r², each once; a node's level is its
   depth (`0x47B098`). The walk starts at the actor's position cell. It goes
   into a node's children only when the cell lets sight through (MAP
   attribute bit 7) or the actor flies (movement class, gamestat value 13,
   nonzero). Out-of-map nodes stop their branch.
5. **Each reached cell.** It gets the player's bit, unless it has MAP
   attribute bit 8 and depth 2 or more. In the corpus, bit-8 cells (1.5 %)
   never have bit 7, so they also block sight: units see a cliff next to
   them, but not one farther away. A mine detector (gamestat value 16, runtime
   `+0x6C`: SARG, PSYC, ENGI, SLOM and the stealing stances) also sets bit
   `1 << team` in the `+0xCA` byte of any actor in the mine grid
   (`map + 0x1004`) on that cell, shaded or not.

The 16 stamping routines (`0x445A24`-`0x44A1E0`, table `0x44A694`) are
specializations of these flags. Bit 0 marks a viewer the local player
shares vision with, and adds bit 31 and the display shading. Bit 1 marks a
mine detector, bit 2 a viewer at least r cells from every map edge (no bounds
checks), and bit 3 a flier (no opacity test).

## The view: fog shading and what is drawn

The main view (`0x436190`) passes the local player's mask to the terrain
pass `0x453B94` and the sprite pass `0x4395D4`.

**Terrain.** `0x453B94` gives every cell of the view, and a one-cell margin
(clamped to the map edge), a brightness out of 16:

- 16 when the cell word has a bit of the mask (in sight);
- 10 when it has not (`mov edx, 0xA` at `0x453BAE`);
- 0 when bit 31 is clear (never seen).

Each tile corner is the mean of the four cells that meet there, rounded
down (`sar 2`). `0x4539F0` builds 17 x 17 ramps of 32 steps,
`(from * (31 - i) + to * i) / 31`, at `0x51462C`. A tile's left and right
edges ramp from its top corners to its bottom ones; each row then ramps
from the left edge to the right one. Every terrain pixel reads the colour
remap (`interface-text.md`) at its brightness, so ground out of sight shows
at 10/16 and fades into black over about a tile at the edge of explored
ground. This is the fade seen in `pedestal-exploiter-stationary.png`. The
colour part of the remap is the day/night tint, `lighting * 7 >> 8`, so the
night greys the ground. dc16 has the same code (`0x453D14`).

**Minimap.** `0x439F64` draws cells never seen black and cells out of sight
from a second copy of the minimap picture whose channels are 2/3 of the
first (`0x43A468`).

**Sprites.** `0x4395D4` walks the actors. The first 120 are the eight
players' 15 reserved slots, and slots 0-5 are city buildings:

- **City buildings.** One is drawn when any cell of its slot pattern
  (`0x444C80`, table `0x47ABE8`) is in sight, which also records its entity
  in the owner's `+0xC20` slot memory. Out of sight it is drawn as that
  remembered entity's standing frame, and not at all if it was never seen.
  Seeing an empty slot clears the memory.
- **Every other actor.** It is drawn when the cell of its position (`+0`
  and `+4` >> 8) is in sight. Another team's mine also needs the local
  team's bit in `+0xCA` (`0x43970F`).

The test covers every team, the local one included. `0x439D88` draws
effects and shots only on cells in sight.

## Consumers

- The target selector skips cells without the scanner player's bits
  (`player + 0x19C0`, which also holds allied players' bits; see the A3
  `vision` item). It skips another team's mine (gamestat value 15) unless the
  mine's `+0xCA` has the scanner's team bit (`0x435829`).
- The steal search needs the victim's cell to be visible to the stance's
  player.
- Mission condition `v(x,z,p)`.

## Port status

| Rule | Status |
| --- | --- |
| Rebuild cadence (first update, then every 16) | implemented |
| Radius blend by lighting level | implemented (`ObservationRange`) |
| Sight trees, opacity, flyers, shaded cells | implemented; trees read from `dc.exe` (`NativeVisionTrees`), MAP attributes from the scenario's MAP |
| Mine detectors and the `+0xCA` revealed bits | implemented (`RevealedTeamMask`) |
| Dying actors' shrinking radius | implemented (see combat-damage.md, Dying state) |
| Placements waiting for contact (`+0xCB` 1 or 2) | implemented: they do not stamp |
| Allied vision in `player + 0x19C0` | implemented (`ScenarioSimulation.Alliances.cs`; `vision`/`ally` set the bits, both players must agree) |
| Explored memory (bit 31) for the display | implemented as cells the local team has ever seen; the view and the minimap draw the rest black |
| Fog shading of the terrain (16 / 10 / 0, corner means, 32-step ramps) | implemented (`FogShading`, `ScenarioSimulation.ViewBrightness`). The port covers each tile with black at opacity 1 - b/16 after the terrain, instead of remapping the palette. Sprites are not shaded. The night tint and the lights are in `day-night.md` |
| Minimap out of sight at 2/3 | implemented (`BuildGameplayMinimap`, rebuilt every 16 updates) |
| Minimap memory of vents (bits 10-17) | not modelled: the port's minimap marks no vents |
| Actors in sight (`0x4395D4`) | implemented (`IsActorVisibleToTeam`, `MainForm.HiddenByFog`). A city building is tested on its slot's cells, anything else on the cell of its position, and mines also need a detector. City buildings, once seen, stay drawn out of sight; the port draws the building standing now rather than the remembered one. The local team's own actors are always drawn; the original also tests them, but their own stamps nearly always cover them. Before 2026-10-06 the port tested a moving unit's occupied cell, never hid structures and drew no shading, only black on unexplored ground |
