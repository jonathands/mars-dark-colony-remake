# Testing

`tests/DarkColony.Engine.Checks` is a dependency-free executable with its own
check runner. It is not xUnit or NUnit.

```powershell
dotnet run --project tests/DarkColony.Engine.Checks                 # every check, about 35 s on 4 cores
dotnet run --project tests/DarkColony.Engine.Checks -- --tag fast   # no installation needed, under a second
```

The checks that read the original game look for `..\Dark Colony` (override
with `--data <installation>`). Without it they are reported as `SKIP`.

## Layout

| File | Contents |
| --- | --- |
| `Program.cs` | The `DeterminismCli` diagnostic modes, then the registration of every topic file and the run. |
| `CheckSuite.cs` | The registry and runner: tags, selection, parallel execution, timing, output, JUnit. |
| `CheckHelpers.cs` | Shared assertions (`Equal` reports `File.cs:line`) and fixtures. |
| `Checks/<Topic>Checks.cs` | The checks, one file per topic (see below). |
| `DeterminismHarness.cs` | Scripted whole-scenario runs, goldens, and the diagnostic modes ([DETERMINISM.md](DETERMINISM.md)). |
| `CatalogSweep.cs` | Every building, troop, research and unit of both races ([CATALOG_SWEEP.md](CATALOG_SWEEP.md)). |

The topics are World, Determinism, Assets, Movement, Acquisition, Combat,
Specials, Vision, Economy, Missions, ComputerPlayer, War, ReplayAndNetwork, and
Interface.

To add a check, put it in its topic's file:

```csharp
Check("a stop lets the in-flight step finish on its destination cell", () =>
{
    ...
    Equal(expected, actual);
});
Check("installed SPR corpus decodes", () => { ... }, CheckTags.Data);
```

A check passes unless it throws. Its name must be unique, and it says what
holds rather than what is being tested.

## Tags

| Tag | Meaning |
| --- | --- |
| `data` | Reads the original installation; skipped without one. |
| `slow` | Takes seconds: whole-scenario runs. |
| `serial` | Runs alone after the others, because it waits on sockets with timeouts. |
| `fast` | Derived: neither `data` nor `slow`. These run without the original game. |

## Options

| Option | Effect |
| --- | --- |
| `--tag <tag>` | Only checks with this tag; may repeat (any of them). |
| `--skip-tag <tag>` | Leave out checks with this tag; may repeat. |
| `--group <topic>` | Only one topic file, such as `--group Combat`. |
| `--filter <text>` | Only checks whose name contains the text. `DARKCOLONY_CHECK_FILTER` does the same. |
| `--list` | Print the selected checks with their topic and tags, and run nothing. |
| `--jobs <n>` | Checks running at once; the default is the processor count. |
| `--sequential` | One check at a time, for debugging. |
| `--junit <file>` | Also write a JUnit XML report. `artifacts/` is ignored by git. |

## Running

- **Parallelism.** Checks run in parallel, slow ones first, and the serial ones afterwards. The long harness loops (golden runs, the scenario smoke, the campaign smoke, both sweep races) also run their scenarios in parallel. The scenarios share only the immutable rules.
- **Output.** A check's console output is buffered and printed under its result line.
- **Order.** Results are printed in registration order, each with its time, followed by the five slowest checks and the totals.
- **Exit code.** 1 when any check fails.

## Plan

The rest of the suite plan, in order. Phase 1 and the catalog sweep are done.

1. **Runner** (done, 2026-10-04): topic files, tags, parallel runs, timing, JUnit. The full suite went from about 85 s to about 35 s.
2. **Engine gaps**:
   - Network robustness: a full lobby, the host leaving, malformed or oversized messages, slow peers.
   - Fuzzing of every file reader with fixed seeds (SCN, SPR, MAP/PTH, TRO, AVI, ISO9660, CUE, `.dcsave`). A bad file must give a controlled error.
3. **Simulation invariants and soak runs**: the catalog sweep's invariants checked during every scripted scenario, and long computer-only War runs.
4. **Wider determinism**:
   - Short goldens for all installed scenarios.
   - Save and load at a random tick equals an uninterrupted run.
   - Debug equals Release.
   - Lockstep with four peers and random orders.
   - A digest per subsystem, to show where a divergence starts.
5. **App**: move testable logic out of `MainForm` (screen flow, hit-testing, lobby state), compare rendered frames with WARP goldens, and make the two-instance network run a check.
6. **Performance**: milliseconds per update and allocations on the heaviest maps, local only.
7. **Native comparison**: read-only memory traces of `dc.exe` on missions that need no input, compared with the port at the same updates.
