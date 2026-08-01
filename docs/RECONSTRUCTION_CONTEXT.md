# Reconstruction context for the compiled engine

## Confirmed foundations

- World update is `dc.exe` `0x4196F4`.
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
  test and draws a provisional cyan marker. It is not compiled as the original
  selection mask/list behavior and does not mutate the simulation.
- External player/system intents now enter through a future-tick-only queue
  ordered by target tick and stable insertion sequence. This is distinct from
  the recovered six-descriptor native per-actor queue. The current right-click
  harness submits a `MoveIntent` and displays its cell but performs no routing
  or position change.
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
  The eight-tick wander/repair scheduler remains disconnected.
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
- Local unit control is now engine-driven for campaign team 0: selection only
  submits future-tick `MoveIntent` commands, `ScenarioSimulation` owns their
  persistent target/order state, segments paths beyond the 32-step buffer, and
  immediately rebuilds a local route after a dynamic playback block. It waits
  four ticks only when that repair cannot find a route. The app only renders
  this state and exposes selected unit/cell/facing/order feedback. Native
  blocker notification/yield and one-cell jitter target policy remain to be
  matched exactly.
- Facing is an 8-bit circle; rendering rounds it to 16 sectors.
- Weapon class selects one of nine `mbullet.txt` rows.
- Entity field 11 selects one of ten defense/armor columns.
- Base damage is raw weapon damage multiplied by the matrix percentage; native
  `0x441930` then applies additional 8.8 upgrade/state multipliers.
- Autonomous SCN team `-1` becomes internal team 9 and is maintained every eight
  simulation ticks.

## Do not compile as facts yet

- Cross-team diplomacy initialization, particularly row/column 9.
- Exact armor-upgrade and transient damage multipliers.
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
6. Minimum selection/gameplay HUD needed to exercise movement.
7. Combat targeting, native projectile records, matrix damage, death, and sound.
8. Single-resource economy, pedestal delivery, construction, production,
   upgrades, triggers, and mission state.
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
- Referenced `encyclo/*` prose resources are absent from this installed copy.
  Flavor text remains unimplemented until the CD/source location is resolved.
- Remaining controls (direction, rewind/fast-forward, indicators, zoom/view
  behavior) require executable-backed state mapping.
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
