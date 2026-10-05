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

## End of the game

War scenarios have no `bail` triggers. The end is a rule of the gameplay
loop, for game types 1 and 2 (settings `+0x14A0`):

- **In the game** (`0x40DE20`): a player is in the game while one of its
  actors is alive.
  - The 800-actor table at `world + 0x7D28` (stride `0xDC`) is walked, and
    state byte `+0x2C` must be 1, not 0 (free) or 10 (dying).
  - Team byte `+7` names the owner.
  - City slot 5 (actor index below `0x78` with index % 15 == 5) does not
    count, nor do entities 41 and 42 (deployed mobile towers) and 45 and 46
    (mines).
  - Units count as much as buildings. So do the idle placed units of an
    empty position, which keep that position in the game until they are
    destroyed.
- **Out** (`0x40A832`): once the local player is no longer in the game,
  gameplay `+0x13B` is set and the message strip shows `maine` text 282,
  "You have been defeated. You are now in observation mode." The game goes
  on.
- **Over** (`0x40DEAC`): every player still in the game must be allied both
  ways (`0x41E820`) with the first of them. A lone survivor ends the game,
  and so does nobody. The loop then sets the quit flag `world + 0x471A8`.
- **Result**: stat (0,0) becomes the local team, or 8 if `+0x13B` is set
  (`0x40A91E`).
  - Leaving through "REALLY QUIT?" sets `+0x13A` (`0x4329C6`) and also
    writes 8 (`0x40A9FB`).
  - The result screen (`0x404776`) plays `avi/hvad1.avi` or
    `avi/avhd1.avi`, by race, when stat (0,0) is the local team. It then
    shows `intrface/multiwn` with "Victory" or "Defeat" and per-player
    "Kills"/"Losses".

The port checks this after every update (`MainForm.CheckWarEnd`, with
`ScenarioSimulation.IsPlayerInGame` and `IsWarOver`). The result reuses
the mission debrief screen, which says "Victory" or "Defeat"; the
`multiwn` layout and its kill and loss counts are not drawn yet. When a
player leaves the game, the port also writes "Team N is out of the game."
in the message strip, which the original does not do.

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
