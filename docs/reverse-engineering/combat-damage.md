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

The projectile list comes from `0x4263D8`:

- Every frame of the fire animation for the shooter's direction whose hotspot
  7 names a loaded animation adds a launch point and a delay.
- The launch point is (hotspot x * 8, -y * 8) from the shooter. The shot is
  aimed from there.
- The delay is the sum of the ticks of the frames before that frame (each
  frame record's word `+2`).
- Without such a frame, one projectile leaves the actor's center at once.

A FIN logical frame is 164 bytes: layer count, delay, then eight hotspots of
a 16-byte name and x, y words. The name `NONAME` means none (`0x4254D4`).

The constructor stores the delay times four at projectile `+0x12`
(`0x44184D`) and starts the projectile in state 0. Each substep of a state-0
projectile only counts that word down. At zero it turns to state 1 and flies
in the same substep (`0x44244D`). It does not age while it waits. The
drawing loop skips a projectile whose `+0x12` is not zero (`0x439DCB`), so
the shot appears only when it leaves.

**What a shot looks like.** The weapon loader (`0x43B84F`) gives a weapon a
projectile animation (weapon `+0x2C`) only when an animation named
`<sprite>BULLET0` exists. The constructor copies it to the projectile
(`+0x20`, mode 0, looping; `0x441862`), or stores 0 (`0x441869`). The
drawing loop (`0x439D88`) skips a projectile without one (`0x439DC3`).
- **Invisible shots.** Thirty-eight of the 64 weapons have none: the 31
  whose sprite is `weapons` (the marines' and warriors' guns among them),
  `SMOK`'s 3 and `SPAK`'s 4. Their shots show only in the shooter's fire
  animation and the hit.
- **Visible shots.** The rest draw their animation's frame for the
  direction `+0x1A >> 3`; every shipped one has only direction 0. The
  layers take the owner's colour (`+0xE & 7`).
- **Timing.** The projectile pass steps the animation once per update from
  the shot's creation, on the actors' clock (`0x442998`, `0x4264C8`).

Before 2026-10-07 the port drew a yellow dot for every shot without an
animation, and stepped the others one frame per update, ignoring the frame
delays.

The shipped animations have at most one muzzle frame per animation (ATRIL
FIREA, BARR FIREA, SCYT FIREB, TURR FIRE, and XENO XDEPLOYFIRE/XDEPLOYSTAND),
so no shot fires more than one projectile. Some directions have none: BARR
FIREA12 fires from the center at once. Two examples:

- BARR FIREA0's muzzle is frame 1 at (0, -19): 152 units north of the
  shooter, after frame 0's 2 ticks.
- ATRIL FIREA0's is frame 4 at (0, -4), after delays 20, 20, 13, 13: 3 + 3 +
  2 + 2 = 10 ticks.

`NativeFireMuzzles` builds this table from the fire animations
`EntityAnimationCatalog.PreferredFire` selects, and `FireWeapon` launches
from it.

The fire animation itself goes on the shooter's channel in mode 1
(`0x42630C` at `0x412E50`). It plays once on the ordinary animation clock,
`(d + 3) * 15 / 100` ticks per frame. `0x42630C` does not restart a channel
that already plays the same family in the same mode. The app follows both
rules; it used to show one frame per update.

The regression check verifies that an ordinary area shot draws table entry one
for its presentation roll.

## Explosions

The weapon loader gives each weapon up to four explosion animations (weapon
`+0x30`) and their count (`+0x40`):

1. `<sprite>EXPLODE`, else `<sprite>EXPL`, when its frame-0 animation is
   loaded (`0x43B88D`), with count 1.
2. A nonzero boom template whose effect list is not empty replaces them
   (`0x43B8F8`). `boomstat.txt` names each effect as an animation of that
   exact name (`0x43B4A0` resolves it with an empty suffix and fills all 32
   directions with it). The loop reads at most four.

For example:

- the Barrage (10-12) and the Atril (24-26) get NUKE or GASY (templates 1
  and 9);
- SPAK's weapon 37 gets SMAY (template 5), not SPAKEXPLODE;
- the mine (38) gets NUKE (template 2).

At impact, when the count is not zero, the projectile draws its explosion
with `rand % count` from the shared stream (`0x411DB4`):

- **Direct hit** (template pattern size 1, `0x44287F`): after the damage
  (`0x441930`), the projectile moves onto the actor it struck (its x, z and
  height) and draws.
- **Area impact** (`0x441BEC`): projectile mode 4 draws at `0x441C9C`. Modes
  5-10 (transport flights) return without one. Every other mode draws at
  `0x441FC5`, before the area damage.

The projectile then plays that animation once (`0x42630C` mode 1, state 2).
The dispatcher removes it when the animation stops (`0x4429A4`, state 4).

The port draws the same value (`ScenarioSimulation.NextExplosionVariant`) and
reports it as `ProjectileImpactEvent.ExplosionVariant`. A direct hit's event
position is the struck actor's. `WeaponExplosionCatalog` holds the names.
The app resolves them in `anim.dat` order: NUKE is in nuke.fin, which the
game loads, and in effects.fin, which it does not.

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
