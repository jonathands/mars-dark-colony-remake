# Dark Colony port contract

This is the central architectural reference for the compiled port. Read it
before adding a subsystem. Detailed reverse-engineering evidence remains in
`../dc-port-26/docs`; this file records the product shape, code boundaries, and
current priorities that should remain stable while individual discoveries
change.

## Product shape

Dark Colony is an RTS built around two substantially symmetric playable races,
Human and Gray. Most shared behavior must have one implementation. Differences
belong in content definitions, capability flags, statistics, build trees, and
the few recovered race-specific rules—not parallel `HumanEngine` and
`GrayEngine` class hierarchies.

The economy has one collected resource. Model resource storage, gathering,
costs, and income generically even if the original UI uses specific names or
art. Do not build a multi-resource abstraction without evidence that gameplay
needs it.

Buildings are delivered/dropped onto a centralized pedestal rather than built
in place through a conventional worker construction loop. Treat pedestal
ownership, availability, production/delivery state, and final footprint
placement as explicit engine concepts. Do not hide this lifecycle inside UI
button handlers.

These three statements are project-level design constraints supplied from
original-game experience. File formats and exact timings still require the
same evidence and validation as other reconstructed behavior.

## Current priorities

Work in this order unless a prerequisite forces a small detour:

1. Deterministic engine and world-state boundaries.
2. Direct3D renderer and native asset presentation.
3. Terrain, occupancy, command queues, pathfinding, and movement playback.
4. Selection and the minimum gameplay HUD needed to exercise movement.
5. Combat, resource collection, pedestal delivery, buildings, and production.
6. Mission scripting and neutral critters.
7. Computer-player policy/AI.
8. Networking, replay transport, and synchronization UI.

AI and networking are deliberately deferred. Engine APIs may remain
deterministic and command-driven so those systems can be added later, but they
must not dictate the initial architecture or delay local movement validation.

## Project boundaries

### `DarkColony.Engine`

Platform-independent, deterministic code:

- `Assets`: original binary formats and decoded asset models;
- `Data`: installation access and original text/binary catalogs;
- `World`: coordinates, entities, teams, occupancy, and map state;
- `Commands`: player/system intentions in deterministic tick order;
- `Movement`: routing, local paths, facing, reservation, and interpolation;
- `Combat`: targeting, projectile state, and damage;
- `Economy`: the single resource, gatherers, costs, and storage;
- `Construction`: pedestal delivery, footprints, placement, and production;
- `Simulation`: the fixed-step scheduler and subsystem orchestration.

The engine must not reference WinForms, Direct3D, audio APIs, wall-clock frame
deltas, or screen pixels.

### `DarkColony.App`

Windows platform and presentation code:

- `Rendering`: D3D11 device lifetime, GPU resources, sprite commands, palette
  remaps, render ordering, native 640x480 target, and display scaling;
- `Ui`: reconstructed screen definitions and interaction adapters;
- `Audio`: playback backend and event-to-sound binding when implemented;
- host/input code that translates OS events into engine commands.

Presentation can interpolate between completed simulation states but cannot
mutate authoritative gameplay state.

## Old-executable reconstruction rules

- Preserve observable behavior, not accidental 1990s implementation damage.
- Keep original data external and decode it through tested readers.
- Read executable data tables (for example the 1 KiB random stream or the
  footprint table) from the user's `dc.exe` through `PeImage` at runtime. Only
  short algorithm constants, such as a 9x9 priority table or direction
  vectors, may be written into code, and only with their source address
  documented beside them.
- Tag conclusions as executable-confirmed, data-derived, observed, provisional,
  or unresolved.
- Use fixed-width types and named value objects where original packed values
  have semantic meaning.
- Do not expose raw executable offsets throughout the engine. Record addresses
  in documentation/tests and translate them at the data boundary.
- Keep entity ID, gamestat code, FIN package, behavior kind, and display name as
  separate identities.
- Prefer a small data-driven rule over race-specific branching.
- Every binary format or deterministic gameplay rule needs a focused check.
- Debug views must be able to explain asset identity, occupancy, path steps,
  facing, animation selection, and command state.

## Simulation and movement invariants

- Authoritative updates use the recovered strict 66 ms fixed step.
- Map cells are integer topology coordinates. World positions are signed 8.8
  fixed point; cell centers are `(cell << 8) + 0x80`.
- Logical cell occupancy/reservation changes before visual interpolation.
- Current cell, reserved destination, fixed-point visual position, movement
  direction, 8-bit facing, and 16-sector render facing are distinct values.
- A route is not animation. Coarse PTH routing, local path steps, command state,
  and interpolated movement remain separate layers.
- The native compact local path is capped at 32 steps and stores two direction
  nibbles per byte. Preserve that contract until evidence justifies an internal
  extension.
- Rendering may run at any rate and must not feed frame delta into simulation.

## Rendering invariants

- Compose the game into a 640x480 logical render target.
- Use nearest-neighbor sampling unless a recovered effect explicitly requires
  another filter.
- UI gadget coordinates place normalized FIN composites. World rendering uses
  logical FIN/SPR origins; never silently mix the two modes.
- Palette/index semantics must survive decoding so team colors, highlight
  remaps, and legacy effects can move into shaders.
- GPU resources are cached by decoded asset/frame identity and disposed with
  the device. Simulation objects never own GPU resources.
- Backgrounds, terrain, FIN layers, bitmap glyphs, controls, and dynamic
  primitives are emitted as ordered GPU commands into the native target. A
  non-presented GDI scratch surface remains only for isolated fallback code.

## Near-term acceptance sequence

1. GPU sprite commands reproduce the visually captured menus and encyclopedia.
2. MAP/BTS terrain renders into the same native target with exact row and
   palette behavior.
3. SCN entities resolve by entity ID and sort correctly in world depth.
4. Static footprints and dynamic occupancy have a debug overlay.
5. A deterministic command moves one unit through PTH-assisted local routing,
   reservation, facing, and interpolation.
6. Multiple units demonstrate native blockage repair without overlap.

Only after these checks should combat/economy expand the playable slice. AI
and networking remain later milestones.
