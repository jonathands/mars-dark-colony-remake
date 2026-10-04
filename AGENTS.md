# Dark Colony compiled port context

This directory is the clean-room compiled port. It may read the user's adjacent
original installation at runtime, but original game assets must not be copied
into this repository.

Read `docs/PORT_CONTRACT.md` before changing architecture or adding a gameplay
subsystem. It is the central statement of project shape and current priorities.

## Evidence hierarchy

1. Confirmed executable disassembly recorded in `../dc-port-26/docs/`.
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
- `tests/DarkColony.Engine.Checks`: dependency-free executable verification.
- `docs/`: compilation-focused specifications and milestone notes.

Do not introduce a third-party game framework until native asset rendering and
simulation requirements demonstrate that one is necessary.

## Verifying changes

- `dotnet build DarkColony.Port.sln` must have zero warnings (warnings are errors).
- `dotnet run --project tests/DarkColony.Engine.Checks` exits non-zero on any failure.
- For visible behavior, use `tools/Run-Port.ps1` (PowerShell 7). It never blocks
  on a dialog; read the printed log before guessing at a failure, and inspect
  the captured screenshot. Drive menus and gameplay with `-Actions`.
- Never leave a check disabled (`#if false`, early return) to make the suite pass.
- The determinism goldens (`docs/DETERMINISM.md`) must not change in a
  refactor. Regenerate them (`--update-goldens`) only for an intended behavior
  change, and say in the commit message what changed and why.
