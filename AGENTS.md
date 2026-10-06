# Dark Colony compiled port context

This directory is the clean-room compiled port. It may read the user's adjacent
original installation at runtime, but original game assets must not be copied
into this repository. The only images taken from the game are the
screenshots in `docs/images` (lossless WebP), which the author chose to
publish on 2026-10-06. Add to them only on request; never add asset files.
The README may say that the port needs a copy of the original game, but not
where to get one.

Read `docs/PORT_CONTRACT.md` before changing architecture or adding a gameplay
subsystem. It is the central statement of project shape and rules.
`docs/HOW_THE_PORT_WAS_MADE.md` explains the method, and `docs/README.md`
indexes the rest.

## Evidence hierarchy

1. Confirmed executable disassembly, recorded in `docs/reverse-engineering/`
   (earlier notes in `../dc-port-26/docs/`).
2. Original data-file structure confirmed by parsers/tests in `../dc-port-26/tools/`.
3. Controlled observations from the original Windows executable.
4. Explicitly labelled approximations.

Newer executable-backed findings override older guesses. Never silently promote
a web-preview approximation into engine behavior.

## Required engine invariants

- Gameplay uses deterministic fixed steps. The default interval is exactly 66
  integer milliseconds, with a strict `elapsed > interval` catch-up comparison.
- World coordinates use 8.8 fixed point where recovered.
- Simulation state is independent from rendering and wall-clock frame rate.
- Original assets remain external and are loaded from a user-selected install.
- Preserve route direction, 8-bit facing, 16-sector art lookup, and interpolated
  position as separate concepts.
- Tests accompany every decoded binary or gameplay contract.

## Project boundaries

- `src/DarkColony.Engine`: platform-independent simulation and original-data readers.
- `src/DarkColony.App`: Windows desktop host and renderer.
- `src/DarkColony.Presentation`: platform-free presentation rules (display
  layout and settings, the anchored gameplay HUD), tested by the checks.
- `tests/DarkColony.Engine.Checks`: dependency-free executable verification.
- `tools/`: PowerShell scripts for unattended runs, posted input and live tests.
- `docs/`: specifications, test and tool guides, and one
  `reverse-engineering/` note per recovered subsystem. List new documents in
  `docs/README.md`; finished plans move to `docs/history/`.

Keep the repository root to the solution and build files, the README,
AGENTS.md and the launchers (`run-*.cmd`, `run-display/`).

Do not introduce a third-party game framework until native asset rendering and
simulation requirements demonstrate that one is necessary.

## Verifying changes

- `dotnet build DarkColony.Port.sln` must have zero warnings (warnings are errors).
- `dotnet run --project tests/DarkColony.Engine.Checks` exits non-zero on any failure.
  While iterating, `-- --tag fast` or `-- --group <topic>` runs a subset. Run
  the full suite before committing. New checks go in their topic file under
  `tests/DarkColony.Engine.Checks/Checks/` (`docs/TESTING.md`).
- For visible behavior, use `tools/Run-Port.ps1` (PowerShell 7). It never blocks
  on a dialog; read the printed log before guessing at a failure, and inspect
  the captured screenshot. Drive menus and gameplay with `-Actions`.
- Never leave a check disabled (`#if false`, early return) to make the suite pass.
- The determinism goldens (`docs/DETERMINISM.md`) must not change in a
  refactor. Regenerate them (`--update-goldens`) only for an intended behavior
  change, and say in the commit message what changed and why.
