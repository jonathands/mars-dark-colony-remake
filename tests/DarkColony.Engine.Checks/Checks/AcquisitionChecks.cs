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

/// <summary>Automatic target selection: the idle command, ring scans, yields, and Move & Attack.</summary>
internal static class AcquisitionChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("Acquisition", name, tags, action);

        Check("blocked allied actor receives a yield notification before jitter wait", () =>
        {
            var catalog = EntityCatalog.Parse("1\nUNIT 0 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 -1 0\n1 3 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 6];
            bytes[1 * 256 + 1] = 1;
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            // Cursor zero increments before each read: entries one and two select
            // X +0 (1 % 3 - 1) and Z -1 (0 % 3 - 1).
            var stream = new uint[NativeRandomTable.Length];
            stream[1] = 1;
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 6, 1),
                randomTable: NativeRandomTable.FromValues(stream));
            // The blocker exists as an actor but only enters the occupancy grid after
            // the mover has committed its initial packed segment, matching a dynamic
            // playback collision rather than an initial route obstruction.
            simulation.GroundOccupancy.Release(2);
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(5, 0)))]);
            simulation.GroundOccupancy.ReplaceClaims(2, [new CellCoordinate(3, 0)]);
            var mover = simulation.Actor(1)!;
            var blocker = simulation.Actor(2)!;
            for (var tick = 0; tick < 80 && blocker.YieldNotificationDirection is null; tick++) simulation.Step([]);
            Equal(PathDirection.East, blocker.YieldNotificationDirection ?? throw new InvalidOperationException("Yield notification missing."));
            Equal(true, mover.MoveOrder is not null);
            // The injected X +0 / Z -1 jitter is clamped by the one-row map, leaving
            // the active target unchanged.
            Equal(new CellCoordinate(5, 0), mover.MoveOrder!.Target);
        });

        Check("notified idle blockers step aside, never back toward the mover", () =>
        {
            // A mover heading east notified the idle unarmed blocker at (5,5). The
            // sideways order (0x479288) tries south first, then north once south is
            // taken.
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            ScenarioSimulation Notified(string extra)
            {
                var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "5 5 1 0 -1 0\n" + extra),
                    catalog, OpenPath(12, 12), targetRings: EuclideanRings());
                simulation.Actor(1)!.YieldNotificationDirection = PathDirection.East;
                simulation.Step([]);
                return simulation;
            }
            var open = Notified("");
            var blocker = open.Actor(1)!;
            Equal(new CellCoordinate(5, 6), blocker.MoveOrder!.Target);
            Equal(true, blocker.YieldNotificationDirection is null);
            for (var tick = 0; tick < 40 && blocker.MoveOrder is not null; tick++) open.Step([]);
            Equal(new CellCoordinate(5, 6), blocker.Movement.OccupiedCell);

            var southTaken = Notified("5 6 4 0 -1 0\n");
            Equal(new CellCoordinate(5, 4), southTaken.Actor(1)!.MoveOrder!.Target);
        });

        Check("armed blockers with a hostile in range yield straight ahead instead of attacking", () =>
        {
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            var weapons = WeaponCatalog.Parse(AcquisitionWeapons);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "5 5 0 0 -1 0\n8 5 1 1 -1 0\n"),
                catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
            var blocker = simulation.Actor(1)!;
            blocker.YieldNotificationDirection = PathDirection.East;
            simulation.Step([]);
            // 0x412A50 tries the notified direction first (0x4792A4).
            Equal(new CellCoordinate(6, 5), blocker.MoveOrder!.Target);
            Equal(true, blocker.AttackTargetInstanceId is null);
        });

        Check("a hostile mine is a target only after a detector of the scanner's team reveals it", () =>
        {
            // GUARD (team 0) has the mine (team 1, value 15) within weapon range. The
            // selector skips it (0x435829) until a value-16 detector of team 0 sees
            // its cell at a visibility refresh (0x44A6D4), which sets +0xCA bit 0.
            var catalog = EntityCatalog.Parse("3\n" +
                "GUARD 0 255 25 8 8 1 -1 -1 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
                // Speed 1 only so the SCN seed enters the mine grid (speed-0 seeds without a footprint enter none).
                "MINE 0 0 1 1 1 -1 -1 -1 1 1 0 800 0 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
                "DETECTOR 0 255 25 4 4 -1 -1 -1 1 1 0 100 0 0 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse(AcquisitionWeapons);
            ScenarioSimulation Run(string extra)
            {
                var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "5 5 0 0 -1 0\n7 5 1 1 -1 0\n" + extra),
                    catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
                for (var tick = 0; tick < 20; tick++) simulation.Step([]);
                return simulation;
            }
            var unrevealed = Run("");
            Equal(0, unrevealed.Actor(2)!.RevealedTeamMask);
            Equal(true, unrevealed.Actor(1)!.AttackTargetInstanceId is null);
            var revealed = Run("6 3 2 0 -1 0\n");
            Equal(1, revealed.Actor(2)!.RevealedTeamMask);
            Equal(2, revealed.Actor(1)!.AttackTargetInstanceId ?? -1);
        });

        Check("a fully blocked yield shuffles its order with the shared random stream", () =>
        {
            // Every sideways candidate around (5,5) is held, so 0x4126A8 fails and
            // 0x412820 draws: entry 1 = 3 swaps preference 3 (offset -1) to the
            // front, which turns east into north-east (6,4). Held live cells are
            // accepted, so the first step there is blocked with no free cell left,
            // and 0x4155D5 jitters the target by entries 2 and 3 (0 % 3 - 1 each).
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            var placements = "5 5 1 0 -1 0\n" + string.Concat(
                new[] { (5, 6), (5, 4), (6, 6), (6, 4), (4, 6), (4, 4), (6, 5) }.Select(cell => $"{cell.Item1} {cell.Item2} 4 0 -1 0\n"));
            var stream = new uint[NativeRandomTable.Length];
            stream[1] = 3;
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + placements), catalog, OpenPath(12, 12),
                randomTable: NativeRandomTable.FromValues(stream), targetRings: EuclideanRings());
            var blocker = simulation.Actor(1)!;
            blocker.YieldNotificationDirection = PathDirection.East;
            simulation.Step([]);
            Equal(new CellCoordinate(5, 3), blocker.MoveOrder!.Target);
        });

        Check("the idle record survives the moves it pushes itself", () =>
        {
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "5 5 1 0 -1 0\n"),
                catalog, OpenPath(12, 12), targetRings: EuclideanRings());
            var blocker = simulation.Actor(1)!;
            simulation.Step([]);
            blocker.IdleMissCount = 2;
            blocker.YieldNotificationDirection = PathDirection.East;
            simulation.Step([]);
            for (var tick = 0; tick < 40 && blocker.MoveOrder is not null; tick++) simulation.Step([]);
            simulation.Step([]);
            Equal(true, blocker.IdleCommandActive);
            Equal(2, blocker.IdleMissCount);

            // A player move replaces the command stack, so the record restarts.
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new MoveIntent(1, new CellCoordinate(5, 8)))]);
            for (var tick = 0; tick < 60 && blocker.MoveOrder is not null; tick++) simulation.Step([]);
            simulation.Step([]);
            Equal(0, blocker.IdleMissCount);
        });

        Check("idle armed actors acquire a visible hostile in weapon range", () =>
        {
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            var weapons = WeaponCatalog.Parse(AcquisitionWeapons);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 0 0 -1 0\n4 2 1 1 -1 0\n"),
                catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
            simulation.Step([]);
            Equal(2, simulation.Actor(1)!.AttackTargetInstanceId!.Value);
            var acquisition = simulation.LastIdleAcquisitions.Single();
            Equal(new IdleAcquisitionEvent(1, 2, Approach: false), acquisition);
        });

        Check("idle selection keeps ring order for ordinary weapons and scores area weapons", () =>
        {
            // Ring 1 lists (+1,0) before (-1,0). The unarmed intruder sits first and
            // the armed guard second.
            var rings = NativeTargetRings.FromRings([[new CellCoordinate(0, 0)], [new CellCoordinate(1, 0), new CellCoordinate(-1, 0)]]);
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            const string placements = "5 5 0 0 -1 0\n6 5 1 1 -1 0\n4 5 0 1 -1 0\n";
            var ordinary = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + placements), catalog, OpenPath(12, 12),
                weaponCatalog: WeaponCatalog.Parse(AcquisitionWeapons), targetRings: rings);
            ordinary.Step([]);
            // The native arithmetic zeroes every ordinary-weapon score, so the first
            // candidate in ring order wins even though the other one is armed.
            Equal(2, ordinary.Actor(1)!.AttackTargetInstanceId!.Value);

            var area = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + placements), catalog, OpenPath(12, 12),
                weaponCatalog: WeaponCatalog.Parse("2\n1 BULLET 0 0 1 10 90 3 7 0 0 0 0\n2 BULLET 0 0 1 10 90 8 0 0 0 0 0\n"), targetRings: rings);
            area.Step([]);
            // Area weapons score armed targets (150) above unarmed ones (50).
            Equal(3, area.Actor(1)!.AttackTargetInstanceId!.Value);
        });

        Check("idle second scan radius depends on player kind and damage", () =>
        {
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            var weapons = WeaponCatalog.Parse(AcquisitionWeapons);
            // The intruder is six cells away: outside weapon range 3 and the calm
            // human radius 4, inside the computer radius 16.
            var human = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 0 0 -1 0\n8 2 1 1 -1 0\n"),
                catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
            human.Step([]);
            Equal(true, human.Actor(1)!.AttackTargetInstanceId is null && human.Actor(1)!.MoveOrder is null);
            Equal(ScenarioSimulation.NativeIdleShortWaitTicks, human.Actor(1)!.IdleWaitTicks);
            Equal(1, human.Actor(1)!.IdleMissCount);

            const string computerTeams = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n4\n%AI\nTEAM 1 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n";
            var computer =ScenarioSimulation.Create(ScenarioDefinition.Parse(computerTeams + "2 2 0 0 -1 0\n8 2 1 1 -1 0\n"),
                catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
            computer.Step([]);
            var approach = computer.LastIdleAcquisitions.Single();
            Equal(new IdleAcquisitionEvent(1, 2, Approach: true), approach);
            Equal(true, computer.Actor(1)!.MoveOrder!.StopOnContact);
            // The closest free cell inside weapon range of the hostile at (8,2).
            Equal(new CellCoordinate(6, 2), computer.Actor(1)!.MoveOrder!.Target);
        });

        Check("damaged human units widen their idle scan and close on the shooter", () =>
        {
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            var weapons = WeaponCatalog.Parse(AcquisitionWeapons);
            // A long-range shooter six cells away hits the idle guard; the guard
            // cannot see past radius 4 until it has been damaged, then looks 9 out.
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 0 0 -1 0\n8 2 2 1 -1 0\n"),
                catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(2, 1))]);
            Equal(true, simulation.Actor(1)!.MoveOrder is null);
            IdleAcquisitionEvent? approach = null;
            for (var tick = 0; tick < 120 && approach is null; tick++)
            {
                simulation.Step([]);
                approach = simulation.LastIdleAcquisitions.SingleOrDefault(acquisition => acquisition.SourceActorInstanceId == 1);
            }
            Equal(true, simulation.Actor(1)!.Health < 1000);
            Equal(new IdleAcquisitionEvent(1, 2, Approach: true), approach ?? throw new InvalidOperationException("The damaged guard never closed on its attacker."));
        });

        Check("idle scans wait 15 ticks for three misses and then 45", () =>
        {
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 0 0 -1 0\n"),
                catalog, OpenPath(8, 8), weaponCatalog: WeaponCatalog.Parse(AcquisitionWeapons), targetRings: EuclideanRings());
            var guard = simulation.Actors.Single();
            var scans = new List<int>();
            for (var tick = 1; tick <= 140; tick++)
            {
                var before = guard.IdleWaiting;
                simulation.Step([]);
                if (!before && guard.IdleWaiting) scans.Add(tick);
            }
            // Each miss pushes a wait (0x412274) and returns 0. The wait counts its
            // word down to zero and pops one update later (0x4122C8), so a 15 wait
            // gives a 17-tick period until the third miss, then 45 gives 47.
            Equal(new[] { 1, 18, 35, 52, 99 }, scans.ToArray());
            Equal(3, guard.IdleMissCount);
        });

        Check("damage ends an idle wait and the idle record scans in the same update", () =>
        {
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 0 0 -1 0\n"),
                catalog, OpenPath(8, 8), weaponCatalog: WeaponCatalog.Parse(AcquisitionWeapons), targetRings: EuclideanRings());
            var guard = simulation.Actors.Single();
            for (var tick = 0; tick < 5; tick++) simulation.Step([]);
            Equal(true, guard.IdleWaiting);
            Equal(ScenarioSimulation.NativeIdleShortWaitTicks - 4, guard.IdleWaitTicks);
            // 0x4122C8 compares the health stored at the push: a change pops the wait
            // and returns 1, so the idle record scans (and misses) in this update.
            guard.Health -= 10;
            simulation.Step([]);
            Equal(true, guard.IdleWaiting);
            Equal(ScenarioSimulation.NativeIdleShortWaitTicks, guard.IdleWaitTicks);
            Equal(guard.Health, guard.IdleWaitHealth);
        });

        Check("an idle fidget turns to its random bearing after the wait before scanning again", () =>
        {
            // Turn rate 2: the fidget record (type 4, 0x412358) steps the facing by 2
            // per update and pops on arrival, letting the idle record scan in the same
            // update.
            var catalog = EntityCatalog.Parse("1\nSLOW 0 2 25 8 8 1 -1 -1 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 0 0 -1 0\n"),
                catalog, OpenPath(8, 8), weaponCatalog: WeaponCatalog.Parse(AcquisitionWeapons), targetRings: EuclideanRings());
            var guard = simulation.Actors.Single();
            for (var tick = 0; tick < 5000 && guard.IdleFidgetFacing is null; tick++) simulation.Step([]);
            var bearing = guard.IdleFidgetFacing ?? throw new InvalidOperationException("No fidget was drawn in 5000 ticks.");
            for (var tick = 0; tick < 100 && guard.IdleWaiting; tick++) simulation.Step([]);
            var difference = (bearing - guard.Facing.Current + 256) % 256;
            var turns = Math.Max(1, (Math.Min(difference, 256 - difference) + 1) / 2);
            var updates = 0;
            while (!guard.IdleWaiting && updates < 200)
            {
                simulation.Step([]);
                updates++;
            }
            Equal(turns, updates);
            Equal(bearing, guard.Facing.Current);
            Equal(false, guard.IdleFidgetFacing.HasValue);
        });

        Check("idle selection skips critters, untargetable props, and unseen cells", () =>
        {
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            var weapons = WeaponCatalog.Parse(AcquisitionWeapons);
            // Team 9 critter and an untargetable prop next to the guard, plus a
            // hostile in weapon range but outside the guard's sight.
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(AcquisitionHeader + "2 2 3 0 -1 0\n3 2 1 9 -1 0\n2 3 4 1 -1 0\n5 2 1 1 -1 0\n"),
                catalog, OpenPath(12, 12), weaponCatalog: weapons, targetRings: EuclideanRings());
            simulation.Step([]);
            Equal(true, simulation.Actor(1)!.AttackTargetInstanceId is null);
            Equal(0, simulation.LastIdleAcquisitions.Count);
        });

        Check("attack-move acquires a visible hostile then resumes its destination", () =>
        {
            var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 2 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n2 1 1 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 10 * 3];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 3), weaponCatalog: weapons);
            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackMoveIntent(1, new CellCoordinate(8, 1)))]);
            Equal(AttackMoveOrderOutcome.Accepted, simulation.LastAttackMoveOrders.Single().Outcome);
            Equal(2, simulation.LastAttackMoveAcquisitions.Single().TargetActorInstanceId);
            for (var tick = 0; tick < 200 && simulation.Actor(1)!.AttackMoveDestination is not null; tick++) simulation.Step([]);
            Equal(true, simulation.Actor(2)!.IsDestroyed);
            Equal(new CellCoordinate(8, 1), simulation.Actor(1)!.Movement.OccupiedCell);
            Equal(false, simulation.Actor(1)!.AttackMoveDestination.HasValue);
        });

        Check("with the installed tables, an idle marine fires back soon after a Gray warrior opens fire on it", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            // Team 0: a marine (0) at (2,4). Team 1: a Gray warrior (8) at (12,4), ordered to attack it.
            const string scenarioText = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\nTEAM 1 1\n1\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n2 4 0 0 -1 0\n12 4 8 1 -1 0\n";
            var pathBytes = new byte[PathRegionMap.RouteTableSize + 16 * 9];
            pathBytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(scenarioText), PathRegionMap.Parse(pathBytes, 16, 9), rules);
            for (var tick = 0; tick < 5; tick++) simulation.Step([]);
            simulation.Step([new ScheduledWorldCommand(simulation.TickCount, 0, new AttackIntent(2, 1))]);
            ulong? grayFired = null, marineFired = null;
            for (var tick = 0; tick < 400 && marineFired is null; tick++)
            {
                simulation.Step([]);
                if (simulation.LastWeaponFires.Any(fire => fire.SourceActorInstanceId == 2)) grayFired ??= simulation.TickCount;
                if (simulation.LastWeaponFires.Any(fire => fire.SourceActorInstanceId == 1)) marineFired ??= simulation.TickCount;
            }
            // Both weapons reach four cells; the idle wait pops as soon as the
            // marine's health changes, so its ring scan answers within a few updates.
            Equal(true, grayFired is not null && marineFired is not null && marineFired - grayFired <= 20);
        }, CheckTags.Data);

        Check("mine acquisition uses native target priority instead of nearest instance ID", () =>
        {
            var catalog = EntityCatalog.Parse("5\nENGI 0 15 30 6 4 -1 -1 -1 1 1 5 800 0 0 0 1 0 0 0 0 0 0 0 0 0 0 0 3 0 0 0 0\nUNUSED 0 0 0 0 0 -1 -1 -1 1 1 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nHMINE 0 40 0 6 4 38 38 38 1 1 7 800 0 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nUNARMED 1 1 25 1 1 -1 -1 -1 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nARMED 1 1 25 1 1 39 39 39 1 1 0 1000 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("2\n38 MINE 6 38 20 100 1 1 2 -1 -1 0 0\n39 GUN 0 0 20 10 1 1 0 -1 -1 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n2 2 0 0 -1 0\n2 3 3 1 -1 0\n2 1 4 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 25];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog,
                PathRegionMap.Parse(bytes, 5, 5), weaponCatalog: weapons);
            simulation.Step([new ScheduledWorldCommand(1, 0, new DeployMineIntent(1))]);
            for (var tick = 0; tick < ScenarioSimulation.NativeImmediateSpecialTicks; tick++) simulation.Step([]);
            Equal(3, simulation.Projectiles.Single().TargetActorInstanceId);
        });

        Check("native target rings decode whole-distance rings 0 through 16", () =>
        {
            var rings = NativeTargetRings.Load(GameInstallation.Open(dataPath).ExecutablePath).Rings;
            Equal(new[] { 1, 8, 16, 20, 24, 40, 36, 48, 56, 56, 68, 64, 80, 92, 88, 96, 4 }, rings.Select(ring => ring.Count).ToArray());
            Equal(new CellCoordinate(0, 1), rings[1][0]);
            Equal(new CellCoordinate(16, 0), rings[16][0]);
            for (var ring = 0; ring < rings.Count; ring++)
                Equal(true, rings[ring].All(offset => (int)Math.Floor(Math.Sqrt(offset.X * offset.X + offset.Z * offset.Z)) == ring));
        }, CheckTags.Data);
    }
}
