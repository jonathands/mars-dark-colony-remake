# Catalog sweep

`dotnet run --project tests/DarkColony.Engine.Checks -- --catalog-sweep`
plays through each race's whole build tree and prints a line per item. The
check "every building, troop, research and unit of both races builds, trains,
moves, attacks, uses its special and dies" runs the same sweep. It takes
about ten seconds.

## Setup

The sweep runs on `mplayer/j4play01`:

- Team 0 takes the race under test, and team 1 the other race.
- No team plays an AI profile, so only the sweep's own orders act.
- Team 0 starts with its headquarters only and receives 1,000,000 P7.

## Steps

Each race runs these steps in order:

1. **Buildings.** Every building item is bought in prerequisite order. It must rise in its city slot as the slot's entity, at full health, and count as built.
2. **Troops.** Every troop item is ordered. It must come out of its queue within 600 updates as an actor of its entity.
3. **Research.** Every research item is ordered from a structure that offers it, as the research tab would. It must complete, and a weapon or armor level must reach the unit.
4. **Units.** Each trained unit, then every other mobile entity of the race, spawned directly (commanders, artifacts, drones, critters, transports), goes through four tests:
   - **Move.** It is ordered eight cells away, to a cell it can reach. It must arrive.
   - **Attack.** An enemy infantry unit is placed three cells away, and the unit is ordered to attack it. An armed unit must damage it within 600 updates. An unarmed one must have the order refused as `Unarmed`.
   - **Special.** Each special must take effect:
     - Engineer and Sloom lay a mine.
     - Turret builder and Xenowort deploy their tower.
     - Cyborg and Psy-raider deploy and retract their steal stance, then fire Napalm or Disease.
     - Healers heal a damaged neighbour.
     - Harvesters attach to a vent and retract.
     - Commanders cast Inspire.
     - Ground Attack (BARR, ATRIL) fires the unit's own weapon at a point, and keeps firing.
   - **Death.** The unit dies, and its body must leave the grid.
5. **City teardown.** Every building is destroyed. Each one must stop counting as built, and the team can buy no more troops.

## Invariants

After every update:

- Actor ids are unique.
- No actor has more health than its maximum.
- No actor stands outside the map.
- No team has negative P7.
- Every live mobile unit holds its own cell (or the next one while it steps) in its movement class's grid.

## Known gaps

`CatalogSweep.KnownGaps` lists behavior the sweep finds missing whose native
rule is not yet recovered. The sweep prints each one as `KNOWN GAP` without
failing. It fails when a listed gap stops occurring, so that the list and its
document can be updated.

| Gap | Units | Document |
| --- | --- | --- |
| A range-1 weapon never fires | Human GRND, AIRD, DROA, SHRI; Gray SALY, AVII, RNAT, SPID, GRUB | [combat-range.md](reverse-engineering/combat-range.md) |

## Bugs it found (2026-10-04)

| Bug | Fix |
| --- | --- |
| After a robot factory or science lab was upgraded, the research items that name its first level could no longer be ordered. Once both slots were upgraded, this blocked the Human mech (REAP) upgrades, items 67-70, and the Gray Scythe Demon upgrades, items 41-44. The research source had to be exactly the level-1 building, while the native availability check `0x438220` accepts any live variant at least as high. | `ScenarioSimulation.StructureSatisfiesBuildItem` accepts higher variants. The research tab uses it too. |
| A level-2 upgrade whose only prerequisite is its level 1 was offered on no structure, so it could never be researched. This blocked items 64, 66, 76 and 78 (Human SCGM and BARR) and 46, 48, 50 and 52 (Gray ORTU and ATRIL). | `ScenarioSimulation.StructureOffersResearch` offers it where its prerequisite upgrade is offered. |
| A unit that drew an idle fidget and then received an attack order never turned toward its target, so it never fired. | A player order replaces the command stack, and the pending fidget goes with it. |
| BARR's and ATRIL's Ground Attack was refused, because value 29 names weapon 0. | State 18 fires their ordinary weapon at the point until another order (`0x417EFD`, `0x41806C`). |
