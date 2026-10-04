# Combat damage trace

This note records the recovered collision/damage arithmetic and the runtime
modifiers that feed it.

## Proven executable path

The runnable `dc16.exe` contains the shared damage helper at `0x441c80`.
The older `dc.exe` image contains its corresponding helper at `0x441930`.
Both images establish the same fixed-point path:

```text
factor  = M88[weaponClass][targetArmorClass]      ; loader 0x43B259: trunc(percent * 0.01 * 256)
damage  = (factor * raw) >> 8
damage  = (damage * multiplier) >> 8              ; caller operand
damage  = (damage * armorFactor) >> 8             ; entity +0x24 + selector * 4
if flag: damage = (damage * 3) >> 2
health -= damage
```

- The armor class is the target entity's runtime `+0x40` (gamestat value 11).
- The selector is `entityRuntime[targetEntity].byte[0x38 + targetTeam]` (see
  below). The dwords at `+0x24/+0x28/+0x2C` are `0x100`, then `25600 / v` for
  gamestat values 9 and 10 (`0x43BD46`), i.e. the armor of levels 1 and 2.
- Direct hits (`0x4427AA`) pass multiplier `0x100`, or, for a shooter under an
  Inspire, the commander's runtime `+0xFC` (gamestat value 26) as
  `(v << 8) / 100`. The three-quarter flag is set when the shooter is Human
  (race 0) at night (`+0x53C = 1`) or Gray (race 1) by day.
- Splash (`0x4420F4`) passes the boom template weight `(percent << 8) / 100`
  (`0x43B55A`), scaled by `0x40 / 0x100` for the shooter's own team (exact
  team byte, not alliance), and never the three-quarter flag.
- The Healer special (`0x413E21`) restores `(36 * M88[7][armor]) >> 8`.

`dc16.exe` `0x441c80` additionally confirms that the helper marks the target
as damaged and updates its bounded accumulated-damage byte before taking the
destruction path. Those writes are presentation/notification state separate
from the authoritative health subtraction.

`DamageMatrix.CalculateNativeDamage` implements this arithmetic; the
simulation supplies the multiplier, armor factor and flag at the two call
sites.

## Recovered per-team upgrade selectors

The entity runtime records start at `0x4f1880` (`dc.exe`; `0x505d90` in
`dc16.exe`) with a `0x118`-byte stride. Each record holds two eight-team
selector byte arrays: `+0x30` picks the levelled weapon dword at `+0x18`, and
`+0x38` picks the armor dword at `+0x24`. The object-definition loader clears
both arrays, and the state loader `0x43c784` restores them, so they are
persisted per-team technology.

Network command 12 (`0x41CA04`, entry 12 of the network command table) is
their only writer. It decodes `(kind, entity, level, team)`:
kind `0` writes `+0x30 + team`, kind `1` writes `+0x38 + team`. A local-team
update refreshes dependency availability (`0x437bc4`). Its player bookkeeping
adds `level * 1000` back to P7 (`+0xBAC`) when the selector already held that
level (the shipped upgrade prices are exactly `level * 1000`, so a redundant
purchase is refunded) and to `+0xBB0` otherwise; the port does not model that
refund or the `+0xBB0` tally.

Its only sender is the dependency purchase path `0x437F3C`: an item of kind 2
(an upgrade) sends command 12 with `(item +0x18, item +0x14, item +0x1C)`,
i.e. `depend.txt` parameters 1, 0 and 2. The port's
`UpgradeCategory`/`UpgradeEntityId`/`UpgradeLevel` therefore map exactly to
`kind`/`entity`/`level`, and `ScenarioSimulation.ArmorUpgradeLevel` selects
the armor factor. The native write replaces the selector, while the port takes
the highest completed level; these agree because each level-2 item in the
shipped `depend.txt` requires its level-1 item.

Projectile damage is resolved at collision, not launch. The projectile retains
the weapon's raw damage while in flight; the impact helper then reads the
armor class of the actor occupying the collision cell. This preserves the
native behavior when an intervening hostile intercepts a shot aimed at a
different unit.

## Area-template port boundary

`weapstat.txt` column ten resolves to `boomstat.txt` template IDs. The port
applies each template's authored radial weight grid around the impact cell to
every occupied team, with the same-team scale above. Delayed effects and the
trailing 3×3 template grid remain untraced.

## Shared random consumption

The executable initializes a 256-dword table at `0x478e04..0x479203` and
increments the shared cursor at `0x479204` before each read. Every consumer
in the simulation uses this one cursor. The table is read from the user's
`dc.exe` by `NativeRandomTable`; engine checks without an installation use an
explicitly synthetic stand-in.

Common fire (`0x412DA0`) runs once the shooter faces its target (`0x4120FC`)
and draws in this order:

1. The FIRE/FIREA/B/C variant (`0x412E13`, modulo the entity's variant count).
2. For each projectile, the area aim, when the weapon has a boom template
   (`0x412ED6`).
3. For each projectile, the constructor `0x441710` stores one more draw at
   projectile `+0x1F` (`0x4417B4`).

The projectile list comes from `0x4263D8`: every frame of the fire animation
for the shooter's direction whose hotspot 7 names a loaded animation adds a
launch point (hotspot x * 8, -y * 8) and a delay (the sum of the frame delays
before it). Without such a frame, one projectile leaves the actor's center at
once. A FIN logical frame is 164 bytes: layer count, delay, then eight
hotspots of a 16-byte name and x, y words; the name `NONAME` means none
(`0x4254D4`). The shipped animations have at most one muzzle frame per
animation (ATRIL FIREA, BARR FIREA, SCYT FIREB, TURR FIRE, and XENO
XDEPLOYFIRE/XDEPLOYSTAND), so no shot fires more than one projectile. The port
draws in the native order but does not model those muzzle offsets and delays.

The regression check verifies that an ordinary area shot draws table entry one
for its presentation roll.

## Dying state

A kill calls `0x416308` and then `0x434D48`. Recovered on 2026-10-04 and
implemented in `Simulation/ScenarioSimulation.Death.cs`.

- `0x416308` sets state byte `+0x2C` to 10, empties the command stack, and
  pushes the dying command (10, `0x416460`) with a zero counter. A city
  building (actor index below 0x78) also zeroes its slot health.
- `0x434D48` clears the actor from the grids around its cell.
- The actor stays in the update list (`world + 0x468EC`). The statistics
  recount (`0x419738`, stat 6 and the per-type live counts) and the troop cap
  (`0x41E744`, any nonzero state) keep counting it. Vision keeps stamping it
  with radius `(150 - t) * r / 150`, at least 1 (`0x445D05`).
- The dying command's first run draws a death animation from the shared
  stream (`0x4164B9`). Each later run adds 1 to the counter. At 150 the actor
  is set to state 0 and leaves the update list (`0x416532`).
- A commander (runtime `+0x100`, the Inspire target limit, nonzero only for
  entities 69-76) never counts up: its counter is set to 1 and waits. Unless
  the tileset is `atlantis.bts` (world byte 0, `0x41BB37`) or the player ran
  `nopickup`, `0x416308` calls a transport of the commander's team
  (`0x418F4C`) to the body's position cell. Its first payload word is zero
  and the next two low bytes hold the actor index. On its first delivery run
  (`0x418CAA`) the transport sets the body's counter to 150 and takes off.
  Transports update before other actors, so the body leaves in that update.
  Otherwise the body stays in state 10 for the rest of the game.
- An actor carried off by an abducting transport (`0x416220`) enters the
  same state with counter 1, which skips the animation draw.
