# Troop production flow

Status: queue, exit cell, and timing recovered from `dc.exe` on 2026-10-04
and implemented in `Simulation/ScenarioSimulation.Production.cs` (see
"Native production queue" below). The sections after it are the earlier
evidence ledger; parts of it predate the `dc.exe`/`dc16.exe` identity
correction.

## Native production queue

Orders are network commands. The opcode is the byte at packet `+2`, and the
handler is entry `opcode - 1` of the table at `0x479384`:

- **Command 9, build** (`0x41C8D4`; emitter `0x40C13C`): bytes `(slot,
  variant, player)`.
  - If the slot already holds that variant at full health, the price
    (`0x4380D8`) is refunded.
  - Otherwise the slot health becomes the entity's full health, the variant is
    stored, and `0x444F14` recreates the slot building at once. That also
    empties the slot's queue and marks it ready. The price is added to the
    player's spending total `+0xBB0`.
- **Command 10, troop order** (`0x41C7F8`; emitter `0x40C168`): bytes
  `(entity, player, count)`.
  - The troop's queue is entity value 21 (runtime `+0xEC`). `count` copies are
    appended to player queue items `+0xCB0 + queue * 800`, and the length
    `+0xCA8 + queue * 2` grows.
  - Price × count is added to `+0xBB0`. P7 itself was already deducted by
    the interface (`0x4566AC`).

City buildings (actors 0-119) run `0x414314` from their idle command every
tick. Slot → queue comes from `0x41AE30`: HQ 2, barracks 0, slot 2 → 1,
slot 4 → 3, slots 13 and 14 → 0, the rest none. When the queue is ready
(`+0xCA0 + queue`) and not empty, the front troop goes through these steps:

1. Its exit is the city origin plus `0x41ADD0[queue][entity value 23]`. If
   any actor holds that cell (in the troop's grid), the holder's `+0x35` is
   set to 0, a step-aside request toward direction 0. The queue then waits.
2. A troop without a build animation (entity `+0x98`) is created on the exit
   at once (`0x41B750` → `0x41AF14`).
3. Otherwise the exit is reserved (`0x3FE`) and the queue stops being ready.
   The building plays the troop's animation once (`0x42630C` with mode 1).
   When the animation stops (`+0x2A` leaves 1), the troop appears on the exit
   and the queue becomes ready again.

**Troop cap.** Every update `0x41E6AC` computes `world + 0x528`:

1. Start from the 648 dynamic actor slots (`800 - 0x98`).
2. Subtract the critter groups' desired populations (`0x43FE6C`).
3. Subtract each live actor of a team 0-8 without a live city slot 0-4 (team
   8 holds the vents).
4. Subtract 100.
5. Divide by the number of players with a city.
6. Clamp to `world + 4`, which the SCN loader sets to `0x96` = 150.

Production starts a troop only while the player's stat 6 (live units outside
the city, recounted at the start of the update) is below the cap. Otherwise
it drops the order, refunds its price (`0x438090`), and posts message `0x77`.

The gamestat loader (`0x43C18C`) resolves `+0x98` to `<code>BUILDSTAND`, or
else `<code>BUILD`. These live in the buildings' FINs (`hubu.fin`,
`albu.fin`, `burn.fin`, `psyc.fin`). Each is the building's door opening and
the troop walking out, so the port draws the current frame over the
producing building at its art anchor (`MainForm.DrawProductionAnimation`,
`EntityAnimationCatalog.PreferredBuild`). The frame loader `0x425674` turns each FIN frame delay `d`
(0 meaning 15) into `(d + 3) * 15 / 100` ticks. The stepper `0x4264C8` leaves
frame 0 on its first tick and stops one step after the last frame. A marine
(`TRSCBUILD0`, 22 frames of delay 6) therefore takes 22 ticks, an Exploiter 9,
and a Gray 37. FINs are searched in `anim.dat` order.

| Rule | Status |
| --- | --- |
| Queue per player, slot → queue, exit offsets | confirmed |
| One troop at a time, exit must be empty, holder asked to step aside | confirmed |
| Build time from the troop's build animation | confirmed (the troop appears `duration` ticks after the reserve tick whichever order the animation step and the command run in) |
| Building recreation clears its queue | confirmed (`0x444F14`) |
| Damage-stage animation during a build (`0x414314` prologue) | confirmed harmless: the prologue sets the body channel (+0x14: STAND/SCRCH/BURN by health); the troop's build animation plays on the second channel (+0x24) |
| Troop cap `world + 0x528` (`0x41E6AC`) and its refund | confirmed, see below |
| Exit reservation marker `0x3FE` | confirmed: only the troop's arrival replaces it. A building lost mid-build leaves it, and the rebuilt slot's queue (reset by `0x444F14`) then waits on that exit forever |
| Queue cooldown byte `+0xCA4 + queue` | never set nonzero natively; not modeled |
| Teams without a city | port adapter: immediate spawn beside the source structure |

## Dependency records

`dc.exe` loads `gamestat/depend.txt` at `0x437870` (source string
`0x476250`). The loaded records begin at `0x4e6d70` with a stride of `84`
bytes. The loader stores the kind at `+0x10`; kind `1` is a troop record and
stores its entity type at `+0x14`. The price is stored at `+0x08`. Up to five
prerequisite dependency IDs are stored from `+0x20`, ending at `-1`.

This corroborates the managed `DependencyCatalog`: troop purchase identity and
cost come from `depend.txt`, while the building tuple is a different kind of
record.

## Availability check

`0x43839c` is the checked troop availability routine (the string
`dep_check_troop(gs, player, build_depend, &type, &cost) == buildable` is
referenced by its callers). Its effective inputs are game state, player index,
and dependency index; on success it returns `1` and writes the troop entity
type and cost to caller-owned output locations.

It rejects inactive dependency records, a player-disabled entry, non-troop
records, a mismatched player state, and unresolved prerequisites. Its recursive
prerequisite walk reads the same five-item list. It does **not** accept a
building actor/entity instance or a production-slot identifier.

## Executable identity correction

The asset-aware launcher starts `dc16.exe`, but its modern DirectDraw/GDI path
becomes covered by Windows' ghost overlay and is unsuitable for interactive
capture. The controlled interactive runtime is `dc.exe` (its window title is
`Direct Draw Driver`). Evidence recorded with an explicit `dc.exe` address is
therefore the authoritative live-image evidence; `dc16.exe` decompilation is a
separate static lead and must not silently replace it.

In `dc16.exe`, `0x40c168` lies inside `0x40c0d0`, the game-state
allocation/initialization routine: it allocates world arrays and initializes
player slots. It does not serialize a player order.

The current command dispatcher is `0x43db64`. Its opcode `0x0a` branch finds
an existing actor by the packed identity fields and calls `0x43dab4`; that
helper copies packed cell coordinates into the actor's route/path fields and
sets its active state. It does not allocate an actor, deduct P7, create a
production record, or choose a spawn point. Consequently opcode 10 must not
be used as evidence for troop production, queues, or rally behavior.

Older findings explicitly tied to a separate `dc.exe` image remain historical
leads only until the image identity and corresponding `dc16.exe` code paths are
revalidated. Data-file findings, including the structure of `depend.txt`, are
unaffected.

## Superseded order hypothesis

The troop-selection path at `0x4566ac` calls `0x43839c`, deducts the returned
cost from the player resource field at `player * 0xe30 + 0xbac`, then calls
`0x40c168`. The emitter constructs a compact command-10 packet and submits it
to the generic queue helper `0x421648` with queue kind 7. Its payload writes
the low bytes of the caller registers as troop type, player, and count; the
packet contains no building instance, footprint, or per-building timer. This
is direct static evidence from `0x40c168`–`0x40c193`, rather than an inference
from the UI.

The preceding `0x40c168`/opcode-10 interpretation is superseded for the
runnable image by the correction above. It must not drive port architecture or
the production adapter.

## Related P7 source accounting

The deployed-harvester update at `0x4139d7` gates on the low four bits of the
world counter, so it offers a payout once per sixteen simulation steps. Its
amount is not a fixed global income constant: `0x413a26` retains the high word
of the source actor's `+0x30`, and when the owning player has flag `+0xbbc`,
`0x413a36` multiplies that amount by player field `+0x19b8` as signed 8.8.
`0x413b6a` credits an eligible 0x4d/0x4e interceptor with the signed half, then
`0x413ba3` credits the owner and `0x413bdf` subtracts the unscaled/scaled
source amount from runtime `+0x0c`. The source fields come from the SCN vent
record (see [city-and-economy.md](city-and-economy.md), "Vent rates"); only
the computer-player multiplier's session percentage remains undecoded.

## Controlled `dc.exe` observations (2026-08-22)

These are deliberately labelled observations. They came from a live
single-player match through elevated `ReadProcessMemory` only: no debugger
breakpoint, game-memory write, or simulation pause was used. The current world
pointer was read from `0x498f6c`; local team from `world + 0x7d1c`; and P7 from
`world + team * 0xe30 + 0xbac`, matching the static HUD read.

- With one deployed Human Exploiter, P7 rose exactly 23 on each observed
  15--16-world-tick sample. This corroborates the static 16-tick producer
  gate, but does not yet identify the source `+0x30` high word or player
  multiplier that produced 23.
- A Human Sci Pod and Robo FTR placement each inferred a 2,000-P7 cost after
  subtracting the observed producer pulses. Their 3-P7 residual is a
  pulse-boundary sampling uncertainty, not a reason to encode a 2,003 cost.
- A newly produced Exploiter spawned immediately outside the Barracks exit;
  five queued Marines were observed clustered at that same exit with no
  autonomous rally movement. Production duration and native queue ownership
  were not sampled in time and remain unresolved.
- Moving a mobile Exploiter onto a geyser caused an automatic transition to
  its deployed form. Leaving the geyser reverses that state. The first two
  observed post-leave samples still yielded +23, after which observed pulses
  were +3. That +3 is the native passive income: every 16 ticks while the
  headquarters stands (see [city-and-economy.md](city-and-economy.md)).

The port models the recovered multiplier branch explicitly through
`PetraFlowRules.ApplyOwnerP7Multiplier` and its signed 8.8
`OwnerP7Multiplier8_8` operand (neutral at `0x100`). This preserves the
executable's fixed-point calculation shape, including its signed arithmetic
shift for negative products, without claiming that the unknown player-field
initialization or SCN source-rate mapping has been recovered.

The first and fourth observations support a reversible vent lifecycle. They do
not establish a repeated route or resource delivery to the main pedestal; a
separate multi-pedestal controlled capture is still required.

## Next controlled production capture

`tools/Capture-DcProductionState.ps1` is a read-only companion for the
interactive `dc.exe`/`dc16.exe` run. Start it in an elevated PowerShell **after
the match loads and before a single troop button is clicked**:

```powershell
.\tools\Capture-DcProductionState.ps1 -CaptureActors -DurationSeconds 120
```

It resolves the live world pointer from `0x498f6c`, samples world tick and P7,
and writes only changed byte ranges from the local player's native `0xe30`
record plus initial/final binary snapshots under ignored `artifacts/`. With
`-CaptureActors`, it also samples the 800-entry world-actor table at
`world+0x7d28`, including the P7 source fields at actor `+0x0c` and `+0x30`.
The capture must use one unit at a time, wait through its appearance, and leave
a short idle period on both sides of the click. Repeat with two quick clicks
on the same button. Comparing those traces will establish whether the state is
a player queue, a building queue, or an external delivery record; it will also
reveal source-rate depletion, charge time, and queue capacity without modifying
or pausing the original process.

## Building delivery

A building bought with command 9 is not instant. The rebuild `0x444F14` calls
`0x41822C`, which marks the slot rising (player `+0xC10 + slot`) and pushes
command 19 (`0x4187E4`) on the new building. The SCN loader also goes through
`0x444F14`, but before the fourth update, so the starting city stands at
once.

**The phases of command 19** (the word of its record):

| Phase | What happens |
| --- | --- |
| 0 | Waits while the player's earlier delivery runs (player `+0x19AA`), then sets the flag. With a build animation (entity `+0x98`) and after the third update, puts that animation on the body channel and calls `0x418504` to bring the ship down. Phase 1. Without one, phase 5 ends the command at once. |
| 1 | The ship comes down. `0x418504` creates DROP (92), or SAUC (93) for race 1, on team 8 above the slot (`0x444BA0`) and pushes command 22 (`0x4183B8`), a vertical flight whose counter runs from 50 to 0. |
| 2 | The build animation plays. When it stops (`+0x1A == 2`), the building takes its stand animation and the slot stops rising (`0x418997`). `0x418504` then sends the ship back up. Phase 3. |
| 3 | The ship goes up, its counter from 0 to 50. |
| 4 / 5 | The player's flag is cleared (`0x418A2D`) and the command pops. |

**While the slot rises:**

- the item's status is 2 (`0x438279`, before anything else);
- items that need it are unavailable (`0x437DD8`);
- the building does not produce, because command 19 sits above its idle
  command.

The build animations show the whole scene: the ship (`drop` or `sauc`
layers), the building appearing, dust (`duts`, `clod`) and sparks (`glit`).
Examples are `SCNCPODBUILD0` in `drop.fin` and `MINDHIVBUILD0` in
`sauc2.fin`.

**Port.** `ScenarioSimulation.Delivery.cs` keeps the phases. The flights last
the 50 updates of command 22, and the build animation its play-once ticks
(`TroopBuildTimings`).

As in the original, a building killed during its delivery loses command 19
with the rest of its stack (`0x416308`). Its player then stays busy and its
slot rising, so that player gets no further deliveries and cannot buy that
slot again. Only the command's end clears the two flags.

The app draws each phase:

- the ship layers of the animation's first frame coming down;
- the animation itself;
- the standing building with the last frame's ship going up.

The flight's height curve is the port's (quadratic, 240 pixels), since
command 22's constants are not decoded. The ship's engine sound (`0x4319C0`,
45 or 82) is not played yet.

## The player's catalog and BUILD

The player buys through the catalog's `count` gadgets and the BUILD button,
implemented by `ScenarioSimulation.Catalog.cs`, `CatalogCountIntent` and
`BuildIntent`.

- **Clicking a gadget** (`0x433124`): event 4 is the left button, event 5 the
  right.
  - The left button adds one to the count and pays the price from P7 at once,
    but only when the price fits P7. A troop counts up to 50; a building or
    research only rises from 0.
  - The right button takes one back and refunds it.
- **BUILD** (`pushb 19` or the space bar, `0x437F3C`): every record in state 1
  with a count loses its count and is ordered:
  - command 9 for a building;
  - command 10 for a troop, with the count;
  - command 12 for research.
- **Command 12** (`0x41CA04`), bytes (type, entity, level, player): sets the
  weapon (`0x4F18B0`) or armour (`0x4F18B8`) level at once. If the level is
  already set, it refunds level × 1000. No building is involved.
- **States** (`0x437BC4`):
  - 0, done: the building's slot holds that variant or a higher one, or the
    research level is reached;
  - 2, unavailable: a prerequisite that is not done, a prerequisite or the
    item itself still rising (`+0xC10`), or a disabled item;
  - 1, offered: anything else.

  `0x437EA0` shows only the offered gadgets. P7 is not part of the state.

Because the port runs in lockstep, the counts are simulation state, kept per
team in `TeamEconomy.CatalogCount`.

The older `PurchaseIntent`, `ProduceUnitIntent` and `ResearchIntent` stay for
saved games, replays and the engine checks. The app no longer sends them.
They name a source structure, and in the original research and troops have
none.

- `depend.txt` prerequisites are evidence-backed. The runnable image's
  purchase helper at `dc16.exe` `0x4566d0` selects from the active player/UI
  list, obtains the cost through `0x4383d4`, compares it with P7 at `+0xbac`,
  applies availability predicate `0x438548`, then debits P7 and resolves an
  action payload through `0x438580`.
- An authored SCN building is live at mission start, not a pending player
  delivery. The compiled simulation now resolves each initial stationary
  building through the executable's `(faction, variant, slot) -> entity`
  table and seeds its matching building dependency as complete. This uses no
  P7 and creates no reservation. It makes scenario prerequisites agree with
  the structures visibly present on the map. The separate eight-value
  `%Depend` team field is retained as an unresolved authored-state field; its
  exact relationship to the broader dependency catalogue is not yet inferred.
- Each team block also carries a sentinel-terminated, fifteen-value
  `%TeamAllies` row. The compiled scenario model preserves it for later
  reconstruction, but does not project it into the runtime relation matrix:
  target acquisition is proven to use a 10×10 matrix and the source-row to
  matrix mapping still needs an executable trace.
- A building-side production queue, build duration, rally point, and spawn
  exit are **not** evidence-backed yet.
- Building purchase and map placement already remain distinct in the compiled
  engine: P7 reservation happens first, and a successful footprint claim is
  what completes the dependency item. The installed SCN corpus exposes entity
  40 only as Petra-7 vent special records; it contains no recoverable pedestal
  actor, pedestal origin, or delivery-timer field. Consequently the current
  direct successful placement is a deliberately documented port policy, not a
  claim that the original building-delivery animation/timing has been matched.
  Do not introduce an authoritative synthetic pedestal or delay until its
  source state and completion boundary are traced.
- Teams with a city train through their city queues (above). Only a scenario
  that declares no city at all, which is an engine fixture, still spawns
  beside a prerequisite structure through `ProduceUnitIntent`.
- The purchase helper sends its resolved action through `0x40c16c`, which
  serializes opcode `9` on queue kind 7. Dispatcher `0x43db64` handles opcode
  `9` by clearing an indexed player dependency byte and refreshing local
  availability through `0x437f24`; that branch neither allocates an actor nor
  creates a production timer, rally point, or spawn cell. The next target is
  the later state-driven consumer of this dependency/action state, not a
  synthetic immediate-spawn interpretation of opcode 9.
- The inverse write occurs in the UI/controller update routine `0x40a754`:
  it sets player bytes `+0x193c` and `+0x194a` together, then calls
  `0x437f24`. This establishes the opcode-9 set/clear pair as
  dependency/availability UI state, not a hidden troop-production queue.
  It therefore cannot justify a timer, rally point, or immediate actor spawn.
