using DarkColony.Engine.Commands;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>Petra-7 harvester deployment, vent income, and theft.</summary>
public sealed partial class ScenarioSimulation
{
    private HarvesterDeploymentEvent RequestHarvesterDeployment(int entityInstanceId, int ventId)
    {
        if (!actorsById.TryGetValue(entityInstanceId, out var actor) || actor.IsDestroyed ||
            actor.Seed.Team < 0 || actor.Definition.Code is not ("EXPL" or "SLUG") ||
            (uint)ventId >= (uint)PetraVents.Count)
            return new HarvesterDeploymentEvent(entityInstanceId, ventId, HarvesterDeploymentOutcome.SourceInvalid);
        var vent = PetraVents[ventId];
        var cell = actor.Movement.OccupiedCell;
        if (vent.HarvesterInstanceId is { } other && other != entityInstanceId ||
            vent.PendingHarvesterInstanceId is { } pending && pending != entityInstanceId)
            return new HarvesterDeploymentEvent(entityInstanceId, ventId, HarvesterDeploymentOutcome.VentUnavailable);
        if (cell == vent.Position)
            return BeginHarvesterAttachment(actor, vent);

        var occupancy = actor.Definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        if (occupancy.TryGetOwner(vent.Position, out var occupant) && occupant != entityInstanceId)
            return new HarvesterDeploymentEvent(entityInstanceId, ventId, HarvesterDeploymentOutcome.NoApproach);
        DetachHarvester(actor);
        actor.AttackTargetInstanceId = null;
        actor.GroundSpecialAttackTarget = null;
        actor.HarvestVentId = ventId;
        actor.MoveOrder = new ActiveMoveOrder(vent.Position);
        var route = StartSegment(actor);
        if (route.StepCount == 0 && actor.Movement.OccupiedCell != vent.Position)
        {
            actor.HarvestVentId = null;
            actor.MoveOrder = null;
            return new HarvesterDeploymentEvent(entityInstanceId, ventId, HarvesterDeploymentOutcome.NoApproach);
        }
        return new HarvesterDeploymentEvent(entityInstanceId, ventId, HarvesterDeploymentOutcome.EnRoute);
    }

    private void UpdateHarvesterDeploymentOrders(ICollection<HarvesterDeploymentEvent> events)
    {
        foreach (var actor in Actors.Where(actor => !actor.IsDestroyed && actor.HarvestVentId is not null).OrderBy(actor => actor.Seed.InstanceId))
        {
            var ventId = actor.HarvestVentId!.Value;
            if ((uint)ventId >= (uint)PetraVents.Count)
            {
                actor.HarvestVentId = null;
                continue;
            }
            var vent = PetraVents[ventId];
            if (vent.HarvesterInstanceId == actor.Seed.InstanceId) continue;
            if (vent.HarvesterInstanceId is { } other && other != actor.Seed.InstanceId)
            {
                actor.HarvestVentId = null;
                continue;
            }
            if (actor.Movement.OccupiedCell != vent.Position) continue;
            if (vent.PendingHarvesterInstanceId is null)
            {
                events.Add(BeginHarvesterAttachment(actor, vent));
                continue;
            }
            if (vent.PendingHarvesterInstanceId != actor.Seed.InstanceId)
            {
                actor.HarvestVentId = null;
                continue;
            }
            // A request issued while already on the vent stores the native
            // 0x32 timer this step; countdown begins on the following step.
            if (events.Any(entry => entry.EntityInstanceId == actor.Seed.InstanceId &&
                    entry.VentId == vent.Id && entry.Outcome == HarvesterDeploymentOutcome.Preparing))
                continue;
            if (vent.AttachTicksRemaining > 0) vent.AttachTicksRemaining--;
            if (vent.AttachTicksRemaining == 0) events.Add(AttachHarvester(actor, vent));
        }
    }

    /// <summary>
    /// The original <c>dc.exe</c> begins the EXPL/SLUG deployment sequence when
    /// an ordinary move command finishes on an unclaimed vent.  This is kept at
    /// the route-completion boundary so crossing a vent, or stopping beside it,
    /// remains an ordinary movement command.
    /// </summary>
    private bool TryBeginHarvesterAttachmentOnVentArrival(
        SimulatedActor actor,
        ICollection<HarvesterDeploymentEvent> events)
    {
        if (actor.IsDestroyed || actor.HarvestVentId is not null || actor.Seed.Team < 0 ||
            actor.Definition.Code is not ("EXPL" or "SLUG"))
            return false;

        var vent = PetraVents.FirstOrDefault(candidate => candidate.Position == actor.Movement.OccupiedCell);
        if (vent is null ||
            vent.HarvesterInstanceId is not null ||
            vent.PendingHarvesterInstanceId is not null)
            return false;

        events.Add(BeginHarvesterAttachment(actor, vent));
        return true;
    }

    private static HarvesterDeploymentEvent BeginHarvesterAttachment(SimulatedActor actor, PetraVent vent)
    {
        StopAfterCurrentStep(actor);
        actor.MoveOrder = null;
        actor.AttackTargetInstanceId = null;
        actor.AttackMoveDestination = null;
        actor.HarvestVentId = vent.Id;
        vent.PendingHarvesterInstanceId = actor.Seed.InstanceId;
        vent.AttachTicksRemaining = NativeHarvesterAttachTicks;
        return new HarvesterDeploymentEvent(actor.Seed.InstanceId, vent.Id, HarvesterDeploymentOutcome.Preparing);
    }

    private HarvesterDeploymentEvent AttachHarvester(SimulatedActor actor, PetraVent vent)
    {
        DetachHarvester(actor);
        actor.Playback?.Cancel();
        actor.Playback = null;
        actor.MoveOrder = null;
        actor.AttackTargetInstanceId = null;
        actor.AttackMoveDestination = null;
        actor.HarvestVentId = vent.Id;
        actor.DeployedEntityId = FindHarvesterDeployedForm(actor);
        if (actor.DeployedEntityId is { } deployedEntityId)
        {
            actor.MaximumHealth = EntityDefinitionFor(deployedEntityId).Health;
            actor.Health = Math.Min(actor.Health, actor.MaximumHealth);
        }
        vent.PendingHarvesterInstanceId = null;
        vent.AttachTicksRemaining = 0;
        vent.HarvesterInstanceId = actor.Seed.InstanceId;
        return new HarvesterDeploymentEvent(actor.Seed.InstanceId, vent.Id, HarvesterDeploymentOutcome.Attached);
    }

    private HarvesterDeploymentEvent RetractHarvester(RetractHarvesterIntent intent)
    {
        if (!actorsById.TryGetValue(intent.EntityInstanceId, out var actor) || actor.IsDestroyed ||
            actor.Definition.Code is not ("EXPL" or "SLUG") || actor.HarvestVentId is not { } ventId ||
            EffectiveDefinition(actor).Code is not ("EDPLY" or "SDPL"))
            return new HarvesterDeploymentEvent(intent.EntityInstanceId, actor?.HarvestVentId ?? -1,
                HarvesterDeploymentOutcome.SourceInvalid);
        // noundeploy (world +0x948): the mining command ignores a pending
        // state-13 request (0x4137CF), and state 13 refuses the deployed
        // forms 0x2F/0x30 (0x4167EF).
        if (NoUndeploy) return new HarvesterDeploymentEvent(actor.Seed.InstanceId, ventId, HarvesterDeploymentOutcome.UndeployLocked);

        // dc.exe 0x417c40 maps EDPLY 47 -> EXPL 6 and SDPL 48 -> SLUG 14.
        // The actor and its exact-cell ground claim survive the form change.
        DetachHarvester(actor);
        actor.MaximumHealth = actor.Definition.Health;
        actor.Health = Math.Min(actor.Health, actor.MaximumHealth);
        return new HarvesterDeploymentEvent(actor.Seed.InstanceId, ventId, HarvesterDeploymentOutcome.Retracted);
    }

    private int? FindHarvesterDeployedForm(SimulatedActor actor)
    {
        var code = actor.Definition.Code switch
        {
            "EXPL" => "EDPLY",
            "SLUG" => "SDPL",
            _ => null,
        };
        if (code is null) return null;
        var form = entityDefinitions.FirstOrDefault(definition =>
            definition.Faction == actor.Definition.Faction && definition.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        return form?.Id;
    }

    private void DetachHarvester(SimulatedActor actor)
    {
        if (actor.Definition.Code is not ("EXPL" or "SLUG")) return;
        foreach (var vent in PetraVents.Where(vent =>
                     vent.HarvesterInstanceId == actor.Seed.InstanceId ||
                     vent.PendingHarvesterInstanceId == actor.Seed.InstanceId))
        {
            vent.HarvesterInstanceId = null;
            vent.PendingHarvesterInstanceId = null;
            vent.AttachTicksRemaining = 0;
        }
        actor.HarvestVentId = null;
        actor.DeployedEntityId = null;
        actor.ThiefInstanceId = null;
    }

    /// <summary>
    /// Engine checks may inject a non-native pulse length; passive and vent
    /// income then share this one counter.
    /// </summary>
    private bool SyntheticPetraPulse()
    {
        if (++petraPulseTicks < petraFlowRules.TicksPerPulse) return false;
        petraPulseTicks = 0;
        return true;
    }

    /// <summary>Passive income (world update <c>0x419B31</c>, <c>world + 0x94C &amp; 15</c>).</summary>
    private List<P7IncomeEvent> ApplyPassiveIncome(bool pulse)
    {
        var income = new List<P7IncomeEvent>();
        if (!pulse) return income;
        foreach (var (teamId, economy) in teamEconomies.OrderBy(pair => pair.Key))
        {
            if (petraFlowRules.PassiveP7PerPulse > 0)
            {
                economy.AddP7(petraFlowRules.PassiveP7PerPulse);
                income.Add(new P7IncomeEvent(teamId, petraFlowRules.PassiveP7PerPulse, null));
            }
            // World update 0x419B2E: every 16 ticks each player whose slot-0
            // headquarters stands earns its passive rate (default 3).
            if (citiesDeclared && teamId is >= 0 and < 8 && CanEarnP7(teamId) && passiveRates[teamId] > 0)
            {
                economy.AddP7(passiveRates[teamId]);
                RecordP7Earned(teamId, passiveRates[teamId]);
                income.Add(new P7IncomeEvent(teamId, passiveRates[teamId], null));
            }
        }
        return income;
    }

    /// <summary>
    /// Vent income: the producer <c>0x4139D7</c> pays on
    /// <c>world + 0x530 &amp; 15</c>, the day/night phase counter, so the pulse
    /// phase shifts at every day/night change.
    /// </summary>
    private List<P7IncomeEvent> ApplyVentIncome(bool pulse)
    {
        LastP7Thefts = [];
        var income = new List<P7IncomeEvent>();
        if (!pulse) return income;
        var thefts = new List<P7TheftEvent>();
        foreach (var vent in PetraVents.OrderBy(vent => vent.Id))
        {
            if (vent.HarvesterInstanceId is not { } harvesterId || !actorsById.TryGetValue(harvesterId, out var harvester) || harvester.IsDestroyed)
            {
                vent.HarvesterInstanceId = null;
                continue;
            }
            var cell = harvester.Movement.OccupiedCell;
            if (Math.Abs(cell.X - vent.Position.X) > 1 || Math.Abs(cell.Z - vent.Position.Z) > 1)
            {
                vent.HarvesterInstanceId = null;
                continue;
            }
            // 0x413826 tests the reservoir with a strict `remaining - rate > 0`
            // before the pulse. 0x413A06: the vent's +0x32 rate; a computer
            // player's +0x19B8 8.8 multiplier (session percentage, not
            // decoded: x1) would apply here.
            var attachedIncome = petraFlowRules.UseVentRates ? vent.Rate : petraFlowRules.EffectiveAttachedP7;
            if (!petraFlowRules.CanCreditReservoir(vent.RemainingReservoir, attachedIncome)) continue;
            // 0x413BA9 drains the full amount whether or not anyone is paid.
            vent.RemainingReservoir -= attachedIncome;
            var paid = attachedIncome;
            if (harvester.ThiefInstanceId is { } thiefId)
            {
                // 0x413A30: a live SARGSTL/PSYCSTL halves the payout and takes
                // the other half (an odd unit is lost). Any other state of the
                // linked actor clears the link (0x413B0D).
                if (actorsById.TryGetValue(thiefId, out var thief) && !thief.IsDestroyed &&
                    EffectiveDefinition(thief).Code is "SARGSTL" or "PSYCSTL")
                {
                    paid = attachedIncome / 2;
                    if (CanEarnP7(thief.Seed.Team) && teamEconomies.TryGetValue(thief.Seed.Team, out var thiefEconomy))
                    {
                        thiefEconomy.AddP7(paid);
                        RecordP7Earned(thief.Seed.Team, paid);
                        income.Add(new P7IncomeEvent(thief.Seed.Team, paid, vent.Id));
                        thefts.Add(new P7TheftEvent(thief.Seed.InstanceId, harvester.Seed.InstanceId,
                            thief.Seed.Team, harvester.Seed.Team, paid, vent.Id));
                    }
                }
                else harvester.ThiefInstanceId = null;
            }
            // 0x413B6A: the owner is paid only while its headquarters stands.
            if (!teamEconomies.TryGetValue(harvester.Seed.Team, out var economy) || !CanEarnP7(harvester.Seed.Team)) continue;
            economy.AddP7(paid);
            RecordP7Earned(harvester.Seed.Team, paid);
            RecordHarvestPulse(harvester.Seed.Team);
            if (paid > 0) income.Add(new P7IncomeEvent(harvester.Seed.Team, paid, vent.Id));
        }
        LastP7Thefts = thefts;
        return income;
    }
}
