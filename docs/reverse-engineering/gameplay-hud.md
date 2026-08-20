# Gameplay HUD identity and lower readouts

Status: source-layout and screenshot corroborated; executable writers remain partly unresolved.

## Recovered control geometry

The shipped `intrface/maine` file defines these text controls at the original
640x480 logical scale:

| UI ID | Declaration bounds | Current evidence-backed role |
| --- | --- | --- |
| 79 | `(520,404,15,1)` | shared selection identity / command-status strip |
| 204 | `(10,425,72,1)` | wide lower readout, exact native writer unresolved |
| 203 | `(10,440,72,1)` | wide lower readout, exact native writer unresolved |
| 148 | `(50,462,61,1)` | bottom message/status strip |
| 75 | `(524,456,72,17)` | numeric P7 counter |
| 234 | `(613,433,...)` | completed-days text |
| 200 | `(480,463,3,1)` | adjacent three-character readout, native writer unresolved |

The original selected-Trooper gameplay capture
`host-game-after-escape2.png` shows `Trooper` in the warm-yellow strip beginning
at `(520,404)`. This disproves the port's earlier mapping of 203/204 to
always-visible invented name/stat lines. Executable tracing below establishes
that command prompts also replace the idle identity in control 79.

The executable click loop corroborates UI 75 as the P7 counter: after resolving
the local team it loads the team's resource value, sets `EDX=0x4b` (75), and
calls `0x42b104`. Therefore the cyan number in this region must not be labelled
as selected-unit health. `mainbut.spr` frames 104 through 113 are ten 12x17
images, exactly filling the counter's 72x17 bounds with up to six digits. The
compiled port draws these shipped digit frames right-aligned instead of using a
modern font.

## Compiled-port rule

- UI 79 displays the selected entity identity, a compact multi-selection
  identity, or the active catalog/options title while idle. Waypoint/target
  prompts and hovered command labels replace it. Text is capped to the authored
  15-character capacity.
- Detailed order/stat text in lower readouts 204/203 is explicitly an F12
  diagnostic until their native executable writers are recovered.
- Authoritative gameplay outcomes continue through the port's message-history
  adapter in control 148. Native initialization at `0x44da88` clears slots
  0 through 15, so the compiled adapter retains 16 entries. Control 200 remains
  blank: its native writer is unresolved and the earlier three-digit history
  index was removed as unsupported guesswork.
- `maine` text messages 180–183 author the runtime phrases `Set waypoints.`,
  `Select target.`, `Too many units`, and `Issuing refund`. The compiled input
  adapter now uses the first three for applicable command/selection feedback.
  Refund execution is still unrecovered, so the fourth phrase is retained in
  the layout model but is not emitted by a fabricated refund path.

The executable strengthens the first two mappings. At `0x4096f6`, command-mode
byte `+0x7c6 == 1` resolves text message 180 through `0x4226a0` and passes the
result to `0x4322d8`; the parallel branch at `0x409896` does the same with
message 181 when the mode is 2. The keyboard handler at `0x40a578` also emits
message 180 before entering mode 1. This establishes waypoint and target-mode
prompts independently of their English labels in `maine`. Both paths call
`0x4322d8`, whose normal branch writes the supplied string to UI 79. While the
local refund timer is active, that dispatcher alternates messages 182/183 every
30 timer units before writing the same control.

The click dispatcher at `0x43370b` maps button 149 to `0x44dacc` (advance when
not already at the newest entry) and button 147 to `0x44dafc` (decrement when
above zero). The refresh routine beginning at `0x433a20` writes the selected
history string to control 148 and changes button availability. This proves the
message line and navigation direction without relying on their English labels.

## Reproducible executable inspection

`dc-port-26/tools/disasm_range.py` prints an explicit PE virtual-address range.
`dc-port-26/tools/disasm_immediates.py` locates literal immediates in executable
code. Both tools enable Capstone's skip-data mode because Watcom's executable
section contains embedded data/alignment gaps; without it, a linear scan stops
before later gameplay code and produces false negative xref reports. The fixed
scan finds direct writers for 79 and 148 but no corresponding direct UI setter
for 203/204. Their roles therefore remain unresolved.

## Selection command-state projection

`UnitCommandProfiles.DescribeSelection` is the common state source for the
ordinary group-40 controls. It reports whether any actor in the complete local
selection can Stop, Move, Attack, or accept Waypoints. Rendering and click
dispatch consume those same flags, so a button cannot be drawn enabled through
one mobile-only query and rejected through a different all-selection query.

Movement, Attack, and Waypoints intentionally apply to the capable subset, as
the corresponding native handlers scan selected actors and reject ineligible
ones individually. The contextual fifth and secondary sixth slots retain the
stricter homogeneous rule: every selected actor must resolve the same command
definition. A mixed selection containing a mobile unit plus a static or
differently typed actor no longer exposes an action by silently discarding the
other selected actors.

The command catalogs also own `UnitCommandActivation`: `Immediate`,
`MapTarget`, or `Pending`. Harvest, Ground Attack, Napalm/Disease, Drop Ship,
and Saucer are map-target commands; mine/tower deployment, healing, stealing,
and Commander Inspire are immediate. Drop Ship/Saucer ownership comes from the
commander entity records (70-72 and 74-76), not from transport entities 92/93.
WinForms still routes concrete intents, but it no longer carries a second
hard-coded list deciding which recovered commands are executable.
