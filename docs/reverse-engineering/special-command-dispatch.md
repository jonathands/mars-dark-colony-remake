# Special-command dispatch boundary

Status: partial executable trace of the installed `dc.exe`; this document
records what is known without assigning generic packet types to a UI ability.

## Generic dispatcher

`dc.exe` `0x43d818` reads the first byte of a queued packet, bounds it through
`0x15`, and dispatches it through the 22-entry jump table at `0x43d7bc`.
This proves that the original world command stream uses compact typed packets.
It does **not** identify which packet, if any, is emitted by the contextual
Napalm/Disease UI buttons.

The helper area at `0x40c13a`–`0x40c406` constructs packets whose leading type
bytes include 9, 10, 12, 13, and 15 before submitting them through queue helper
`0x421648`. Existing production research has direct evidence for the type-10
emitter at `0x40c168`, but that evidence must not be generalized to the two
special abilities.

## Dispatcher handler boundary

The recovered table is useful as a *boundary map*, rather than as a command
name table. Its index is the packet type and its entries are executable
handlers:

| Type | Handler |
| ---: | ---: |
| 0–4 | `0x43d840`, `0x43d91f`, `0x43e18b`, `0x43d96c`, `0x43d85e` |
| 5–9 | `0x43daf7`, `0x43dacc`, `0x43d9aa`, `0x43d9d0`, `0x43da8d` |
| 10–14 | `0x43e08d`, `0x43d877`, `0x43d900`, `0x43dda3`, `0x43dc3e` |
| 15–19 | `0x43e349`, `0x43e0fe`, `0x43e3c7`, `0x43e463`, `0x43e1b1` |
| 20–21 | `0x43da31`, `0x43da6f` |

Type 10's handler scans the active-unit array using its two serialized identity
bytes and then calls `0x43d764`; this is the downstream boundary recorded for
the separately evidenced troop-production route. The nearby constructors
cannot be named from packet shape alone: they share the queue helper and the
dispatcher supports many unrelated state, unit, and team operations.

In particular, neither frame 72 nor frame 73 occurs as a direct immediate in
the executable command code. Those frame values come from the external UI
definition loaded at runtime, so a static constant search cannot recover their
callback. A runtime breakpoint/capture at `0x421648` while pressing each
researched special button is the smallest next experiment.

## Why the ability remains disabled

- The original `intrface/maine` and `bdf.txt` establish the ownership, icon,
  research gates, and labels of the Cyborg cruise-missile/Napalm and
  Psy-raider virus actions.
- `weapstat.txt` and `sound2.dat` give data-supported candidate effect records
  50 (`CY2NDFI.WAV`) and 51 (`PSY2NDFI.WAV`).
- No direct call edge currently ties either source UI event to a packet
  constructor, candidate weapon, ground/actor target, or cooldown state.

Therefore the compiled port may display tech readiness and candidate data, but
must not emit a guessed `WorldCommand` or make either ability executable.

## Next evidence required

Trace a click on button 72 or 73 through the original UI callback to the
packet constructor, then follow that packet through `0x43d818`. The result must
identify command type, payload layout, validation/gating, target representation,
and effect creation before a deterministic engine intent is added.
