# Unit special-command reconstruction

This reference covers the contextual controls in `intrface/maine` group 40.
It records only identities and transitions demonstrated by installed data or
the executable-recovered UI; it does not turn a label into a game rule.

| Native label | `maine` frame | Command owner | Proven resulting form | Port state | Still unresolved |
| --- | ---: | --- | --- | --- | --- |
| Deploy | 74 | Human `EXPL`, Gray `SLUG` | `EDPLY`, `SDPL` | Implemented: routes/attaches to one Petra-7 vent and changes visual form. | Native income amount/timing and detach edge cases. |
| Deploy Mine | 69 | Human `ENGI`, Gray `SLOM` | faction-matched `HMINE` (45/46) | Implemented: a clear target cell creates an armed static mine. | Arming delay, scan cadence, and multi-shot behavior. |
| Heal Units | 122 | Human `BEON`, Gray `ZISP` | None established. | Implemented: cooperative live-unit target, original class-7 amount, cursor, and sound. | Native range, cadence, and whether repair shares the path. |
| Deploy Turret | 68 | Human `TURR`, Gray `XENO` | `T` (41), `XDEPLOY` (42) | Implemented: in-place static form, original deploy FIN, and decoded tower weapons. | Native cancellation/redeployment policy. |
| Inspire Troops | 121 | Commander entity IDs 69–76 | None established. | Mapped and disabled. | Target area, stat bonus, duration, and stacking. |
| Steal Money | 75 | Human `SARG`, Gray `PSYC` | `SARGSTL` (77), `PSYCSTL` (78) | Implemented in part: recovered mobile→static stance, DEPLOY FIN, and DPY sound. | Victim targeting, transfer amount/timing, and return/cancel rule. |

## Sixth-slot special attacks

`maine` reserves the adjacent sixth command position `(518,317)` for its
contextual special attacks. It contains a generic **Second Attack** declaration
using `mainbut.spr` frame 2, plus **Napalm Attack** (frame 72) and **Disease
Attack** (frame 73). The companion `intrface/bdf.txt` button dictionary names
frame 72 “cyborg call cruise missile” and frame 73 “psych raider deploy virus”.
That direct icon/action vocabulary is stronger evidence than the adjacent
generic comment, so the port maps Human Cyborg (`SARG`) to **Napalm Attack** /
72 and Gray Psy-raider (`PSYC`) to **Disease Attack** / 73. Both remain disabled
until their target rule, effect execution, and cooldown are traced. The generic
frame-2 and **Ground Attack** declaration have no verified owner yet.

The paired gates are data-derived. Human item 80's source comment calls it
“cyborg nuke 2”; its UI ID 131 is **Napalm** using icon 35, which `bdf.txt`
calls “cyborg cruise missile ability.” Gray item 54 targets entity 12 and its
UI ID 78 is **Virus Sac**, using icon 45 “psych raider virus ability.” Despite
both records carrying kind-2 weapon-category/level-2-shaped fields, they are
ability research rather than weapon-stat levels. The compiled port preserves
both completion states and uses them to distinguish missing research from the
remaining missing executor. It does not enable either action or infer their
targeting/effect rules. In the unit HUD this is visible as **TECH REQUIRED**
before the prerequisite is complete and **EXECUTOR PENDING** afterwards; both
states intentionally retain the disabled native icon. The selected-unit stat
line additionally shows `NAPALM TECH:ON/OFF` or `DISEASE TECH:ON/OFF`; this is
research capability state only, never a claim that the pending executor works.

`weapstat.txt` now narrows the presentation candidates without proving the
executor: weapon 50 is explicitly **Napalm effect**, uses weapon class 6 and
area template 4, and its sound ID 173 resolves to `CY2NDFI.WAV`; weapon 51 is
the paired class-6 effect, has a 12-shot authored burst, also uses area template
4, and sound ID 174 resolves to `PSY2NDFI.WAV`. SARG's ordinary slots are
13/14/14 and PSYC's are 30/27/28, so 50/51 cannot be their normal fire
progression. The engine catalog retains them as *candidate* effect-weapon IDs
only. The direct command→weapon call, target selection, effect timing, and use
of the effect's area template remain unrecovered and must not be inferred from
names alone.

The SARG FIN has separate `FIREA` and `FIREB` families, while PSYC has one
`FIRE` family. These establish available presentation families but do not prove
which one the unrecovered special commands select. Likewise, `depend.txt`
describing Human item 80 as “cyborg nuke 2” is insufficient to prove its button
or executor.

The same physical slot has two further source labels: **Drop Ship** (frame
125) and **Saucer** (frame 126). `gamestat.txt` explicitly identifies entity
92 as Human `DROP` and entity 93 as Gray `SAUC`; the port therefore displays
the corresponding disabled sixth-slot control for homogeneous selections of
those units. The matching packet, beacon, deployment, and transport behavior
is not established by the labels or asset names and remains disabled.

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
- No shipped SCN placement uses entity 77 (`SARGSTL`) or 78 (`PSYCSTL`). Map
  context therefore cannot establish a victim, range, or transfer pulse for
  Steal Money; those rules require executable runtime tracing.
- Healer identity is additionally corroborated by `slist.dat`: both entity 49
  (`BEON`) and entity 50 (`ZISP`) use action category `DPY` sound 138, and
  `sound2.dat` resolves 138 to `HEAL.WAV`. `animate/curs.fin` also contains the
  dedicated `HEAL&REPAIR` cursor family (frames 32–34). Together with
  `mbullet.txt` class 7 ("healing ray resistance"), this establishes a real
  contextual heal/repair path rather than a generic attack. The collision
  routine at `dc.exe` `0x413e21` reads that row using the target's defense
  class and restores `floor(36 * matrixValue / 256)`, capped at missing health.
  It does not establish range, cadence, or whether structures share the same
  executor. The port uses the healer's shipped sight radius as an explicit
  provisional range policy and only allows cooperative live-unit targets.
- `beon.fin` and `zisp.fin` contain stand, move, die, and `DEPLOY` families,
  but no local `HEAL` or `FIRE` family. Shared `glot.fin` supplies
  `BEONFIREA0` (frames 0–17) and `ZISPFIREA0` (18–35), which the entity
  animation catalog resolves as their directional firing presentation. The
  port plays that recovered animation alongside the heal's cursor, sound, HP
  feedback, and facing update. The meaning of the local `DEPLOY` families is
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
own authoritative validation, and pending sixth-slot abilities remain
non-executable.

The compiled selected-unit HUD uses the original text message for a hovered
group-40 command in `maine`'s command-status readout (control 79), and applies
the recovered hover highlight to the icon. This makes inactive/pending controls
inspectable without representing them as implemented simulation behavior.
