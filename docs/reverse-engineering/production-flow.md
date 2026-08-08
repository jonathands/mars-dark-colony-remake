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

## Recovered order boundary

The troop-selection path at `0x4566ac` calls `0x43839c`, deducts the returned
cost from the player resource field at `player * 0xe30 + 0xbac`, then calls
`0x40c168`. The emitter constructs a compact command-10 packet and submits it
to the generic queue helper `0x421648` with queue kind 7. Its payload writes
the low bytes of the caller registers as troop type, player, and count; the
packet contains no building instance, footprint, or per-building timer. This
is direct static evidence from `0x40c168`–`0x40c193`, rather than an inference
from the UI.

`0x40c168` ultimately feeds the world command dispatcher rooted at `0x43d818`.
The exact message layout and the create-unit branch have not yet been traced
far enough to name the native troop spawn coordinate or any delay. The command
path should therefore not be conflated with the unrelated selected-structure
adapter in the current port.

## Port implications

- `depend.txt` prerequisites and P7 deductions are evidence-backed.
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
- The next reverse-engineering target is the handler reached from the
  `0x40c168` command serialization, followed by a runtime capture of a single
  troop purchase to validate its delay and origin.
