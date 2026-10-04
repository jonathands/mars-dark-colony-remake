# Determinism and behavior guard

The engine checks include a whole-simulation guard: scripted runs of installed
scenarios are hashed every tick and compared with recorded goldens. Any change
to observable simulation behavior breaks it; a refactor that preserves
behavior does not.

## Pieces

- `SimulationDigest` (engine) renders the canonical text of all **public**
  simulation state: actors, the three occupancy grids, projectiles,
  transports, vents, team economies, team relations, the day/night clock, and
  every per-tick `Last*` event list. Sets and dictionaries are sorted and
  catalog definitions are reduced to their identity. Because only the public
  API is read, reorganizing private fields or splitting `ScenarioSimulation`
  into subsystems keeps every digest identical.
- `SimulationRules` (engine) is the single bundle of installed rule tables.
  The game host and the checks both create simulations from it.
- `ScriptedCommander` (checks) issues every kind of `WorldCommand` to every
  team on fixed cadences: moves, waypoints, stops, attacks, attack-moves, vent
  harvesting, faction-matched purchases, building placement, troop
  production, research, and each unit's special. It uses its own LCG and never
  consumes the simulation's native random stream.
- `DeterminismHarness` (checks) runs a scenario under the commander and folds
  the full digest into a running SHA-256 after every tick, so a divergence is
  caught at the tick where it first becomes observable.

## Checks

1. Every complete installed scenario (108) runs 300 scripted ticks without an
   exception.
2. Two runs of the same scenario in one process produce identical digests.
3. Eight golden scenarios run 1,200 ticks and must match
   `tests/DarkColony.Engine.Checks/determinism-goldens.txt` at ticks 1, 100,
   300, 600, 900, and 1,200. The goldens were chosen with `--coverage-scan`
   for the widest event coverage: `human01`, `human06`, `human15`, `alien06`,
   `alien13`, `atrain6`, `j4play01`, and `d2play01`.

## Command-line modes

Run from the repository root; add `--data <installation>` when the original
game is not at `..\Dark Colony`.

| Mode | Purpose |
| --- | --- |
| `--verify-goldens` | Golden comparison only, with the eight scenarios in parallel (about 15 s); exit code 1 on divergence. |
| `--update-goldens` | Rewrite the goldens file. |
| `--dump-digest <scenario> <tick> <file>` | Write the canonical state at a tick, to diff two builds. |
| `--event-summary <scenario> <ticks>` | Count events and outcomes reached by a scripted run. |
| `--coverage-scan <ticks>` | Distinct event kinds reached, for every scenario. |
| `--timing <ticks>` | Load time and mean milliseconds per tick, for every scenario. |
| `--run <scenario> <ticks>` | Simulation only, no digests: the workload for a profiler. |

Example: `dotnet run --project tests/DarkColony.Engine.Checks -- --verify-goldens`.

## Policy

- A golden mismatch means simulation behavior changed. Regenerate the goldens
  only for an intended change, and state in the commit message which behavior
  changed and why.
- Refactors and performance work must leave the goldens untouched.
- To locate a divergence, run `--dump-digest` for the reported scenario and
  checkpoint on both builds and diff the two files.

## Validation

Mutations applied on 2026-10-04 to confirm that the guard detects small
behavior changes:

| Mutation | Result |
| --- | --- |
| Damage matrix divisor 100 → 101 | Goldens: 8 scenarios diverged |
| Ability-charge cadence 0x20 → 0x40 | Goldens: 6 scenarios diverged |
| Blocked-route jitter `% 3 - 1` → `% 3` | Goldens: 7 scenarios diverged |
| One row of the local-path priority table | Goldens: 1 scenario diverged |
| Autonomous wander cadence `& 7` → `& 15` | Goldens: 5 scenarios diverged |
| Vent reservoir boundary `>` → `>=` | Goldens unchanged (25,000-unit reservoirs never drain in 1,200 ticks); caught by two focused checks |

Edge cases that long runs never reach remain the job of focused checks.

## Coverage gaps

No scenario reaches these within 600 scripted ticks, so only focused checks
cover them: area heals, mine deployment, research completion, battlefield
transports, P7 theft, day/night transitions (phases last over 5,400 ticks),
and accepted ground special attacks.

## Findings from the first runs

- **Crash, fixed:** cancelling a step mid-transition threw when another actor
  had entered the vacated source cell (found in `human01`). The step now
  completes on the destination the actor still owns; see
  `PackedPathPlayback.Cancel`.
- **Performance, fixed:** a tick cost up to 28 ms in Debug (14 ms in
  Release; `alien09`, 118 actors) against a 66 ms budget, almost all of it in
  the local path search. Four exact changes keep every golden identical:
  - the per-node LINQ candidate sort became a precomputed table;
  - per-search dictionaries became reused stamped arrays;
  - superseded queue entries are skipped;
  - an unenterable target answers `NoRoute` without flooding its region
    (since 2026-10-04 an occupied target is enterable, as the native search
    seeds it; only region rules make a target unenterable).

  The worst scenario now costs 0.94 ms per tick in Release
  (`d4play08`), and `alien09` costs 1.2 ms. Measure with `--timing`.
  Profile with `dotnet-trace collect --profile dotnet-sampled-thread-time`
  against `--run <scenario> <ticks>`.
- **War economy cannot start:** War teams begin with 1,500 P7, the cheapest
  root building (Exo Center, item 0) costs 2,000, and no War starting roster
  contains a harvester. With the passive trickle removed, a War team can never
  buy anything. This conflicts with the user-observed note that a slow
  trickle speeds up once an Exploiter is deployed, so the removal needs
  original-game evidence before it stands.
- **Cross-platform risk:** `NativeBearing` uses `Math.Cos`, `Math.Sin`, and
  `Math.Atan`, whose results can differ between CPUs and runtimes. Replace them
  with recovered tables or integer math before networked play relies on
  lockstep. `Math.Sqrt` is correctly rounded and safe.
