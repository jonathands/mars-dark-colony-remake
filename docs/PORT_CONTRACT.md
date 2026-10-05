# Dark Colony port contract

This is the central architectural reference for the compiled port. Read it
before adding a subsystem. The evidence for individual rules is in
`docs/reverse-engineering/` (file-format research in `../dc-port-26/docs`);
this file records the product shape, code boundaries, and rules that should
remain stable while individual discoveries change.

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

## Status

The original priority order was followed and is complete:

1. the deterministic engine;
2. the renderer;
3. movement;
4. the HUD;
5. combat and economy;
6. missions;
7. the computer player;
8. networking.

See `docs/HOW_THE_PORT_WAS_MADE.md`. New work is of two kinds:

- fidelity fixes traced in `dc.exe` (`docs/GAMEPLAY_FIXES_PLAN.md`);
- port enhancements, such as the display modes, that leave the simulation and
  the goldens untouched.

## Project boundaries

### `DarkColony.Engine`

Platform-independent, deterministic code:

- `Assets`: original binary formats (SPR, FIN, fonts) and animation catalogs;
- `Data`: installation access, original text/binary catalogs, and tables read
  from `dc.exe` through `PeImage`;
- `Scenario`, `Terrain`: SCN/TRO/MTG scenarios, War sessions, MAP/BTS terrain;
- `World`: coordinates, entities, occupancy, and footprints;
- `Commands`: player/system intentions in deterministic tick order;
- `Movement`: routing, local paths, facing, reservation, and interpolation;
- `Combat`, `Economy`, `Environment`: projectiles and damage, Petra-7 vents and
  team economies, the day/night cycle;
- `Missions`: compiled `.tro` trigger scripts and their expression language;
- `Interface`: rules of original screens (options, teletype text, pictures);
- `Audio`, `Video`: the CD image, its music tracks, and Cinepak AVIs;
- `Network`: lockstep sessions and the network War lobby;
- `Simulation`: the fixed-step scheduler, `ScenarioSimulation` and its
  subsystems, the Krusty computer player, the state digest, and saved games.

`ScenarioSimulation` owns the authoritative per-mission state, so its
subsystem logic lives in partial files beside it:
`Simulation/ScenarioSimulation.<Subsystem>.cs` (Movement, Combat,
Acquisition, Vision, Economy, City, Construction, Production, Delivery,
Missions, Ai, War and others). `Step` lists the
tick's phases in order. Put new behavior in the matching partial file, keep
every field in `ScenarioSimulation.cs`, and keep stateless rules, data types,
and readers in the subsystem folders above. Event records live in
`SimulationEvents.cs`.

The engine must not reference WinForms, Direct3D, audio APIs, wall-clock frame
deltas, or screen pixels.

### `DarkColony.Presentation`

Platform-free presentation rules, with no WinForms or Direct3D types (only
`System.Drawing` primitives), so the engine checks can test them:

- `DisplayLayout`: where the logical picture lands on the output, and the
  output↔logical point mapping;
- `DisplaySettings` and `DisplaySettingsEditor`: the port's display settings,
  their flags and file, and the Video panel's rows;
- `GameplayScreen` and `GameplayHudLayout`: the gameplay screen at any size,
  with the `maine` HUD anchored to its edges.

See `docs/DISPLAY.md`. Nothing here may reach the simulation.

### `DarkColony.App`

Windows platform and presentation code:

- `Rendering`: D3D11 device lifetime, the flip-model swap chain, atlas pages
  and the sprite batch, palette remaps, the logical frame target (640x480 or
  the gameplay view) and its placement on the output (`DisplayLayout`),
  display-mode switching, and the game loop paced by the swap chain;
- `Ui`: reconstructed screen definitions and interaction adapters;
- `Audio`: the waveOut stream for the CD music and the video soundtracks;
- `Diagnostics`: the session log and the frame profiler (`--perf`);
- host/input code that translates OS events into engine commands.

`MainForm` is likewise split by responsibility: `MainForm.cs` (fields,
screen switching, frame composition) plus one partial file per area: `Menus`,
`WarLobby`, `Network`, `Encyclopedia`, `Credits`, `Assets`, `World`,
`Feedback`, `Hud`, `Input`, `Commands`, `Options`, `SaveGames`, `Music`,
`Video`, `Display` (window modes, DPI, gameplay screen size, pointer),
`VideoPanel` and `QuitConfirm`.

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

- Compose each frame into a logical render target: 640x480, or the gameplay
  view on the gameplay screen (`docs/DISPLAY.md`).
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
