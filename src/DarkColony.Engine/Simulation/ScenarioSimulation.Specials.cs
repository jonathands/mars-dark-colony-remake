using DarkColony.Engine.Commands;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>Unit specials: Inspire, area heal, mines, towers, money stealing, and ground special attacks.</summary>
public sealed partial class ScenarioSimulation
{
    private List<InspireEvent> UpdateInspireState()
    {
        var events = new List<InspireEvent>();
        // 0x4192F0 gates actor byte +0xd6 on the low four bits of the
        // day/night phase counter world+0x530.
        if ((DayNight.PhaseTicks & (NativeInspireCountdownCadence - 1)) == 0)
        {
            foreach (var actor in Actors.Where(actor => actor.InspirationTicksRemaining > 0))
            {
                actor.InspirationTicksRemaining--;
                if (actor.InspirationTicksRemaining == 0)
                    actor.InspirationSourceActorInstanceId = null;
            }
        }

        foreach (var commander in Actors.Where(actor => actor.InspireCastTicksRemaining > 0)
                     .OrderBy(actor => actor.Seed.InstanceId))
        {
            commander.InspireCastTicksRemaining--;
            if (commander.InspireCastTicksRemaining == 0)
                events.AddRange(ApplyInspire(commander));
        }
        return events;
    }

    private void UpdateAbilityCharge()
    {
        // Common actor update 0x4192C0 runs this only when the low five bits
        // of the day/night phase counter world+0x530 are zero. Runtime +0xf8
        // is gamestat source value 25.
        if ((DayNight.PhaseTicks & (NativeAbilityChargeCadence - 1)) != 0) return;
        foreach (var actor in Actors.Where(actor => !actor.IsDestroyed))
        {
            var recovery = Math.Max(0, EffectiveDefinition(actor).AbilityChargeRecovery);
            if (recovery == 0) continue;
            actor.AbilityCharge = actor.AbilityCharge >= SimulatedActor.NativeMaximumAbilityCharge - recovery
                ? SimulatedActor.NativeMaximumAbilityCharge
                : actor.AbilityCharge + recovery;
        }
    }

    private IReadOnlyList<MineDeploymentEvent> UpdateMineDeploymentState()
    {
        var events = new List<MineDeploymentEvent>();
        foreach (var actor in Actors.Where(actor => actor.MineDeployTicksRemaining > 0)
                     .OrderBy(actor => actor.Seed.InstanceId))
        {
            if (actor.IsDestroyed)
            {
                actor.MineDeployTicksRemaining = 0;
                continue;
            }
            actor.MineDeployTicksRemaining--;
            if (actor.MineDeployTicksRemaining == 0)
                events.Add(CompleteMineDeployment(actor));
        }
        return events;
    }

    private InspireEvent BeginInspire(InspireTroopsIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source))
            return new(intent.EntityInstanceId, intent.EntityInstanceId, 0, InspireOutcome.SourceMissing);
        if (source.IsDestroyed)
            return new(intent.EntityInstanceId, intent.EntityInstanceId, 0, InspireOutcome.SourceDestroyed);
        var definition = EffectiveDefinition(source);
        if (definition.Id is < 69 or > 76 || definition.ImmediateSpecialCode == 0 || !definition.HasImmediateAreaEffect)
            return new(intent.EntityInstanceId, intent.EntityInstanceId, 0, InspireOutcome.SourceNotCommander);

        source.Playback?.Cancel();
        source.Playback = null;
        source.MoveOrder = null;
        source.AttackTargetInstanceId = null;
        source.GroundSpecialAttackTarget = null;
        source.AttackMoveDestination = null;
        source.InspireCastTicksRemaining = NativeImmediateSpecialTicks;
        return new(source.Seed.InstanceId, source.Seed.InstanceId, NativeImmediateSpecialTicks, InspireOutcome.Preparing);
    }

    private IReadOnlyList<InspireEvent> ApplyInspire(SimulatedActor source)
    {
        var remainingHits = Math.Max(0, EffectiveDefinition(source).ImmediateAreaTargetLimit);
        var events = new List<InspireEvent>();
        var origin = source.Movement.OccupiedCell;
        // 0x4171C7-0x41739E scans four sides for radii 0..10. Each side spans
        // -20..20 cells and probes the ordinary layer before the alternate
        // layer. Deliberately retain duplicate centre-line hits: the native
        // helper decrements its 6/8/10/12 limit for every qualifying probe.
        for (var radius = 0; radius <= 10 && remainingHits > 0; radius++)
        for (var offset = -20; offset <= 20 && remainingHits > 0; offset++)
        for (var side = 0; side < 4 && remainingHits > 0; side++)
        {
            var cell = side switch
            {
                0 => new CellCoordinate(origin.X - radius, origin.Z + offset),
                1 => new CellCoordinate(origin.X + radius, origin.Z + offset),
                2 => new CellCoordinate(origin.X + offset, origin.Z - radius),
                _ => new CellCoordinate(origin.X + offset, origin.Z + radius),
            };
            if ((uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height) continue;
            if (TryInspireOccupant(GroundOccupancy, cell, source, ref remainingHits, events) && remainingHits == 0)
                break;
            _ = TryInspireOccupant(AlternateOccupancy, cell, source, ref remainingHits, events);
        }
        return events.Count != 0
            ? events
            : [new InspireEvent(source.Seed.InstanceId, source.Seed.InstanceId, 0, InspireOutcome.NoEligibleTargets)];
    }

    private bool TryInspireOccupant(
        CellOccupancy occupancy,
        CellCoordinate cell,
        SimulatedActor source,
        ref int remainingHits,
        ICollection<InspireEvent> events)
    {
        if (remainingHits == 0 || !occupancy.TryGetOwner(cell, out var targetId) ||
            !actorsById.TryGetValue(targetId, out var target) || target.IsDestroyed ||
            target.Seed.Team != source.Seed.Team || EffectiveDefinition(target).HasImmediateAreaEffect ||
            !TryGetWeapon(target, out _)) return false;
        var countdown = NativeInspireMinimumCountdown + (int)(NextInspireRandom() & NativeInspireCountdownMask);
        target.InspirationTicksRemaining = countdown;
        target.InspirationSourceActorInstanceId = source.Seed.InstanceId;
        events.Add(new(source.Seed.InstanceId, target.Seed.InstanceId, countdown, InspireOutcome.Applied));
        remainingHits--;
        return true;
    }

    private uint NextInspireRandom()
    {
        // dc.exe uses its shared 256-entry random stream. Until that table is
        // recovered, keep the proven low-nibble mask deterministic for replay.
        inspireRandomState = unchecked(inspireRandomState * 214013 + 2531011);
        return inspireRandomState >> 16;
    }

    private MineDeploymentEvent BeginMineDeployment(DeployMineIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source) || source.IsDestroyed ||
            source.Seed.Team < 0 || source.Definition.Code is not ("ENGI" or "SLOM") ||
            source.Definition.ImmediateSpecialCode == 0)
            return new MineDeploymentEvent(intent.EntityInstanceId, 0, 0, source?.Movement.OccupiedCell ?? default, MineDeploymentOutcome.SourceInvalid);
        var target = source.Movement.OccupiedCell;
        if (source.DeployedEntityId is not null)
            return new MineDeploymentEvent(intent.EntityInstanceId, source.Seed.InstanceId, source.DeployedEntityId.Value, target, MineDeploymentOutcome.AlreadyDeployed);
        var mine = entityDefinitions.FirstOrDefault(definition =>
            definition.Id == source.Definition.Id + 2 &&
            definition.Faction == source.Definition.Faction &&
            definition.Code == "HMINE");
        if (mine is null)
            return new MineDeploymentEvent(intent.EntityInstanceId, source.Seed.InstanceId, 0, target, MineDeploymentOutcome.EntityUnresolved);
        if (source.MineDeployTicksRemaining > 0)
            return new MineDeploymentEvent(intent.EntityInstanceId, source.Seed.InstanceId, mine.Id, target, MineDeploymentOutcome.Preparing);

        source.Playback?.Cancel();
        source.Playback = null;
        source.MoveOrder = null;
        source.AttackTargetInstanceId = null;
        source.GroundSpecialAttackTarget = null;
        source.AttackMoveDestination = null;
        source.MineDeployTicksRemaining = NativeImmediateSpecialTicks;
        return new MineDeploymentEvent(intent.EntityInstanceId, source.Seed.InstanceId, mine.Id, target, MineDeploymentOutcome.Preparing);
    }

    private MineDeploymentEvent CompleteMineDeployment(SimulatedActor source)
    {
        var target = source.Movement.OccupiedCell;
        // dc.exe 0x417d50 changes ENGI/SLOM (43/44) to entity ID +2
        // (HMINE 45/46), removes the old grid claim, and inserts the same actor
        // into the dedicated mine grid at its current 8.8 cell. This executes
        // only after state 13's recovered 50-tick timer.
        var mine = entityDefinitions.FirstOrDefault(definition =>
            definition.Id == source.Definition.Id + 2 &&
            definition.Faction == source.Definition.Faction &&
            definition.Code == "HMINE");
        if (mine is null)
            return new MineDeploymentEvent(source.Seed.InstanceId, source.Seed.InstanceId, 0, target, MineDeploymentOutcome.EntityUnresolved);
        if (!MineOccupancy.TryClaim(source.Seed.InstanceId, [target]))
            return new MineDeploymentEvent(source.Seed.InstanceId, source.Seed.InstanceId, mine.Id, target, MineDeploymentOutcome.Occupied);

        var previousOccupancy = source.Definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        previousOccupancy.Release(source.Seed.InstanceId);
        source.Playback?.Cancel();
        source.Playback = null;
        source.MoveOrder = null;
        source.AttackTargetInstanceId = null;
        source.AttackMoveDestination = null;
        source.DeployedEntityId = mine.Id;
        source.MaximumHealth = mine.Health;
        source.Health = Math.Min(source.Health, source.MaximumHealth);
        return new MineDeploymentEvent(source.Seed.InstanceId, source.Seed.InstanceId, mine.Id, target, MineDeploymentOutcome.Deployed);
    }

    private TowerDeploymentEvent DeployTower(DeployTowerIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source) || source.IsDestroyed || source.Seed.Team < 0 ||
            source.Definition.Code is not ("TURR" or "XENO"))
            return new TowerDeploymentEvent(intent.EntityInstanceId, 0, TowerDeploymentOutcome.SourceInvalid);
        if (source.DeployedEntityId is not null)
            return new TowerDeploymentEvent(intent.EntityInstanceId, source.DeployedEntityId.Value, TowerDeploymentOutcome.AlreadyDeployed);

        var deployedCode = source.Definition.Code == "TURR" ? "T" : "XDEPLOY";
        var form = entityDefinitions.FirstOrDefault(definition => definition.Faction == source.Definition.Faction && definition.Code == deployedCode);
        if (form is null)
            return new TowerDeploymentEvent(intent.EntityInstanceId, 0, TowerDeploymentOutcome.EntityUnresolved);

        // The paired gamestat forms turn movement speed from 15 to zero and
        // supply the actual tower weapon slots. Keep the existing actor and
        // ground claim: deployment is a state change, not a second building.
        source.Playback?.Cancel();
        source.Playback = null;
        source.MoveOrder = null;
        source.AttackMoveDestination = null;
        source.DeployedEntityId = form.Id;
        // The deployed record is the authoritative active form for combat
        // stats. Preserve live damage where possible, while preventing a
        // previously mobile tower from exceeding its static form's ceiling.
        source.MaximumHealth = form.Health;
        source.Health = Math.Min(source.Health, source.MaximumHealth);
        return new TowerDeploymentEvent(intent.EntityInstanceId, form.Id, TowerDeploymentOutcome.Deployed);
    }

    private StealDeploymentEvent DeploySteal(DeployStealIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source) || source.IsDestroyed || source.Seed.Team < 0 ||
            source.Definition.Code is not ("SARG" or "PSYC"))
            return new StealDeploymentEvent(intent.EntityInstanceId, 0, StealDeploymentOutcome.SourceInvalid);
        if (source.StealTransitionTicksRemaining > 0)
            return new StealDeploymentEvent(intent.EntityInstanceId, EffectiveDefinition(source).Id, StealDeploymentOutcome.Preparing);
        if (source.DeployedEntityId is not null)
            return new StealDeploymentEvent(intent.EntityInstanceId, source.DeployedEntityId.Value, StealDeploymentOutcome.AlreadyDeployed);
        if (StealingForm(source) is null)
            return new StealDeploymentEvent(intent.EntityInstanceId, 0, StealDeploymentOutcome.EntityUnresolved);
        return BeginStealTransition(source);
    }

    private StealDeploymentEvent RetractSteal(RetractStealIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source) || source.IsDestroyed ||
            source.Definition.Code is not ("SARG" or "PSYC") ||
            EffectiveDefinition(source).Code is not ("SARGSTL" or "PSYCSTL"))
            return new StealDeploymentEvent(intent.EntityInstanceId, source?.DeployedEntityId ?? 0,
                StealDeploymentOutcome.SourceInvalid);
        if (source.StealTransitionTicksRemaining > 0)
            return new StealDeploymentEvent(intent.EntityInstanceId, EffectiveDefinition(source).Id, StealDeploymentOutcome.Preparing);
        return BeginStealTransition(source);
    }

    /// <summary>
    /// State 13 (<c>0x416784</c>) in both directions: the unit stops and the
    /// 50-tick timer starts; the completion (<c>0x417B0C</c>) swaps the type.
    /// </summary>
    private StealDeploymentEvent BeginStealTransition(SimulatedActor source)
    {
        source.Playback?.Cancel();
        source.Playback = null;
        source.MoveOrder = null;
        source.AttackTargetInstanceId = null;
        source.AttackMoveDestination = null;
        source.StealTransitionTicksRemaining = NativeImmediateSpecialTicks;
        return new StealDeploymentEvent(source.Seed.InstanceId, EffectiveDefinition(source).Id, StealDeploymentOutcome.Preparing);
    }

    private EntityDefinition? StealingForm(SimulatedActor source)
    {
        var code = source.Definition.Code == "SARG" ? "SARGSTL" : "PSYCSTL";
        return entityDefinitions.FirstOrDefault(definition => definition.Faction == source.Definition.Faction && definition.Code == code);
    }

    /// <summary>
    /// Runs the steal transition timers, then the stance handler
    /// <c>0x413BC0</c>: a stance keeps its victim only while that actor lives
    /// as a deployed harvester (47/48). Otherwise the stance retracts.
    /// </summary>
    private IReadOnlyList<StealDeploymentEvent> UpdateStealStances()
    {
        var events = new List<StealDeploymentEvent>();
        // The victim search reads this update's visibility, not the snapshot
        // left by the previous update's actors.
        scanVisibility.Clear();
        foreach (var actor in actors.Where(actor => actor.StealTransitionTicksRemaining > 0 ||
                     actor.DeployedEntityId is not null && actor.Definition.Code is "SARG" or "PSYC").ToArray())
        {
            if (actor.IsDestroyed)
            {
                actor.StealTransitionTicksRemaining = 0;
                actor.StealVictimInstanceId = null;
                continue;
            }
            if (actor.StealTransitionTicksRemaining > 0)
            {
                if (--actor.StealTransitionTicksRemaining == 0) events.Add(CompleteStealTransition(actor));
                continue;
            }
            if (actor.StealVictimInstanceId is { } victimId && actorsById.TryGetValue(victimId, out var victim) &&
                !victim.IsDestroyed && EffectiveDefinition(victim).Code is "EDPLY" or "SDPL")
                continue;
            actor.StealVictimInstanceId = null;
            BeginStealTransition(actor);
            events.Add(new StealDeploymentEvent(actor.Seed.InstanceId, EffectiveDefinition(actor).Id, StealDeploymentOutcome.VictimLost));
        }
        return events;
    }

    /// <summary>
    /// <c>0x417D0D</c> swaps SARG/PSYC (4/12) with SARGSTL/PSYCSTL (77/78). A
    /// new stance then searches for its victim (<c>0x417944</c>). It links the
    /// victim unless another stance already drains it (<c>0x417E91</c>: the
    /// first thief keeps the harvester). Without a victim it retracts at once.
    /// </summary>
    private StealDeploymentEvent CompleteStealTransition(SimulatedActor actor)
    {
        if (actor.DeployedEntityId is not null)
        {
            actor.DeployedEntityId = null;
            actor.StealVictimInstanceId = null;
            actor.MaximumHealth = actor.Definition.Health;
            actor.Health = Math.Min(actor.Health, actor.MaximumHealth);
            return new StealDeploymentEvent(actor.Seed.InstanceId, actor.Definition.Id, StealDeploymentOutcome.Retracted);
        }
        if (StealingForm(actor) is not { } form)
            return new StealDeploymentEvent(actor.Seed.InstanceId, 0, StealDeploymentOutcome.EntityUnresolved);
        actor.DeployedEntityId = form.Id;
        actor.MaximumHealth = form.Health;
        actor.Health = Math.Min(actor.Health, actor.MaximumHealth);
        var victim = FindStealVictim(actor);
        if (victim is null || victim.ThiefInstanceId is not null)
        {
            BeginStealTransition(actor);
            return new StealDeploymentEvent(actor.Seed.InstanceId, form.Id,
                victim is null ? StealDeploymentOutcome.NoVictim : StealDeploymentOutcome.VictimTaken);
        }
        actor.StealVictimInstanceId = victim.Seed.InstanceId;
        victim.ThiefInstanceId = actor.Seed.InstanceId;
        return new StealDeploymentEvent(actor.Seed.InstanceId, form.Id, StealDeploymentOutcome.Deployed);
    }

    /// <summary>
    /// <c>0x417944</c>: for rings 0 to 11 around the stance's cell, and for
    /// offsets -22 to 22, it probes the columns x - ring and x + ring at
    /// z + offset, then the rows z - ring and z + ring at x + offset. The area
    /// is a cross: |dx| &lt;= 11 with |dz| &lt;= 22, or |dz| &lt;= 11 with
    /// |dx| &lt;= 22. The first ground-grid actor of another team (allies
    /// included) that is a live deployed harvester whose cell the stance's
    /// team sees wins. (The hidden-entity check on gamestat value 15 never
    /// applies to harvesters.)
    /// </summary>
    private SimulatedActor? FindStealVictim(SimulatedActor thief)
    {
        var origin = thief.Movement.OccupiedCell;
        for (var ring = 0; ring <= NativeStealSearchRings; ring++)
        for (var offset = -2 * NativeStealSearchRings; offset <= 2 * NativeStealSearchRings; offset++)
        for (var side = 0; side < 4; side++)
        {
            var (x, z) = side switch
            {
                0 => (origin.X - ring, origin.Z + offset),
                1 => (origin.X + ring, origin.Z + offset),
                2 => (origin.X + offset, origin.Z - ring),
                _ => (origin.X + offset, origin.Z + ring),
            };
            if (x < 0 || z < 0 || x >= path.Width || z >= path.Height) continue;
            if (!GroundOccupancy.TryGetOwner(new CellCoordinate(x, z), out var owner) ||
                !actorsById.TryGetValue(owner, out var candidate)) continue;
            if (candidate.IsDestroyed || candidate.Seed.Team == thief.Seed.Team ||
                EffectiveDefinition(candidate).Code is not ("EDPLY" or "SDPL")) continue;
            if (!IsCellVisibleForScan(thief.Seed.Team, candidate.Movement.OccupiedCell)) continue;
            return candidate;
        }
        return null;
    }

    private GroundSpecialAttackEvent IssueGroundSpecialAttack(GroundSpecialAttackIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var actor))
            return new(intent.EntityInstanceId, intent.TargetCell, null, GroundSpecialAttackOutcome.SourceMissing);
        if (actor.IsDestroyed)
            return new(intent.EntityInstanceId, intent.TargetCell, null, GroundSpecialAttackOutcome.SourceDestroyed);
        var definition = EffectiveDefinition(actor);
        if (!definition.HasGroundSpecialAttack || !UnitSecondaryCommandCatalog.TryGet(definition, out var command) || command.CandidateEffectWeaponId != definition.GroundSpecialWeaponId)
            return new(intent.EntityInstanceId, intent.TargetCell, null, GroundSpecialAttackOutcome.Unsupported);
        if ((uint)intent.TargetCell.X >= (uint)path.Width || (uint)intent.TargetCell.Z >= (uint)path.Height)
            return new(intent.EntityInstanceId, intent.TargetCell, definition.GroundSpecialWeaponId, GroundSpecialAttackOutcome.InvalidTarget);
        if (command.RequiredResearchItemId is { } researchId &&
            (!teamEconomies.TryGetValue(actor.Seed.Team, out var economy) || !economy.CompletedItems.Contains(researchId)))
            return new(intent.EntityInstanceId, intent.TargetCell, definition.GroundSpecialWeaponId, GroundSpecialAttackOutcome.ResearchRequired);
        if (weaponCatalog?.TryGet(definition.GroundSpecialWeaponId, out _) != true)
            return new(intent.EntityInstanceId, intent.TargetCell, definition.GroundSpecialWeaponId, GroundSpecialAttackOutcome.WeaponUnavailable);

        actor.AttackTargetInstanceId = null;
        actor.AttackMoveDestination = null;
        actor.GroundSpecialAttackTarget = intent.TargetCell;
        actor.Playback?.Cancel();
        actor.Playback = null;
        actor.MoveOrder = null;
        return new(intent.EntityInstanceId, intent.TargetCell, definition.GroundSpecialWeaponId, GroundSpecialAttackOutcome.Accepted);
    }

    /// <summary>
    /// Executes the BEON/ZISP contextual area scan recovered at 0x413c20.
    /// Native code scans both ordinary occupancy layers in expanding
    /// Chebyshev squares through radius 7, admits the healer's exact team byte,
    /// and applies the class-7 amount recovered at 0x413e21.
    /// </summary>
    private IReadOnlyList<HealEvent> IssueAreaHeal(HealAreaIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var source))
            return [new HealEvent(intent.EntityInstanceId, intent.EntityInstanceId, 0, HealOutcome.SourceMissing)];
        if (source.IsDestroyed)
            return [new HealEvent(intent.EntityInstanceId, intent.EntityInstanceId, 0, HealOutcome.SourceDestroyed)];
        if (source.Definition.Code is not ("BEON" or "ZISP"))
            return [new HealEvent(intent.EntityInstanceId, intent.EntityInstanceId, 0, HealOutcome.SourceNotHealer)];
        if (source.AbilityCharge < NativeHealMinimumCharge)
            return [new HealEvent(intent.EntityInstanceId, intent.EntityInstanceId, 0, HealOutcome.InsufficientCharge)];
        if (damageMatrix is null)
            return [new HealEvent(intent.EntityInstanceId, intent.EntityInstanceId, 0, HealOutcome.MatrixUnavailable)];

        var origin = source.Movement.OccupiedCell;
        // 0x413C8B..0x414066 scans every square from radius 0 through 7,
        // visiting ground then alternate occupancy at each X/Z. The first
        // successful heal clears source +0x0a, so the following loop guard
        // aborts instead of healing every damaged ally in the area.
        for (var radius = 0; radius <= 7; radius++)
        for (var x = origin.X - radius; x <= origin.X + radius; x++)
        for (var z = origin.Z - radius; z <= origin.Z + radius; z++)
        foreach (var occupancy in new[] { GroundOccupancy, AlternateOccupancy })
        {
            var cell = new CellCoordinate(x, z);
            if (!occupancy.TryGetOwner(cell, out var targetId) ||
                !actorsById.TryGetValue(targetId, out var target) || target.IsDestroyed ||
                target.Seed.Team != source.Seed.Team || target.Health >= target.MaximumHealth) continue;
            var resistance = damageMatrix[7, EffectiveDefinition(target).ArmorClass];
            var amount = Math.Min(target.MaximumHealth - target.Health, checked(36 * resistance / 256));
            if (amount <= 0) continue;
            target.Health += amount;
            source.AbilityCharge = 0;
            AddPlayerStatistic(target.Seed.Team, 9, amount);
            return [new HealEvent(source.Seed.InstanceId, target.Seed.InstanceId, amount, HealOutcome.Healed)];
        }
        return [new HealEvent(source.Seed.InstanceId, source.Seed.InstanceId, 0, HealOutcome.NoEligibleTargets)];
    }

    private SimulatedActor? FindMineTriggerTarget(SimulatedActor source, WeaponDefinition weapon)
    {
        SimulatedActor? best = null;
        var bestScore = -1;
        var rangeRaw = (long)Math.Max(0, weapon.Range) * FixedPointPosition.One;
        foreach (var offset in NativeMineTargetOffsets)
        {
            var cell = new CellCoordinate(
                source.Movement.OccupiedCell.X + offset.X,
                source.Movement.OccupiedCell.Z + offset.Z);
            if ((uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height) continue;
            foreach (var occupancy in new[] { GroundOccupancy, AlternateOccupancy, MineOccupancy })
            {
                if (!occupancy.TryGetOwner(cell, out var candidateId) || candidateId == source.Seed.InstanceId ||
                    !actorsById.TryGetValue(candidateId, out var candidate) || candidate.IsDestroyed ||
                    candidate.Seed.Team is 8 or 9 ||
                    !TeamRelations.IsHostile(source.Seed.Team, candidate.Seed.Team) ||
                    DistanceSquared(source.Movement.VisualPosition, candidate.Movement.VisualPosition) > rangeRaw * rangeRaw)
                    continue;
                // Target search 0x435A06 rejects a weapon/armor pairing whose
                // mbullet entry is zero before assigning any priority.
                if (damageMatrix is not null &&
                    damageMatrix[weapon.WeaponClass, EffectiveDefinition(candidate).ArmorClass] == 0)
                    continue;

                // 0x435A4E starts an armed candidate at 150 and an unarmed one
                // at 50. For a boom weapon, 0x435A97 then inspects the ordinary
                // ground layer in the candidate's 3x3 neighborhood: hostile
                // occupants add 10 and cooperative occupants subtract 15.
                var score = TryGetWeapon(candidate, out _) ? 150 : 50;
                if (weapon.HasAreaEffect)
                {
                    for (var z = cell.Z - 1; z <= cell.Z + 1; z++)
                    for (var x = cell.X - 1; x <= cell.X + 1; x++)
                    {
                        var nearbyCell = new CellCoordinate(x, z);
                        if (!GroundOccupancy.TryGetOwner(nearbyCell, out var nearbyId) ||
                            !actorsById.TryGetValue(nearbyId, out var nearby) || nearby.IsDestroyed) continue;
                        score += TeamRelations.IsHostile(source.Seed.Team, nearby.Seed.Team) ? 10 : -15;
                    }
                }
                // Native replaces its result only for a strictly greater score,
                // preserving the fixed offset/layer order on ties.
                if (score <= bestScore) continue;
                best = candidate;
                bestScore = score;
            }
        }
        return best;
    }

    private void UpdateGroundSpecialAttack(SimulatedActor actor, CellCoordinate target, ICollection<WeaponFireEvent> fired)
    {
        var definition = EffectiveDefinition(actor);
        if (weaponCatalog?.TryGet(definition.GroundSpecialWeaponId, out var weapon) != true)
        {
            actor.GroundSpecialAttackTarget = null;
            return;
        }
        var destination = FixedPointPosition.AtCellCenter(target);
        actor.Facing.FaceTowards(actor.Movement.VisualPosition, destination);
        var range = (long)weapon.Range * FixedPointPosition.One;
        if (DistanceSquared(actor.Movement.VisualPosition, destination) < range * range)
        {
            actor.Playback?.Cancel();
            actor.Playback = null;
            actor.MoveOrder = null;
            if (actor.CooldownTicks > 0 || actor.Facing.Current != actor.Facing.Target) return;
            SpawnGroundProjectile(actor, target, weapon, fired);
            actor.GroundSpecialAttackTarget = null;
            return;
        }
        if (definition.MovementSpeed <= 0 || actor.Playback is not null || actor.MoveOrder is not null) return;
        actor.MoveOrder = new ActiveMoveOrder(target);
        _ = StartSegment(actor);
    }
}
