# Troop production flow

Status: partially recovered from the installed `dc.exe`; this is an evidence
ledger, not a claim that production timing or spawn placement is complete.

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
source amount from runtime `+0x0c`. The initialization provenance of those
source fields and the multiplier remain a live-capture target.

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

## Port implications

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
- The present engine spawns outside a completed prerequisite structure's
  executable footprint. With no structure selected, the Build page falls back
  deterministically to the earliest matching live structure. With one selected,
  the HUD instead projects only troop records whose prerequisite list matches
  that exact structure and sends its instance ID to the port-side spawn adapter.
  This makes production buttons and the authoritative validation agree without
  claiming that the native command packet carried a building instance.
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
