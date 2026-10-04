using DarkColony.Engine.Commands;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.World;

/// <summary>
/// Deterministic stand-in for players: it issues every kind of world command
/// to every team on a fixed cadence, using only public simulation state and
/// its own LCG (never the simulation's random stream). Its orders are not a
/// strategy; they exist to drive movement, combat, economy, construction,
/// production, research, and specials through as many paths as possible.
/// </summary>
internal sealed class ScriptedCommander
{
    private readonly ScenarioSimulation simulation;
    private readonly PathRegionMap path;
    private readonly SimulationRules rules;
    private readonly DependencyCatalog dependencies;
    private uint state;
    private ulong sequence;

    public ScriptedCommander(ScenarioSimulation simulation, PathRegionMap path, SimulationRules rules, uint seed)
    {
        this.simulation = simulation;
        this.path = path;
        this.rules = rules;
        dependencies = rules.Dependencies;
        state = seed;
    }

    public List<ScheduledWorldCommand> CommandsFor(ulong tick)
    {
        var commands = new List<ScheduledWorldCommand>();
        void Add(WorldCommand command) => commands.Add(new ScheduledWorldCommand(tick, sequence++, command));

        var living = simulation.Actors.Where(actor => !actor.IsDestroyed).OrderBy(actor => actor.Seed.InstanceId).ToList();
        var teams = living.Select(actor => actor.Seed.Team)
            .Where(team => team >= 0 && team != AutonomousSpawnSeeder.InternalNeutralTeam)
            .Distinct().Order().ToList();

        if (tick % 10 == 0)
        {
            foreach (var actor in living)
            {
                if (!teams.Contains(actor.Seed.Team) || simulation.EffectiveDefinition(actor).MovementSpeed <= 0) continue;
                if ((ulong)(actor.Seed.InstanceId + (int)(tick / 10)) % 6 != 0) continue;
                IssueUnitOrder(actor, living, Add);
            }
        }
        if (tick % 25 == 0)
        {
            foreach (var team in teams) IssueEconomyOrders(team, living, Add);
        }
        if (tick % 60 == 0)
        {
            foreach (var actor in living)
            {
                if (!teams.Contains(actor.Seed.Team) || (ulong)(actor.Seed.InstanceId + (int)(tick / 60)) % 5 != 0) continue;
                IssueSpecialOrder(actor, living, Add);
            }
        }
        return commands;
    }

    private void IssueUnitOrder(SimulatedActor actor, List<SimulatedActor> living, Action<WorldCommand> add)
    {
        var id = actor.Seed.InstanceId;
        var code = simulation.EffectiveDefinition(actor).Code;
        if (code is "EXPL" or "SLUG" && actor.HarvestVentId is null)
        {
            var vent = simulation.PetraVents
                .Where(candidate => candidate.HarvesterInstanceId is null && candidate.PendingHarvesterInstanceId is null)
                .OrderBy(candidate => Distance(candidate.Position, actor.Movement.OccupiedCell))
                .ThenBy(candidate => candidate.Id)
                .FirstOrDefault();
            if (vent is not null)
            {
                add(new HarvestVentIntent(id, vent.Id));
                return;
            }
        }

        var hostile = NearestHostile(actor, living);
        var roll = Next(10);
        if (roll == 0)
        {
            add(new StopIntent(id));
        }
        else if (roll <= 3 && hostile is not null && simulation.EffectiveWeaponFor(actor) is not null)
        {
            add(new AttackIntent(id, hostile.Seed.InstanceId));
        }
        else if (roll <= 5 && hostile is not null)
        {
            add(new AttackMoveIntent(id, hostile.Movement.OccupiedCell));
        }
        else
        {
            add(new MoveIntent(id, NearbyCell(actor.Movement.OccupiedCell, 10), AppendWaypoint: roll == 6));
        }
    }

    private void IssueEconomyOrders(int team, List<SimulatedActor> living, Action<WorldCommand> add)
    {
        var economy = simulation.EconomyForTeam(team);
        if (economy is null) return;
        var own = living.Where(actor => actor.Seed.Team == team).ToList();
        if (own.Count == 0) return;
        // The team's faction is that of most of its actors; buying the other
        // faction's items only exercises the WrongFaction rejection.
        var faction = own.GroupBy(actor => actor.Definition.Faction)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key).First().Key;
        var items = dependencies.Items.Values.Where(item => ItemFaction(item) == faction).OrderBy(item => item.Id).ToArray();
        if (items.Length != 0)
        {
            var offset = Next(items.Length);
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[(offset + index) % items.Length];
                if (economy.Evaluate(dependencies, item.Id) != PurchaseEligibility.Available) continue;
                add(new PurchaseIntent(team, item.Id));
                break;
            }
        }

        var structures = own.Where(actor => simulation.EffectiveDefinition(actor).MovementSpeed <= 0).ToList();
        var anchor = (structures.Count != 0 ? structures : own)[0].Movement.OccupiedCell;
        foreach (var itemId in economy.ReservedItems.Distinct().ToArray())
        {
            if (!dependencies.TryGet(itemId, out var item)) continue;
            if (item.IsBuilding)
            {
                add(new PlaceBuildingIntent(team, itemId, NearbyCell(anchor, 6)));
            }
            else if (structures.Count != 0)
            {
                var source = structures[Next(structures.Count)].Seed.InstanceId;
                add(item.IsUpgrade ? new ResearchIntent(team, itemId, source) : new ProduceUnitIntent(team, itemId, source));
            }
        }
    }

    private int? ItemFaction(DependencyDefinition item)
    {
        int? entityId = item.IsBuilding
            ? rules.Footprints.TryResolveBuildingEntity(item.BuildingFaction!.Value, item.BuildingVariant!.Value, item.BuildingSlot!.Value, out var building) ? building : null
            : item.IsTroop ? item.TroopEntityId : item.UpgradeEntityId;
        return entityId is { } id && id >= 0 && id < rules.Entities.Entities.Count ? rules.Entities.Entities[id].Faction : null;
    }

    private void IssueSpecialOrder(SimulatedActor actor, List<SimulatedActor> living, Action<WorldCommand> add)
    {
        var id = actor.Seed.InstanceId;
        var definition = simulation.EffectiveDefinition(actor);
        // Mostly target the unit's own special; one in eight tries an arbitrary
        // one so the rejection paths stay covered too.
        if (Next(8) != 0)
        {
            WorldCommand? own = definition.Code switch
            {
                "ENGI" or "SLOM" => new DeployMineIntent(id),
                "TURR" or "XENO" => new DeployTowerIntent(id),
                "SARG" or "PSYC" => new DeployStealIntent(id),
                "SARGSTL" or "PSYCSTL" => new RetractStealIntent(id),
                "BEON" or "ZISP" => new HealAreaIntent(id),
                "EDPLY" or "SDPL" => new RetractHarvesterIntent(id),
                _ when definition.Id is >= 69 and <= 76 => new InspireTroopsIntent(id),
                _ when definition.HasGroundSpecialAttack => new GroundSpecialAttackIntent(id,
                    NearestHostile(actor, living)?.Movement.OccupiedCell ?? NearbyCell(actor.Movement.OccupiedCell, 6)),
                _ => null,
            };
            if (own is not null)
            {
                add(own);
                return;
            }
        }
        switch (Next(8))
        {
            case 0: add(new InspireTroopsIntent(id)); break;
            case 1: add(new HealAreaIntent(id)); break;
            case 2: add(new DeployMineIntent(id)); break;
            case 3: add(new DeployTowerIntent(id)); break;
            case 4: add(new DeployStealIntent(id)); break;
            case 5: add(new RetractStealIntent(id)); break;
            case 6: add(new RetractHarvesterIntent(id)); break;
            default:
                var hostile = NearestHostile(actor, living);
                add(new GroundSpecialAttackIntent(id, hostile?.Movement.OccupiedCell ?? NearbyCell(actor.Movement.OccupiedCell, 6)));
                break;
        }
    }

    private SimulatedActor? NearestHostile(SimulatedActor actor, List<SimulatedActor> living) => living
        .Where(other => other.Seed.Team != actor.Seed.Team && other.Seed.Team >= 0 &&
            simulation.TeamRelations.IsHostile(actor.Seed.Team, other.Seed.Team))
        .OrderBy(other => Distance(other.Movement.OccupiedCell, actor.Movement.OccupiedCell))
        .ThenBy(other => other.Seed.InstanceId)
        .FirstOrDefault();

    private CellCoordinate NearbyCell(CellCoordinate origin, int radius) => new(
        Math.Clamp(origin.X + Next(radius * 2 + 1) - radius, 0, path.Width - 1),
        Math.Clamp(origin.Z + Next(radius * 2 + 1) - radius, 0, path.Height - 1));

    private static int Distance(CellCoordinate left, CellCoordinate right) =>
        Math.Max(Math.Abs(left.X - right.X), Math.Abs(left.Z - right.Z));

    private int Next(int bound)
    {
        state = unchecked(state * 1664525 + 1013904223);
        return (int)((state >> 8) % (uint)bound);
    }
}

internal sealed record DeterminismRun(string Scenario, ulong Ticks, IReadOnlyList<(ulong Tick, string Hash)> Checkpoints, string FinalDescription);

/// <summary>Runs installed scenarios under <see cref="ScriptedCommander"/> and records digests.</summary>
internal static class DeterminismHarness
{
    /// <summary>
    /// Scenarios with recorded golden digests, chosen by --coverage-scan for the
    /// widest event coverage across both campaigns, training, and War maps.
    /// </summary>
    public static readonly string[] GoldenScenarios =
        ["human/human01", "human/human06", "human/human15", "alien/alien06", "alien/alien13", "test/atrain6", "mplayer/j4play01", "mplayer/d2play01"];
    public const ulong GoldenTicks = 1200;
    public static readonly ulong[] GoldenCheckpoints = [1, 100, 300, 600, 900, 1200];
    public const string GoldenFileName = "determinism-goldens.txt";

    public static (ScenarioSimulation Simulation, PathRegionMap Path) Load(GameInstallation installation, SimulationRules rules, string scenario)
    {
        var file = installation.DataFile(["scenario", .. scenario.Split('/')]) + ".scn";
        var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
        var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
        return (ScenarioSimulation.Create(ScenarioDefinition.Load(file), path, rules), path);
    }

    public static IReadOnlyList<string> InstalledScenarios(GameInstallation installation) => Directory
        .GetFiles(installation.ScenarioPath, "*.scn", SearchOption.AllDirectories)
        .Where(file => File.Exists(Path.ChangeExtension(file, ".map")) && File.Exists(Path.ChangeExtension(file, ".pth")))
        .Select(file => Path.GetRelativePath(installation.ScenarioPath, Path.ChangeExtension(file, null)).Replace('\\', '/'))
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <param name="digestEveryTick">Fold the full state into the running hash after each tick (slower, strongest).</param>
    public static DeterminismRun Run(GameInstallation installation, SimulationRules rules, string scenario, ulong ticks,
        IReadOnlyCollection<ulong> checkpoints, bool digestEveryTick = true, ulong? describeAtTick = null)
    {
        var (simulation, path) = Load(installation, rules, scenario);
        var commander = new ScriptedCommander(simulation, path, rules, StableSeed(scenario));
        var accumulator = new SimulationDigest.Accumulator();
        accumulator.Add(simulation);
        var results = new List<(ulong, string)>();
        string? description = null;
        for (ulong tick = 1; tick <= ticks; tick++)
        {
            simulation.Step(commander.CommandsFor(tick));
            if (digestEveryTick || checkpoints.Contains(tick)) accumulator.Add(simulation);
            if (checkpoints.Contains(tick)) results.Add((tick, accumulator.Current));
            if (describeAtTick == tick) description = SimulationDigest.Describe(simulation);
        }
        return new DeterminismRun(scenario, ticks, results, description ?? SimulationDigest.Describe(simulation));
    }

    /// <summary>
    /// Counts each per-tick event (and its outcome, when it has one) over a
    /// scripted run: a coverage report of which simulation paths the scripted
    /// commander actually reaches.
    /// </summary>
    public static SortedDictionary<string, int> EventSummary(GameInstallation installation, SimulationRules rules, string scenario, ulong ticks)
    {
        var (simulation, path) = Load(installation, rules, scenario);
        var commander = new ScriptedCommander(simulation, path, rules, StableSeed(scenario));
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var eventProperties = typeof(ScenarioSimulation).GetProperties()
            .Where(property => property.Name.StartsWith("Last", StringComparison.Ordinal))
            .ToArray();
        for (ulong tick = 1; tick <= ticks; tick++)
        {
            simulation.Step(commander.CommandsFor(tick));
            foreach (var property in eventProperties)
            {
                if (property.GetValue(simulation) is not System.Collections.IEnumerable events) continue;
                foreach (var item in events)
                {
                    var outcome = item.GetType().GetProperty("Outcome")?.GetValue(item);
                    var key = outcome is null ? property.Name : $"{property.Name}.{outcome}";
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
        }
        foreach (var team in simulation.TeamResources.Keys.Order())
        {
            var economy = simulation.EconomyForTeam(team)!;
            counts[$"final team {team}: P7={economy.P7} completed={economy.CompletedItems.Count} reserved={economy.ReservedItems.Count}"] = 0;
        }
        counts[$"final living actors {simulation.Actors.Count(actor => !actor.IsDestroyed)}/{simulation.Actors.Count}"] = 0;
        return counts;
    }

    /// <summary>FNV-1a of the scenario name: a seed that never depends on process hash randomization.</summary>
    public static uint StableSeed(string scenario)
    {
        var hash = 2166136261u;
        foreach (var character in scenario) hash = unchecked((hash ^ character) * 16777619u);
        return hash;
    }

    /// <summary>Runs every golden scenario; returns the earliest divergent checkpoint of each.</summary>
    public static List<string> CompareGoldens(GameInstallation installation, SimulationRules rules)
    {
        var goldens = ParseGoldens(File.ReadAllText(GoldenFilePath()));
        var mismatches = new List<string>();
        foreach (var scenario in GoldenScenarios)
        {
            var run = Run(installation, rules, scenario, GoldenTicks, GoldenCheckpoints);
            foreach (var (tick, hash) in run.Checkpoints)
            {
                if (!goldens.TryGetValue((scenario, tick), out var expected))
                {
                    mismatches.Add($"{scenario}@{tick}: no golden recorded");
                    break;
                }
                if (expected == hash) continue;
                // Later checkpoints fold in the first divergence, so report only the earliest.
                mismatches.Add($"{scenario}@{tick}: expected {expected}, got {hash}");
                break;
            }
        }
        return mismatches;
    }

    public static string FormatGoldens(IEnumerable<DeterminismRun> runs) =>
        "# Determinism goldens: scenario tick running-digest. Regenerate only for an intended behavior\n" +
        "# change and explain it in the commit: dotnet run --project tests/DarkColony.Engine.Checks -- --update-goldens\n" +
        string.Concat(runs.SelectMany(run => run.Checkpoints.Select(point => $"{run.Scenario} {point.Tick} {point.Hash}\n")));

    public static Dictionary<(string Scenario, ulong Tick), string> ParseGoldens(string text) => text
        .Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length != 0 && !line.StartsWith('#'))
        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .ToDictionary(parts => (parts[0], ulong.Parse(parts[1])), parts => parts[2]);

    /// <summary>The checked-in goldens file, found by walking up from the build output.</summary>
    public static string GoldenFilePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "DarkColony.Engine.Checks", GoldenFileName);
            if (File.Exists(Path.Combine(directory.FullName, "DarkColony.Port.sln"))) return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root from the checks output directory.");
    }
}

/// <summary>
/// Command-line modes of the checks program that do not run the suite:
/// <c>--update-goldens</c> rewrites the golden file, and
/// <c>--dump-digest &lt;scenario&gt; &lt;tick&gt; &lt;file&gt;</c> writes the canonical state text
/// at a tick for diffing two builds.
/// </summary>
internal static class DeterminismCli
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        var update = args.Contains("--update-goldens");
        var dump = Array.IndexOf(args, "--dump-digest");
        var summary = Array.IndexOf(args, "--event-summary");
        string[] modes = ["--update-goldens", "--verify-goldens", "--dump-digest", "--event-summary", "--timing", "--coverage-scan"];
        if (!args.Any(modes.Contains)) return false;

        var dataIndex = Array.IndexOf(args, "--data");
        var installation = GameInstallation.Open(dataIndex >= 0 ? args[dataIndex + 1] : Path.Combine("..", "Dark Colony"));
        var rules = SimulationRules.Load(installation);
        if (update)
        {
            var runs = DeterminismHarness.GoldenScenarios
                .Select(scenario => DeterminismHarness.Run(installation, rules, scenario, DeterminismHarness.GoldenTicks, DeterminismHarness.GoldenCheckpoints))
                .ToArray();
            var file = DeterminismHarness.GoldenFilePath();
            File.WriteAllText(file, DeterminismHarness.FormatGoldens(runs));
            Console.WriteLine($"Wrote {runs.Sum(run => run.Checkpoints.Count)} golden digests to {file}");
            return true;
        }

        if (args.Contains("--verify-goldens"))
        {
            var mismatches = DeterminismHarness.CompareGoldens(installation, rules);
            foreach (var mismatch in mismatches) Console.WriteLine(mismatch);
            Console.WriteLine(mismatches.Count == 0 ? "All golden digests match." : $"{mismatches.Count} golden scenario(s) diverged.");
            exitCode = mismatches.Count == 0 ? 0 : 1;
            return true;
        }

        var timing = Array.IndexOf(args, "--timing");
        if (timing >= 0)
        {
            var timedTicks = ulong.Parse(args[timing + 1]);
            foreach (var timedScenario in DeterminismHarness.InstalledScenarios(installation))
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var (simulation, path) = DeterminismHarness.Load(installation, rules, timedScenario);
                var loaded = clock.Elapsed.TotalMilliseconds;
                var commander = new ScriptedCommander(simulation, path, rules, DeterminismHarness.StableSeed(timedScenario));
                clock.Restart();
                for (ulong timedTick = 1; timedTick <= timedTicks; timedTick++) simulation.Step(commander.CommandsFor(timedTick));
                var perTick = clock.Elapsed.TotalMilliseconds / timedTicks;
                Console.WriteLine($"{timedScenario,-18} actors {simulation.Actors.Count,4} load {loaded,7:0.0} ms  tick {perTick,7:0.000} ms");
            }
            return true;
        }

        var scan = Array.IndexOf(args, "--coverage-scan");
        if (scan >= 0)
        {
            // Which scenarios reach the most distinct event kinds/outcomes?
            var scanTicks = ulong.Parse(args[scan + 1]);
            foreach (var scanned in DeterminismHarness.InstalledScenarios(installation))
            {
                var keys = DeterminismHarness.EventSummary(installation, rules, scanned, scanTicks)
                    .Where(pair => pair.Value > 0).Select(pair => pair.Key).ToArray();
                Console.WriteLine($"{keys.Length,3} {scanned,-18} {string.Join(' ', keys.Where(key => !key.EndsWith("SourceInvalid") && !key.EndsWith("Unsupported") && !key.Contains("SourceNot")).Select(key => key.Replace("Last", "")))}");
            }
            return true;
        }

        if (summary >= 0)
        {
            var counts = DeterminismHarness.EventSummary(installation, rules, args[summary + 1], ulong.Parse(args[summary + 2]));
            foreach (var (name, count) in counts) Console.WriteLine($"{count,8} {name}");
            return true;
        }

        var scenario = args[dump + 1];
        var tick = ulong.Parse(args[dump + 2]);
        var output = args[dump + 3];
        var run = DeterminismHarness.Run(installation, rules, scenario, tick, [], digestEveryTick: false, describeAtTick: tick);
        File.WriteAllText(output, run.FinalDescription);
        Console.WriteLine($"Wrote {scenario} state at tick {tick} to {output}");
        return true;
    }
}
