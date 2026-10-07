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
| `--sweep` | Every 64 updates, kills the hostile actors of the players named by the victory triggers (`b(p, ...)`/`s(p, ...)` in a trigger with `bail 0`), or every hostile when none is named. It then sends an idle unit of team 0 to the nearest cell of each open trip area, least-tried trip first, except the trips only another team sets off (`(S==k)`, k not 0): units parked there would only block the way, as they blocked alien08's escorted commander. Kills go through the ordinary damage path, so the losses statistics count them. |
| `--trace` | Prints every 4000 ticks the open triggers, the units and their move orders, and the occupants around stuck units. |

The check "every campaign and training mission reaches its script's outcome
in the campaign smoke" runs the `--sweep` mode for up to 40000 ticks. It
requires all 44 missions to end, but does not fix whether each one ends in
victory or defeat: it guards the mission runtime, not the play.

## Results (2026-10-07)

"Natural" is the Krusty planner alone, 40000 ticks. "Sweep" is `--sweep`.
Times are ticks. Since 2026-10-07 the missions start with the alliances
their SCNs declare (the `%TeamAllies` row had been read from the wrong line,
so every team started hostile); several results moved with that.

| Mission | Natural | Sweep |
| --- | --- | --- |
| alien01 | none | victory 39 |
| alien02 | none | victory 39 |
| alien03 | defeat 2 at 3703 | victory 327 |
| alien04 | defeat 3 at 839 | defeat 3 at 855 |
| alien05 | none | defeat 2 at 703 |
| alien06 | none | victory 39 |
| alien07 | defeat 2 at 39431 | victory 103 |
| alien08 | none | defeat 2 at 3031 |
| alien09 | none | defeat 2 at 2007 |
| alien10 | defeat 2 at 38847 | victory 39 |
| alien11 | defeat 2 at 15167 | victory 39 |
| alien12 | defeat 2 at 38559 | victory 39 |
| alien13 | none | victory 39 |
| alien14 | none | victory 1606 |
| alien15 | defeat 2 at 16671 | victory 39 |
| human01 | defeat 2 at 19215 | victory 975 |
| human02 | victory 20007 | victory 14183 |
| human03 | defeat 3 at 22951 | victory 39 |
| human04 | none | victory 511 |
| human05 | none | victory 2256 |
| human06 | none | victory 2302 |
| human07 | defeat 3 at 28223 | victory 39 |
| human08 | defeat 2 at 18215 | victory 39 |
| human09 | none | victory 39 |
| human10 | none | victory 39 |
| human11 | none | victory 2117 |
| human12 | defeat 2 at 16111 | victory 39 |
| human13 | none | victory 39 |
| human14 | none | victory 577 |
| human15 | defeat 2 at 20511 | victory 39 |
| atrain1-7 | 1 victory, 6 defeats | 1 victory, 6 defeats |
| htrain1-7 | 1 victory, 6 defeats | 4 victories, 3 defeats |

Notes:

- **Missions the Krusty planner leaves open.** Most victories need the enemy
  bases destroyed (`b(p, slot) == 0`) or areas reached (`trip`). The planner
  only attacks enemies near its guard posts, so it rarely wins on its own.
  It also never walks into trip areas.
- **Training missions** score the player's interface actions; their scripts
  end in defeat when the tutorial timer runs out (6527 ticks).
- **alien04, alien05, alien08, alien09 under the sweep** lose their
  commander (the defeat `s(0,0,73..76) == 1`). The trip walker sends it into areas whose
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
