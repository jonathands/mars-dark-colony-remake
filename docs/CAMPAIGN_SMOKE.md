# Campaign smoke

`dotnet run --project tests/DarkColony.Engine.Checks -- --campaign-smoke <ticks> [filter] [--strike] [--sweep] [--trace]`
runs every campaign and training mission headless:

- human01-15, alien01-15, atrain1-7 and htrain1-7 (44 missions). The
  installation has no human16; `human/demo` is not a mission.
- Team 0 is played by the Krusty planner (profile 3).
- A mission runs until its script ends it (`bail`) or the tick limit.

Options:

| Option | Effect |
| --- | --- |
| `--strike` | Every 16 updates, each idle armed unit of team 0 attacks the nearest hostile. |
| `--sweep` | Every 64 updates, kills the hostile actors of the players named by the victory triggers (`b(p, ...)`/`s(p, ...)` in a trigger with `bail 0`), or every hostile when none is named. It then sends an idle unit of team 0 to the nearest cell of each open trip area, least-tried trip first. Kills go through the ordinary damage path, so the losses statistics count them. |
| `--trace` | Prints every 4000 ticks the open triggers, the units and their move orders, and the occupants around stuck units. |

The check "every campaign and training mission reaches its script's outcome
in the campaign smoke" runs the `--sweep` mode for up to 40000 ticks. It
requires all 44 missions to end, but does not fix whether each one ends in
victory or defeat: it guards the mission runtime, not the play.

## Results (2026-10-04)

"Natural" is the Krusty planner alone, 40000 ticks. "Sweep" is `--sweep`.
Times are ticks.

| Mission | Natural | Sweep |
| --- | --- | --- |
| alien01 | none | victory 39 |
| alien02 | victory 503 | victory 39 |
| alien03 | defeat 2 at 7159 | victory 327 |
| alien04 | none | defeat 3 at 695 |
| alien05 | none | defeat 2 at 711 |
| alien06 | none | victory 39 |
| alien07 | none | victory 47 |
| alien08 | none | victory 887 |
| alien09 | none | defeat 2 at 799 |
| alien10 | defeat 2 at 23447 | victory 39 |
| alien11 | defeat 2 at 14447 | victory 39 |
| alien12 | none | victory 39 |
| alien13 | none | victory 39 |
| alien14 | none | victory 1607 |
| alien15 | defeat 2 at 35319 | victory 39 |
| human01 | defeat 2 at 19215 | victory 1007 |
| human02 | defeat 3 at 3343 | victory 14183 |
| human03 | defeat 3 at 9311 | victory 39 |
| human04 | victory 37487 | victory 511 |
| human05 | none | victory 1721 |
| human06 | none | victory 2943 |
| human07 | defeat 3 at 12975 | victory 39 |
| human08 | defeat 3 at 2087 | victory 39 |
| human09 | none | victory 39 |
| human10 | none | victory 39 |
| human11 | none | victory 2098 |
| human12 | none | victory 39 |
| human13 | none | victory 39 |
| human14 | none | victory 577 |
| human15 | defeat 2 at 30319 | victory 39 |
| atrain1-7 | defeat (time limit) | 1 victory, 6 defeats |
| htrain1-7 | 1 victory, 6 defeats | 2 victories, 5 defeats |

Notes:

- **Missions the Krusty planner leaves open.** Most victories need the enemy
  bases destroyed (`b(p, slot) == 0`) or areas reached (`trip`). The planner
  only attacks enemies near its guard posts, so it rarely wins on its own.
  It also never walks into trip areas.
- **Training missions** score the player's interface actions; their scripts
  end in defeat when the tutorial timer runs out (6527 ticks).
- **alien04, alien05, alien09 under the sweep** lose their commander (the
  defeat `s(0,0,73..76) == 1`). The trip walker sends it into areas whose
  triggers spawn enemies. With other helper timings alien05 reaches victory
  at 3359. Their victory paths are a commander escort (alien05: trips 17 and
  18, then a timer) and timers.
- **Victory proven elsewhere.** human01 and alien01 also have dedicated checks
  that reach the script's own victory with a strike force.

## Fixes the smoke found

- A ground move to a cell of region 0 now heads for the first passable cell
  on square rings around it, x outer and z inner (`0x414E32`). Before, the
  move retried forever. The Krusty scouts' nudged targets can land in region
  0, which left alien05's last unit stuck.
- Sweep kills record losses (`ScenarioSimulation.Kill`), so `s(p, 0, type)`
  victory tests can fire.
