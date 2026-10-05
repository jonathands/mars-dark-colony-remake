# Special-command dispatch boundary

Status: statically recovered from the installed `dc.exe` and implemented in
the deterministic engine for ground attacks and commander transports.

## UI dispatch and packet

The predicate at `0x4365d0` recognizes controls 144 (**Napalm Attack**), 145
(**Disease Attack**), 146 (**Ground Attack**), 197 (**Drop Ship**), 143 (the
generic/mixed sentinel), and 198 (**Saucer**). Each calls `0x408ed0` with mode
2, which stores target mode 2 at UI state `+0x7c6`.

On the next world click, `0x409896` converts the pointer to an exact 8.8 cell
centre, calls packet constructor `0x409124`, and clears the mode. The compact
packet sent through `0x421770` is:

| Field | Size | Meaning |
| --- | ---: | --- |
| opcode | 1 byte | `0x1b` |
| team | 1 byte | issuing team from world `+0x7d1c` |
| X | signed 16-bit | ground target X |
| Z | signed 16-bit | ground target Z |

This is a coordinate attack, not an actor-target command.

## Handler and actor state

The opcode-`0x1b` handler at `0x41d06c` reads team/X/Z, scans the 0x320 actor
array for actors selected by that team, and requires entity runtime byte
`+0x10c`. Matching actors receive state `0x12` (18) and store X/Z at actor
`+0xa6/+0xa8`.

The `gamestat` loader at `0x43bab4` maps source values 28 and 29 to runtime
`+0x108` and `+0x110`, then derives `+0x10c = (+0x108 != 0)`. Consequently:

- value 28 is the native ground-special capability/gate source;
- value 29 is the exact special weapon ID;
- SARG (entity 4) resolves weapon 50;
- PSYC (entity 12) resolves weapon 51.

The HUD resolver at `0x436604` masks value 28 to six bits and indexes the
control table at `0x436580`:

| Low-six-bit value | Control | Meaning |
| ---: | ---: | --- |
| 1 | 144 | Napalm Attack |
| 2 | 145 | Disease Attack |
| 3 | 146 | Ground Attack |
| 4 | 197 | Drop Ship |
| 5 | 143 | generic/mixed sentinel |
| 6 | 198 | Saucer |

This direct mapping supersedes the earlier candidate-only inference. BARR (3)
and ATRIL (11) own Ground Attack with weapon 0. Human commander IDs 70/71/72
own Drop Ship with weapons 57/58/59, while Gray commander IDs 74/75/76 own
Saucer with weapons 60/63/64. Lieutenant IDs 69/73 do not own either transport
command.

## State-18 execution

State 18 dispatches through table `0x4792b8` to `0x417ec8`. It reads the
value-29 weapon, compares squared 8.8 distance to `weapon.range² << 16`, and
uses the normal movement path when out of range. The queued-state executor at
`0x418028` additionally requires the SARG or PSYC team ability state to equal
2 (the completed Napalm/Virus research chain).

Ground Attack has no weapon of its own. Both the state handler
(`0x417EFD`) and the executor (`0x41806C`) test the entity type first:

- **BARR (3) and ATRIL (11)** use their ordinary weapon at its current
  team level, read through the `+0x30 + team` selector into `+0x18`.
- The executor then calls `0x412D00` at the stored point every update, with
  no time limit and no ability charge, until a pending order (`+0x36`) pops
  the state. The unit bombards the point until it is given another order.
- **Every other type** reads `+0x110`. A zero there pops the state.

The port implements this in `GroundSpecialWeaponFor` and
`UpdateGroundSpecialAttack`. The range test is `<=`, as at `0x417FD4`.

When ready, the executor temporarily substitutes weapon 50/51 into the
actor's active weapon slot, calls common ground-fire routine `0x412d00` with
the stored coordinate, and restores the ordinary weapon. The shared routine
therefore owns projectile creation plus the ordinary weapon rate, burst, and
reload fields. Weapon owner 50/51 resolves its GUN sound through `slist.dat` to
sound 173 `CY2NDFI.WAV` or 174 `PSY2NDFI.WAV`.

## Projectile modes and transport payloads

The weapon loader at `0x43b784..0x43b7dd` stores the penultimate numeric
source column at weapon byte `+0x44`; common fire reads that byte at `0x413130`
and the projectile constructor stores it at projectile byte `+0x1e`. It is the
`ProjectileMode`. The final source column is stored at weapon `+0x28` and is
retained as `PostFireReset`.

Impact resolver `0x441bec` treats mode 4 as the Napalm/Disease special-effect
path. Modes 5/6/7 construct cumulative Human reinforcement payloads before
calling `0x418f4c`: two entity-0 Security Troops; then one entity-2 Reaper;
then one entity-3 Thunderbolt. The helper creates entity 92 `DROP` as the
Human transport presentation.

Modes 8/9/10 use radii 4/6/8. Helper `0x416f54` scans at most nine hostile,
armed, non-commander actors; the resolver groups them in threes and calls
`0x418f4c` once per group, producing entity 93 `SAUC`. Thus a single packet
may create multiple Saucer deliveries.

## Port boundary

`GroundSpecialAttackIntent` retains the exact cell target. The simulation
validates the source capability, source weapon, completed research item
(Human 80 or Gray 54), map bounds, and installed weapon record. It turns and
moves the actor until the strict native range test passes, and fires one
ground-target projectile through shared cooldown state. On landing, the
projectile applies the authored `boomstat` area template at the target cell
even if no actor occupies that cell. A mode-4 projectile burns instead (see
"The burn").

## The burn

A projectile of mode 4 (Napalm, weapon 50; Disease, weapon 51) does not end
at its impact. `0x441BEC` (`0x441C7B`):

- zeroes its word `+0x18`;
- sets state 3;
- draws its explosion (`0x441C9C`) and puts it on the projectile's channel in
  mode 0, a loop.

No damage is done at the impact itself.

From then on, each substep of the projectile update calls `0x4421B8` for it in
place of a flight. It does work when `+0x18 & 3` is 0, then always adds 1 to
`+0x18`, so it works once an update. Each working call:

1. At count 0, starts sound 63 (`0x431834`) and keeps its handle at `+0x16`.
2. Walks the template's square: half-size `size / 2`, 3 for templates 10 and
   12. It goes x outer, z inner, inside the map, over the ground grid (the
   occupant in the low 10 bits of each cell):
   - An empty cell (`0x3FF`) gets the burning mark `0x3FE` (`0x44232D`), so
     nothing can walk into it.
   - A marked cell is skipped.
   - An actor's cell, when `+0x18 & 0x3F` is 0 (every 16 updates), hits that
     actor through `0x441930`. The multiplier is the cell's template weight,
     the three-quarter flag 0, and there is no same-team scaling. A building
     is hit once per cell it covers, and the burning unit's own side is hit
     too.
3. Once the count passes `0x348` (840), it instead turns every `0x3FE` back
   into `0x3FF`. It then stops the sound (`0x431A58`) and sets state 4, so the
   dispatcher removes the projectile (`0x4429DE`).

The last working count is 844, about 211 updates (14 s at 66 ms). That gives
14 hits, at counts 0, 64, …, 832.

The port does this in `ScenarioSimulation.Burn.cs`. `ProjectileState`
keeps `BurnSubsteps` and its explosion. `BurningCellOccupant` (-0x3FE) is the
mark in `GroundOccupancy`, and the app loops the explosion at the fire. Sound
63 is not played.

The compiled HUD enables frame 72/73 only after its faction's research is
complete and exposes Ground Attack, Drop Ship frame 125, and Saucer frame 126
for their recovered owners. Transport packet impacts use the recovered Human
payloads and Gray radius/eligibility/grouping rules and now create an
engine-owned entity-92/93 lifecycle rather than resolving immediately. Helper
`0x4182e8` and update `0x4183b8` establish 50-tick vertical segments with
`height = base + 3*t*t`: base `0x258` for Drop Ship and `0x4b0` for Saucer.
Creation at `0x41906f..0x4190e5` independently offsets X and Z by exactly one
cell in either direction. State-21 handler `0x418a48` processes one payload
entry per actor update, queues ascent when finished, and removes the transport
after its ascent. The deterministic engine and renderer implement those
phases, heights, offsets, delivery cadence, and cleanup. When a victim is more
than `0x100` Manhattan raw units away, the port now mirrors the state-21 call
to `0x412388`. Native bearing helpers `0x4413a0`/`0x4121a0` reduce an integer
X/Z ratio through the arctangent table to a full 8-bit bearing. Command 4 first
turns the entity from its value-22 initial facing 216 by authored turn speed 10
along the shortest wrapped arc. Command 5 then uses `0x441504`'s 256-bearing
sine vector, projected distance divided by movement speed 50, and signed 8.8
updates until state 21 resumes at the exact victim position. The Gray `0xff`
payload header consumes one update before victim processing, as native. Only
parity with the executable's shared random-table sequence remains open here.

## Firing animation variants

There is no special-only FIREB selection. Loader `0x43b970` builds the entity
fire pointer array: plain `FIRE` occupies slot zero, `FIREA` replaces slot zero
when present, then `FIREB` and `FIREC` append. At `0x412e13`, the common fire
routine consumes a random value modulo entity runtime `+0xe4` and selects the
corresponding pointer from `+0xa0`. Because state 18 calls this same routine
after its temporary weapon substitution, SARG ordinary and Napalm shots both
choose between FIREA/FIREB; PSYC has one plain FIRE family.

The port retains a deterministic variant roll in each `WeaponFireEvent`,
resolves it through the same loader ordering, and consumes it from the same
recovered 256-entry native stream as area-shot scatter and movement jitter.

Still open: exact projectile lifetime integration inside `0x441710`
and whether the original area resolver permits any friendly-fire exceptions.
