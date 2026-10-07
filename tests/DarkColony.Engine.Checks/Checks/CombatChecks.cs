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

/// <summary>Attack orders, pursuit, projectiles, damage, area effects, and dying.</summary>
internal static class CombatChecks
{
    public static void Register(CheckSuite suite)
    {
        var dataPath = suite.DataPath;
        void Check(string name, Action action, CheckTags tags = CheckTags.None) => suite.Add("Combat", name, tags, action);

        Check("projectile elapsed lifetime advances with authoritative flight", () =>
        {
            var projectile = new ProjectileState(1, 2, 3, 4, 5, new FixedPointPosition(100, 200), 7, -9, 3);
            Equal(3, projectile.TotalTicks);
            Equal(3, projectile.MaximumLifetimeTicks);
            Equal(0, projectile.ElapsedTicks);
            Equal(0, projectile.AnimationTicks);
            projectile.Step();
            Equal(false, projectile.ReachedAimedPosition);
            Equal(1, projectile.ElapsedTicks);
            Equal(2, projectile.RemainingTicks);
            Equal(new FixedPointPosition(107, 191), projectile.Position);
        });

        Check("mode-one timed projectiles follow the recovered 17-sample height arc", () =>
        {
            var projectile = new ProjectileState(1, 2, -1, 50, 20, new FixedPointPosition(128, 128),
                32, 0, 8, 64, new CellCoordinate(1, 0), new CellCoordinate(1, 0), projectileMode: 1);
            projectile.Step();
            Equal(3, projectile.HeightRaw); // table[16] 25 * 8 >> 6
            projectile.Step();
            Equal(35, projectile.HeightRaw); // table[14] 280 * 8 >> 6
            Equal(1, projectile.ProjectileMode);
        });

        Check("weapon catalog maps stale header columns to recovered native fields", () =>
        {
            var catalog = WeaponCatalog.Parse("1\n7 TEST 3 10 75 250 60 12 1 -1 -1 1 0\n");
            var weapon = catalog.Weapons[7];
            Equal(1, weapon.AreaEffectTemplateId);
            Equal(-1, weapon.BurstShotLimit);
            Equal(-1, weapon.BurstReloadTicks);
            Equal(0, weapon.PostFireReset);
            Equal(1, weapon.ProjectileMode);
            Equal(69, weapon.ProjectileLifetimeTicks);
        });

        Check("scenario simulation retains explicit attack targets until stopped", () =>
        {
            var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 4 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 -1 0\n1 1 1 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 12];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 3), weaponCatalog: weapons);
            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
            var attackOutcome = simulation.LastAttackOrders.Single().Outcome;
            var fired = simulation.LastWeaponFires.Count != 0;
            for (var tick = 0; tick < 20 && simulation.Actor(2)!.Health == 100; tick++) simulation.Step([]);
            Equal(2, simulation.Actor(1)!.AttackTargetInstanceId!.Value);
            Equal(AttackOrderOutcome.Acquired, attackOutcome);
            Equal(true, simulation.IsAttackTargetInRange(simulation.Actor(1)!));
            Equal((byte)64, simulation.Actor(1)!.Facing.Target);
            Equal(90, simulation.Actor(2)!.Health);
            Equal(true, fired);
            Equal(1, simulation.LastProjectileImpacts.Count);
            Equal(1, simulation.LastProjectileImpacts[0].SourceActorInstanceId);
            Equal(0, simulation.LastProjectileImpacts[0].WeaponClass);
            for (var tick = 0; tick < 9; tick++) simulation.Step([]);
            Equal(true, simulation.Actor(2)!.IsDestroyed);
            Equal(false, simulation.GroundOccupancy.IsOccupied(new CellCoordinate(1, 1)));
            Equal(true, simulation.Actor(1)!.AttackTargetInstanceId is null);
            Equal(1, simulation.LastDestroyedActors.Count);
            Equal(1, simulation.LastDestroyedActors[0].EntityId);
            simulation.Step([new ScheduledWorldCommand(2, 0, new StopIntent(1))]);
            Equal(true, simulation.Actor(1)!.AttackTargetInstanceId is null);
        });

        Check("a stop-on-contact approach ends once a hostile is in weapon range", () =>
        {
            var catalog = EntityCatalog.Parse(AcquisitionEntities);
            // Both teams share race 0 so the loader's race swap leaves the fixtures alone.
            const string computerTeams = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n4\n%AI\nTEAM 1 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n";
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(computerTeams + "1 2 0 0 -1 0\n8 2 1 1 -1 0\n"),
                catalog, OpenPath(14, 6), weaponCatalog: WeaponCatalog.Parse(AcquisitionWeapons), targetRings: EuclideanRings());
            var guard = simulation.Actor(1)!;
            var start = guard.Movement.VisualPosition;
            simulation.Step([]);
            Equal(true, guard.MoveOrder!.StopOnContact);
            // The idle handler returns 0 after pushing the move (0x414C20): no step
            // yet. The next update reserves the first cell.
            Equal(start, guard.Movement.VisualPosition);
            Equal(guard.Movement.OccupiedCell, guard.Movement.ReservedDestination);
            simulation.Step([]);
            Equal(true, guard.Movement.ReservedDestination != guard.Movement.OccupiedCell);
            for (var tick = 0; tick < 300 && guard.AttackTargetInstanceId is null; tick++) simulation.Step([]);
            Equal(2, guard.AttackTargetInstanceId ?? throw new InvalidOperationException("The approach never turned into an attack."));
            // Contact came at ring 3 (distance 3, at X = 5), one cell before the
            // (6,2) approach destination; the idle scan then took the target.
            Equal(new CellCoordinate(5, 2), guard.Movement.OccupiedCell);
        });

        Check("replacement move cancels an explicit attack target", () =>
        {
            var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 4 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 -1 0\n1 1 1 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 12];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 3), weaponCatalog: weapons);
            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
            Equal(2, simulation.Actor(1)!.AttackTargetInstanceId!.Value);
            simulation.Step([new ScheduledWorldCommand(2, 0, new MoveIntent(1, new CellCoordinate(3, 0)))]);
            Equal(true, simulation.Actor(1)!.AttackTargetInstanceId is null);
        });

        Check("a direct attack lets the in-flight step finish, then drops the move", () =>
        {
            var catalog = EntityCatalog.Parse("2\nATTACKER 0 1 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 1 4 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 -1 0\n1 1 0 2 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 5 * 3];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 3), weaponCatalog: weapons);
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(4, 0)))]);
            var oldReserved = simulation.Actor(1)!.Movement.ReservedDestination;
            Equal(true, simulation.GroundOccupancy.IsOccupied(oldReserved));
            simulation.Step([new ScheduledWorldCommand(2, 0, new AttackIntent(1, 2))]);
            // The step command (type 5, 0x4125BC) ignores the pending order.
            var attacker = simulation.Actor(1)!;
            Equal(true, simulation.GroundOccupancy.IsOccupied(oldReserved));
            Equal(true, attacker.FinishingStep is not null);
            Equal(2, attacker.AttackTargetInstanceId!.Value);
            for (var tick = 0; tick < 100 && attacker.FinishingStep is not null; tick++) simulation.Step([]);
            Equal(oldReserved, attacker.Movement.OccupiedCell);
            Equal(true, attacker.MoveOrder is null);
        });

        Check("an attack-move lets the in-flight step finish first", () =>
        {
            var catalog = EntityCatalog.Parse("2\nATTACKER 0 1 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 1 4 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 -1 0\n1 1 0 2 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 5 * 3];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 3), weaponCatalog: weapons);
            simulation.Step([new ScheduledWorldCommand(1, 0, new MoveIntent(1, new CellCoordinate(4, 0)))]);
            var oldReserved = simulation.Actor(1)!.Movement.ReservedDestination;
            simulation.Step([new ScheduledWorldCommand(2, 0, new AttackMoveIntent(1, new CellCoordinate(0, 2)))]);
            var mover = simulation.Actor(1)!;
            Equal(true, simulation.GroundOccupancy.IsOccupied(oldReserved));
            Equal(new CellCoordinate(0, 2), mover.AttackMoveDestination!.Value);
            for (var tick = 0; tick < 100 && mover.FinishingStep is not null; tick++) simulation.Step([]);
            Equal(oldReserved, mover.Movement.OccupiedCell);
        });

        Check("weapon bursts use rate between shots then decoded reload", () =>
        {
            var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            // rate=1, burst limit=2, reload=4. Projectile speed makes each hit resolve
            // within its firing tick, isolating cadence from flight time.
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 1 90 4 0 2 4 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 -1 0\n1 1 1 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 12];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 4, 3), weaponCatalog: weapons);

            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
            Equal(1, simulation.Actor(1)!.BurstShotCount);
            Equal(1, simulation.Actor(1)!.CooldownTicks);
            simulation.Step([]);
            Equal(0, simulation.Actor(1)!.BurstShotCount);
            Equal(4, simulation.Actor(1)!.CooldownTicks);
            for (var tick = 0; tick < 3; tick++)
            {
                simulation.Step([]);
                Equal(0, simulation.LastWeaponFires.Count);
            }
            simulation.Step([]);
            Equal(1, simulation.LastWeaponFires.Count);
            Equal(1, simulation.Actor(1)!.BurstShotCount);
            // Stat 8 counts every projectile the player launches (0x44178F).
            Equal(3, simulation.PlayerStatistic(simulation.Actor(1)!.Seed.Team, 8));
        });

        Check("area-effect weapon applies its authored radial percentage", () =>
        {
            var catalog = EntityCatalog.Parse("4\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 200 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 200 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nNEARBY 1 1 25 1 1 -1 -1 -1 1 1 0 200 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nFRIENDLY 0 1 25 1 1 -1 -1 -1 1 1 0 200 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BLAST 0 0 1 100 90 4 1 0 0 1 0\n");
            var areas = AreaEffectCatalog.Parse("1\n1 3\nNONE\n0 100 0\n0 100 50\n0 0 0\n0 0 0\n0 100 0\n0 0 0\n");
            const string source = "t\ni\nd\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n2 2 0 0 -1 0\n3 2 1 1 -1 0\n4 2 2 1 -1 0\n3 1 3 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 30]; bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 6, 5), weaponCatalog: weapons, areaEffects: areas);
            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
            for (var tick = 0; tick < 20 && simulation.LastProjectileImpacts.Count == 0; tick++) simulation.Step([]);
            Equal(100, simulation.Actor(2)!.Health);
            Equal(150, simulation.Actor(3)!.Health);
            Equal(175, simulation.Actor(4)!.Health);
        });

        Check("projectiles collide with an intervening hostile instead of remaining target locked", () =>
        {
            var catalog = EntityCatalog.Parse("3\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nBLOCKER 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            // Resolve impact damage against the intervening actor's armor class.
            ((int[])catalog[2].Values)[10] = 1;
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 20 25 90 5 0 0 0 0 0\n");
            var matrix = DamageMatrix.Parse("10\n9\n25 12 25 18 25 90 5 50 0 5\n100 25 0 25 50 100 10 50 0 10\n100 100 100 100 100 100 100 100 100 100\n100 100 100 100 100 100 100 100 100 100\n100 100 100 100 100 100 100 100 100 100\n100 100 100 100 100 100 100 100 100 100\n100 100 100 100 100 100 100 100 100 100\n100 100 100 100 100 100 100 100 100 100\n100 100 100 100 100 100 100 100 100 100\n");
            const string source = "t\ni\nd\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 1 0 0 -1 0\n3 1 1 1 -1 0\n1 1 2 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 15]; bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 5, 3), weaponCatalog: weapons, damageMatrix: matrix);
            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
            for (var tick = 0; tick < 20 && simulation.LastProjectileImpacts.Count == 0; tick++) simulation.Step([]);
            Equal(3, simulation.LastProjectileImpacts.Single().TargetActorInstanceId);
            // Matrix [0][1] is 12%, the 8.8 factor trunc(12 * 2.56) = 30: (30 * 25) >> 8 = 2.
            Equal(98, simulation.Actor(3)!.Health);
            Equal(100, simulation.Actor(2)!.Health);
        });

        Check("area trajectories skip intervening actors and detonate at their launch-time cell", () =>
        {
            var catalog = EntityCatalog.Parse("3\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nBLOCKER 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BLAST 0 0 100 25 15 10 1 0 0 1 0\n");
            var areas = AreaEffectCatalog.Parse("1\n1 3\nNONE\n0 0 0\n0 100 0\n0 0 0\n0 0 0\n0 100 0\n0 0 0\n");
            const string source = "t\ni\nd\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 1 0 0 -1 0\n4 1 1 1 -1 0\n1 1 2 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 21]; bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 7, 3), weaponCatalog: weapons, areaEffects: areas);
            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
            Equal(new CellCoordinate(4, 1), simulation.Projectiles.Single().TimedImpactCell!.Value);
            for (var tick = 0; tick < 30 && simulation.LastProjectileImpacts.Count == 0; tick++) simulation.Step([]);
            Equal(new CellCoordinate(4, 1), simulation.LastProjectileImpacts.Single().Position.Cell);
            Equal(75, simulation.Actor(2)!.Health);
            Equal(100, simulation.Actor(3)!.Health);
        });

        Check("world ticks execute the native four projectile substeps", () =>
        {
            Equal(4, ScenarioSimulation.NativeProjectileSubstepsPerTick);
            var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 100 10 15 10 0 0 0 0 0\n");
            const string source = "t\ni\nd\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n0 1 0 0 -1 0\n4 1 1 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 18]; bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 6, 3), weaponCatalog: weapons);
            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
            var projectile = simulation.Projectiles.Single();
            Equal(4, projectile.ElapsedTicks);
            Equal(1, projectile.AnimationTicks);
            Equal(new FixedPointPosition(FixedPointPosition.AtCellCenter(new CellCoordinate(0, 1)).XRaw + 60,
                FixedPointPosition.AtCellCenter(new CellCoordinate(0, 1)).ZRaw), projectile.Position);
        });

        Check("attack orders honor the recovered team relation matrix", () =>
        {
            var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 4 0 0 0 0 0\n");
            const string sameTeam = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 -1 0\n2 0 1 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 12];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(sameTeam), catalog, PathRegionMap.Parse(bytes, 4, 3), weaponCatalog: weapons);
            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
            Equal(AttackOrderOutcome.NonHostile, simulation.LastAttackOrders.Single().Outcome);
            Equal(false, simulation.Actor(1)!.AttackTargetInstanceId.HasValue);

            const string opposingTeams = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 0 0 0 -1 0\n2 1 1 1 -1 0\n";
            // Relations among players follow the mutual alliance bits, recomputed at
            // the start of every update (0x4198D3).
            simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(opposingTeams), catalog, PathRegionMap.Parse(bytes, 4, 3), weaponCatalog: weapons);
            simulation.SetAllianceBit(0, 1, true);
            simulation.SetAllianceBit(1, 0, true);
            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
            Equal(AttackOrderOutcome.NonHostile, simulation.LastAttackOrders.Single().Outcome);
            // One side withdrawing ends the alliance.
            simulation.SetAllianceBit(1, 0, false);
            simulation.Step([new ScheduledWorldCommand(2, 0, new AttackIntent(1, 2))]);
            Equal(AttackOrderOutcome.Acquired, simulation.LastAttackOrders.Single().Outcome);
        });

        Check("attack orders pursue an out-of-range target through normal movement", () =>
        {
            var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 2 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n7 1 1 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 10 * 3];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 3), weaponCatalog: weapons);
            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
            var fired = simulation.LastWeaponFires.Count != 0;
            for (var tick = 0; tick < 100 && !simulation.IsAttackTargetInRange(simulation.Actor(1)!); tick++)
            {
                simulation.Step([]);
                fired |= simulation.LastWeaponFires.Count != 0;
            }
            var pursuer = simulation.Actor(1)!;
            if (pursuer.Movement.OccupiedCell.X <= 1 || !simulation.IsAttackTargetInRange(pursuer) || !fired)
                throw new InvalidOperationException($"pursuit cell={pursuer.Movement.OccupiedCell}, targetCell={simulation.Actor(2)!.Movement.OccupiedCell}, range={weapons.Weapons[1].Range}, inRange={simulation.IsAttackTargetInRange(pursuer)}, fired={fired}, target={pursuer.AttackTargetInstanceId}, order={pursuer.MoveOrder?.Target}, playback={pursuer.Playback is not null}");
        });

        Check("target destruction cancels another attacker's reserved pursuit", () =>
        {
            var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 200 90 2 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n6 1 1 1 -1 0\n5 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 10 * 3];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 3), weaponCatalog: weapons);

            simulation.Step([
                new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2)),
                new ScheduledWorldCommand(1, 1, new AttackIntent(3, 2)),
            ]);
            for (var tick = 0; tick < 120 && !simulation.Actor(2)!.IsDestroyed; tick++) simulation.Step([]);

            if (!simulation.Actor(2)!.IsDestroyed)
                throw new InvalidOperationException($"Killer did not destroy target: health={simulation.Actor(2)!.Health}, fires={simulation.LastWeaponFires.Count}, projectileCount={simulation.Projectiles.Count}, killerTarget={simulation.Actor(3)!.AttackTargetInstanceId}");
            var pursuer = simulation.Actor(1)!;
            Equal(false, pursuer.AttackTargetInstanceId.HasValue);
            Equal(true, pursuer.Playback is null && pursuer.MoveOrder is null);
        });

        Check("attack-move resumes its destination when another attacker destroys its target", () =>
        {
            var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 200 90 2 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n6 1 1 1 -1 0\n5 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 10 * 3];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 3), weaponCatalog: weapons);

            simulation.Step([
                new ScheduledWorldCommand(1, 0, new AttackMoveIntent(1, new CellCoordinate(9, 1))),
                new ScheduledWorldCommand(1, 1, new AttackIntent(3, 2)),
            ]);
            for (var tick = 0; tick < 120 && !simulation.Actor(2)!.IsDestroyed; tick++) simulation.Step([]);
            Equal(true, simulation.Actor(2)!.IsDestroyed);
            Equal(new CellCoordinate(9, 1), simulation.Actor(1)!.AttackMoveDestination!.Value);
            for (var tick = 0; tick < 240 && simulation.Actor(1)!.AttackMoveDestination is not null; tick++) simulation.Step([]);
            Equal(new CellCoordinate(9, 1), simulation.Actor(1)!.Movement.OccupiedCell);
            Equal(false, simulation.Actor(1)!.AttackMoveDestination.HasValue);
        });

        Check("attack-move excludes a team marked cooperative in the relation matrix", () =>
        {
            var catalog = EntityCatalog.Parse("2\nATTACKER 0 255 25 1 1 1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\nTARGET 1 1 25 1 1 -1 -1 -1 1 1 0 100 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
            var weapons = WeaponCatalog.Parse("1\n1 BULLET 0 0 1 10 90 2 0 0 0 0 0\n");
            const string source = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n1 1 0 0 -1 0\n2 1 1 1 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 10 * 3];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), catalog, PathRegionMap.Parse(bytes, 10, 3), weaponCatalog: weapons);
            simulation.SetAllianceBit(0, 1, true);
            simulation.SetAllianceBit(1, 0, true);

            simulation.Step([new ScheduledWorldCommand(1, 0, new AttackMoveIntent(1, new CellCoordinate(8, 1)))]);

            Equal(AttackMoveOrderOutcome.Accepted, simulation.LastAttackMoveOrders.Single().Outcome);
            Equal(0, simulation.LastAttackMoveAcquisitions.Count);
            Equal(false, simulation.Actor(1)!.AttackTargetInstanceId.HasValue);
        });

        Check("original damage matrix loads", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var matrix = DamageMatrix.Load(install.DataFile("gamestat", "mbullet.txt"));
            Equal(25, matrix[0, 0]);
            Equal(25, matrix.CalculateBaseDamage(100, 0, 0));
            Equal(18, matrix.CalculateNativeDamage(100, 0, 0, reduceToThreeQuarters: true));
            Equal(19, matrix.CalculateNativeDamage(104, 0, 0, reduceToThreeQuarters: true));
        }, CheckTags.Data);

        Check("original area-effect templates retain radial patterns and weapon links", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var effects = AreaEffectCatalog.Load(install.DataFile("gamestat", "boomstat.txt"));
            var weapons = WeaponCatalog.Load(install.DataFile("gamestat", "weapstat.txt"));
            Equal(13, effects.Templates.Count);
            Equal(5, effects.Templates[1].PatternSize);
            Equal(new[] { "NUKE", "GASY" }, effects.Templates[1].EffectNames.ToArray());
            Equal(100, effects.Templates[1].DamagePattern[2][2]);
            Equal(7, effects.Templates[10].PatternSize);
            Equal(new[] { "NAPALM" }, effects.Templates[10].EffectNames.ToArray());
            Equal(100, effects.Templates[10].AimWeights[1][1]);
            Equal(1, weapons.Weapons[10].AreaEffectTemplateId);
            Equal(10, weapons.Weapons[50].AreaEffectTemplateId);
            Equal(true, weapons.Weapons.Values.Where(weapon => weapon.HasAreaEffect)
                .All(weapon => effects.Templates.ContainsKey(weapon.AreaEffectTemplateId)));
        }, CheckTags.Data);

        Check("weapons end in the explosions dc.exe's loader gives them: EXPLODE, else EXPL, replaced by a boom template's effects", () =>
        {
            var explosions = SimulationRules.Load(GameInstallation.Open(dataPath)).Explosions!;
            // 10-12 Barrage and 24-26 Atril: Arty templates 1 and 9. 30 SPAK and 5
            // SMOK their own EXPLODE. 37 SPAK: template 5's SMAY replaces it. 38
            // the mine: template 2. 1 the marine's: nothing.
            Equal(new[] { "NUKE", "GASY" }, explosions.Names(10).ToArray());
            Equal(new[] { "NUKE", "GASY" }, explosions.Names(24).ToArray());
            Equal(new[] { "SPAKEXPLODE0" }, explosions.Names(30).ToArray());
            Equal(new[] { "SMOKEXPLODE0" }, explosions.Names(5).ToArray());
            Equal(new[] { "SMAY" }, explosions.Names(37).ToArray());
            Equal(new[] { "NUKE" }, explosions.Names(38).ToArray());
            Equal(0, explosions.Count(1));
        }, CheckTags.Data);

        Check("FIN frames keep their hotspots, and hotspot 7 of a fire animation is where its projectile leaves", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var barr = AnimationDefinition.Load(install.DataFile("animate", "barr.fin"));
            var fire = barr.Animations.Single(animation => animation.Name == "BARRFIREA0");
            Equal(new FinHotspot("BARRDIE10", 0, -19), barr.LogicalFrames[fire.FirstFrame + 1].Hotspots[NativeFireMuzzles.MuzzleHotspot]!);
            Equal(true, barr.LogicalFrames[fire.FirstFrame].Hotspots.All(hotspot => hotspot is null));

            // 0x4263D8: (x * 8, -y * 8) from the shooter, after the ticks of the
            // frames before. BARRFIREA0's frames have delay 0 (2 ticks each);
            // ATRILFIREA0's muzzle is frame 4, after delays 20, 20, 13, 13 (3+3+2+2).
            var rules = SimulationRules.Load(install);
            var animations = EntityAnimationCatalog.Build(rules.Entities, install.DataFile("animate"), EntityAnimationCatalog.LoadOrder(install.DataFile("anim.dat")));
            FireMuzzle MuzzleOf(string code, string animationName)
            {
                var entity = rules.Entities.Entities.First(definition => definition.Code == code);
                var sector = Enumerable.Range(0, 16).First(sector => animations.PreferredFire(entity.Id, sector, 0)!.Candidate.AnimationName == animationName);
                return rules.FireMuzzles!.Muzzles(entity.Id, sector, 0).Single();
            }
            Equal(new FireMuzzle(0, 152, 2), MuzzleOf("BARR", "BARRFIREA0"));
            Equal(new FireMuzzle(0, 32, 10), MuzzleOf("ATRIL", "ATRILFIREA0"));
            // BARRFIREA12 has no muzzle frame: that shot leaves the centre at once.
            var barrage = rules.Entities.Entities.First(definition => definition.Code == "BARR");
            var plain = Enumerable.Range(0, 16).First(sector => animations.PreferredFire(barrage.Id, sector, 0)!.Candidate.AnimationName == "BARRFIREA12");
            Equal(0, rules.FireMuzzles!.Muzzles(barrage.Id, plain, 0).Count);
        }, CheckTags.Data);

        Check("a Barrage shell leaves its muzzle after the fire frames before it, and its impact draws one of two explosions", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            var barrage = rules.Entities.Entities.First(definition => definition.Code == "BARR");
            const string header = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 32 * 32];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var path = PathRegionMap.Parse(bytes, 32, 32);
            ScenarioSimulation Create(bool explosions) => ScenarioSimulation.Create(
                ScenarioDefinition.Parse(header + $"10 5 {barrage.Id} 0 -1 0\n10 13 0 1 -1 0\n"), rules.Entities, path,
                weaponCatalog: rules.Weapons, damageMatrix: rules.DamageMatrix, areaEffects: rules.AreaEffects, randomTable: rules.RandomTable,
                fireMuzzles: rules.FireMuzzles, weaponExplosions: explosions ? rules.Explosions : null);
            var simulation = Create(explosions: true);
            var reference = Create(explosions: false);
            void Send(params WorldCommand[] commands)
            {
                var scheduled = commands.Select((command, index) => new ScheduledWorldCommand(simulation.TickCount, (ulong)index, command)).ToArray();
                simulation.Step(scheduled);
                reference.Step(scheduled);
            }
            Send(new AttackIntent(1, 2));
            for (var tick = 0; tick < 64 && simulation.Projectiles.Count == 0; tick++) Send();
            var shooter = simulation.Actor(1)!;
            var fired = simulation.LastWeaponFires.Single();
            var muzzle = rules.FireMuzzles!.Muzzles(barrage.Id, shooter.Facing.RenderSector16, fired.PresentationVariantRoll).Single();
            var shell = simulation.Projectiles.Single();
            var start = shooter.Movement.VisualPosition.AddRaw(muzzle.XRaw, muzzle.ZRaw);
            Equal(start, shell.Position);
            Equal(true, shell.LaunchDelaySubsteps > 0);
            // A waiting shell stays at the muzzle and does not age.
            while (shell.LaunchDelaySubsteps > 0) { Equal((start, 0), (shell.Position, shell.ElapsedTicks)); Send(); }

            // The two runs draw alike until the impact, which draws once more for the explosion.
            Equal(reference.NativeRandomCursor, simulation.NativeRandomCursor);
            for (var tick = 0; tick < 64 && simulation.LastProjectileImpacts.Count == 0; tick++) Send();
            var impact = simulation.LastProjectileImpacts.Single();
            Equal((true, -1), (impact.ExplosionVariant is 0 or 1, reference.LastProjectileImpacts.Single().ExplosionVariant));
            Equal((reference.NativeRandomCursor + 1) & 0xff, simulation.NativeRandomCursor);
        }, CheckTags.Data);

        Check("original weapon catalog loads", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var weapons = WeaponCatalog.Load(install.DataFile("gamestat", "weapstat.txt"));
            Equal(64, weapons.Weapons.Count);
            var humanWeapon = weapons.Weapons[1];
            Equal("Human weapon 0", humanWeapon.DisplayName);
            Equal(0, humanWeapon.WeaponClass);
            Equal(100, humanWeapon.Damage);
            Equal(4, humanWeapon.Range);
            Equal(0, humanWeapon.AreaEffectTemplateId);
            Equal(0, humanWeapon.ProjectileMode);
            Equal(1, weapons.Weapons[10].AreaEffectTemplateId);
            Equal(1, weapons.Weapons[10].ProjectileMode);
            var explosions = SimulationRules.Load(install).Explosions!;
            var effects = WeaponEffectCatalog.Build(weapons, install.DataFile("animate"), explosionNames: explosions,
                loadOrder: EntityAnimationCatalog.LoadOrder(install.DataFile("anim.dat")));
            var barragerWeapon = weapons.Weapons.Values.First(weapon => weapon.Sprite.Equals("BARR", StringComparison.OrdinalIgnoreCase));
            Equal(true, effects.Bullet(barragerWeapon.Id) is not null);
            // 0x43B84F gives a weapon a projectile animation only when
            // <sprite>BULLET0 exists; the others' shots are not drawn (0x439DC3).
            // That leaves the 31 "weapons" ones (the marines', the warriors'...),
            // SMOK's 3 and SPAK's 4 without.
            var unseen = weapons.Weapons.Values.Where(weapon => effects.Bullet(weapon.Id) is null).ToArray();
            Equal(38, unseen.Length);
            Equal(new[] { "SMOK", "SPAK", "weapons" }, unseen.Select(weapon => weapon.Sprite).Distinct().Order(StringComparer.Ordinal).ToArray());
            var spakWeapon = weapons.Weapons.Values.First(weapon => weapon.Sprite.Equals("SPAK", StringComparison.OrdinalIgnoreCase));
            Equal("SPAKEXPLODE0", effects.Explosion(spakWeapon.Id, 0)!.AnimationName);
            // The Barrage's shell ends in the Arty template's NUKE or GASY, from
            // the files the game loads (effects.fin also has a NUKE; it is not loaded).
            Equal(("nuke.fin", "NUKE"), (Path.GetFileName(effects.Explosion(10, 0)!.FinPath).ToLowerInvariant(), effects.Explosion(10, 0)!.AnimationName));
            Equal("GASY", effects.Explosion(10, 1)!.AnimationName);
            Equal(true, effects.Explosion(10, 2) is null && effects.Explosion(1, 0) is null);
            var napalm = weapons.Weapons[50];
            Equal("BARR", napalm.Sprite);
            Equal(6, napalm.WeaponClass);
            Equal(10, napalm.AreaEffectTemplateId);
            Equal(4, napalm.ProjectileMode);
            var psyEffect = weapons.Weapons[51];
            Equal("BARR", psyEffect.Sprite);
            Equal(12, psyEffect.AreaEffectTemplateId);
            Equal(4, psyEffect.ProjectileMode);
            Equal(5, weapons.Weapons[57].ProjectileMode);
            Equal(6, weapons.Weapons[58].ProjectileMode);
            Equal(7, weapons.Weapons[59].ProjectileMode);
            Equal(8, weapons.Weapons[60].ProjectileMode);
            Equal(9, weapons.Weapons[63].ProjectileMode);
            Equal(10, weapons.Weapons[64].ProjectileMode);
            var spak = weapons.Weapons[37];
            Equal(5, spak.AreaEffectTemplateId);
            Equal(3, spak.BurstShotLimit);
            Equal(30, spak.BurstReloadTicks);
        }, CheckTags.Data);

        Check("a killed actor stays counted and seeing for 150 updates in the dying state", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var rules = SimulationRules.Load(install);
            const string source = "desert.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\nTEAM 0 1\n0\n%Race\n0\n%Money\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n" +
                "2 1 0 0 -1 0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 16 * 4];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var simulation = ScenarioSimulation.Create(ScenarioDefinition.Parse(source), PathRegionMap.Parse(bytes, 16, 4), rules);
            var unit = simulation.Actors.Single();
            var radius = simulation.ObservationRange(unit);
            Equal(true, radius >= 2);
            var edge = new CellCoordinate(2 + radius, 1);
            for (var tick = 0; tick < 15; tick++) simulation.Step([]);
            // 0x416308 pushes the dying command with counter 0; 0x434D48 clears the grids.
            simulation.Destroy(unit, new List<DestroyedActorEvent>());
            Equal(0, unit.DeathTicks!.Value);
            Equal(false, simulation.GroundOccupancy.IsOccupied(new CellCoordinate(2, 1)));
            simulation.Step([]);
            // The refresh at update 16 still stamps the full radius (counter 1).
            Equal(1, unit.DeathTicks!.Value);
            Equal(true, simulation.IsCellVisibleToTeam(0, edge));
            Equal(1, simulation.PlayerStatistic(0, 6));
            // 0x445D05: (150 - t) * r / 150, at least 1.
            while (unit.DeathTicks < 140) simulation.Step([]);
            while (simulation.TickCount % 16 != 0) simulation.Step([]);
            Equal(false, simulation.IsCellVisibleToTeam(0, edge));
            Equal(true, simulation.IsCellVisibleToTeam(0, new CellCoordinate(2, 1)));
            Equal(1, simulation.PlayerStatistic(0, 6));
            while (unit.IsDying) simulation.Step([]);
            // Out of the update list after 150 runs of the dying command.
            Equal(15UL + ScenarioSimulation.NativeDeathTicks, simulation.TickCount);
            simulation.Step([]);
            Equal(0, simulation.PlayerStatistic(0, 6));
        }, CheckTags.Data);

        Check("boomstat aim weights scatter ordinary area shots while Inspire locks center", () =>
        {
            var install = GameInstallation.Open(dataPath);
            var entities = EntityCatalog.Load(install.DataFile("gamestat", "gamestat.txt"));
            var weapons = WeaponCatalog.Load(install.DataFile("gamestat", "weapstat.txt"));
            var areaSource = entities.Entities.First(entity => entity.MovementSpeed > 0 && !entity.HasImmediateAreaEffect &&
                entity.WeaponSlots[0] >= 0 && weapons.Weapons[entity.WeaponSlots[0]].AreaEffectTemplateId == 1);
            var forcedTopLeft = AreaEffectCatalog.Parse("1\n1 3\nNONE\n0 0 0\n0 100 0\n0 0 0\n100 0 0\n0 0 0\n0 0 0\n");
            const string header = "tiles.bts\ninternal\ndisplay\n0\n0\n0\n0\n0\n%City\n0\n0\n0\n0\n0\n0\n0\n0\n0\n";
            var bytes = new byte[PathRegionMap.RouteTableSize + 32 * 32];
            bytes.AsSpan(PathRegionMap.RouteTableSize).Fill(1);
            var path = PathRegionMap.Parse(bytes, 32, 32);

            var ordinaryScenario = ScenarioDefinition.Parse(header +
                $"10 5 {areaSource.Id} 0 -1 0\n15 5 0 1 -1 0\n");
            var ordinary = ScenarioSimulation.Create(ordinaryScenario, entities, path,
                weaponCatalog: weapons, areaEffects: forcedTopLeft, randomTable: NativeRandomTable.Load(install.ExecutablePath));
            ordinary.Step([new ScheduledWorldCommand(1, 0, new AttackIntent(1, 2))]);
            for (var tick = 0; tick < 32 && ordinary.Projectiles.Count == 0; tick++) ordinary.Step([]);
            Equal(new CellCoordinate(14, 4), ordinary.Projectiles.Single().TimedImpactCell!.Value);
            // Common fire draws the presentation variant first (0x412E13, table
            // index one), then the area aim (index two), then the projectile
            // constructor's byte (0x4417B4, index three), all from one stream.
            Equal(0x7e, ordinary.LastWeaponFires.Single().PresentationVariantRoll & 0xff);

            var inspiredScenario = ScenarioDefinition.Parse(header +
                $"10 10 69 0 -1 0\n10 5 {areaSource.Id} 0 -1 0\n15 5 0 1 -1 0\n");
            var inspired = ScenarioSimulation.Create(inspiredScenario, entities, path,
                weaponCatalog: weapons, areaEffects: forcedTopLeft);
            inspired.Step([new ScheduledWorldCommand(1, 0, new InspireTroopsIntent(1))]);
            for (var tick = 0; tick < ScenarioSimulation.NativeImmediateSpecialTicks; tick++) inspired.Step([]);
            Equal(true, inspired.Actor(2)!.InspirationTicksRemaining > 0);
            inspired.Step([new ScheduledWorldCommand(2, 0, new AttackIntent(2, 3))]);
            for (var tick = 0; tick < 32 && inspired.Projectiles.Count == 0; tick++) inspired.Step([]);
            Equal(new CellCoordinate(15, 5), inspired.Projectiles.Single().TimedImpactCell!.Value);
        }, CheckTags.Data);
    }
}
