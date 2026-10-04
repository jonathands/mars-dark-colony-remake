# War session start

Both Single Player and Multi Player War leave the `multie` lobby through
`0x40123C`. The lobby (`0x4104B0`) runs first. Once it confirms, the mode
`+0x14A0` becomes 2 and `0x4014FB`-`0x401779` turns the eight lobby rows
(72 bytes each at `0x48A018`; type at `+0x20`, race at `+0x1C`) into players.
The mode values are:

| Mode | Game |
| --- | --- |
| 0 | Campaign |
| 3 | Training |
| 1, 2 | War |

## Seating

1. **List the rows.** The rows whose type is not None (3) are listed in row order, and the rest of the list is -1.
2. **Seed the stream.** The shared random stream (index `0x479204`, table `0x478E04`) is seeded with `0x49469C`. Nothing in this build writes that address, so the seed is 0.
3. **Shuffle.** A Fisher-Yates pass over the first N entries swaps entry c with `c + r % (N - c)`, where r is the next table value. N is the player count in the map name, the digit at `0x4942A5` (`d2play01` holds 2, `j8play03` 8).
4. **Seat.** Position c becomes player c, which is scenario team c. The local player's row maps to its position (`0x494264`, `world + 0x7D1C`).
5. **Reseed.** The scenario loader (`0x41B933`) seeds the stream with the same 0 again, so the shuffle's draws do not reach the simulation.

Because the seed is always 0, the seating is fixed. For example, a human row
and a computer row on `j4play01` land on teams 4 and 2.

## Per player

| Row | Player kind `+0xBBC` | Notes |
| --- | --- | --- |
| Human | 0 | |
| Computer | 3 (Krusty) | Vent multiplier `+0x19B8` = 0x100. |
| Computer+ | 3 (Krusty) | `+0x19B8` = 0x200, but `0x401799` then overwrites `+0x19B8` for every player from a session percentage. |
| Empty position | 4 | Profile 4's controller (`0x47B34C`) has only stub methods, so the team's placed units stand idle. |

- **Race.** The row's race becomes `+0xBB8`. The SCN loader (`0x41C4E3`) then swaps the team's placements to that race.
- **Session.** `+0x1524 + team` is 1 exactly for occupied positions.
- **City slots.** For every other team, the loader (`0x41C155`) zeroes the level and health of every city slot. Those teams get no buildings.

## Port

`WarSession.Assign` and `WarSession.Apply` (Engine/Scenario) implement both
steps. `SinglePlayerWarScenario.TryCreateSession` builds a launch from lobby
rows.

| Where | What it does |
| --- | --- |
| Single Player War (app) | Uses it with the lobby's own rows. The first Human row is the local player, and any other Human row plays as a computer. |
| Network War | Every peer runs it from the host's shared lobby; see NETWORK_AND_REPLAY.md. |
| Saves | Keep the rows (`SavedWarLaunch.Rows`) so loading reseats identically. |

Older launches built from a team choice (`TryCreateLaunch`) keep the earlier behaviour: the first enabled team of the race, and every other team on the computer.

Not modelled:

- the lobby's colour and team columns;
- the session percentages behind `+0x19B8` (see city-and-economy.md).
