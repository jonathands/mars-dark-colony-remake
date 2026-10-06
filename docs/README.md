# Documentation

Start with [HOW_THE_PORT_WAS_MADE.md](HOW_THE_PORT_WAS_MADE.md) for the story
and the method. Read [PORT_CONTRACT.md](PORT_CONTRACT.md) before changing
code.

[images/](images/) holds the screenshots: the port and the original for
comparison, which the main README shows, and fixes before and after, which
[HOW_THE_PORT_WAS_MADE.md](HOW_THE_PORT_WAS_MADE.md) shows.

## Using the port

| Document | Contents |
| --- | --- |
| [DISPLAY.md](DISPLAY.md) | Window and fullscreen modes, DPI, gameplay views, the Video panel, display flags |
| [SAVE_GAMES.md](SAVE_GAMES.md) | Saves as command journals, and how loading verifies them |
| [NETWORK_AND_REPLAY.md](NETWORK_AND_REPLAY.md) | Replays, LAN lockstep, Multi Player War, `--replay`/`--load` |

## Working on it

| Document | Contents |
| --- | --- |
| [PORT_CONTRACT.md](PORT_CONTRACT.md) | Architecture, project boundaries, reconstruction rules, invariants |
| [TESTING.md](TESTING.md) | The check runner: tags, groups, filters, live tests |
| [DETERMINISM.md](DETERMINISM.md) | The state digest and the golden runs |
| [CATALOG_SWEEP.md](CATALOG_SWEEP.md) | Every build, troop and research item of both races, end to end |
| [CAMPAIGN_SMOKE.md](CAMPAIGN_SMOKE.md) | All 44 campaign and training missions played headless |
| [PERFORMANCE.md](PERFORMANCE.md) | The frame pipeline, the profiler, measured costs |
| [GAMEPLAY_FIXES_PLAN.md](GAMEPLAY_FIXES_PLAN.md) | Play-test reports, their evidence and status (the open items live here) |

## Reverse engineering of `dc.exe`

Each note gives addresses, what is confirmed, and what the port implements.

| Note | Subject |
| --- | --- |
| [vision.md](reverse-engineering/vision.md) | Sight trees, the 16-update rebuild, explored memory, fog of war |
| [day-night.md](reverse-engineering/day-night.md) | The night tint of the ground, lights, the HUD clock |
| [target-acquisition.md](reverse-engineering/target-acquisition.md) | How idle and moving units pick targets |
| [combat-range.md](reverse-engineering/combat-range.md) | Weapon range rings and chasing |
| [combat-damage.md](reverse-engineering/combat-damage.md) | Damage arithmetic, armour, splash, the dying state |
| [unit-special-commands.md](reverse-engineering/unit-special-commands.md) | Deploy, heal, steal money, artifacts and other specials |
| [special-command-dispatch.md](reverse-engineering/special-command-dispatch.md) | Ground attacks, Napalm, commander transports |
| [production-flow.md](reverse-engineering/production-flow.md) | Troop queues, exit cells, build timing |
| [city-and-economy.md](reverse-engineering/city-and-economy.md) | Player cities, building slots, Petra-7 income |
| [computer-player.md](reverse-engineering/computer-player.md) | The AI cadence and the Krusty planner |
| [mission-triggers.md](reverse-engineering/mission-triggers.md) | The `.tro` script language and its runtime |
| [war-session.md](reverse-engineering/war-session.md) | How a War game turns lobby rows into players |
| [single-player-war-lobby.md](reverse-engineering/single-player-war-lobby.md) | The `multie` lobby screen |
| [gameplay-hud.md](reverse-engineering/gameplay-hud.md) | HUD geometry and readouts |
| [gameplay-input.md](reverse-engineering/gameplay-input.md) | Command keys and mouse handling |
| [gameplay-ui-dispatch.md](reverse-engineering/gameplay-ui-dispatch.md) | From UI events to HUD controls |
| [game-options.md](reverse-engineering/game-options.md) | The in-game OPTIONS popup |
| [interface-text.md](reverse-engineering/interface-text.md) | Font colour remap and the credits teletype |
| [interface-widgets.md](reverse-engineering/interface-widgets.md) | Menu definitions: buttons, lists, scroll bars, gadgets, the new campaign screen |
| [fin-layers.md](reverse-engineering/fin-layers.md) | FIN draw layers: mirroring, lights, draw types; which suffix a facing shows |
| [cd-music.md](reverse-engineering/cd-music.md) | The CD soundtrack schedule |
| [video.md](reverse-engineering/video.md) | When and how the Cinepak videos play |

The research on the file formats themselves (SPR, FIN, MAP, PTH, SCN) is in
`../dc-port-26/docs`, outside this repository.

## History

Plans and logs kept as a record. Newer code and notes override them.

| Document | Contents |
| --- | --- |
| [history/RECONSTRUCTION_CONTEXT.md](history/RECONSTRUCTION_CONTEXT.md) | The August 2026 milestone notes and early decoded facts |
| [history/AUTONOMOUS_ROADMAP_PROMPT.md](history/AUTONOMOUS_ROADMAP_PROMPT.md) | The brief for the October roadmap (goals 0-8) |
| [history/DISPLAY_MODES_PLAN.md](history/DISPLAY_MODES_PLAN.md) | The display-modes plan |
