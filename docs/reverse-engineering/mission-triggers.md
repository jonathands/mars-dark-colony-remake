# Mission trigger scripts

Dark Colony campaign scenarios pair their map data with text `.tro` scripts.
They are not an SCN trailer: `human01.tro`, for example, has independent
numbered blocks with conditions such as `(c>1200)` and `(S==0)`, followed by
commands including `bail`, `newtype`, `waypoint`, `reinforce`, and `msg`.

`ScenarioTriggers` parses these files structurally. Since 2026-10-04,
`Missions/MissionScript` compiles them the way the executable does, and
`ScenarioSimulation` runs them (see "Native runtime" below).

`ScenarioTriggerConditionParser` additionally exposes the header condition as
a structural expression tree: numeric literals, named variables/functions and
their arguments, comparison operators, grouping, and `&&` / `||`. It has no
state evaluator, so names such as `c`, `S`, `s(...)`, `b(...)`, and `m(...)`
remain data syntax rather than asserted mission semantics.

The installed corpus also has one bare-value header, `(1)` in `alien08.tro`.
It is represented as a value node; the reader does **not** treat it as a
truthy constant or infer what native trigger mode it gates.

Every loaded `ScenarioTrigger` carries that tree as `ParsedCondition`, while
retaining the original `Condition` text. A condition outside this grammar is
an explicit opaque node, never a silently repaired or evaluated mission rule.
The direct parser itself remains strict for callers that require grammar
validation.

One installed `alien08.tro` header contains `b(1,3)&&==0`, a malformed
comparison in an otherwise compound condition. It is the sole opaque condition
asserted by the installed-corpus check; the port preserves its exact source
text and does not change it to a guessed `==0` expression.

The reader rejects malformed/unterminated blocks and duplicate IDs. Installed
script-corpus checks prevent later mission work from depending on an accidental
single-mission interpretation.

## Installed script vocabulary

The installed 101-script corpus contains 2,465 trigger blocks. This is a
corpus fact, not an execution mapping: the names and argument counts identify
the work that must be traced, but do not establish what a command changes in
the native game.

| Command | Observed argument counts | Occurrences |
| --- | ---: | ---: |
| `setlifes` | 2 | 1,726 |
| `setarray` | 2 | 961 |
| `newrate2` | 3 | 701 |
| `setmoney` | 3 | 701 |
| `msg` | 5 | 347 |
| `reinforce2` | 5, 7, 13 | 266 |
| `newrate` | 3 | 229 |
| `artifact` | 2 | 184 |
| `reinforce` | 11, 12, 13, 15 | 155 |
| `waypoint` | 5, 7, 9, 11 | 134 |
| `bail` | 1, 2 | 126 |
| `exomoney` | 2 | 56 |
| `aimsg` | 4 | 24 |
| `ai` | 2 | 16 |
| `abduct` | 2 | 15 |
| `ally` | 3 | 14 |
| `noundeploy` | 0 | 13 |
| `vision` | 2, 3 | 7 |
| `newtype` | 3 | 2 |
| `dfiddle` | 3 | 1 |
| `nopickup` | 1 | 1 |

The regression suite asserts the full normalized vocabulary against installed
data. This makes an accidental parser loss (or a data-set change) visible
before command handlers are added. The native parser evidence already
establishes that `waypoint` accepts one to four point pairs (at most eight
coordinates); runtime consumer tracing is still required for team/unit
selection, timing, and all other command effects.

## Mission text

`ScenarioMissionText.LoadForScenario` also loads the optional briefing
(`.txt`), trigger-message (`.msg`), and numbered outcome (`.001`–`.005`) files
beside a scenario. It strips the original `~<number>` colour markup only for
the engine-facing text; reconstructing styled dialogue layout remains a UI
task. Trigger execution is still disabled, so these texts are available for
the eventual `msg` and `bail` handlers without claiming that mission state is
already evaluated.

## Native runtime (traced 2026-10-04)

`trigger.c` loads `<scenario>.tro` from the SCN loader (`0x43FB90`). The
parser `0x43E658` fills a 128-entry table at `0x4FBC48` (stride 16): mode
(`norm` 0, `trip` 1), compiled condition, lives (the header's count, one
byte), and the action list. Each action is a 0x1C-byte record pushed at the
head of the list, so **a block's commands run in reverse source order**.

Fixed arguments are read with `strtol` and stored as bytes or words, so they
truncate; extra arguments are ignored and missing ones read as 0.
`setarray`, `setlifes`, `setmoney`, and `newrate2` compile the rest of
their line as an expression. Command → action type:

| Command | Type | Effect (executor `0x43D814`) |
| --- | ---: | --- |
| `ai p v` | 0 | player +0xBBC (%AI) = v |
| `die` | 1 | debug assertion |
| `reinforce t x z (type n)×5` | 2 | a transport (entity 92 human / 93 alien, team 8) flies in with the cargo (`0x418F4C`, command 13) |
| `bail a b` | 3 | stat (0,0) = a, stat (7,0) = b; the game ends 10,000 ms later. The corpus uses a = 0 for victory (`bail 0 1`, text .001) and a = 1 for defeat with text .00b |
| `aimsg` | 4 | AI message (`0x41AD68`) |
| `newrate r x z` | 5 | the vent at (x, z) pays r × multiplier >> 8 per pulse |
| `setarray i e` | 6 | type statistic (0, i, 2) = e |
| `setlifes i e` | 7 | trigger i lives = e |
| `ally a b v` | 8 | relation [a][b] = v, plus alliance bits (`0x41E7D8`) |
| `dfiddle p i v` | 9 | player +0x193C + i: depend item i disabled |
| `waypoint x z n ...` | 10 | the actor on (x, z) gets up to 8 points, state 9 (`0x43D764`) |
| `msg a b c d e` | 11 | queue line c of the `.msg` text (`0x44D918`) |
| `exomoney p r` | 12 | player passive rate +0x19B4 = r |
| `setmoney x z e` | 13 | the vent at (x, z) holds e × multiplier >> 8; creates one if absent |
| `newrate2 x z e` | 14 | `newrate` with an expression |
| `reinforce2 t x z (type n)×5` | 15 | units created at once (`0x41B634`, square-ring free cell `0x41B4A0`) |
| `newtype x z t` | 16 | the ground actor on (x, z) becomes entity t |
| `artifact a b` | 17 | an artifact (entity 0x3F + r % 5) is handed to a pickup |
| `noundeploy` | 18 | world +0x948 = 1 |
| `abduct s d` | 19 | a transport takes player d's commander |
| `vision a b v` | 20 | alliance visibility bits |
| `nopickup p` | 21 | player +0xBB4 = 1 |

**Runners.** Every eighth world update (`world + 0x94C & 7`, `0x419A4E`),
`0x43E4D0` walks slots 0-127. A `norm` trigger with lives left whose condition
holds runs its actions and loses one life; later slots see earlier slots'
effects in the same pass. Path steps (`0x415E6E`) call `0x43E530` with the
trip ID of the cell they reserve, so a `trip` trigger runs the same way with
the stepping unit as `S`/`t`. The `.mtg` file holds those IDs: width and
height bytes, then one byte per cell, stored as `id << 10` in the alternate
grid at row `ysize - 1 - row` (`0x453320`).

**Expressions.** The compiler (`0x43C5B8`) is recursive descent over
single-character names, and the evaluator (`0x43CF2C`) a 16-bit stack
machine:

- Literals; `c` = world `+0x52C >> 4`; `r` = the shared random stream.
- `S` / `t` = the tripping unit's team / entity type.
- `b(p,s)` = player p's city slot s health.
- `s(p,k)` = player statistic k (`0x4956E0`).
- `s(p,k,i)` = per-type statistic (`0x495860`).
- `m(x,z)` = the live-mine map bit.
- `v(x,z,p)` = visibility.
- `u(i)` = a word at `0x4FE04C`.

The grammar has quirks:
- Addition binds tighter than multiplication.
- A comparison is not chained.
- `&&` and `||` share one precedence and associate to the right, so
  `A&&B||C&&D` means `A & (B | (C & D))`.
- An unknown character emits nothing, which makes the malformed `&&==0` of
  human09/alien08 consume an operand of the enclosing chain.

**Statistics.** Player statistics (12 per player):

| k | Meaning |
| ---: | --- |
| 0 | lobby options for players 1-6; `bail` values for players 0 and 7 |
| 1 | P7 earned, including the starting money |
| 2 | kills |
| 3 | losses |
| 4 | the player's side; `0x41A830` sums a statistic over the players of one side |
| 5 | harvester pulses that paid the player (`0x413B9C`) |
| 6 | live units outside the city, recounted every update |
| 8 | projectiles launched (`0x44178F`, in the constructor `0x441710`) |
| 9 | health restored by healers to the player's actors (`0x413F04`, `0x413FD8`) |
| 10 | player 0: 1 − night; player 1: day fraction × 256 |
| 11 | kills (`0x441BDC`) whose victim is less than 20 cells away (Manhattan) from the actor in the killer's word `+0x1934` |

Per-type statistics (4 per player and entity type):

| k | Meaning |
| ---: | --- |
| 0 | losses by type |
| 1 | live count, recounted every update |
| 2 | the `setarray` array (player 0) |
| 3 | kills by type |

| Rule | Status |
| --- | --- |
| Parser, action layout, reverse order | confirmed |
| Expression grammar and evaluator | confirmed (corpus compiles: 101 scripts, 2465 triggers) |
| Norm cadence (every 8 updates, after critter groups and before passive income and actors), lives, trip map | confirmed |
| Statistics 1, 2, 3, 5, 6, 8, 9, 10 and per-type 0-3 | confirmed writers. The corpus scripts read only stats 0, 1, 2, 3, 6 and 10 |
| Statistic 11 | not modeled. Only the side sum `0x41A830` reads it. The word `+0x1934` (four words from `+0x1934`) is copied from interface state (`0x40AFBB`), and the update `0x419AC7` resets an entry to -1 when its actor dies, then zeroes stat 11 of the player whose index equals the entry's index (an apparent bug in the original). What sets the words in play was not traced |
| Lobby options in stat 0 | confirmed: session start `0x40123C` sets them, and this build never changes the defaults. Players 1/2 hold the vent rate/money multipliers (4 << 6 = 256); players 3-6 hold 0, so the multiplayer vent-respawn scripts (`s(3,0)==1`) never fire |
| `reinforce` transport flight | provisional: cargo placed at once like `reinforce2` |
| Vent rate/reservoir multipliers | confirmed: `newrate` uses stat (1,0) and `setmoney` stat (2,0) (`0x41A538` with AL = 0), both 256 = x1 |
| `waypoint` state 9 | provisional: one pass through the points |
| `ally` alliance bits, `vision`, `abduct`, `artifact`, `aimsg`, `nopickup` | not modeled; reported as `LastUnmodeledMissionActions` |
| `newtype` | provisional through the deployed-form override |
| Bail delay | provisional: 152 ticks for the native 10,000 ms |
| Malformed-condition stack floor | provisional (reads 0 below the stack) |
