# Team visibility

How `dc.exe` decides which cells each player sees. Recovered on 2026-10-04,
implemented in `Simulation/ScenarioSimulation.Vision.cs` and
`Data/NativeVisionTrees.cs`.

## Storage and cadence

Each ground-grid word (`map + 0x804`) holds the actor index in bits 0-9 and
one "seen" bit per player: player p uses `0x40000000 >> p` (bits 30 down to
23). Bit 31 is set for the local player's display and is never cleared
(explored-terrain memory). Bits 10-17 hold a per-cell display value that
the local player's stamps clear (`and 0xFFFC03FF`). The terrain pass
(`0x438A22`) hands any non-zero value on a cell outside the local player's
current sight to `0x4387B0`. That routine reads `value >> 3` through
`0x454DAC` and `value & 7` as an index into 0xE30-byte records (`+0xC98`): a
remembered overlay, not a brightness.

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
| Allied vision in `player + 0x19C0` | pending (A3 `vision` / `ally`) |
| Explored memory (bit 31) for the display | implemented as cells the local team has ever seen; the main view blacks out unexplored cells |
| Explored-edge look | not recovered. The minimap (`0x439FF8`) draws unexplored cells black, cell by cell, as the port does. The native main view instead fades into black over roughly 40-60 px (`pedestal-exploiter-stationary.png`, lower left). Bit 31 is only tested by the minimap and the actor code, and bits 10-17 are not that fade, so its source is still unknown. The port keeps hard 32-px cells |
| Remembered overlays (bits 10-17) | not modelled |
