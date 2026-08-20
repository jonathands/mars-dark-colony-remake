# Gameplay command-key boundary

Status: UI-source-derived shortcuts mapped into the compiled gameplay adapter.

`intrface/bdf.txt` annotates the original `mainbut` frames as `stop (S)`,
`move (M)`, and `waypoints (W)`. Their matching `intrface/maine` group-40
controls are gadget 150 / frame 62, gadget 33 / frame 63, and gadget 36 /
frame 66. The compiled app therefore maps **S**, **M**, and **W** to its
authoritative Stop, Move-only, and Waypoint-mode adapters, respectively.

Each shortcut remains presentation/input-only: it creates no game state by
itself. A following map command is queued for the next deterministic simulation
tick, and the engine validates the selected actors.

Map-targeting modes are selection-bound in the compiled adapter. Changing the
selection retains attack, ground-special, waypoint, or harvest targeting only when
the new selection still exposes the required engine command profile. This also
applies when a selected actor is destroyed. A paid building-placement mode is
excluded from this cancellation rule because the unrecovered refund path means
the existing reservation must remain completable.

Napalm/Disease use the original target-mode-2 coordinate semantics: the engine
receives an exact map cell rather than an actor identity. The original mode-2
loop consumes a left world click; the compiled control adapter currently keeps
its established RTS convention of selecting with left click and committing all
map orders with right click. That input-side difference does not alter the
serialized deterministic `GroundSpecialAttackIntent`.

## Unresolved frame 65

The same two source files disagree in vocabulary for frame 65: `maine` names
the check button **Move & Attack**, while `bdf.txt` calls the icon `patrol (P)`.
The compiled port retains the former label and its existing attack-move adapter
because its terrain-target behavior is separately implemented and tested. It
does **not** bind **P** or claim that attack-move is native patrol behavior.

## Waypoint queue feedback

The authoritative `ActiveMoveOrder` retains its native-capped destination
queue and exposes a read-only execution-order projection for presentation and
future replay inspection. The F12 diagnostic readout shows the selected unit's
active target and number of remaining queued waypoints (`+N WP`). This does not
create a second UI-side queue: only deterministic `MoveIntent` processing mutates it.
For the selected lead unit, the map also draws the same authoritative target
sequence as numbered cyan markers. The route begins at the actor's interpolated
visual position and every destination remains an exact 8.8 cell centre.

## Native selection gesture and storage

The viewport handler at `0x4096e8` records ordinary primary-button down and
promotes it to a box after more than 45 Manhattan pixels or 1,500 ms. Its
selection worker at `0x436d1c` clears the previous list unless Shift is held;
Shift toggles matching actors. The list begins at gameplay `+0x150`, ends at a
`-1` word sentinel, and spans `0x320` entries up to the waypoint state at
`+0x790`. This matches the executable's 800-slot world-actor table.

The compiled app now uses normal left drag for this selection gesture and caps
the deterministic instance-ID selection at 800. Middle drag remains an
explicit port camera-pan convenience; native edge scrolling, minimap panning,
and keyboard navigation are unchanged.

The three scans in `0x436d1c` are now mapped through the construction path at
`0x41b3f9`: runtime field `+0x68` takes priority and selects grid `+0x1004`,
otherwise movement class zero selects grid `+0x804` and a nonzero class selects
grid `+0xc04`. The `gamestat.txt` loader at `0x43bab4` maps `+0x68` to numeric
field 14; only entity 45 and 46 (`HMINE`) set it. The layers are therefore
ground, air, and mines. At `0x43713b`, Alt skips ground and Ctrl skips air; the
mine scan is never skipped. The port captures those modifiers when the gesture
starts and applies the same filter to both click and rectangle selection. Alt
selects air plus mines, Ctrl selects ground plus mines, and both select mines.

After scanning, `0x4371e0` calls `0x41ae78` for each selected actor. That
predicate compares actor owner byte `+7` with the local team at world `+0x7d1c`.
If any selected actor is local, the worker compacts the list to local actors;
if none is local, it retains the remote-only selection. The port now preserves
this distinction: visible remote actors can be selected and display their unit
identity (with live statistics available through F12 diagnostics) as
inspection-only, while every command adapter continues
to consume only locally controllable actors.

## Native viewport unit hotkeys

Gameplay keyboard dispatcher `0x40a044` maps internal actions 8-17 to F1-F10.
Each action calls rectangle-selection adapter `0x409624` over the current
viewport with a paired Human/Gray entity identity. The adapter forwards the
keyboard event's Shift, Ctrl, and Alt bits into the same selection worker, so
Shift toggles while Ctrl/Alt retain their ground/air filters.

| Key | Entity IDs | Recovered identity group |
| --- | --- | --- |
| F1 | 69-72, 73-76 | all four Human/Gray commander ranks |
| F2 | 0, 8 | Human/Gray basic troops |
| F3 | 2, 10 | Reaper/Scythe pair |
| F4 | 49, 50 | Beon/Zisp healer pair |
| F5 | 5, 13 | SCGM/Ortu pair |
| F6 | 3, 11 | Barrager/Atril pair |
| F7 | 43, 44 | Engineer/Slom pair |
| F8 | 4, 12 | Cyborg/Psy-raider pair |
| F9 | 6, 14 | Exploiter/Slug harvester pair |
| F10 | 1, 9 | Turret/Xeno pair |

The F1 handler calls the selection adapter four times and sets gameplay
`+0x137` only for calls two through four. That field is therefore an internal
append/toggle flag for assembling all commander ranks, not another physical
modifier or a double-click state. The compiled port applies each recovered
identity set in one deterministic selection operation. Its debug overlays have
moved from conflicting F3/F4 bindings to F12 (asset names) and Shift+F12 (PTH
regions).

Click and rectangle selection now share the renderer's active-frame projection:
deployed, firing, hit, moving, and standing forms all resolve the same FIN frame,
origin, canvas, and opaque visual rectangle used for drawing. Clicks retain an
exact alpha test; box selection intersects the opaque rectangle. This replaces
the earlier logical-origin-only box test, which could miss a large actor even
when its visible body lay inside the selection rectangle.
