# Weapon range tests

Recorded on 2026-10-04, when the catalog sweep (`docs/CATALOG_SWEEP.md`) found
that no range-1 weapon ever fires in the port.

## Port

`ScenarioSimulation.IsAttackTargetInRange` gates every shot at an actor target
(`FireAttackers`). It is a strict 8.8 test:

```text
dx² + dz² < (range × 0x100)²
```

An adjacent cell is exactly 0x100 away, so a range-1 weapon is never in range.
The pursuit helper (`FindAttackApproachCell`) applies the same strict test,
finds no approach cell, and the attacker stands still. Critters and drones
(Human GRND, AIRD, DROA, SHRI; Gray SALY, AVII, RNAT, SPID, GRUB) therefore
never attack.

## `dc.exe`

`dc.exe` runs five range tests, and none of them alone matches the port. The
weapon table is at `0x4F0200` (stride 0x48), and `+0x14` holds the
`weapstat.txt` range. The loader (`0x43B935`) derives `+0x18` from it but does not change it.

| Address | Context | Test |
| --- | --- | --- |
| `0x415AA5`-`0x415ACC` | Path-step mode 3, chasing an ordered target | Whole cells: `(x>>8)` and `(z>>8)` differences, squared, `< range²` (strict). In range, it calls the attack start `0x41481C`; otherwise it keeps moving. |
| `0x415BE3`-`0x415C21` | Path-step mode 4, moving into range of a point | 8.8: `dx² + dz² <= range² << 16`. |
| `0x417FBC`-`0x417FD6` | State 18 ground special | 8.8: `dx² + dz² <= range² << 16`. Out of range, it pushes a mode-4 move. |
| `0x435C14` → `0x435570` | Idle scan | Ring selector up to the weapon range (`0x435C88`). Ring r holds the cells with r ≤ distance < r + 1, so ring 1 includes all eight neighbours. |
| `0x413056`-`0x413072` | Common fire `0x412D00` | No gate. The aim offset is cut to 95% (`×19×5/100`) until `dx² + dz² <= range² << 16`, so a shot at a farther target falls short. |

An idle-scan hit calls `0x41481C`, which calls the fire routine `0x412D00`
with no further distance test. A melee unit standing next to a hostile
therefore fires, even though chase mode 3 never counts that neighbour as in
range. Once a chase ends beside the target, the unit returns to its idle
command, and its scan finds the target.

## Not yet decided

Aligning the port means splitting the single strict test by context:

- shots from an idle-scan acquisition need no distance test;
- the mode-3 chase test uses whole cells;
- modes 4 and 18 use the 8.8 test with `<=`;
- the fire routine shortens the aim.

That changes combat for every unit and therefore every golden digest. Before
it is done, two things need tracing: the native attack command loop after
`0x412D00` (when the next shot comes, and whether the target is re-scanned),
and how an explicit attack order hands over to the idle command.
