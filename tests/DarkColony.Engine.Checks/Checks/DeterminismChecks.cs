using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Missions;
using DarkColony.Engine.Movement;
using DarkColony.Engine.World;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.Commands;
using DarkColony.Engine.Interface;
using DarkColony.Engine.Audio;
using DarkColony.Engine.Video;
using DarkColony.Engine.Network;
using System.Buffers.Binary;
using static CheckHelpers;

/// <summary>Scripted whole-scenario runs: no faults, repeatable, and equal to the recorded golden digests.</summary>
internal static class DeterminismChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("Determinism", name, tags, action);

        Check("clock uses strict comparison", () =>
        {
            var clock = new FixedStepClock(1_000);
            var ticks = 0;
            Equal(0, clock.Advance(1_066, () => ticks++));
            Equal(0, ticks);
            Equal(1, clock.Advance(1_067, () => ticks++));
            Equal(1, ticks);
        });

        Check("clock catches up deterministically", () =>
        {
            var clock = new FixedStepClock(0);
            var ticks = 0;
            Equal(3, clock.Advance(199, () => ticks++));
            Equal(3, ticks);
            Equal(198L, clock.AccumulatedTimestamp);
        });

        Check("every installed scenario runs scripted orders without faults", () =>
        {
            const ulong smokeTicks = 300;
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var scenarios = DeterminismHarness.InstalledScenarios(install);
            if (scenarios.Count < 101) throw new InvalidOperationException($"Expected at least 101 complete scenarios, found {scenarios.Count}.");
            var started = System.Diagnostics.Stopwatch.StartNew();
            var errors = new Exception?[scenarios.Count];
            Parallel.For(0, scenarios.Count, index =>
            {
                try
                {
                    DeterminismHarness.Run(install, rules, scenarios[index], smokeTicks, [smokeTicks], digestEveryTick: false);
                }
                catch (Exception error)
                {
                    errors[index] = error;
                }
            });
            // Report the first failing scenario in installation order.
            if (errors.Select((error, index) => (error, index)).FirstOrDefault(pair => pair.error is not null) is ({ } failure, var failed))
                throw new InvalidOperationException($"{scenarios[failed]}: {failure.GetType().Name}: {failure.Message}", failure);
            Console.WriteLine($"  scripted smoke: {scenarios.Count} scenarios x {smokeTicks} ticks in {started.Elapsed.TotalSeconds:0.0}s");
        }, CheckTags.Data | CheckTags.Slow);

        Check("scripted scenario runs are repeatable within one process", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            ulong[] checkpoints = [50, 150, 300];
            var first = DeterminismHarness.Run(install, rules, "mplayer/j4play01", 300, checkpoints);
            var second = DeterminismHarness.Run(install, rules, "mplayer/j4play01", 300, checkpoints);
            Equal(string.Join(' ', first.Checkpoints), string.Join(' ', second.Checkpoints));
            Equal(first.FinalDescription, second.FinalDescription);
        }, CheckTags.Data | CheckTags.Slow);

        Check("scripted scenario runs match recorded golden digests", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var mismatches = DeterminismHarness.CompareGoldens(install, SimulationRules.Load(install));
            if (mismatches.Count != 0)
                throw new InvalidOperationException(
                    "simulation behavior changed: " + string.Join("; ", mismatches) +
                    ". If intended, rerun with --update-goldens and explain the change in the commit; " +
                    "compare states with --dump-digest <scenario> <tick> <file> on both builds.");
        }, CheckTags.Data | CheckTags.Slow);
    }
}
