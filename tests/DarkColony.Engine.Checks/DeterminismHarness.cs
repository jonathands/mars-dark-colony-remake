using DarkColony.Engine.Commands;
using DarkColony.Engine.Assets;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Missions;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.Simulation;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.World;
using DarkColony.Engine.Video;
using DarkColony.Engine.Network;

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
        return (ScenarioSimulation.Create(ScenarioDefinition.Load(file), path, rules, MissionScript.LoadForScenario(file), map), path);
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
        string[] modes = ["--dump-fin", "--animation-order", "--update-goldens", "--verify-goldens", "--dump-digest", "--event-summary", "--timing", "--coverage-scan", "--run", "--render-map", "--ai-report", "--campaign-smoke", "--decode-avi", "--lockstep", "--catalog-sweep"];
        if (!args.Any(modes.Contains)) return false;

        // --decode-avi <file.avi> [frame ...]: SHA-256 prefixes of decoded RGB24 frames (compare with ffmpeg -pix_fmt rgb24).
        var decodeAvi = Array.IndexOf(args, "--decode-avi");
        if (decodeAvi >= 0)
        {
            var avi = AviFile.Parse(File.ReadAllBytes(args[decodeAvi + 1]));
            var wanted = args.Skip(decodeAvi + 2).TakeWhile(argument => !argument.StartsWith("--", StringComparison.Ordinal)).Select(int.Parse).ToHashSet();
            var decoder = new CinepakDecoder(avi.Width, avi.Height);
            Console.WriteLine($"{avi.VideoHandler} {avi.Width}x{avi.Height} {avi.VideoFrames.Count} frames, {avi.MicrosecondsPerFrame} us, audio {avi.AudioFormat} {avi.Audio.Length} bytes");
            for (var frame = 0; frame < avi.VideoFrames.Count; frame++)
            {
                decoder.Decode(avi.VideoFrames[frame]);
                if (wanted.Contains(frame) || (wanted.Contains(-1) && frame == avi.VideoFrames.Count - 1))
                    Console.WriteLine($"  {frame} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(decoder.Frame))[..16].ToLowerInvariant()}");
            }
            return true;
        }

        var dataIndex = Array.IndexOf(args, "--data");
        var installation = GameInstallation.Open(dataIndex >= 0 ? args[dataIndex + 1] : Path.Combine("..", "Dark Colony"));
        var rules = SimulationRules.Load(installation);

        // --lockstep host|join <port> <ticks>: one peer of a two-process scripted match on mplayer/d2play01.
        var lockstep = Array.IndexOf(args, "--lockstep");
        if (lockstep >= 0)
        {
            exitCode = RunLockstepPeer(installation, rules, args[lockstep + 1], int.Parse(args[lockstep + 2]), ulong.Parse(args[lockstep + 3]));
            return true;
        }
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

        // --catalog-sweep: every building, troop, research and unit of both races, with a line per item.
        if (args.Contains("--catalog-sweep"))
        {
            var failures = 0;
            foreach (var race in new[] { 0, 1 })
            {
                var sweep = CatalogSweep.Run(installation, rules, race);
                sweep.Report.ForEach(Console.WriteLine);
                sweep.Gaps.ForEach(gap => Console.WriteLine("KNOWN GAP " + gap));
                sweep.Failures.ForEach(failure => Console.WriteLine("FAIL " + failure));
                Console.WriteLine($"{(race == 0 ? "Human" : "Gray")}: {sweep.Buildings} buildings, {sweep.Troops} troops, {sweep.Research} research, {sweep.Units} units, {sweep.Gaps.Count} known gaps, {sweep.Failures.Count} failures");
                failures += sweep.Failures.Count;
            }
            exitCode = failures == 0 ? 0 : 1;
            return true;
        }

        var renderMap = Array.IndexOf(args, "--render-map");
        if (renderMap >= 0)
        {
            MapDiagnostics.Render(installation, rules, args[renderMap + 1], args[renderMap + 2]);
            return true;
        }

        var plainRun = Array.IndexOf(args, "--run");
        if (plainRun >= 0)
        {
            // Simulation only, no digests: the workload to attach a profiler to.
            var (simulation, path) = DeterminismHarness.Load(installation, rules, args[plainRun + 1]);
            var commander = new ScriptedCommander(simulation, path, rules, DeterminismHarness.StableSeed(args[plainRun + 1]));
            var runTicks = ulong.Parse(args[plainRun + 2]);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (ulong runTick = 1; runTick <= runTicks; runTick++) simulation.Step(commander.CommandsFor(runTick));
            Console.WriteLine($"{args[plainRun + 1]}: {runTicks} ticks, {clock.Elapsed.TotalMilliseconds / runTicks:0.000} ms/tick");
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

        var dumpFin = Array.IndexOf(args, "--dump-fin");
        if (dumpFin >= 0)
        {
            // --dump-fin <file.fin> <animation> <directory>: each frame as a PPM (black background).
            var definition = AnimationDefinition.Load(installation.DataFile("animate", args[dumpFin + 1]));
            var range = definition.Animations.First(animation => animation.Name.Equals(args[dumpFin + 2], StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(args[dumpFin + 3]);
            Sprite Load(string name) => Sprite.Load(File.Exists(installation.DataFile("sprites", name + ".spr"))
                ? installation.DataFile("sprites", name + ".spr") : installation.DataFile("intrface", name + ".spr"));
            // Optional 5th argument: an interface GIF whose global palette replaces the sprites' own.
            IReadOnlyList<VgaColor>? screenPalette = dumpFin + 4 < args.Length && !args[dumpFin + 4].StartsWith("--")
                ? GifPalette.Load(installation.DataFile("intrface", args[dumpFin + 4] + ".gif"))
                : null;
            for (var frame = range.FirstFrame; frame <= range.LastFrame; frame++)
            {
                var composite = definition.Compose(frame, Load, palette: screenPalette);
                var ppm = new List<byte>(System.Text.Encoding.ASCII.GetBytes($"P6 {Math.Max(1, composite.Width)} {Math.Max(1, composite.Height)} 255\n"));
                for (var pixel = 0; pixel < composite.Width * composite.Height; pixel++)
                    ppm.AddRange(composite.Rgba[pixel * 4 + 3] == 0 ? [0, 0, 0] : [composite.Rgba[pixel * 4], composite.Rgba[pixel * 4 + 1], composite.Rgba[pixel * 4 + 2]]);
                File.WriteAllBytes(Path.Combine(args[dumpFin + 3], $"{range.Name}-{frame}-d{definition.LogicalFrames[frame].Delay}.ppm"), [.. ppm]);
            }
            return true;
        }

        if (args.Contains("--animation-order"))
        {
            // Entity animations that the anim.dat load order resolves differently.
            var animate = installation.DataFile("animate");
            var byStem = EntityAnimationCatalog.Build(rules.Entities, animate);
            var byLoad = EntityAnimationCatalog.Build(rules.Entities, animate, EntityAnimationCatalog.LoadOrder(installation.DataFile("anim.dat")));
            static string Name(EntityAnimationCandidate? candidate) => candidate is null ? "-" : $"{Path.GetFileName(candidate.FinPath)}:{candidate.AnimationName}";
            foreach (var entity in rules.Entities.Entities)
            {
                (string Kind, string Old, string New)[] pairs =
                [
                    ("stand", Name(byStem.Preferred(entity.Id)), Name(byLoad.Preferred(entity.Id))),
                    ("die", Name(byStem.PreferredDeath(entity.Id)), Name(byLoad.PreferredDeath(entity.Id))),
                    ("move0", Name(byStem.PreferredMove(entity.Id, 0)?.Candidate), Name(byLoad.PreferredMove(entity.Id, 0)?.Candidate)),
                    ("fire0", Name(byStem.PreferredFire(entity.Id, 0)?.Candidate), Name(byLoad.PreferredFire(entity.Id, 0)?.Candidate)),
                ];
                foreach (var (kind, old, @new) in pairs.Where(pair => pair.Old != pair.New))
                    Console.WriteLine($"{entity.Id,3} {entity.Code,-9} {kind,-6} {old} -> {@new}");
            }
            return true;
        }

        var campaignSmoke = Array.IndexOf(args, "--campaign-smoke");
        if (campaignSmoke >= 0)
        {
            var smokeFilter = campaignSmoke + 2 < args.Length && !args[campaignSmoke + 2].StartsWith("--") ? args[campaignSmoke + 2] : null;
            foreach (var line in CampaignSmoke(installation, rules, ulong.Parse(args[campaignSmoke + 1]), smokeFilter, args.Contains("--strike"), args.Contains("--sweep"), args.Contains("--trace")))
                Console.WriteLine(line);
            return true;
        }

        var aiReport = Array.IndexOf(args, "--ai-report");
        if (aiReport >= 0)
        {
            AiReport(installation, rules, args[aiReport + 1], ulong.Parse(args[aiReport + 2]), args.Contains("--war"));
            return true;
        }

        if (summary >= 0)
        {
            var counts = DeterminismHarness.EventSummary(installation, rules, args[summary + 1], ulong.Parse(args[summary + 2]));
            foreach (var (name, count) in counts) Console.WriteLine($"{count,8} {name}");
            return true;
        }

        return RunDump(installation, rules, args, dump);
    }

    /// <summary>
    /// <c>--ai-report &lt;scenario&gt; &lt;ticks&gt; [--war]</c>: runs without player
    /// commands (with <c>--war</c>, every other enabled team gets the Krusty
    /// planner) and prints each computer player's state every 600 ticks.
    /// </summary>
    private static void AiReport(GameInstallation installation, SimulationRules rules, string scenario, ulong ticks, bool war)
    {
        var file = installation.DataFile(["scenario", .. scenario.Split('/')]) + ".scn";
        var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
        var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
        var definition = ScenarioDefinition.Load(file);
        var local = definition.Teams.First(team => team.Enabled).TeamId;
        if (war) definition = definition.WithComputerOpponents(local);
        var simulation = ScenarioSimulation.Create(definition, path, rules, MissionScript.LoadForScenario(file), map);
        var built = new Dictionary<int, int>();
        for (ulong tick = 1; tick <= ticks; tick++)
        {
            simulation.Step([]);
            foreach (var placement in simulation.LastBuildingPlacements)
                built[placement.TeamId] = built.GetValueOrDefault(placement.TeamId) + 1;
            if (tick % 600 != 0 && tick != ticks) continue;
            Console.WriteLine($"-- tick {tick}");
            for (var player = 0; player < 8; player++)
            {
                if (simulation.KrustyState(player) is not { } brain) continue;
                var units = simulation.Actors.Where(actor => actor.Seed.Team == player && !actor.IsDestroyed)
                    .GroupBy(actor => simulation.EffectiveDefinition(actor).Code).OrderBy(group => group.Key)
                    .Select(group => $"{group.Key}x{group.Count()}");
                Console.WriteLine($"  p{player} P7={simulation.ResourceForTeam(player)} thinks={brain.Thinks} built={built.GetValueOrDefault(player)} units: {string.Join(' ', units)}");
                foreach (var group in brain.Groups)
                {
                    var tasks = group.Tasks.Select((task, index) => (task, index)).Where(entry => entry.task.Active)
                        .Select(entry => $"t{entry.index}[{entry.task.Mode} {entry.task.Current}->{entry.task.Target} n={entry.task.Members.Count}]");
                    Console.WriteLine($"    g{group.Index}: {string.Join(' ', tasks)}");
                }
            }
        }
    }

    /// <summary>
    /// One process of a lockstep pair: the host listens on <paramref name="port"/>
    /// and plays team 0, the joiner plays team 1. Each side's scripted
    /// commander orders only its own team. Prints the digest every 100 ticks;
    /// exit code 0 when no desync was seen.
    /// </summary>
    private static int RunLockstepPeer(GameInstallation installation, SimulationRules rules, string role, int port, ulong ticks)
    {
        const string scenario = "mplayer/d2play01";
        var player = role == "host" ? 0 : 1;
        ILockstepTransport transport;
        if (player == 0)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            Console.WriteLine($"host: waiting on port {port}");
            transport = TcpLockstepTransport.Host(listener, clients: 1, TimeSpan.FromSeconds(60));
            listener.Stop();
        }
        else transport = TcpLockstepTransport.Join(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), TimeSpan.FromSeconds(60));

        using (transport)
        {
            var (simulation, path) = DeterminismHarness.Load(installation, rules, scenario);
            var session = new LockstepSession(player, 2, transport);
            var commander = new ScriptedCommander(simulation, path, rules, DeterminismHarness.StableSeed(scenario));
            var started = DateTime.UtcNow;
            while (simulation.TickCount < ticks && session.Desync is null)
            {
                foreach (var scheduled in commander.CommandsFor(simulation.TickCount + 1))
                {
                    var command = scheduled.Command;
                    var team = command.GetType().GetProperty("EntityInstanceId")?.GetValue(command) is int id
                        ? simulation.Actor(id)?.Seed.Team ?? -1
                        : command.GetType().GetProperty("TeamId")?.GetValue(command) as int? ?? -1;
                    if (team == player) session.Queue(command);
                }
                if (session.TryAdvance(simulation) is null)
                {
                    Thread.Sleep(1);
                    continue;
                }
                if (simulation.TickCount % 100 == 0) Console.WriteLine($"{role}: tick {simulation.TickCount} {SimulationDigest.Hash(simulation)[..16]}");
            }
            Console.WriteLine(session.Desync is { } desync
                ? $"{role}: DESYNC at tick {desync.Tick} (player {desync.Player})"
                : $"{role}: {simulation.TickCount} ticks in sync, {session.ConfirmedDigests} digests confirmed, {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
            return session.Desync is null ? 0 : 1;
        }
    }

    /// <summary>The campaign and training missions, in campaign order.</summary>
    public static IReadOnlyList<string> CampaignMissions(GameInstallation installation) => DeterminismHarness.InstalledScenarios(installation)
        .Where(scenario => scenario.StartsWith("human/human", StringComparison.OrdinalIgnoreCase) ||
                           scenario.StartsWith("alien/alien", StringComparison.OrdinalIgnoreCase) ||
                           scenario.StartsWith("test/", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    /// <summary>
    /// <c>--campaign-smoke &lt;ticks&gt; [filter]</c>: each campaign mission with
    /// the local team (0) played by the Krusty planner, until the script ends
    /// it or the tick limit. One line per mission: outcome, tick, units left.
    /// </summary>
    public static IReadOnlyList<string> CampaignSmoke(GameInstallation installation, SimulationRules rules, ulong ticks, string? filter, bool strike = false, bool sweep = false, bool trace = false)
    {
        var missions = CampaignMissions(installation)
            .Where(mission => filter is null || mission.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        var lines = new string[missions.Length];
        Parallel.For(0, missions.Length, index =>
        {
            var mission = missions[index];
            var file = installation.DataFile(["scenario", .. mission.Split('/')]) + ".scn";
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            var definition = ScenarioDefinition.Load(file).WithTeamAiProfile(0, ScenarioSimulation.KrustyAiProfile);
            var script = MissionScript.LoadForScenario(file);
            var simulation = ScenarioSimulation.Create(definition, path, rules, script, map);
            var tripCells = TripCells(script, path);
            var tripAttempts = new Dictionary<int, int>();
            var sweepTeams = VictoryTeams(Path.ChangeExtension(file, ".tro"));
            ulong tick = 0;
            try
            {
                while (tick < ticks && simulation.Outcome is not { } done)
                {
                    var orders = new List<ScheduledWorldCommand>();
                    if (sweep && tick % 64 == 32)
                    {
                        SweepHostiles(simulation, 0, sweepTeams);
                        orders.AddRange(TripVisits(simulation, script, tripCells, tripAttempts, 0));
                    }
                    if (strike && tick % 16 == 0) orders.AddRange(StrikeOrders(simulation, 0));
                    if (trace && tick % 4000 == 0 && script is not null)
                    {
                        var open = script.Triggers.Where(trigger => simulation.MissionLives[trigger.Slot] > 0)
                            .Select(trigger => $"{(trigger.Trip ? "t" : "n")}{trigger.Slot}");
                        var units = simulation.Actors.Where(actor => actor.Seed.Team == 0 && !actor.IsDestroyed)
                            .Select(actor => $"{simulation.EffectiveDefinition(actor).Code}@{actor.Movement.OccupiedCell.X},{actor.Movement.OccupiedCell.Z}{(actor.MoveOrder is { } order ? $"->{order.Target.X},{order.Target.Z} seg{order.SegmentCount} blk{order.BlockedTicksRemaining}{(order.BlockedWaiting ? "w" : "")} pb{(actor.Playback is null ? 0 : 1)}" : "")}");
                        Console.WriteLine($"{mission} {tick}: open {string.Join(' ', open)} | {string.Join(' ', units)}");
                        foreach (var actor in simulation.Actors.Where(actor => actor.Seed.Team == 0 && !actor.IsDestroyed && actor.MoveOrder is not null))
                        {
                            var at = actor.Movement.OccupiedCell;
                            var near = simulation.GroundOccupancy.Claims.Where(claim => Math.Abs(claim.Key.X - at.X) <= 2 && Math.Abs(claim.Key.Z - at.Z) <= 1)
                                .Select(claim => $"{claim.Key.X},{claim.Key.Z}=#{claim.Value}:{(simulation.Actor(claim.Value) is { } o ? $"{simulation.EffectiveDefinition(o).Code}/t{o.Seed.Team}/hp{o.Health}{(o.IsDying ? "dying" : "")}" : "gone")}");
                            Console.WriteLine($"   near {at.X},{at.Z}: {string.Join(' ', near)} region {path.RegionAt(at)} -> {path.RegionAt(actor.MoveOrder!.Target)}");
                        }
                    }
                    simulation.Step(orders);
                    tick++;
                }
            }
            catch (Exception error)
            {
                lines[index] = $"{mission,-16} FAULT at {tick}: {error.GetType().Name}: {error.Message}";
                return;
            }
            var own = simulation.Actors.Count(actor => actor.Seed.Team == 0 && !actor.IsDestroyed);
            lines[index] = simulation.Outcome is { } outcome
                ? $"{mission,-16} {(outcome.Victory ? "victory" : $"defeat r{outcome.Result}"),-10} text {outcome.OutcomeText,2} at {outcome.RequestedAtTick,6}  own {own}"
                : $"{mission,-16} {"none",-10} after {tick,6}  own {own}";
        });
        return lines;
    }

    /// <summary>
    /// The smoke's strike helper: each idle armed mobile unit of the team
    /// (commanders excluded) attacks the nearest live hostile actor.
    /// </summary>
    public static IReadOnlyList<ScheduledWorldCommand> StrikeOrders(ScenarioSimulation simulation, int team)
    {
        var targets = simulation.Actors.Where(actor => !actor.IsDestroyed && actor.Seed.Team is >= 0 and < 8 &&
                                                       simulation.TeamRelations.IsHostile(team, actor.Seed.Team) &&
                                                       !simulation.EffectiveDefinition(actor).IsNativeUntargetable).ToArray();
        var commands = new List<ScheduledWorldCommand>();
        if (targets.Length == 0) return commands;
        foreach (var attacker in simulation.Actors.Where(actor => !actor.IsDestroyed && actor.Seed.Team == team &&
                     actor.AttackTargetInstanceId is null && simulation.EffectiveDefinition(actor).MovementSpeed > 0 &&
                     simulation.EffectiveDefinition(actor).WeaponSlots[0] >= 0 &&
                     simulation.EffectiveDefinition(actor).Id is < 69 or > 76))
        {
            var from = attacker.Movement.OccupiedCell;
            var target = targets.MinBy(candidate => Math.Abs(candidate.Movement.OccupiedCell.X - from.X) +
                                                    Math.Abs(candidate.Movement.OccupiedCell.Z - from.Z))!;
            commands.Add(new ScheduledWorldCommand(simulation.TickCount, (ulong)commands.Count,
                new AttackIntent(attacker.Seed.InstanceId, target.Seed.InstanceId)));
        }
        return commands;
    }

    /// <summary>
    /// The players a mission's victory triggers (those whose actions include
    /// <c>bail 0</c>) test with <c>b(p, ...)</c> or <c>s(p, ...)</c>, so the sweep
    /// spares the teams the player must protect. Null when none are named.
    /// </summary>
    private static IReadOnlySet<int>? VictoryTeams(string troPath)
    {
        if (!File.Exists(troPath)) return null;
        var teams = new HashSet<int>();
        var text = File.ReadAllText(troPath, System.Text.Encoding.Latin1).Replace("\r", "");
        foreach (var block in System.Text.RegularExpressions.Regex.Split(text, @"\n\s*\n"))
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0 || !lines.Skip(1).Any(line => line.StartsWith("bail 0", StringComparison.Ordinal))) continue;
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(lines[0], @"[bs]\((\d+),"))
                teams.Add(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        return teams.Count == 0 ? null : teams;
    }

    /// <summary>The passable cells of each trip trigger's area in the mission's MTG.</summary>
    private static Dictionary<int, List<CellCoordinate>> TripCells(MissionScript? script, PathRegionMap path)
    {
        var cells = new Dictionary<int, List<CellCoordinate>>();
        if (script?.TripMap is not { } trips) return cells;
        for (var z = 0; z < Math.Min(trips.Height, path.Height); z++)
            for (var x = 0; x < Math.Min(trips.Width, path.Width); x++)
            {
                var cell = new CellCoordinate(x, z);
                var slot = trips.TriggerAt(cell);
                if (slot == 0 || path.RegionAt(cell) == 0) continue;
                if (!cells.TryGetValue(slot, out var list)) cells[slot] = list = [];
                list.Add(cell);
            }
        return cells;
    }

    /// <summary>
    /// The smoke's trip visits: each trip trigger that still has lives gets
    /// the nearest idle mobile unit of the team, sent to the nearest cell of its area.
    /// </summary>
    private static IEnumerable<ScheduledWorldCommand> TripVisits(ScenarioSimulation simulation, MissionScript? script,
        Dictionary<int, List<CellCoordinate>> tripCells, Dictionary<int, int> attempts, int team)
    {
        if (script is null) yield break;
        var lives = simulation.MissionLives;
        var idle = simulation.Actors.Where(actor => !actor.IsDestroyed && actor.Seed.Team == team && actor.MoveOrder is null &&
                                                    actor.AttackTargetInstanceId is null &&
                                                    simulation.EffectiveDefinition(actor).MovementSpeed > 0).ToList();
        ulong sequence = 1000;
        // The least tried trip first, so an unreachable area does not starve the rest.
        foreach (var trigger in script.Triggers.Where(trigger => trigger.Trip && lives[trigger.Slot] > 0)
                     .OrderBy(trigger => attempts.GetValueOrDefault(trigger.Slot)).ThenBy(trigger => trigger.Slot))
        {
            if (idle.Count == 0 || !tripCells.TryGetValue(trigger.Slot, out var cells)) continue;
            static int Distance(CellCoordinate a, CellCoordinate b) => Math.Abs(a.X - b.X) + Math.Abs(a.Z - b.Z);
            var unit = idle.MinBy(actor => cells.Min(cell => Distance(cell, actor.Movement.OccupiedCell)))!;
            idle.Remove(unit);
            attempts[trigger.Slot] = attempts.GetValueOrDefault(trigger.Slot) + 1;
            var target = cells.MinBy(cell => Distance(cell, unit.Movement.OccupiedCell));
            yield return new ScheduledWorldCommand(simulation.TickCount, sequence++, new MoveIntent(unit.Seed.InstanceId, target));
        }
    }

    /// <summary>
    /// The smoke's sweep: destroys every live actor of a player hostile to the
    /// team (allies and teams 8-9 stay), so a script's own victory checks can run.
    /// </summary>
    public static void SweepHostiles(ScenarioSimulation simulation, int team, IReadOnlySet<int>? only = null)
    {
        var destroyed = new List<DestroyedActorEvent>();
        foreach (var actor in simulation.Actors.Where(actor => !actor.IsDestroyed && actor.Seed.Team is >= 0 and < 8 &&
                                                              simulation.TeamRelations.IsHostile(team, actor.Seed.Team) &&
                                                              (only is null || only.Contains(actor.Seed.Team))).ToArray())
            simulation.Kill(actor, team, destroyed);
    }

    private static bool RunDump(GameInstallation installation, SimulationRules rules, string[] args, int dump)
    {
        var scenario = args[dump + 1];
        var tick = ulong.Parse(args[dump + 2]);
        var output = args[dump + 3];
        var run = DeterminismHarness.Run(installation, rules, scenario, tick, [], digestEveryTick: false, describeAtTick: tick);
        File.WriteAllText(output, run.FinalDescription);
        Console.WriteLine($"Wrote {scenario} state at tick {tick} to {output}");
        return true;
    }
}
