# Computer player (Krusty)

Recovered from `dc.exe` and implemented in
`Simulation/ScenarioSimulation.Ai.cs` (cadence and profiles) and
`Simulation/KrustyBrain*.cs` (the planner). The source file names come from
the executable's assertions: `ai.c`, `krusty.c`, `krusty_general.c`,
`krusty_attack.c`, `krusty_defend.c`, `krusty_scout.c` and `krusty_army.c`.

## Cadence and profiles (`ai.c`)

- `0x41AC2C` runs after the actors and projectiles of each update. It acts
  only when the update counter is a multiple of 4.
  - At update 4, every computer player thinks.
  - After that, a round-robin cursor advances one player slot per run, so each
    player thinks every 32 updates.
- `0x41AB20` scores every module of the player's profile (`+0xBBC`). Each
  module draws once from the shared stream, and the weighted choice
  `score > draw * sum / 32767` picks the module that acts.
- Profiles:
  - 3 is Krusty: one module with score 1 and action `0x44BE40`.
  - 4 is passive: score 0.
  - 1 and 2 appear in no shipped scenario.
  - Single Player War gives profile 3 to every computer slot (`0x41DB12`).

## State and think

The state is 0x6C40 bytes at player `+0xBC0`, created by `0x44BD2C`, which:

1. Sets the ally bytes (`+0x6C38`), with only the player itself allied.
2. Builds the region map.
3. Sets up groups 2, 1, 0 and 3.
4. Writes the constants: guard share `0xC0` (out of 256) and the category
   weights of the army purchase.
5. Copies the 18 default goals from `0x48903C`.

A think (`0x44BE40`) runs, in order:

1. The unit assignment (`0x457568`), on the first think only.
2. The influence map (`0x456AD0`).
3. The census and goal walk (`0x457940`).
4. For each group 0-3, the methods pre, purge, update and post.

## Regions

The planner's zones are PTH regions (256 records of 18 bytes):

- Bytes `+2/+3`: a representative cell. It is the first cell of the region
  found on square rings (rows outer) around the region's centroid.
- Byte `+0xD`: hop distance from home. Home is the city origin, or the start
  point when the city origin has a zero coordinate (`0x457210`), moved to the
  nearest free ground cell. The distances come from a unit-weight search over
  the route table.
- Three owner/strength slots, described under Influence.
- Word `+0x10`: count.
- Byte `+0x12`: flags. Bit 0 is contested. Bits 1 and 2 are set only by
  `aimsg` settings 9-12.

The PTH loader (`0x442D1F`) also builds a neighbor list per region at map
`+0x984A8`. For regions 1-254 it holds the distinct nonzero `NextRegion(z, t)`
for targets 1-254, in order of first appearance, at most 31 of them.

Helpers:

- **Hop distance** (`0x44B640`): the number of route-table steps between two
  regions. The result is 0xFF when either region is 0 or the route breaks.
- **Path danger** (`0x45817C`): mark the route's regions and their neighbors,
  then the neighbors of those (two steps). The result is the sum of the
  ground strength (slot B) of marked regions owned by another team. A route
  that reaches region 0 gives -1.

## Influence (`0x456AD0`)

Every think, the slots are cleared. For every actor of teams 0-7, in slot
order:

1. The actor is **seen** when any ally can see its cell. A mine that an
   ally's detector has not revealed is not seen.
2. If it is seen, its cell, type and team are used. For another team they are
   also stored as a **ghost** (4 bytes per slot at `+0x1202`).
3. If it is not seen, the ghost is used. If the player now sees the ghost's
   cell, the ghost is dropped.
4. Allied teams are skipped.
5. An unarmed enemy object adds 1 to the region's count, unless its armor
   class is 8.
6. An armed one adds, using the 8.8 damage table:
   - ground strength `25 * M[w][1] / M[1][armor]` (divided by 50 for armor
     class 2) to slot B;
   - anti-air `M[w][2]` to slot A;
   - fliers also add the ground strength to slot C.

A slot has a single owner:

- The owner's team adds to it.
- Another team subtracts from it, marks the region contested, and takes the
  slot when it is at least as strong.
- Values are words, so they wrap.

## Census and goals (`krusty_general.c`)

**Categories** (`0x4563F0`):

| Types | Category |
| --- | --- |
| Human types 0-7 and Gray types 8-15 | 0-7 by kind (6 harvester, 5 flier) |
| Types 41/42 (deployed towers) | 1 |
| Types 49/50 | 7 |
| Anything else | 8 |

**Census** (`0x457940`) runs the assignment, then counts each group's members
by category. It adds every type waiting in the four production queues, then
walks the goals. The first goal that is not satisfied acts, and the walk stops.

| Kind | Satisfied | Acts |
| --- | --- | --- |
| Harvesters (`0x45642C`) | census[6] >= p | Buys every buildable harvester troop, one each, while P7 lasts. |
| Building (`0x4564C8`) | the item of goal p for the race (`0x488FF4`) is not buildable | Buys it (command 9). |
| Army (`0x456664`) | stat 6 >= troop cap, or categories 0 and 2-5 >= p | Buys the buildable troop whose category has the least census x weight. |
| Stop | never | Nothing. |

- Default order: building 8 (headquarters), harvesters 1, building 0,
  army 5, ... army 200, stop.
- Building goals 6 and 7 name upgrade items, which the building test rates as
  done.
- The sender deducts the price at once. Commands 9 and 10 (10 carries the
  entity type) run at the next update.
- Category weights start at 1, 2, 1, 2, 4, 2, -, 4 for categories 0-7.
  Harvesters weigh 10000, so the army goal never buys one.

**Assignment** (`0x457568`) counts unassigned live units by category and sets
quotas:

- Harvesters go to group 0.
- Category 1 goes to group 1.
- Fliers go to group 3. With no fliers to place and none buildable, plain
  infantry joins group 3 while group 3 is under a quarter of the armies.
- The rest is split: group 1 takes a unit while `count1 * 0xC0 <= 0x40 *
  count2`, so group 2 holds three quarters.

Units (not city buildings) then join the first group with quota left. The
task is the one the group's accept method picks, and new members go to the
head of the task's list.

## Groups

| Group | Role | Update | Post | Accept |
| --- | --- | --- | --- | --- |
| 0 | harvest | - | `0x4598B0` | task 0 |
| 1 | guard | `0x4593A8` | army `0x463E78` | `0x459660` |
| 2 | attack | `0x458B44` | army `0x463E78` | `0x458F3C` |
| 3 | scout | - | `0x459F80` | task 0 |

Each group has 16 tasks. The purge (`0x44BA48`) drops dying members.

- **Harvesters.**
  - A harvester stuck for more than 10 checks forgets its vent (byte `+0x11`).
  - One that sees danger on the way to its vent goes back to the guard
    group's roaming post.
  - The last idle harvester in the list moves (state 2) to the nearest paying
    free vent. The vent must be in a region no harvester has, with no danger
    on the way. Arriving on a vent deploys it.
- **Army tasks** (`krusty_army.c`). A task has a current region, a target and
  a route.
  - Members far from the current region (more than 3 hops) are sent there: an
    attack-move for new members, a plain move for the others.
  - When every arrived member is close, the task advances one region along
    its route and attack-moves everyone there.
  - Turrets and Xenoworts that settle near the region deploy (state 13).
- **Guards.**
  - Task 0 roams. On 1/16 of thinks it picks a random region within one hop
    of home and forces the next post to re-send everyone (the global byte
    `0x489510`).
  - Tasks 1-15 hold one harvested vent region each.
  - Accept prefers task 0 at half weight.
- **Attackers.** Tasks 0 and 1 start idle. An idle or guarding task rates the
  regions within the guard posts' reach plus 3 that hold enemies or are
  contested:
  - closer regions score higher, and unarmed enemy objects add half the reach;
  - the score is task strength x value / path danger;
  - the best region becomes the target.
  
  Otherwise an idle task guards the guard post that the fewest attack tasks
  hold. An attacking task gives up when the danger on its route reaches twice
  its strength, or when the target is cleared.
- **Scouts and bombers.** An idle scout picks one of three targets:
  - the richest enemy region with no anti-air within two steps;
  - a random cell;
  - a random vent, nudged until no enemy ground force is near.
  
  Fliers (types 5/13) fly there. Others attack-move. A scout that is hit, or
  on 1/64 of checks, picks again.

Unit orders are network command 7 (waypoints) plus command 5 (state) per unit.
The port issues them as `MoveIntent` (state 2), `AttackMoveIntent` (state 7)
or `DeployTowerIntent` (state 13). They run at the next update, before the
player's commands.

## Executable quirks kept

- The attack group indexes two of its per-region arrays by task number while
  building them.
- Its "contested" test reads the region numbered like the task.
- The scouts' vent nudge sums owner team numbers, so an enemy of team 0 never
  counts.
- Task strength rates each category with the Human entity of that type number,
  whatever the race.

## Not ported

- `aimsg` settings 6-8 (protected regions, `0x4591A0`/`0x4592A8`) and 13.
  Setting 13 is the cautious attack search behind `+0x6C34`, with its
  Dijkstra route `0x457CC0`. The shipped corpus sends only settings 1-4.
- The attack task's "gathering" mode. Only unreachable code sets it.
- The slot-build animation. Command 9's building rises through order 0x13
  (`0x41822C`), with `+0xC10[slot]` set until it ends. The building goal
  treats the slot as busy meanwhile. The port builds at once.
- Freed actor slots are not reused. The port keeps creation order after the
  city buildings, so loops that depend on slot order can differ once units
  have died.
