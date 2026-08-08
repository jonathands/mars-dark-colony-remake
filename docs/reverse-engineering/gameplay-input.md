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

## Unresolved frame 65

The same two source files disagree in vocabulary for frame 65: `maine` names
the check button **Move & Attack**, while `bdf.txt` calls the icon `patrol (P)`.
The compiled port retains the former label and its existing attack-move adapter
because its terrain-target behavior is separately implemented and tested. It
does **not** bind **P** or claim that attack-move is native patrol behavior

## Waypoint queue feedback

The authoritative `ActiveMoveOrder` retains its native-capped destination
queue and exposes a read-only execution-order projection for presentation and
future replay inspection. The selected-unit status shows the active target and
the number of remaining queued waypoints (`+N WP`). This does not create a
second UI-side queue: only deterministic `MoveIntent` processing mutates it.
For the selected lead unit, the map also draws the same authoritative target
sequence as numbered cyan markers. The route begins at the actor's interpolated
visual position and every destination remains an exact 8.8 cell centre.
until the original control callback and packet path are traced.
