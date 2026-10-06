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

/// <summary>Single Player War rosters, maps, and the native session start.</summary>
internal static class WarChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("War", name, tags, action);

        Check("in a local War on Dead Man's Wharf the computer harvests, builds, trains and attacks", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var file = install.DataFile("scenario", "mplayer", "d2play01.scn");
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            var scenario = ScenarioDefinition.Load(file).WithComputerOpponents(0);
            var simulation = ScenarioSimulation.Create(scenario, path, rules, MissionScript.LoadForScenario(file), map);
            Equal(ScenarioSimulation.KrustyAiProfile, simulation.AiProfile(1));
            var built = 0;
            var trained = 0;
            var deployed = false;
            var attacked = false;
            for (var tick = 0; tick < 20000; tick++)
            {
                simulation.Step([]);
                built += simulation.LastBuildingPlacements.Count(placement => placement.TeamId == 1);
                trained += simulation.LastUnitProductions.Count(production => production.TeamId == 1 && production.Outcome == UnitProductionOutcome.Produced);
                deployed |= simulation.LastHarvesterDeployments.Any(deployment =>
                    simulation.Actor(deployment.EntityInstanceId)?.Seed.Team == 1 && deployment.Outcome == HarvesterDeploymentOutcome.Attached);
                attacked |= simulation.KrustyState(1)?.Groups[2].Tasks.Any(task => task.Active && task.Mode == KrustyTaskMode.Attacking) == true;
            }
            if (built < 1 || trained < 5 || !deployed || !attacked)
                throw new InvalidOperationException($"Built {built}, trained {trained}, deployed {deployed}, attacked {attacked}.");
        }, CheckTags.Data | CheckTags.Slow);

        Check("Single Player War catalog exposes complete faction-matched scenarios", () =>
        {
        var catalog = SinglePlayerWarCatalog.Load(GameInstallation.Open(dataPath));
        Equal(56, catalog.Scenarios.Count);
        Equal("j4play01", catalog.Scenarios[0].Stem);
        Equal("4 Kingdoms", catalog.Scenarios[0].DisplayName);
        Equal(54, catalog.ForRace(0).Count);
        Equal(30, catalog.ForRace(1).Count);
        foreach (var scenario in catalog.ForRace(0))
        {
            Equal(true, scenario.EnabledTeamForRace(0) is not null);
            Equal(true, scenario.TryCreateLaunch(0, out var launch));
            Equal(scenario.Stem, launch.Stem);
            Equal(scenario.EnabledTeamForRace(0)?.TeamId ?? -1, launch.LocalTeamId);
            Equal(SinglePlayerWarSettings.Default, launch.Settings);
        }
        foreach (var scenario in catalog.ForRace(1))
        {
            Equal(true, scenario.EnabledTeamForRace(1) is not null);
            Equal(true, scenario.TryCreateLaunch(1, out var launch));
            Equal(scenario.Stem, launch.Stem);
            Equal(scenario.EnabledTeamForRace(1)?.TeamId ?? -1, launch.LocalTeamId);
        }
        foreach (var scenario in catalog.Scenarios.Where(scenario => scenario.EnabledTeamForRace(1) is null))
            Equal(false, scenario.TryCreateLaunch(1, out _));
        var configured = new SinglePlayerWarSettings(3, 2, true, true, 500, 25, 3);
        Equal(true, catalog.ForRace(0)[0].TryCreateLaunch(0, configured, out var configuredLaunch));
        Equal(configured, configuredLaunch.Settings);
        Equal(false, catalog.ForRace(0)[0].TryCreateLaunch(0, configured with { P7FlowPercent = 110 }, out _));

        // Free-War SCNs seed entity 69 (Human lieutenant) for both teams. The
        // selected launch must transform only the local team's commander slot;
        // otherwise choosing Gray still gives the player a Human marine.
        var twoRaceMap = catalog.Scenarios.Single(scenario => scenario.Stem == "d2play01");
        Equal(true, twoRaceMap.TryCreateLaunch(1, configured with { CommanderRank = 2 }, out var grayLaunch));
        var grayDefinition = grayLaunch.ApplyTo(twoRaceMap.Definition);
        Equal(75, grayDefinition.Placements.Single(placement => placement.Team == grayLaunch.LocalTeamId && placement.EntityId is >= 69 and <= 76).EntityId);
        Equal(69, grayDefinition.Placements.Single(placement => placement.Team == 0 && placement.EntityId is >= 69 and <= 76).EntityId);

        Equal(true, twoRaceMap.TryCreateLaunch(0, configured with { CommanderRank = 3 }, out var humanLaunch));
        var humanDefinition = humanLaunch.ApplyTo(twoRaceMap.Definition);
        Equal(72, humanDefinition.Placements.Single(placement => placement.Team == humanLaunch.LocalTeamId && placement.EntityId is >= 69 and <= 76).EntityId);

        var eightPlayerMap = catalog.Scenarios.Single(scenario => scenario.Stem == "d8play01");
        Equal(true, eightPlayerMap.TryCreateLaunch(1, out var eightPlayerGrayLaunch));
        var eightPlayerGrayDefinition = eightPlayerGrayLaunch.ApplyTo(eightPlayerMap.Definition);
        Equal(8, eightPlayerGrayDefinition.Placements.Single(placement => placement.Team == eightPlayerGrayLaunch.LocalTeamId && placement.EntityId is 0 or 8).EntityId);
        Equal(73, eightPlayerGrayDefinition.Placements.Single(placement => placement.Team == eightPlayerGrayLaunch.LocalTeamId && placement.EntityId is >= 69 and <= 76).EntityId);
        }, CheckTags.Data);

        Check("single-player War maps load controllable local units", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            var footprints = BuildingFootprintCatalog.Load(install.ExecutablePath);
            var files = Directory.GetFiles(install.DataFile("scenario", "mplayer"), "*.scn")
                .Where(file => File.Exists(Path.ChangeExtension(file, ".map")) && File.Exists(Path.ChangeExtension(file, ".pth")))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0) throw new InvalidOperationException("No complete mplayer scenario triplets found.");
            var controllableActors = 0;
            foreach (var file in files)
            {
                var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
                var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
                var simulation = ScenarioSimulation.Create(ScenarioDefinition.Load(file), catalog, path, footprints);
                var localActors = simulation.Actors.Count(actor => actor.Seed.Team == 0 && actor.Definition.MovementSpeed > 0);
                if (localActors == 0)
                    throw new InvalidDataException($"{Path.GetFileName(file)} has no mobile team-0 unit for local control.");
                controllableActors += localActors;
            }
            Console.WriteLine($"  single-player War: {files.Length} maps / {controllableActors} local mobile actors");
        }, CheckTags.Data);

        Check("single-player War rosters expose controllable Human and Gray teams", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            var footprints = BuildingFootprintCatalog.Load(install.ExecutablePath);
            var available = new int[2];
            foreach (var file in Directory.GetFiles(install.DataFile("scenario", "mplayer"), "*.scn"))
            {
                if (!File.Exists(Path.ChangeExtension(file, ".map")) || !File.Exists(Path.ChangeExtension(file, ".pth"))) continue;
                var scenario = ScenarioDefinition.Load(file);
                var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
                var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
                var simulation = ScenarioSimulation.Create(scenario, catalog, path, footprints);
                for (var faction = 0; faction <= 1; faction++)
                {
                    var team = scenario.EnabledTeamForRace(faction);
                    if (team is null) continue;
                    if (!simulation.Actors.Any(actor => actor.Seed.Team == team.TeamId && actor.Definition.MovementSpeed > 0))
                        throw new InvalidDataException($"{Path.GetFileName(file)} {faction} team {team.TeamId} has no mobile unit.");
                    available[faction]++;
                }
            }
            if (available[0] == 0 || available[1] == 0)
                throw new InvalidOperationException("Installed War maps do not expose both playable factions.");
            Console.WriteLine($"  selectable War rosters: Human {available[0]} / Gray {available[1]}");
        }, CheckTags.Data);

        Check("single-player War maps complete a local movement order", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            var footprints = BuildingFootprintCatalog.Load(install.ExecutablePath);
            var files = Directory.GetFiles(install.DataFile("scenario", "mplayer"), "*.scn")
                .Where(file => File.Exists(Path.ChangeExtension(file, ".map")) && File.Exists(Path.ChangeExtension(file, ".pth")))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var movedMaps = 0;
            foreach (var file in files)
            {
                var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
                var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
                var simulation = ScenarioSimulation.Create(ScenarioDefinition.Load(file), catalog, path, footprints);
                var finder = new DiagnosticLocalPathfinder(path, simulation.GroundOccupancy, simulation.AlternateOccupancy);
                SimulatedActor? actor = null;
                CellCoordinate? target = null;
                foreach (var candidateActor in simulation.Actors
                             .Where(candidate => candidate.Seed.Team == 0 && candidate.Definition.MovementSpeed > 0)
                             .OrderBy(candidate => candidate.Seed.InstanceId))
                {
                    for (var radius = 1; radius <= 4 && target is null; radius++)
                    for (var z = -radius; z <= radius && target is null; z++)
                    for (var x = -radius; x <= radius; x++)
                    {
                        if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius) continue;
                        var candidate = new CellCoordinate(candidateActor.Movement.OccupiedCell.X + x, candidateActor.Movement.OccupiedCell.Z + z);
                        if (finder.Find(candidateActor.Movement.OccupiedCell, candidate, candidateActor.Definition.MovementClass, candidateActor.Seed.InstanceId).Steps.Count == 0) continue;
                        actor = candidateActor;
                        target = candidate;
                        break;
                    }
                }
                if (actor is null || target is null)
                    throw new InvalidDataException($"{Path.GetFileName(file)} has no mobile team-0 unit with a legal nearby movement target.");
                simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(actor.Seed.InstanceId, target.Value))]);
                for (var tick = 0; tick < 1_000 && actor.MoveOrder is not null; tick++) simulation.Step([]);
                if (actor.Movement.OccupiedCell != target.Value || actor.MoveOrder is not null)
                {
                    var owner = simulation.GroundOccupancy.TryGetOwner(target.Value, out var ownerId) ? ownerId.ToString() : "none";
                    throw new InvalidDataException(
                        $"{Path.GetFileName(file)} local unit {actor.Seed.InstanceId} did not complete its movement order " +
                        $"({actor.Movement.OccupiedCell} -> {target.Value}, target owner {owner}).");
                }
                movedMaps++;
            }
            Equal(files.Length, movedMaps);
        Console.WriteLine($"  single-player War movement: {movedMaps} maps completed one local order");
        }, CheckTags.Data);

        Check("faction-selected War rosters complete a local movement order", () =>
        {
        var install = GameInstallation.Open(dataPath);
        var catalog = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
        var footprints = BuildingFootprintCatalog.Load(install.ExecutablePath);
        var warCatalog = SinglePlayerWarCatalog.Load(install);
        var movedRosters = 0;
        for (var faction = 0; faction <= 1; faction++)
        foreach (var choice in warCatalog.ForRace(faction))
        {
            var file = install.DataFile("scenario", "mplayer", $"{choice.Stem}.scn");
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            var team = choice.EnabledTeamForRace(faction) ?? throw new InvalidDataException($"{choice.Stem} lost its {faction} team.");
            if (!choice.TryCreateLaunch(faction, out var launch))
                throw new InvalidDataException($"{choice.Stem} cannot create its {faction} launch.");
            var simulation = ScenarioSimulation.Create(launch.ApplyTo(choice.Definition), catalog, path, footprints);
            Equal(Math.Max(0, team.StartingResource ?? 0), simulation.ResourceForTeam(team.TeamId));
            var commanders = simulation.Actors
                .Where(actor => actor.Seed.Team == team.TeamId && actor.Seed.EntityId is >= 69 and <= 76)
                .ToArray();
            if (commanders.Any(actor => actor.Definition.Faction != faction))
                throw new InvalidDataException($"{choice.Stem} {faction} launch retained an opposing-faction commander.");
            var localMobileActors = simulation.Actors
                .Where(actor => actor.Seed.Team == team.TeamId && actor.Definition.MovementSpeed > 0)
                .ToArray();
            if (localMobileActors.Any(actor => actor.Definition.Faction != faction))
                throw new InvalidDataException($"{choice.Stem} {faction} launch exposes an opposing-faction local mobile actor.");
            var finder = new DiagnosticLocalPathfinder(path, simulation.GroundOccupancy, simulation.AlternateOccupancy);
            SimulatedActor? actor = null;
            CellCoordinate? target = null;
            foreach (var candidateActor in simulation.Actors
                         .Where(candidate => candidate.Seed.Team == team.TeamId && candidate.Definition.MovementSpeed > 0)
                         .OrderBy(candidate => candidate.Seed.InstanceId))
            {
                for (var radius = 1; radius <= 4 && target is null; radius++)
                for (var z = -radius; z <= radius && target is null; z++)
                for (var x = -radius; x <= radius; x++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(z)) != radius) continue;
                    var candidate = new CellCoordinate(candidateActor.Movement.OccupiedCell.X + x, candidateActor.Movement.OccupiedCell.Z + z);
                    if (finder.Find(candidateActor.Movement.OccupiedCell, candidate, candidateActor.Definition.MovementClass, candidateActor.Seed.InstanceId).Steps.Count == 0) continue;
                    actor = candidateActor;
                    target = candidate;
                    break;
                }
            }

            if (actor is null || target is null)
                throw new InvalidDataException($"{choice.Stem} {faction} team {team.TeamId} has no legal local movement target.");
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(actor.Seed.InstanceId, target.Value))]);
            for (var tick = 0; tick < 1_000 && actor.MoveOrder is not null; tick++) simulation.Step([]);
            if (actor.Movement.OccupiedCell != target.Value || actor.MoveOrder is not null)
                throw new InvalidDataException($"{choice.Stem} {faction} team {team.TeamId} did not complete its local move.");
            movedRosters++;
        }

        Equal(84, movedRosters);
        Console.WriteLine($"  faction-selected War movement: {movedRosters} rosters completed one local order");
        }, CheckTags.Data | CheckTags.Slow);

        Check("the War session start shuffles occupied rows over the map's positions and gates absent teams", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var catalog = SinglePlayerWarCatalog.Load(install);
            WarLobbyRow[] rows =
            [
                new(WarSeatKind.Human, 0, 0), new(WarSeatKind.Computer, 1),
                new(WarSeatKind.None, 0), new(WarSeatKind.None, 1), new(WarSeatKind.None, 0),
                new(WarSeatKind.None, 1), new(WarSeatKind.None, 0), new(WarSeatKind.None, 1),
            ];
            Equal(2, WarSession.PlayerPositions("d2play01"));
            Equal(8, WarSession.PlayerPositions("j8play01"));
            // Every War map seats one human and one computer.
            foreach (var map in catalog.Scenarios)
                if (!map.TryCreateSession(rows, 0, SinglePlayerWarSettings.Default, rules.RandomTable, out _))
                    throw new InvalidOperationException($"{map.Stem} cannot seat a human and a computer.");

            // Seed 0: on a two-player map the rows keep their order; on j4play01 the
            // draws 5758 % 4, 10113 % 3, 17515 % 2 put the human on team 4 and the computer on team 2.
            var two = WarSession.Assign("d2play01", rows, rules.RandomTable);
            Equal("0:Human 1:Computer", string.Join(' ', two.Where(seat => seat is not null).Select(seat => $"{seat!.TeamId}:{seat.Kind}")));
            var four = catalog.Scenarios.Single(map => map.Stem == "j4play01");
            Equal(true, four.TryCreateSession(rows, 0, SinglePlayerWarSettings.Default, rules.RandomTable, out var launch));
            Equal(3, launch.LocalTeamId);
            Equal("1:Computer:1 3:Human:0", string.Join(' ', launch.Seats!.Where(seat => seat is not null).Select(seat => $"{seat!.TeamId}:{seat.Kind}:{seat.Race}")));

            var file = install.DataFile("scenario", "mplayer", "j4play01.scn");
            var scenario = launch.ApplyTo(ScenarioDefinition.Load(file));
            Equal(true, scenario.Teams.Where(team => team.TeamId is 0 or 2).All(team => team.CitySlots.All(slot => slot.Level == 0) && team.AiProfile == 0));
            Equal(1, scenario.Teams.Single(team => team.TeamId == 1).Race ?? -1);
            var map4 = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var simulation = ScenarioSimulation.Create(scenario, PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map4.Width, map4.Height),
                rules, MissionScript.LoadForScenario(file), map4);
            Equal(ScenarioSimulation.KrustyAiProfile, simulation.AiProfile(1));
            Equal(0, simulation.AiProfile(3));
            // 0x41C155: teams outside the session raise no city buildings.
            var cityTeams = Enumerable.Range(0, 4).Where(team => Enumerable.Range(0, 15).Any(slot => simulation.CityBuilding(team, slot) is not null));
            Equal("1,3", string.Join(',', cityTeams));
        }, CheckTags.Data);

        Check("the War lobby's options become players 1-6 stat 0 and scale every vent's rate and money", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var catalog = SinglePlayerWarCatalog.Load(install);
            WarLobbyRow[] rows =
            [
                new(WarSeatKind.Human, 0, 0), new(WarSeatKind.Computer, 1),
                new(WarSeatKind.None, 0), new(WarSeatKind.None, 1), new(WarSeatKind.None, 0),
                new(WarSeatKind.None, 1), new(WarSeatKind.None, 0), new(WarSeatKind.None, 1),
            ];
            var file = install.DataFile("scenario", "mplayer", "j4play01.scn");
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            ScenarioSimulation Start(SinglePlayerWarSettings settings)
            {
                Equal(true, catalog.Scenarios.Single(war => war.Stem == "j4play01").TryCreateSession(rows, 0, settings, rules.RandomTable, out var launch));
                return ScenarioSimulation.Create(launch.ApplyTo(ScenarioDefinition.Load(file)),
                    PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height), rules, MissionScript.LoadForScenario(file), map);
            }
            var standard = Start(SinglePlayerWarSettings.Default);
            Equal("256 256 0 0 0 0", string.Join(' ', Enumerable.Range(1, 6).Select(player => standard.PlayerStatistic(player, 0))));

            // Flow 50%, quantity 200%, both vent options, storage 2, artifacts 1.
            var custom = Start(new SinglePlayerWarSettings(2, 1, true, true, 200, 50, 0));
            Equal("128 512 1 1 2 1", string.Join(' ', Enumerable.Range(1, 6).Select(player => custom.PlayerStatistic(player, 0))));
            // The vent loader: rate x stat (1,0) >> 8, money x stat (2,0) >> 8.
            foreach (var (normal, scaled) in standard.PetraVents.Zip(custom.PetraVents))
                Equal((normal.Rate / 2, normal.InitialReservoir * 2), (scaled.Rate, scaled.InitialReservoir));
            Equal(true, standard.PetraVents.Any(vent => vent.Rate > 0));
        }, CheckTags.Data);

        Check("a War's Storage Cells and Artifacts options place pickups and open the map's artifact sites", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var catalog = SinglePlayerWarCatalog.Load(install);
            WarLobbyRow[] rows =
            [
                new(WarSeatKind.Human, 0, 0), new(WarSeatKind.Computer, 1),
                new(WarSeatKind.None, 0), new(WarSeatKind.None, 1), new(WarSeatKind.None, 0),
                new(WarSeatKind.None, 1), new(WarSeatKind.None, 0), new(WarSeatKind.None, 1),
            ];
            var file = install.DataFile("scenario", "mplayer", "j4play01.scn");
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var path = PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height);
            ScenarioSimulation Start(SinglePlayerWarSettings settings)
            {
                Equal(true, catalog.Scenarios.Single(war => war.Stem == "j4play01").TryCreateSession(rows, 0, settings, rules.RandomTable, out var launch));
                return ScenarioSimulation.Create(launch.ApplyTo(ScenarioDefinition.Load(file)), path, rules, MissionScript.LoadForScenario(file), map);
            }
            static SimulatedActor[] Cells(ScenarioSimulation simulation) => simulation.Actors
                .Where(actor => actor.Seed.EntityId is ScenarioSimulation.FuelStorageCellEntity or ScenarioSimulation.FillStorageCellEntity).ToArray();
            static int Sites(ScenarioSimulation simulation) => simulation.Actors.Count(actor => actor.Seed.EntityId == ScenarioSimulation.ArtifactSiteEntity);

            // Both options off: no cells, and 0x41C5C0 skips j4play01's four sites.
            var off = Start(SinglePlayerWarSettings.Default);
            Equal((0, 0, 0), (Cells(off).Length, Sites(off), off.ArtifactContainers.Count));

            // 0x41C6B4: HIGH is three cells for each of the two positions in the
            // session, team 9 pickups at their catalog health on PTH regions.
            var high = Start(new SinglePlayerWarSettings(3, 1, false, false, 100, 100, 0));
            var cells = Cells(high);
            Equal(6, cells.Length);
            foreach (var cell in cells)
            {
                Equal((AutonomousSpawnSeeder.InternalNeutralTeam, ContactRole.Pickup, rules.Entities[cell.Seed.EntityId].Health),
                    (cell.Seed.Team, cell.ContactRole, cell.Health));
                Equal(true, path.RegionAt(cell.Seed.SpawnCell) != 0);
            }
            Equal(6, cells.Select(cell => cell.Seed.SpawnCell).Distinct().Count());

            // LOW artifacts open the four sites, and each site's s(6,0)==1
            // trigger buries two artifacts (entity 0x3F + r % 5) there.
            Equal((4, 4), (Sites(high), high.ArtifactContainers.Count));
            // The norm triggers run every eighth update (0x419A4E).
            for (var tick = 0; tick < 8; tick++) high.Step([]);
            Equal("2 2 2 2", string.Join(' ', high.ArtifactContainers.Select(container => container.Items.Count)));
            Equal(true, high.ArtifactContainers.SelectMany(container => container.Items).All(item => item is >= 0x3f and <= 0x43));
        }, CheckTags.Data);

        Check("a War ends once every player still in it is allied both ways with the first, units and empty positions included", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var catalog = SinglePlayerWarCatalog.Load(install);
            WarLobbyRow[] rows =
            [
                new(WarSeatKind.Human, 0, 0), new(WarSeatKind.Computer, 1),
                new(WarSeatKind.None, 0), new(WarSeatKind.None, 1), new(WarSeatKind.None, 0),
                new(WarSeatKind.None, 1), new(WarSeatKind.None, 0), new(WarSeatKind.None, 1),
            ];
            Equal(true, catalog.Scenarios.Single(map => map.Stem == "j4play01").TryCreateSession(rows, 0, SinglePlayerWarSettings.Default, rules.RandomTable, out var launch));
            var file = install.DataFile("scenario", "mplayer", "j4play01.scn");
            var map = TerrainMap.Load(Path.ChangeExtension(file, ".map"));
            var simulation = ScenarioSimulation.Create(launch.ApplyTo(ScenarioDefinition.Load(file)),
                PathRegionMap.Load(Path.ChangeExtension(file, ".pth"), map.Width, map.Height), rules, MissionScript.LoadForScenario(file), map);
            // The human is team 3 and the computer team 1; 0 and 2 are empty positions.
            Equal(false, simulation.IsWarOver());

            // Everything of teams 0-2 dies but one placed actor of an empty position (0x40DE20).
            var destroyed = new List<DestroyedActorEvent>();
            var others = simulation.Actors.Where(actor => actor.Seed.Team is 0 or 1 or 2 && !actor.IsDestroyed).ToArray();
            var holdout = others.FirstOrDefault(actor => actor.Seed.Team is 0 or 2)
                ?? throw new InvalidOperationException("j4play01's empty positions place no actors.");
            foreach (var actor in others.Where(actor => actor != holdout)) simulation.Kill(actor, 3, destroyed);
            Equal((false, true, true, false), (simulation.IsPlayerInGame(1), simulation.IsPlayerInGame(holdout.Seed.Team), simulation.IsPlayerInGame(3), simulation.IsWarOver()));

            // One-way alliance is not enough (0x41E820); both ways ends it.
            simulation.SetAllianceBit(holdout.Seed.Team, 3, true);
            Equal(false, simulation.IsWarOver());
            simulation.SetAllianceBit(3, holdout.Seed.Team, true);
            Equal(true, simulation.IsWarOver());
            simulation.SetAllianceBit(3, holdout.Seed.Team, false);

            // A lone survivor wins.
            simulation.Kill(holdout, 3, destroyed);
            Equal((false, true), (simulation.IsPlayerInGame(holdout.Seed.Team), simulation.IsWarOver()));
        }, CheckTags.Data);
    }
}
