# Unit special-command reconstruction

This reference covers the contextual controls in `intrface/maine` group 40.
It records only identities and transitions demonstrated by installed data or
the executable-recovered UI; it does not turn a label into a game rule.

For a multi-unit selection, the fifth and sixth slots are exposed only when
every selected unit resolves to the same command definition. Rendering and
click dispatch use that same engine-owned aggregation rule, so a mixed group
cannot display a disabled generic slot yet execute the action for only its
matching members.

| Native label | `maine` frame | Command owner | Proven resulting form | Port state | Still unresolved |
| --- | ---: | --- | --- | --- | --- |
| Deploy | 74 | Human `EXPL`, Gray `SLUG` | `EDPLY`, `SDPL` | Implemented: exact-cell 50-tick attachment plus proven EDPLY/SDPL→mobile retraction from the same control/Enter path. | Native income amount/timing. |
| Deploy Mine | 69 | Human `ENGI`, Gray `SLOM` | faction-matched `HMINE` (45/46) | Implemented: 50-tick state-13 preparation, same-actor ID +2 transition into the mine grid, native ordered/scored acquisition inside firing range, decoded cooldown, and 300-integrity-per-trigger lifecycle. | Persistence of a native out-of-range state-1 target lock. |
| Heal Units | 122 | Human `BEON`, Gray `ZISP` | None established. | Implemented: native radius-7 scan order, first damaged ally only, class-7 amount, charge threshold/drain/recovery, HUD charge, animation, and sound. | Exact UI button-disable predicate, if any. |
| Deploy Turret | 68 | Human `TURR`, Gray `XENO` | `T` (41), `XDEPLOY` (42) | Implemented: in-place static form, original deploy FIN, and decoded tower weapons. | Native cancellation/redeployment policy. |
| Inspire Troops | 121 | Commander entity IDs 69–76 | None (temporary actor state). | Implemented: native 50-tick cast, occupancy scan, rank limits, recipient filter, randomized countdown, HUD marker, and exact-center aim state. | Exact native random-table sequence and the normal shot-spread weight table. |
| Steal Money | 75 | Human `SARG`, Gray `PSYC` | `SARGSTL` (77), `PSYCSTL` (78) | Implemented: recovered bidirectional mobile/static transition, DEPLOY/RETRACT FIN presentation, and campaign-authored 50% interception on the miner income pulse. | Exact native range and competing-thief arbitration. |

## Sixth-slot special attacks

`maine` reserves the adjacent sixth command position `(518,317)` for its
contextual special attacks. It contains a generic **Second Attack** declaration
using `mainbut.spr` frame 2, plus **Napalm Attack** (frame 72) and **Disease
Attack** (frame 73). The companion `intrface/bdf.txt` button dictionary names
frame 72 “cyborg call cruise missile” and frame 73 “psych raider deploy virus”.
That direct icon/action vocabulary is stronger evidence than the adjacent
generic comment, so the port maps Human Cyborg (`SARG`) to **Napalm Attack** /
72 and Gray Psy-raider (`PSYC`) to **Disease Attack** / 73. Both are now live
after their authored research gate. The generic frame-2 and **Ground Attack**
declaration have no verified owner yet.

The paired gates are data-derived. Human item 80's source comment calls it
“cyborg nuke 2”; its UI ID 131 is **Napalm** using icon 35, which `bdf.txt`
calls “cyborg cruise missile ability.” Gray item 54 targets entity 12 and its
UI ID 78 is **Virus Sac**, using icon 45 “psych raider virus ability.” Despite
both records carrying kind-2 weapon-category/level-2-shaped fields, they are
ability research rather than weapon-stat levels. The compiled port preserves
both completion states: the unit HUD reports **TECH REQUIRED** before the
prerequisite and enables the original frame afterwards. The selected-unit stat
line additionally shows `NAPALM TECH:ON/OFF` or `DISEASE TECH:ON/OFF`.

`weapstat.txt` weapon 50 is explicitly **Napalm effect**, uses weapon class 6
and boom template 10; weapon owner 50's GUN list resolves to sound 173
`CY2NDFI.WAV`. Weapon 51 is the paired class-6 effect, has a 12-shot authored
burst, uses boom template 12, and owner 51 resolves sound 174
`PSY2NDFI.WAV`. SARG's ordinary slots are 13/14/14 and PSYC's are 30/27/28, so
50/51 are not their normal progression. The direct link is now proven by the
state-18 read of entity runtime `+0x110`, which the loader maps to `gamestat`
value 29. Target mode 2 emits opcode `0x1b` with exact 8.8 X/Z, and state 18
temporarily substitutes that weapon before calling the common fire routine.

The SARG FIN has separate `FIREA` and `FIREB` families, while PSYC has one
`FIRE` family. Executable loader `0x43b970` places FIRE/FIREA in pointer slot
zero and appends FIREB/FIREC; common fire routine `0x412e13` chooses a pointer
by random value modulo the installed variant count. State 18 calls that common
routine, so Napalm has no dedicated FIREB rule: both ordinary and special SARG
shots may use A or B, while PSYC always uses its sole plain family. The port
projects this choice through a deterministic `WeaponFireEvent` roll.

The same physical slot has two further source labels: **Drop Ship** (frame
125) and **Saucer** (frame 126). They belong to commander ranks, not to the
transport entities themselves. HUD routine `0x436604` reads entity runtime
`+0x108 & 0x3f` and uses the table at `0x436580`: index 1 selects Napalm
(control 144), 2 Disease (145), 3 Ground Attack (146), 4 Drop Ship (197), 5
the generic Second Attack sentinel (143), and 6 Saucer (198). Loader evidence
maps `+0x108` to `gamestat` value 28. Human commander IDs 70/71/72 carry value
132 (low six bits 4) and special weapons 57/58/59. Gray commander IDs 74/75/76
carry value 134 (low six bits 6) and weapons 60/63/64. The lieutenant records
69/73 do not own either transport command. Entity 92 `DROP` and entity 93
`SAUC` are the resulting presentation/transport actors.

The weapon loader maps the penultimate numeric source column to runtime byte
`+0x44` (`ProjectileMode`) and the final column to `+0x28`
(`PostFireReset`). The recovered impact resolver assigns modes 5/6/7 to Human
reinforcement packets: respectively two Security Troops; those two plus one
Reaper; and those three plus one Thunderbolt. Modes 8/9/10 are Gray abduction
packets with radii 4/6/8. The native scan collects at most nine hostile armed
non-commander actors and creates one Saucer payload per group of at most three.
The compiled engine implements these payload and eligibility rules and exposes
both controls as live map-target commands. It now owns the recovered transport
lifecycle: a 50-tick state-22 descent, state-21 incremental payload processing,
and a 50-tick state-22 ascent followed by cleanup. Drop Ship uses base height
`0x258`, Saucer uses `0x4b0`, and both begin at independently selected
one-cell X/Z offsets. Saucer pursuit uses the same command-4/5 sequence as
native movement. Entity 93 starts at facing byte 216 (`gamestat` value 22),
turns along the shortest wrapped arc by its authored speed 10 per update, then
uses the full 256-bearing sine-vector projection at movement speed 50. State 21
copies the exact target after integer-residue interpolation before removal. The
Gray `0xff` payload header also consumes its separate native update before the
first victim. Only shared random-stream parity remains open in this path.

## Identity evidence

- `maine` text controls 138–142 supply the labels and their associated button
  frames: Heal 122, Deploy Turret 68, Deploy Mine 69, Inspire Troops 121, and
  Steal Money 75.
- `bdf.txt` is a partial (0–103) icon dictionary, not a replacement runtime
  binding table for group 40. It calls icon 67 “dig (slug & exploiter),” but
  `maine` places the actual generic Deploy control at frame 74. The same
  mismatch repeats for 68/69/75: `bdf.txt` calls 68 Human turret, 69 Xenowort,
  and 75 mine, whereas `maine` assigns 68 Deploy Turret, 69 Deploy Mine, and
  75 Steal Money. Therefore `maine` remains the port's HUD frame/slot
  authority, while `bdf.txt` is corroborating vocabulary only where the two
  sources agree (notably the distinct 72/73 special attacks). The deployed
  `EDPLY`/`SDPL` records, paired Deploy/RETRACT FIN ranges, and deploy sounds
  independently support the implemented Exploiter/Slug vent transition; they
  do not establish a frame-67 replacement.
- `gamestat.txt` supplies the paired Human/Gray records and their entity IDs.
  The commander range is explicit in the record comments (69–72 Human, 73–76
  Gray), even though each rank shares its race's ordinary `TRSC`/`GRAY` art
  code.
- `turr.fin` contains `TURRDEPLOY0` (32–48); `xeno.fin` contains
  `XENODEPLOY0` (150–161). Their deployed records provide static movement and
  populated tower weapon slots.
- `sarg.fin` and `psyc.fin` contain `SARGDEPLOY…` / `PSYCDEPLOY…` transition
  families immediately beside `SARGSTLSTAND…` / `PSYCSTLSTAND…`. Thus the
  mobile SARG/PSYC records own the Steal Money command; the `*STL` records are
  output forms, not alternate command owners.
- `gamestat.txt` field 30 is a Human/Gray counterpart reference, not a
  transform destination: `SARG` (4) links to `PSYC` (12), `BEON` (49) to
  `ZISP` (50), and `SARGSTL` (77) to `PSYCSTL` (78). That eliminates a
  tempting but incorrect generic deployment rule for the Steal Money forms.
- No shipped SCN placement uses entity 77 (`SARGSTL`) or 78 (`PSYCSTL`), so
  placement alone cannot establish a victim or range. Human 10 and Alien 11
  campaign briefings supply the gameplay contract: a deployed S.A.R.G.E. or
  Gorrem near an enemy mining unit intercepts 50% of that miner's income, has
  long range, and does not require visual sight. The port applies that share
  on the deterministic attached-harvester pulse. Until the executable yields
  an exact metric, it uses a documented 12-cell Chebyshev range and assigns a
  miner to the nearest eligible thief (then lowest instance ID) without
  stacking.

## Executable immediate-special path

Gameplay click dispatch at `0x43363c` classifies controls 37 and 138–142 with
the table at `0x436560`, then calls `0x409418`. That routine emits packet
opcode `0x1a` with only the issuing team: no button ID, map coordinate, or
actor target is serialized. Handler `0x41cf8c` scans the selected-actor mask
and enters actor state 13 only when runtime entity field `+0x104` is nonzero.
The loader at `0x43bab4` proves that field is numeric `gamestat.txt` value 27.

State handler `0x416784` revalidates the entity and requeues state 13 with a
50-tick timer. Its admitted identities include the tower pair, mine pair,
healers, commanders, deployed stealing pair, deployed harvesters, and artifact
entities. This proves the native buttons share an entity-specific immediate
state mechanism; it does not prove a button owner merely from its text. In
particular, `BEON`/`ZISP` and `EXPL`/`SLUG` have value 27 equal to zero, so
their already implemented heal and vent interactions must not be cited as
opcode-`0x1a` behavior without another executable path.

### Commander Inspire completion

State-13 completion at `0x417caa` supplies the missing Commander rule. When
the active entity's runtime `+0xfc` is nonzero it passes runtime `+0x100`, the
commander actor index, and the commander's 8.8 X/Z to helper `0x417168`.
Loader `0x43bab4` maps those fields to `gamestat` values 25 and 26. The eight
commander records use value 25 as a nonzero area-effect gate and value 26 as
rank limits 6, 8, 10, and 12 for both races.

The helper scans both ordinary occupancy layers in a fixed isometric area:
for radii 0 through 10 it walks four sides, each spanning offsets -20 through
20. It tests the ground grid before the alternate grid and intentionally does
not deduplicate repeated center-line probes. A recipient must have the exact
same team byte, a resolved normal weapon (`entity runtime +0x0c != -1`), and
its own `+0xfc` must be zero. Each qualifying occupancy hit consumes one
rank-limit count, stores the commander index at actor word `+0xd8`, and
replaces actor byte `+0xd6` with `(random & 0x0f) + 0x14`, or 20 through 35.

Actor update `0x4192f0` decrements `+0xd6` only when the low four world-counter
bits are zero, so the effect lasts 320 through 560 world ticks (about 21.12 to
36.96 seconds at the port's 66 ms fixed tick). Common fire `0x412ef7` checks
that byte: while active it selects the exact center `(1,1)` instead of the
normal 3x3 random aim point. This is an accuracy/aim-lock effect, not a damage,
armor, speed, or rate-of-fire bonus. Recasting overwrites the countdown; there
is no additive stacking in this path.

The compiled engine owns the delayed cast, exact scan/probe order,
eligibility, rank limit, deterministic low-nibble countdown, source link, and
16-tick decay. The HUD enables frame 121, reports casting/completion, and marks
inspired actors in gold. Loader `0x43b596` proves the 3x3 weights come from each
`boomstat.txt` record's trailing square, not sprite opacity. Ordinary area
shots now consume those row-major weights from the recovered shared random
table; inspired actors bypass the roll and aim at the exact center.

The shipped training scripts also preserve a user-facing shortcut. Human and
Alien training 3 says Enter deploys the
Sentinel/Sloom mine unit, while training 6 says Enter deploys the
Firestorm/Xenowort and uses the same wording for the Exploiter/Brozaar. A
native keyboard branch at `0x40a546` accepts both line-feed and Enter and calls
the same `0x409418` immediate-special packet path as the HUD controls. The
compiled adapter now maps Enter for the executable-backed in-place tower,
mine, and stealing transitions, gated by the active form's
`ImmediateSpecialCode`. Harvester
deployment still asks for a vent target and is correctly excluded from opcode
`0x1a` because mobile EXPL/SLUG value 27 is zero. Once attached, EDPLY/SDPL
carry value 7 and Enter invokes their proven reverse transition. Likewise,
SARGSTL/PSYCSTL retain value 5 and toggle back to SARG/PSYC. Deployed towers
and mines carry zero, so the port does not invent reverse transitions for them.

## Harvester vent-target path

The separate path is now recovered. Gameplay target dispatch at `0x414970`
recognizes entity 40 (`VENT`) and calls `0x413490`. That routine uses the
vent's exact 8.8 cell to read the ordinary ground occupancy grid, accepts only
an occupant whose entity byte is 6 (`EXPL`) or 14 (`SLUG`), and stores `0x32`
(50) in the vent-side countdown. When it expires, the routine copies the
harvester's owning byte to the vent, changes the same occupant's entity byte
from 6 to 47 (`EDPLY`) or 14 to 48 (`SDPL`), and submits the resulting actor
state through `0x411dd8`. This disproves the prior adjacent-cell attachment
approximation and confirms that the deployed tower is a form of the original
harvester, not a spawned replacement. The engine therefore paths to the exact
vent cell, exposes the pending countdown in the unit HUD, and starts P7 income
only after the authoritative transition event.

The state-completion routine at `0x417b0c` provides the decisive mine model.
At `0x417d50`, ENGI/SLOM IDs 43/44 are accepted, `0x434d48` removes the prior
world-grid membership, the entity byte is incremented by two to HMINE 45/46,
and the same actor index is stored in world grid `+0x1004`. The earlier
spawn-an-extra-mine approximation was therefore wrong. The engine now keeps
the actor instance and position, changes its effective definition and health
ceiling, releases its old movement-layer claim, and claims a distinct mine
occupancy layer. The executable also checks that mine-grid cell for its empty
`0x3ff` sentinel before allowing the transition.

Common fire supplies the remaining trigger lifecycle. At `0x41310f` it
recognizes HMINE IDs 45/46, subtracts `0x12c` (300) from actor health, clamps
the result to one, and then continues through the ordinary projectile and
weapon-cooldown paths. The shipped mine starts at 800 health and weapon 38 has
rate 150 plus boomstat template 2, so the integrity sequence is
`800 -> 500 -> 200 -> 1`. Its ordinary area effect includes its source under
the same-team splash rule, allowing the third projectile to destroy the
one-health mine rather than requiring a special delete. The compiled engine
now preserves that three-trigger model and exposes remaining triggers/rearm
ticks in the mine debug HUD. State 13's existing 50-tick timer is the supported
deployment/arming delay; no second post-transition delay has been found.

Idle state 1 calls selector `0x435570`. It walks the fixed offset table at
`0x434090`, probes ground then alternate then mine occupancy, rejects hostile
targets that the weapon/armor matrix says cannot be damaged, and scores armed
targets above unarmed targets. Because weapon 38 is an area weapon, the
candidate's ordinary-ground 3x3 neighborhood then contributes +10 per hostile
occupant and -15 per cooperative occupant. Equal scores preserve the table's
order. The range-one firing comparison admits the mine's cell and its four
cardinal neighbors, so the compiled trigger selector now reproduces those
native filters and priorities. Native state 1 may also lock a farther actor
before the static executor cannot pursue it; that non-firing persistence edge
remains isolated rather than being invented in the direct trigger adapter.

The same completion switch establishes which forms are reversible. It maps
EDPLY 47→EXPL 6, SDPL 48→SLUG 14, SARGSTL 77→SARG 4, and PSYCSTL 78→PSYC 12.
The engine retains actor identity and occupancy, clears only the active form,
and restores the source definition's movement and health ceiling. The asset
catalog now resolves the shipped directional `RETRACT` families separately
from `DEPLOY`, so presentation follows the transition instead of replaying the
deployment sequence backwards. No corresponding reverse case or nonzero
immediate-special code exists for T/XDEPLOY or HMINE.
- Healer identity is additionally corroborated by `slist.dat`: both entity 49
  (`BEON`) and entity 50 (`ZISP`) use action category `DPY` sound 138, and
  `sound2.dat` resolves 138 to `HEAL.WAV`. `animate/curs.fin` also contains the
  dedicated `HEAL&REPAIR` cursor family (frames 32–34). Together with
  `mbullet.txt` class 7 ("healing ray resistance"), this establishes a real
  contextual heal/repair path rather than a generic attack. The collision
  routine at `dc.exe` `0x413e21` reads that row using the target's defense
  class and restores `floor(36 * matrixValue / 256)`, capped at missing health.
  Its caller at `0x413c20` establishes the executor: it scans both ordinary
  occupancy grids for every square radius 0 through 7 and rejects dead actors
  and team-byte mismatches. Source byte `+0x0a` must be at least 4 before a
  probe; the first successful heal clears it to zero, so scanning stops before
  a second damaged ally. Constructor `0x41b335` initializes the byte to `0x40`.
  Common actor update `0x4192c0` adds entity runtime `+0xf8` every 32 world
  ticks and saturates at 255; the loader maps `+0xf8` to source value 25, which
  is 1 for BEON and ZISP. A successful action then submits state 13 with timer
  `0x32`. Thus the command is an immediate first-matching same-team area action,
  not a selected-target cursor or a heal-all pulse. The engine implements that
  state, and the selected-unit diagnostic line exposes `HEAL:current/255`.
- `beon.fin` and `zisp.fin` contain stand, move, die, and `DEPLOY` families,
  but no local `HEAL` or `FIRE` family. Shared `glot.fin` supplies
  `BEONFIREA0` (frames 0–17) and `ZISPFIREA0` (18–35), which the entity
  animation catalog resolves as their directional firing presentation. The
  port plays that recovered animation alongside the heal sound and aggregate
  HP feedback. The meaning of the local `DEPLOY` families is
  still untraced.

## Implementation boundary

`DarkColony.Engine.Data.UnitSpecialCommandCatalog` and
`UnitSecondaryCommandCatalog` are the sole ownership/frame mappings. The
WinForms/Direct3D app projects them into the HUD and only enables commands
whose engine intent has a tested authoritative path. This prevents the UI from
drifting into a second command-identity table or presenting a plausible but
invented special ability as playable.

The App-side `GameplayHudLayout` is the corresponding geometry adapter for
the common group-40 controls and selected-unit readouts. At runtime it loads
the shipped `maine` definition through `InterfaceDefinition`, preserving the
source gadget IDs, native rectangles/roots, frames, and labels rather than
maintaining a parallel hard-coded layout. Its explicit constants are only the
no-installation / invalid-interface fallback.
The adjacent Build, Research, and Options tabs use the same adapter: their
hit rectangles come from push controls 0–2 and their visual frames from the
overlapping picture controls 3–5. This retains the source's separate input and
presentation declarations.
The six gameplay-options controls (Quit, Save Game, Options, Allies, Pause,
and Objectives) are likewise read directly from source IDs 62, 63, 64, 151,
196, and 202. The port's Pause/Resume label is live state layered over the
original Pause control; it does not replace that UI identity.
Production and research rendering/hit-testing now resolves every authored UI
ID through that same definition. Their small faction tables remain only for
the no-install fallback and the evidence-backed overlap policy between paired
upgrade/build variants; installed geometry is not duplicated in the App.
The Last/Next message controls likewise use source IDs 147 and 149, preserving
the original bottom-strip navigation geometry around port-side status history.
That includes `maine` `in_text` 204 (name at `(10,425)`), 203 (stats at
`(10,440)`), 79 (command status at `(520,404)`), and 148 (resource status at
`(50,462)`). Command highlighting is bound to the port's command-mode identity,
not a rendered label string; the options page keeps its separately overlapping
group geometry. This matters because `maine` reuses the same screen rectangles
across contextual groups.

`DarkColony.Engine.Data.InterfaceDefinition` now decodes the source definition
as external data (logical size, labels, gadget rectangles/frames, and raw group
values). It intentionally does not infer callback behavior or treat a group's
trailing value as a control membership rule. The App layout remains an explicit
projection of the recovered common controls until every overlapping contextual
group is mapped into a presentation adapter.

`UnitCommandProfiles` is the engine-side command-identity boundary used by the
gameplay HUD. It combines decoded movement capability, the simulation's
resolved normal weapon, and the two recovered contextual catalogs. Deployed
forms retain their original identity for contextual-command lookup but use the
effective form's movement state for move/waypoint eligibility. It only decides
whether a slot may be presented; command executors continue to perform their
own authoritative validation. Ground Attack, Napalm, Disease, Drop Ship, and
Saucer are executable map-target commands. The transport commands are owned by
commander rank records; `DROP`/`SAUC` are outcome actors and must not be used as
command owners.

The compiled selected-unit HUD uses the original text message for a hovered
group-40 command in `maine`'s command-status readout (control 79), and applies
the recovered hover highlight to the icon. This makes inactive/pending controls
inspectable without representing them as implemented simulation behavior.
