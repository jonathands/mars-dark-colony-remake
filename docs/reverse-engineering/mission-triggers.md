# Mission trigger scripts

Dark Colony campaign scenarios pair their map data with text `.tro` scripts.
They are not an SCN trailer: `human01.tro`, for example, has independent
numbered blocks with conditions such as `(c>1200)` and `(S==0)`, followed by
commands including `bail`, `newtype`, `waypoint`, `reinforce`, and `msg`.

The compiled engine now parses these files structurally through
`ScenarioTriggers`. It preserves trigger ID, mode, repeat count, condition, and
source-order commands without executing them. This is deliberately a data
boundary, not a mission-system claim: condition variables and each command's
runtime effect need executable tracing before they can mutate simulation state.

`ScenarioTriggerConditionParser` additionally exposes the header condition as
a structural expression tree: numeric literals, named variables/functions and
their arguments, comparison operators, grouping, and `&&` / `||`. It has no
state evaluator, so names such as `c`, `S`, `s(...)`, `b(...)`, and `m(...)`
remain data syntax rather than asserted mission semantics.

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
