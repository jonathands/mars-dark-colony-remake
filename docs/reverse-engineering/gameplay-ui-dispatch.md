# Gameplay UI dispatch boundary

Status: partial static trace of the installed `dc.exe`. This records the
event-to-dynamic-UI boundary needed to resolve individual `maine` controls;
it does not assign a behavior to a control from its icon or label.

## Shared gameplay loop

`0x433124` is the gameplay UI event loop. It reads records through `0x4240dc`
and checks event type 4/5 branches. The branch does not contain literal IDs
for group-40's Move-only (33), Move & Attack (35), or Waypoints (36) controls.
Those IDs belong to `maine` records constructed at runtime, so a scan for
`0x23` in the outer dispatcher is not a valid command mapping.

The nearby `0x43326a` onwards branches deal with other dynamic control ranges
and gameplay panels. They establish that the UI is resolved from active
records, but they do not yet expose the callback selected for control 35.

## Recovered handler call contract

`0x423ae0` is the active-record mouse dispatcher. For ordinary uncaptured
input it scans the same 300 records (`gameplayUi + 0x88 + index * 52`), requires
the active bytes at `+2/+3`, tests the record rectangle at `+8/+0xc/+0x10/+0x14`,
and checks the handler object at `+0x2c` is enabled (`handler + 4 != 0`). On a
hit it invokes:

```text
EAX = gameplay UI context
EBX = input-event record
EDX = resolved control index
CALL [handler object + 4]
```

The captured-control route uses the same virtual slot, with the cached index
at gameplay-UI `+0x431c`. This is the direct executable boundary for a
group-40 callback. The handler pointer itself is loaded when the external UI
definition is constructed, so it cannot be recovered by scanning the static
code for control 35 or frame 65.

The examined construction region (`0x428514` and `0x428a78`) is likewise
polymorphic: it stores parsed construction state and delegates resource/object
creation through interface method slots (`+0x40`, `+0x44`, and `+0x5c`). It
does not reveal one static push-button callback address. This corroborates the
need to inspect the handler pointer after the `maine` record has been built.

## Excluded generic-gadget helpers

The two apparent downstream functions are now classified and excluded from
the command behavior trace:

- `0x42776c` computes `gameplayUi + 0x88 + index * 52`, asserts gadget type
  `6` at record `+1`, and returns the first word of the record's `+0x28`
  payload.
- `0x4277fc` addresses the same type-6 record, writes its caller value into
  that `+0x28` payload, then calls generic UI refresh helper `0x421cc0`.

They are generic type-6 gadget data accessors/setters. They do not invoke a
per-control handler and do not map control 35 to patrol, attack-move, or any
other unit behavior.

## Next trace

Start from a loaded `maine` group-40 record after its hit test resolves, then
follow the handler object / virtual callback invoked by the UI hit-test
dispatcher (`0x423ae0`) at `[handler + 4]`. A runtime capture of that callback
is preferable to matching icon frames: `bdf.txt` and `maine` use different
naming/frame roles, and the executor behavior is not encoded in a shared
integer constant.

This is the same evidence boundary that prevents promoting the current
Move-&-Attack adapter to a claim of native patrol behavior.
