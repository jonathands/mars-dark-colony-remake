# Weapon range tests

Recorded on 2026-10-04, when the catalog sweep (`docs/CATALOG_SWEEP.md`) found
that no range-1 weapon ever fired in the port. Implemented the same day in
`ScenarioSimulation.IsAttackTargetInRange` and `FindAttackApproachCell`.

## `dc.exe`

The weapon table is at `0x4F0200` (stride 0x48). `+0x14` holds the
`weapstat.txt` range, and the loader (`0x43B935`) derives `+0x18` from it
without changing it. `dc.exe` runs these range tests:

| Address | Context | Test |
| --- | --- | --- |
| `0x435C14` → `0x435570` | Idle scan, and Move & Attack (path-step mode 1) | Ring selector up to the weapon range (`0x435C88`). Ring r holds the cells with r ≤ distance < r + 1, so ring 1 includes all eight neighbours. A hit calls `0x41481C` → fire `0x412D00`, with no further distance test. |
| `0x415AA5`-`0x415ACC` | Path-step mode 3, chasing an ordered target | Whole cells: `(x>>8)` and `(z>>8)` differences, squared, `< range²` (strict). In range, it fires through `0x41481C`; otherwise it keeps moving. |
| `0x415BE3`-`0x415C21` | Path-step mode 4, moving into range of a point | 8.8: `dx² + dz² <= range² << 16`. |
| `0x417FBC`-`0x417FD6` | State 18 ground special | 8.8: `dx² + dz² <= range² << 16`. Out of range, it pushes a mode-4 move. |
| `0x413056`-`0x413072` | Common fire `0x412D00` | No gate. The aim offset is cut to 95% (`×19×5/100`) until `dx² + dz² <= range² << 16`. |

## Attack loop

The fire routine `0x412D00` first turns the actor (`0x4120FC`) and returns
without firing while it is still turning. After a shot it pushes command 11
(`0x4121D8`): a reload wait of the weapon's rate (`+0x08`), or `+0x24` once the
burst count (`+0x34` against `+0x20`) is reached. Its handler `0x4121F8`
counts down and then pops, so the command below runs again:

- **Idle command.** It scans again, so a target stays under fire while it is in the rings.
- **Mode-3 chase.** It tests the whole-cell range again.

A chase whose path ends beside the target (the target's cell is occupied)
pops back to the idle command, and the idle scan fires at what its rings hold.
For a range-1 weapon the chase test never passes (a neighbour is at whole
distance 1, not below 1), so a melee unit always ends its chase that way.

## Port

`IsAttackTargetInRange` decides whether an attacker fires now:

- **A target from a ring scan** is in range while it owns a cell in the weapon's rings (`IsWithinWeaponRings`). This covers the idle acquisition (`IdleIssuedAttackTarget`) and Move & Attack (`AttackMoveDestination`).
- **An ordered target** is in range by the mode-3 whole-cell test. When the attacker stands still and no free cell inside that range remains, the ring test applies instead, as when the native chase hands over to the idle scan.

`FindAttackApproachCell` picks the closest free cell inside the chase range. When there is none, it falls back to the closest free cell from which the target lies within the rings.

State 18 uses the `<=` test (`IsGroundSpecialTargetInRange`).

Not modelled: the fire routine's aim shortening. It only changes a shot at a
target beyond the whole range, inside the last ring, and it keeps the shot's
direction. In the catalog sweep, every melee attacker damages its target.
