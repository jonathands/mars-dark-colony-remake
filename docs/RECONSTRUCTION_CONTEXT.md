# Reconstruction context for the compiled engine

## Confirmed foundations

- World update is `dc.exe` `0x4196F4`.
- `gamestat/depend.txt` is now decoded as the single-P7 purchase tree. Its
  records have `(itemId, cost, uiId, kind)`, then a kind-specific prefix
  (seven fields for buildings/kind 0, five for troops/kind 1), then
  prerequisite item IDs followed by `-1`. Active extension IDs 83 and 84
  prove the declared 80 is a record count rather than an item-ID ceiling.
  Troop records directly identify their produced entity; building records
  carry a `(build-slot, variant, faction)` tuple. `dc.exe` table `0x47AFA8`
  resolves that tuple through its four 15-slot build sets, e.g. `(0,0,0)` is
  Human Exo Center entity 16 and `(1,0,2)` is Gray Breeder Hive 1 entity 30.
  For example, Human
  security troops are item 9, cost 350 P7, entity 0, prerequisite item 1;
  Gray warriors are item 23, cost 350 P7, entity 8, prerequisite item 15.
- SCN entity-40 vent records now become deterministic Petra-7 runtime objects;
  their fifth field is retained as the established independent cycle interval.
  The compiled Deploy command accepts Human `EXPL` and Gray `SLUG`, routes one
  to a free adjacent cell when necessary, then atomically attaches it to the
  vent. A mobile/stop/attack order detaches it; one harvester may own a vent.
  While attached, the renderer selects the data-resolved `EDPLY` (Human) or
  `SDPL` (Gray) mining-tower form while retaining the original mobile actor
  identity for economy and command ownership. The simulation owns the resulting
  income. `gamestat` already supplies distinct day/night observation values,
  and the simulation selects those from a deterministic phase clock. Team
  visibility is now an engine query over each living actor's current decoded
  observation radius; the gameplay renderer uses it to black out unseen cells
  and suppress unseen hostile minimap markers. This recovers the original
  black unexplored-space behavior without turning presentation into a second
  sight model. Persistent explored-terrain memory remains unrecovered. The
  native P7 gain constants and exact phase duration remain unrecovered:
  current 15-tick pulses, +1 passive P7, +4 deployed-vent P7, and 900-tick
  half-days are explicit provisional rules rather than claimed executable fact.
- `intrface/maine` explicitly declares its read-only “days counter” as text
  control 234 at `(613,433)`. The compiled HUD renders the deterministic count
  of completed full day/night pairs at that native location; phase text remains
  a port diagnostic until the original dial-update routine is traced.
- The native-scale gameplay HUD presents the live local P7 balance and phase,
  reports each Deploy outcome, and labels a selected harvester as either
  `EN ROUTE` or `ATTACHED` to its vent. Build, production, and research buttons
  use the engine's same P7/prerequisite eligibility result for their enabled
  state; the UI remains an intent adapter and does not mutate economy state.
  `slist.dat` explicitly supplies `DPY` deploy sounds for Human Exploiter (6)
  and Gray Slug (14); the app plays those only on the authoritative `Attached`
  deployment event, not on an unconfirmed right-click request.
- `maine` command group 40 also explicitly labels its shared deploy slot as
  **Deploy Mine** (frame 69). `gamestat.txt` pairs Human `ENGI` (43) and Gray
  `SLOM` (44) with faction-specific static `HMINE` forms (45/46), and
  `slist.dat` supplies their dedicated `DPY` sounds. The port now maps that
  control for an Engineer/Sloom-only selection and creates the matching mine
  as a deterministic occupied ground actor on a clear target cell. Mine
  records resolve the explicitly named `weapstat.txt` weapon 38 (class 6,
  range 1), so an armed mine now performs a deterministic nearest-hostile scan
  at or within that decoded range, fires through the normal projectile/matrix
  damage pipeline, then self-destructs. The native scan cadence, arming delay,
  and multi-shot interpretation are still unresolved; this is a clearly
  bounded port policy rather than a claim of frame-perfect mine behavior.
- The shared group-40 slot is now contextual in the HUD rather than always
  labelled `DEPLOY`: it shows its original frame/label for resource deploy
  (74), Deploy Mine (69), Heal Units (122), Deploy Turret (68), Inspire
  Troops (121), and Steal Money (75) when the selected unit identity is
  unambiguous. Engineer/Sloom, harvester, tower-builder, and healer entries
  are enabled because their engine paths exist. The remaining recovered
  controls stay visibly disabled and explain their missing target/state rule
  on click; this preserves native UI evidence without inventing inspiration
  or theft mechanics.
  The identity/frame mapping now lives in Engine `UnitSpecialCommandCatalog`,
  with a regression check for the paired Human/Gray codes. This makes the HUD
  a projection of recovered unit data instead of a second, drifting set of
  app-only conditionals. Healing's evidence boundary is explicit: `maine`
  labels the command, `gamestat` identifies BEON/ZISP as paired healing units,
  `slist.dat` routes both units' `DPY` action to `sound2.dat` 138
  (`HEAL.WAV`), `animate/curs.fin` provides `HEAL&REPAIR`, and `mbullet.txt`
  contains class-7 healing-ray resistance. The native collision routine at
  `dc.exe` `0x413e21` now recovers the amount exactly: it restores
  `floor(36 * mbullet[7, targetArmorClass] / 256)`, capped at missing health.
  The port enables cooperative unit healing with that formula and the native
  sound/cursor. Target range currently uses the healer's decoded sight radius;
  native range, cadence, and structure-target rules remain untraced.
- `maine` declares the sixth group-40 position `(518,317)` for contextual
  specials. Its companion `bdf.txt` maps button 72 to the Cyborg cruise-missile
  action and 73 to the Psy-raider virus action; the compiled HUD therefore
  shows **Napalm Attack** for `SARG` and **Disease Attack** for `PSYC`, rather
  than the earlier generic frame-2 Second Attack guess. Both remain disabled:
  their weapon/effect, research gate, target rule, and cooldown are not yet
  established. `sarg.fin` `FIREA`/`FIREB` and `psyc.fin` `FIRE` are available
  presentation evidence only. The generic Second Attack/Ground Attack entries
  have no verified unit owner. The engine-owned `UnitSecondaryCommandCatalog`
  preserves these distinctions for later tracing.
- The command identities are intentionally not just filename guesses. The
  regular Human `SARG` and Gray `PSYC` FIN files contain their `DEPLOY`
  transitions directly into `SARGSTL`/`PSYCSTL` static forms, so **Steal
  Money** belongs to the regular units, not the resulting forms. Conversely,
  `gamestat` gives Human/Gray commander ranks a distinct entity-ID range
  69–76 while reusing `TRSC`/`GRAY` art codes; that exact range owns the
  recovered **Inspire Troops** slot. Neither action is enabled until its
  target, transfer/bonus, duration, and cadence are traced.
  [`reverse-engineering/unit-special-commands.md`](reverse-engineering/unit-special-commands.md)
  is the detailed per-command evidence and implementation ledger.
  When the asset-name debug overlay is enabled, each live actor now also shows
  its recovered contextual action label, making owner/form mapping directly
  inspectable on a gameplay map.
- Tower deployment is now engine-backed rather than grouped with the still
  unresolved abilities. `gamestat` supplies Human builder `TURR` and Gray
  builder `XENO` as mobile forms, plus their same-faction deployed forms `T`
  (41) and `XDEPLOY` (42): those forms have movement speed zero and populate
  tower weapon slots 34–36 / 40–42. Clicking the recovered **Deploy Turret**
  control now atomically changes that actor's live form in place, cancels its
  movement, retains the existing ground claim, and enables the decoded tower
  weapon path. The renderer uses the deployed form's FIN stand/fire art and
  the app waits for the simulation result before playing the source `DPY`
  sound. This is distinct from a tech-tree building drop; no cost, footprint,
  or fabricated delivery timer is applied. Active-form resolution also drives
  weapon/armor upgrade lookup and selected-unit stats, so research targeting
  deployed tower entity 41 applies to `T`, not the mobile builder entity. A
  deployed tower remains eligible for direct hostile targeting but is excluded
  from empty-ground Move & Attack / ordinary move orders because its active
  form is static. The presentation now plays the shipped transition FIN range
  `TURRDEPLOY0` (32–48) or `XENODEPLOY0` (150–161) from the authoritative
  deployment event before settling into the deployed form's stand animation.
  Active-form state now also owns the health ceiling: deployment applies the
  static record's maximum health and clamps any existing mobile-form health to
  that ceiling, while the HUD reads the actor's live maximum rather than a
  stale seed definition.
  The Build-tab selection adapter likewise classifies an in-place deployed
  tower from its active record: it now receives a static-combat panel with
  Stop and direct Attack only, rather than the mobile builder's move/waypoint
  controls or a production-building catalog. Direct hostile targeting remains
  available through the engine's existing active-weapon path.
- Building drops now use one deterministic lifecycle: `PurchaseIntent` checks
  P7 and completed `depend.txt` prerequisites, `PlaceBuildingIntent` resolves
  the purchased building through `0x47AFA8`, atomically claims the exact
  `0x47ABE8` footprint, creates the live entity, and only then marks its build
  item complete. The dependency record's Human/Gray faction is checked against
  the owning SCN team before placement. The compiled Build tab exposes each faction's seven original
  building records when no unit is selected; it reserves the selected item and
  accepts a map right-click as the drop origin. The native pedestal transport,
  animation, and timing are still unrecovered, so placement is presently the
  deterministic completion boundary rather than an invented delivery timer.
  While a building is pending, the map now draws a green/red ghost over the
  exact decoded occupied cells under the cursor. Its preflight uses the same
  map bounds and `GroundOccupancy` checks as `PlaceBuildingIntent`, so a clear
  ghost is a faithful preview of immediate engine acceptance rather than a
  separate UI approximation. A retryable rejected drop (occupied, out of
  bounds, or invalid footprint) preserves its paid reservation and returns to
  the same placement mode/ghost, so the player can select a new origin rather
  than stranding a tech-tree item and its P7 cost.
  A building or research dependency item is single-use at this lifecycle
  boundary: it cannot reserve P7 while already reserved or after completion.
  Troop records deliberately remain repeatable reservations/production orders.
  The gameplay bottom strip now presents the authoritative local-team outcome
  of purchases, building drops, production, research, and unit orders. A
  successful production message names the spawned entity instance, its source
  structure, and its actual engine-selected spawn cell; it does not imply that
  native production duration, queues, or rally-point behavior are recovered.
  Spawn search is a documented port policy pending recovery of the native exit
  routine: it walks deterministic Chebyshev rings outside the source building's
  exact `0x47ABE8` footprint, using the produced unit's own occupancy class.
  The source-building requirement itself is also provisional: the recovered
  `dep_check_troop` path (`0x43839c`) receives player and dependency item and
  emits a troop type/cost/count command without a building instance or timer.
  The current selection/spawn adapter therefore must not be mistaken for a
  recovered native production queue. See
  [`reverse-engineering/production-flow.md`](reverse-engineering/production-flow.md)
  for the address-level evidence and next tracing boundary.
- Completed `depend.txt` weapon research now selects the matching levelled
  `gamestat.txt` weapon slot for every actor of its target entity and team.
  This is data-derived: the three entity weapon slots contain base, level-1,
  and level-2 weapon IDs (Human security troops: 1/2/3), while upgrade item 59
  targets entity 0 weapon level 1. The resolver is dynamic, so research affects
  existing and later-produced units equally. Armor research is recorded and
  shown by the HUD, but its runtime damage multiplier is still unrecovered and
  intentionally remains neutral.
- The Cyborg/Psy-raider level-2-looking dependency records are explicit
  exceptions to that stat-tech rule. Human item 80 targets entity 4 and is
  `maine` UI ID 131 (**Napalm** / `bdf.txt` cruise-missile ability icon 35).
  Gray item 54 targets entity 12 and is UI ID 78 (**Virus Sac** / virus-ability
  icon 45). Both carry packed weapon-category/level-2 fields but are authored
  abilities, not `WPN+2`. The decoder records their capability completion
  without selecting weapon slot 2; each contextual command reports missing
  research separately and remains disabled until its native executor is traced.
- The gameplay research HUD now follows the original `maine` per-UI-ID grid
  rather than showing a sorted four-item subset. Each shared physical slot
  presents its first unfinished dependency (then the next level/ability once
  completed), preserving the authored positions for the Human Cyborg
  Weapon→Napalm chain (UI 130→131) and Gray Psy-raider Psych→Virus Sac chain
  (UI 60→78). This makes those completed-data gates reachable through the
  compiled UI; exact native research-panel context switching remains open.
- Scheduler `0x41E104` uses `timeGetTime`, accumulated timestamp `+0x960`,
  interval `+0x970`, and strict `elapsed > interval` catch-up.
- Default interval is `0x42`, exactly 66 ms.
- Positions and cell centers use 8.8 fixed point.
- Movement uses at most 32 local steps, packed as two direction nibbles per byte.
- Logical occupancy is reserved before visual interpolation.
- SCN loading separates ordinary six-field placements from special five-field
  entity-40 vent records. Ordinary placements receive stable instance IDs and
  signed 8.8 cell-center positions; vents do not enter actor occupancy.
- Building masks are decoded from the user-owned PE32 executable at recovered
  addresses `0x47ABE8` and `0x47AFA8`. Only entity IDs with one unambiguous
  build slot claim static cells, so entity 25 (Renat) remains excluded.
- Corpus validation seeds all 3,244 ordinary SCN placements without conflicts
  and finds zero executable-proven building masks among them. This supports the
  boundary that pedestals/vents are terrain or special records while buildable
  structures enter occupancy through runtime pedestal delivery.
- All 106 positional `gamestat.txt` entities resolve through exact
  `<CODE>STAND…` animation names found across the FIN corpus. Filenames remain
  packaging evidence only; ambiguous candidates are retained and the preferred
  candidate favors an exact code filename. Gameplay draws these actors using
  FIN logical origins and ordinary world Z/X ordering. F3 exposes the resolved
  entity ID, code, and FIN for visual auditing.
- The gameplay camera is presentation state, pans by 16 screen pixels, clamps
  to decoded MAP dimensions, and only invalidates its terrain viewport cache.
  Arrow keys are intercepted before WinForms control navigation. Left-dragging
  past four pixels captures the D3D surface and pans without generating a
  selection click on release.
  Current left-click selection uses composed FIN alpha as an isolated UI hit
  test and draws a provisional cyan marker. Selection remains presentation
  state, not a simulation mutation. Shift-drag is the port's explicit adapter
  for box selection so ordinary left-drag panning remains available; inside
  that adapter the executable-confirmed gesture promotion is preserved: more
  than 45 Manhattan pixels or 1,500 ms.
- The compiled surface now renders the recovered `animate/curs.fin` cursor in
  place of the generic OS cursor, preserving each composed FIN hotspot offset. It
  selects data-named `DEFAULT`, `UNITSELECT`, `DRAWBOX`, `MOVE`, `ATTACK`, and
  the eight recovered edge-scroll cursor ranges from actual input state; the
  special repair/dig/confirm cursor transitions are still not implemented.
- The executable minimap handler `0x409c94` establishes a 96x84 interior at
  `(519,6)` and uses centred odd-numerator coordinates with vertical inversion.
  The compiled HUD now renders a cached MAP/BTS sampled minimap in that exact
  well, overlays live local/opposing actors and the camera rectangle, and
  lets click/drag move only presentation camera state through the same
  transform. Its simple centre-pixel terrain sample and port-side dot colors
  are reconstruction aids, not recovered native minimap rasterization.
- External player/system intents now enter through a future-tick-only queue
  ordered by target tick and stable insertion sequence. This is distinct from
  the recovered six-descriptor native per-actor queue. Right-click input now
  drives the implemented move, waypoint, attack, harvester-deploy, and
  building-drop intent paths; their authoritative routing and playback belong
  to `ScenarioSimulation`, not the HUD.
- PTH decoding validates all 101 installed files as a 65,536-byte next-region
  table plus a bottom-up map-sized region grid. Coordinate access performs the
  proven vertical conversion. Coarse chains terminate explicitly on target,
  zero, repetition, or 256 steps. F4 visualizes zero and region boundaries;
  neither the overlay nor the engine calls region zero definitively blocked.
- The gamestat field after X/Y dimensions is now correctly named movement
  class (Bat is `2`; ground critters are `0`), and the following field is
  health. The executable-confirmed autonomous spawn validator requires class
  zero to have a nonzero PTH region and empty ground occupancy; nonzero classes
  use the alternate occupancy grid. This narrow spawn rule is not reused as a
  claim about the still-incomplete local-path neighbor predicate.
- SCN team `-1` rows are typed autonomous spawn groups, not ordinary placed
  actors. Corpus checks confirm 561 groups, 1,767 requested members, native
  population/group limits, and only entity IDs 23/24/25/26/36. Initial seeding
  expands each group through the recovered cell search, assigns internal team
  9, and claims separate ground/alternate occupancy so members do not stack.
  Their scheduler now runs every eight simulation ticks: each member has the
  executable-confirmed `random & 3 == 0` wander gate and targets the group
  origin plus `((random & 15) - 7)` per axis through ordinary path playback;
  a `random & 0x7f == 0` one-member repair attempt is also implemented. The
  original random-generator algorithm and order-7-specific setup remain open,
  so the port uses a documented deterministic LCG solely to reproduce the
  recovered masks, timing, and target expression.
- A diagnostic local search now exercises the route-to-packed-path boundary:
  eight neighbors, independent diagonal admission/corner-cutting,
  movement-class occupancy, coarse-region restriction for ground actors, and
  a maximum 32-step low/high-nibble segment. Its breadth-first costs and
  tie-breaking are explicitly provisional, so the harness draws the chain but
  does not dispatch native actor commands or mutate position.
- Fresh disassembly of `0x441504`, `0x412388`, and command-5 executor
  `0x4125BC` confirms 2,048-scale direction vectors: cardinal magnitude 2,048
  and diagonal magnitude 1,448. Velocity is signed integer
  `vector*speed/2048`; duration is projected distance divided by speed.
  Command 5 advances only while its counter is nonzero and removes itself on
  the following execution. It does not snap to the destination, so integer
  residue is preserved.
- Packed-path playback now rechecks occupancy for each step, atomically moves
  the authoritative cell claim before starting interpolation, and reports a
  contested destination without releasing the source. Native blockage
  repair/yield/jitter behavior remains the required next layer.
- `ScenarioSimulation` now owns live actor movement, both occupancy grids,
  autonomous members, and deterministic intent consumption. Corpus creation
  succeeds for all missions with 4,450 seeded actors. Ordinary constructor
  `0x41AF14` clears and overwrites low occupancy bits without checking empty,
  which preserves deliberately stacked SCN records; only autonomous spawning
  uses the expanding empty-cell search. The app renders live fixed-point
  positions and no longer owns authoritative motion.
- Actor facing is persistent 8-bit state. Turn helper `0x4120FC` uses wrapped
  target-current difference, advances by gamestat turn speed on the shorter
  arc, and snaps only when the remainder is smaller than that step. Rendering
  derives 16 sectors with `((facing+8)&255)>>4`. Full MOVE families resolve
  exact sectors; incomplete/even-only families currently use a documented
  nearest-sector presentation fallback because the native doubled selector and
  mirroring policy remain unresolved.
- Firing presentation now selects a named animation family deterministically:
  plain `FIRE` takes precedence, followed by `FIREA`, then `FIREB` and `FIREC`.
  The installed Cyborg `SARG` asset has both A and B families; the map’s
  ordinary fire path therefore stays on `SARGFIREA*` rather than relying on
  filesystem/source order to choose a sequence. This is a presentation policy
  derived from asset family ordering, not evidence that it implements the
  disabled Napalm Attack command.
- Local unit control is now engine-driven for campaign team 0: selection only
  submits future-tick `MoveIntent` commands, `ScenarioSimulation` owns their
  persistent target/order state, segments paths beyond the 32-step buffer, and
  immediately rebuilds a local route after a dynamic playback block. It waits
  four ticks only when that repair cannot find a route. The app only renders
  this state and exposes selected unit/cell/facing/order feedback. Native
  blocker notification/yield and one-cell jitter target policy remain to be
  matched exactly.
- The common `intrface/maine` command column is now mapped at its native
  positions: Stop `(518,112)`, Move Only `(518,153)`, Move & Attack
  `(518,194)`, Waypoints `(518,235)`, and Deploy `(518,276)`. The compiled HUD
  uses recovered `mainbut.spr` frames 62, 63, 65, 66, and 74, respectively.
  Stop/Move/Waypoint, Move & Attack, and Deploy now control deterministic
  simulation orders. Deploy is limited to recovered P7 harvesters; its
  provisional income rule is documented below.
- `maine` also declares **Last Msg** `(4,460,20×19)` / **Next Msg**
  `(24,460,20×19)` buttons using `mainbut` frames 40 and 57. The compiled HUD
  now renders those source controls and retains the last 32 distinct gameplay
  status messages for browsing. This is deliberately a port message-history
  adapter: the executable's message-buffer storage and mission-dialogue link
  are still untraced.
- The recovered Game Options **Objectives** control at `(518,317)` now opens
  the source-layout `storye` briefing view when the selected mission ships a
  `.txt` briefing. It pauses the local simulation while open and resumes the
  same loaded scenario through an explicit no-reload path on Back/Next. This
  provides a safe in-game read-only objective/briefing view; the original
  Objectives action's exact runtime and trigger-state presentation remain open.
  Escape follows that same return-to-map path when this view was opened from
  gameplay.
- `maine` frame 65 is the source's **Move & Attack** control, not a
  target-only button. A terrain command now creates an authoritative
  `AttackMoveIntent`; the actor retains the player destination, acquires the
  nearest hostile inside its decoded current day/night observation range,
  fights it with the normal target/pursuit path, then resumes the destination.
  Clicking a hostile remains a direct `AttackIntent`. The source's exact
  acquisition predicate is still untraced, so observation range is an explicit
  data-backed provisional boundary rather than a claim of frame-perfect AI.
  Selected-unit details expose the live direct target, P7 deployment, move, or
  attack-move destination in that precedence order, while rejected/acquired
  attack-move events are reported through the gameplay status strip.
- Weapon cadence now preserves the native burst boundary rather than treating
  every weapon as a single flat cooldown. In `dc.exe` `0x413181`–`0x4131A3`, a
  shot increments actor byte `+0x34`; if decoded weapon field `+0x20` is
  positive and that counter reaches it, the counter resets and the following
  delay uses field `+0x24` (reload). Otherwise it uses field `+0x08`
  (rate-of-fire). `WeaponDefinition.Shots` and `Reload` now drive that exact
  branch in the authoritative simulation. Multi-muzzle emission, scatter, and
  exact zero-delay behavior remain separate unresolved firing details.
  The selected-unit target readout exposes this engine state as `READY` or
  `RELOAD <ticks> B<current>/<limit>` while a target is in range. This is a
  port diagnostic, not a claim that the original HUD showed these fields.
- Resolved `BULLET` FIN effects now advance one frame per authoritative
  projectile simulation step, using the projectile's tracked elapsed lifetime
  rather than a renderer clock. The native projectile duration itself remains
  provisional; this change only removes the former frozen-first-frame visual.
- Weapon prefixes now resolve their separate `EXPLODE`/`EXPL` FIN families as
  optional impact effects. On an authoritative projectile-impact event, the
  renderer plays that family at the target's live fixed-point position; absent
  assets still use the ordinary HIT/death paths. Effect selection is data
  driven and does not treat the Exploiter's `EXPL` identity as an explosion.
  Current impact placement uses the target's live position because the
  provisional linear projectile step can overshoot a close target; recovering
  the native collision/lifetime boundary is required before promoting a
  projectile endpoint to the effect position.
- `weapstat.txt` has eleven numeric columns after its sprite token. Its own
  header names column nine `magic_chewing`, so `WeaponDefinition.MagicChewing`
  preserves that source identity without claiming a runtime effect. Earlier
  port code silently discarded the final two. Column ten is now recovered as
  `AreaEffectTemplateId`: every nonzero value is a valid `boomstat.txt` record,
  such as artillery weapon 10 → Arty template 1 and weapon 50 → template 4.
  `AreaEffectCatalog` preserves each template's named effect layers, authored
  radial percentage square, and structurally separate trailing 3×3 square.
  The latter's purpose, plus the native area-damage application path, remain
  untraced; column eleven remains `UnknownField11`. This is a data boundary,
  not an activation of area damage.
- The selected-unit stat line now reports data-derived live HP, movement speed,
  effective weapon damage/range, current/opposite-phase sight, and completed
  weapon/armor research levels. Armor level is display-only until the native
  multiplier path can be named.
  The bottom HUD exposes the selected entity's `gamestat` health, movement,
  day-sight, and active waypoint count. The authoritative `ActiveMoveOrder`
  now preserves the native eight-waypoint cap and rejects consecutive duplicate
  destinations.
- `maine` tab controls are also stateful in the compiled HUD: Build `(518,92,
  40x20)` / frame 77, Research `(557,92,41x20)` / frame 78, and Game Options
  `(598,92,40x20)` / frame 79. Build is now the source's composite Human group
  84 or Gray group 53: nine troop controls occupy the left column plus the two
  upper-right cells, while the building group occupies the remaining five
  right-column cells. The advanced Science Pod/Robot Factory variants reuse
  their base button coordinates exactly as declared by `maine`; the port picks
  the currently eligible variant at that shared control. This corrects the
  earlier port-only “buildings when deselected, troops when selected” layout.
  A troop click prefers a selected matching prerequisite structure but may use
  the earliest matching live structure as its temporary deterministic spawn
  anchor, because the native create location is not yet decoded. Research maps
  representative Human group 96 or Gray group 61 upgrade slots, and Options
  maps group 65. These pages are mutually exclusive; they do not draw every
  overlapping `maine` gadget simultaneously. Pause/Resume and the port-owned
  Allies panel have simulation behavior; save, generic options, and objectives
  remain visible evidence-backed controls pending their owning engine systems.
- Each `SimulatedActor` now owns live health initialized from its immutable
  `gamestat` maximum. The selected-unit marker/panel renders `current/max`;
  this is the required simulation boundary for future hit, healing, and death
  commands. `gamestat/weapstat.txt` is parsed into a 64-record `WeaponCatalog`
  with original weapon class, rate, raw damage, projectile speed, range, burst,
  reload, and special fields. Unit panels resolve their first populated weapon
  slot through that catalog. Targeting/projectiles/damage remain unimplemented.
- A `Move & Attack` HUD state now submits deterministic `AttackIntent` target
  acquisition on an opposing actor. It retains a target until Stop, marks the
  target in red, rotates the attacker through its existing 8-bit facing state,
  and reports the confirmed strict fixed-point squared-range precondition from
  the selected original weapon. The Combat scaffold now creates simulation-owned
  projectile records after target-facing/range/cooldown, advances a provisional
  linear velocity from the original weapon speed field, and applies the
  original weapon-class/armor-class matrix damage on impact. Projectiles render
  as a diagnostic map glow until their original effect FIN/lifetime updater and
  scatter/burst details are recovered. This must not be treated as final native
  projectile timing.
- Attack intent eligibility is now owned by a deterministic 10-by-10
  `TeamRelationMatrix`, matching the recovered world table whose zero byte
  means hostile/attackable and nonzero means cooperative. Direct intents now
  report acquired, missing, destroyed, unarmed, or non-hostile outcomes; the
  HUD uses that same rule before it queues an attack. The default diagonal is
  cooperative and other pairs hostile, preserving the port's existing
  Human-vs-Gray behavior while leaving the still-unrecovered per-mission/team-9
  table initialization explicitly configurable rather than invented.
- The recovered `maine` Options group 65 has an `ALLIES` entry but no recoverable
  dialog definition. The compiled port therefore opens a clearly port-owned
  local allies panel from that exact control. It lists enabled scenario teams
  and writes both directions of the recovered relation matrix together: `ALLY`
  is nonzero/cooperative and `FOE` is zero/hostile. This reciprocal UI policy
  is deliberately distinct from the matrix API, which remains directed for
  later mission/script reconstruction. Direct attacks and attack-move target
  acquisition immediately use the changed relation; networking and diplomacy
  rules remain outside the current scope.
- When an attack target is outside strict weapon range, the attacker now picks
  the nearest unoccupied cell within that range and approaches it through the
  ordinary local movement/occupancy pipeline. It does not replace an active
  approach segment every tick; after arrival it re-evaluates range and fires.
  The exact executable chase/re-target policy remains unrecovered.
- Weapon projectile presentation now resolves the second `weapstat.txt` token
  as the executable-confirmed `PREFIXBULLET…` animation-name family across the
  whole FIN namespace; it does not infer a sprite filename. Resolved effects
  render at the projectile position, while weapons without a shipped resolved
  family retain the diagnostic glow fallback.
- Entity animation discovery now also indexes directional `FIRE` and `HIT`
  FIN families by their trailing 16-sector number (for example,
  `TRSCFIREA0` and `GRAYHITB0`). App presentation consumes the authoritative
  weapon-fire and projectile-impact event lists to play each source/target
  sequence once, then returns to normal movement/stand selection. Choosing the
  first resolved subfamily (`A`/`B`) is a documented presentation fallback;
  the native weapon-to-subfamily selector remains to be traced.
- `sound2.dat` and weighted `slist.dat` owner/category lists are parsed as the
  original sound identity boundary. The compiled UI now plays the first
  recovered `SEL` list entry for an explicit selection, `ACK` for movement or
  attack commands, and `DEA` when a destruction event is consumed. Repeated
  weighted entries are preserved by the catalog; deterministic/random selection
  policy, GUN/EXP events, ambience, positional panning, and native mixer
  parameters remain open.
- Combat now emits simulation-owned weapon-fire and projectile-impact events.
  The app plays `GUN` with weapon ID ownership and `EXP` with weapon-class
  ownership, exactly matching the recovered `slist.dat` table categories. The
  events are covered by the deterministic combat lifecycle check; weighted
  random selection remains intentionally unimplemented (the port uses the
  first recovered entry for presentation).
- `ScenarioSimulation` now owns one P7 resource balance per enabled SCN team,
  initialized from the already-decoded postfix `%Money` value. The native HUD
  P7 field renders the local team balance. The 84 selectable Human/Gray War
  roster corpus asserts that each simulation receives its source team’s exact
  starting balance. Purchase costs, provisional passive pulses, and deployed
  harvester pulses mutate this engine-owned balance; native gain constants and
  storage/multiplier handling remain unrecovered.
- An impact reducing health to zero now destroys the actor in simulation: it
  cancels movement/attack state, releases both occupancy grids, clears every
  attacker's target reference, removes the actor from render/selection, and
  leaves damaged surviving actors with a visible in-world health bar. Death FIN
  playback is resolved from `<CODE>DIE…` candidates and runs from the emitted
  destruction event after the actor has left simulation. Death sound is still
  separate asset/event work.
- Facing is an 8-bit circle; rendering rounds it to 16 sectors.
- Weapon class selects one of nine `mbullet.txt` rows.
- Entity field 11 selects one of ten defense/armor columns.
- Base damage is raw weapon damage multiplied by the matrix percentage; native
  `0x441930` then applies additional 8.8 upgrade/state multipliers.
- Autonomous SCN team `-1` becomes internal team 9 and is maintained every eight
  simulation ticks.

## Do not compile as facts yet

- Cross-team diplomacy initialization, particularly row/column 9.
- Exact armor-upgrade and transient damage multipliers. See
  [`reverse-engineering/combat-damage.md`](reverse-engineering/combat-damage.md)
  for the bounded collision-helper trace and why armor technology is not yet
  applied to port damage.
- Exact projectile display scale, scatter, burst, and FIN event timing.
- Web-preview cross-team-hostile policy.
- Web-preview click masks, vent phase hash, or projectile duration.

## Implementation milestones

The authoritative ordering and subsystem boundaries are in
[`PORT_CONTRACT.md`](PORT_CONTRACT.md). This list tracks reconstruction detail;
it does not override the engine -> renderer -> movement priority or promote AI
and networking ahead of the local playable slice.

0. **Implemented:** native 640×480 menu host, external GIF backgrounds,
   recovered button rectangles/labels, and initial screen navigation.
   D3D11 device/swap-chain presentation is active and visually launch-tested.
   The uploaded parity frame is point-sampled by a shader into a native 640x480
   render target, and that target is point-sampled into the back buffer in a
   second pass. CPU composition remains only as the input bridge until all menu
   primitives use the GPU sprite command path.
1. **Implemented in part:** native raw/RLE SPR decoding, VGA palette expansion,
   standard-marker FIN parsing, layered RGBA composition, the 29-frame `DCSS`
   opening, original `LARGEBUTTON`/`MEDBUTTON` animation ranges, campaign race
   portraits (`HLOOP`/`ALOOP`), and single-player commander portraits
   (`HCOM`/`ACOM`). Menu controls now use `mfonto5.spr`: character frame is
   `codepoint - 31`, glyph X/Y descriptors are bearings, and advance is
   `bearing X + cropped width` (an empty space frame advances by its X value).
   Palette-remap states and remaining gadget transition policies still need
   executable-backed recovery.
2. **Implemented in part:** MAP/BTS readers validate all 101 installed maps
   (1,262,544 cells) and four tilesets (4,764 tiles). The exact 32x32 base and
   transparent-overlay compositor, independent horizontal flips, BTS frame-ID
   lookup, and VGA palette expansion render `htrain1` into the confirmed
   516x458 gameplay viewport under the original HUD. Camera state and GPU tile
   commands remain next.
3. SCN placements, exact identity mappings, footprints, and render depth.
4. Compact command queue, PTH routing, local path playback, and occupancy.
5. Facing, MOVE animation, interpolation, blockage repair, and replanning.
6. **Implemented in part:** gameplay HUD with recovered common command
   geometry, local-unit selection, deterministic Stop/Move/Move & Attack,
   capped waypoints, contextual Deploy controls, static-tower controls, live
   unit/structure stats, build placement, structure production, and research
   catalogs. Portrait selection, per-type gadget groups beyond recovered common
   controls, native transition/disabled states, and target rules for the
   remaining contextual commands still need executable-backed recovery.
7. **Implemented in part:** combat targeting, decoded projectile records,
   matrix damage, death, and sound. Exact target reacquisition, effect timing,
   healing, armor reduction, and special-command rules remain open.
8. **Implemented in part:** single-resource economy, P7 harvesting,
   construction, production, and upgrades. Pedestal delivery, native production
   queues/timing/rally behavior, triggers, and campaign mission state remain open.
9. Autonomous team-9 spawn groups and wandering.
10. Computer-player policy/AI after local simulation behavior is stable.
11. Networking and replay transport after deterministic command execution is
    validated locally.

## Single Player War roster decoding

- The recovered `intrface/shumane` definition remains the authoritative source
  for the 640x480 War-screen geometry. It declares a shared commander viewport
  containing both `HCOM` and `ACOM`; which one is shown is runtime state, not a
  second screen definition.
- The `scenario/mplayer/*.scn` team block is postfix-labelled. Its first value
  after `TEAM <id> <enabled>` is the race; each later `%Race`, `%Money`, `%AI`,
  and `%TeamColour` label names the value immediately *before* it. For example,
  `d2play01` declares Human team 0 and Gray team 1, both with 1,500 starting
  resource. The prior reader incorrectly interpreted 1,500 as the race.
- The compiled port preserves the user’s selected Human/Gray state from the
  recovered New Game race controls. When a War map is launched it finds that
  faction’s first enabled SCN team and routes selection, camera focus, and
  orders through that team. It does not rewrite original placements or invent a
  replacement roster. A faction missing from a map is reported before launch.
- Verification currently proves 54 installed maps expose a controllable Human
  roster and 30 expose a controllable Gray roster. The original filtering rule
  is not yet named in the executable trace, but the compiled port filters its
  reconstructed list to those data-proven playable rosters; a visible map can
  therefore always launch for the selected faction.
- `SinglePlayerWarCatalog` is the engine-side data boundary for that list. It
  scans the native `scenario/mplayer` root only for complete SCN/MAP/PTH sets,
  sorts them by their original scenario stems, preserves the decoded SCN
  definition, and resolves the enabled local team by faction. The shipped
  directory's own SCN enumeration is the same order (starting `a2play01`,
  then `d2play01`), so this deterministic sort also preserves the installed
  layout. The UI only presents this catalog; it does not duplicate roster
  parsing or infer teams.
- Executable frontend routine `0x402FB4` loads `intrface/shuman.dat` and
  `intrface/shuman`, then uses persistent frontend state at `+0x1494` to toggle
  `shumane` gadgets 36 (`HCOM`) and 35 (`ACOM`). This corroborates that the
  screen consumes an already-chosen faction; it does not declare its own race
  buttons. Both gadgets are declared `anim_stopped`, so the compiled port uses
  their first stopped frame rather than looping them. The port therefore keeps
  faction selection on the recovered New Game screen rather than adding
  unproven controls to `shumane`.
- The same routine updates source text controls 5, 6, and 7. The port keeps
  those at `(392,27)`, `(312,162)`, and `(28,277)` respectively. Its map-list
  contents remain a port reconstruction, but are clipped to the paired source
  scroll control 40 at `(617,229,10,179)` rather than spilling into the arrow
  controls at y=195 and y=415. The compiled list maps a pointer position on
  that exact track to its decoded, faction-filtered map entries; the visual
  thumb is port-owned because the source defines the control rectangle but not
  its runtime thumb art/state.
- `newgamee` control 5 is the 17-character leader-name input at `(205,308)`.
  The port keeps this name in frontend state and renders it left-aligned in
  `shumane` input control 5, matching the executable’s persistent-state data
  flow. No shipped profile/save file establishes a default name, so a fresh
  port begins with an empty field. Native text-edit focus/caret timing is still
  unrecovered.
- The source declares `newgamee` portrait gadgets `HREZIN`, `HREZOUT`,
  `HLOOP`, `AREZIN`, `AREZOUT`, and `ALOOP` as `anim_stopped`. The compiled
  screen shows the stopped `HLOOP`/`ALOOP` frames; their names alone are not
  evidence for a looping playback policy.
- Mplayer SCN line 3 is the user-facing map title (for example, `d2play01` is
  `Dead Man's Wharf`), while line 2 is an internal scenario stem. The War list
  renders the former and uses the latter only for external-file resolution.
- `DC.EXE` `0x403270`–`0x403292` iterates `shumane` gadgets 25–30 and reads a
  corresponding frontend profile slot at `+0x1448 + gadgetId * 4`. A value of
  `-1` disables a medal; another value selects its stopped frame. Because the
  compiled port has no recovered career-profile persistence, its neutral War
  screen renders no medals. The rank call at `0x4032dd`–`0x403326` selects
  `RANKS` with `rankIndex + 4`; the port's fresh rank index is zero and thus
  renders the corresponding fifth logical `RANKS` frame. Career progression
  and rank persistence remain unrecovered.

## Encyclopedia checkpoint

- `intrface/encyclo.txt` is a three-group stream with implicit first IDs:
  Gray starts at 0, Human at 10, and artifacts at 20. Each entry's following
  integer is the next native ID/group boundary.
- The compiled screen loads all 25 canonical names/resource stems, switches
  categories, scrolls selection, and draws confirmed entity FIN stand/move
  animations without scaling them by guesswork.
- The referenced resources are present at the installation root as
  `ENCYCLO/*.TXT` (case differs from the lower-case logical stems in
  `encyclo.txt`). The compiled screen now decodes their `~0`/`~1`/`~4` palette
  controls, word-wraps the original specification/flavor text into the native
  left panel, and plays the adjacent per-entry WAV when a category/entry is
  changed. It leaves the external files untouched. This establishes, for
  example, S.A.R.G.E.'s authored Napalm alternate weapon and Gorrem's Disease
  Spore rather than inventing those identities from later gameplay guesses.
- The compiled encyclopedia additionally maps the 20 mobile entry stems to
  explicit `gamestat` IDs (the screen's implicit native IDs are a different
  namespace) and fills `encycloe`'s nearby `(307,287)` text field with compact
  runtime HP/speed. This is a port diagnostic for comparing source prose with
  decoded play stats; artifact entries intentionally do not receive guessed
  unit-stat bindings.
- Remaining controls (direction, rewind/fast-forward, indicators, zoom/view
  behavior) require executable-backed state mapping. The known `encycloe`
  gadget names/coordinates nevertheless map LEFT `(343,220)`, RIGHT
  `(445,220)`, REW `(511,220)`, and FFW `(601,220)` into the compiled unit
  preview. FIN data establishes that ordinary stand names carry even facing
  sectors, so LEFT/RIGHT choose those named directions; REW/FFW seek inside the
  selected range. This is an explicit preview adapter, not a claim about native
  camera or playback semantics.
- UI FIN composites are normalized into their declared gadget rectangles. Their
  composite X/Y bounds are cropping/origin evidence and must not be added to an
  exact `gadget` coordinate a second time. Gameplay/world placement will expose
  a separate logical-origin draw operation.
- `knobe.fin` button ranges are one-off construction transitions, not idle
  loops. Normal non-intro controls use the terminal stopped frame; the current
  highlighted state uses the completed outline immediately before the terminal
  collapse. Exact palette remaps for pushed/highlight states remain open.
- All 25 catalog identities now resolve to composable installed FIN data.
  Solaris uses catalog stem `lns` -> `lens.fin`; `solar.fin` references an
  unshipped `solar.spr` and is not the encyclopedia asset.

Detailed evidence remains in `../dc-port-26/docs/`; copy conclusions into this
document only when the compiled implementation actually depends on them.
