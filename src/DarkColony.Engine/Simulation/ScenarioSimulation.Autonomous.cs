using DarkColony.Engine.Commands;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>Neutral team-9 spawn groups and their wandering.</summary>
public sealed partial class ScenarioSimulation
{
    private sealed class AutonomousGroupRuntime(AutonomousSpawnGroup definition, IEnumerable<int> memberIds)
    {
        public AutonomousSpawnGroup Definition { get; } = definition;
        public List<int> MemberIds { get; } = memberIds.ToList();
    }

    private IReadOnlyList<AutonomousWanderEvent> UpdateAutonomousActors()
    {
        // Called as 0x43FEAC from the world update's every-eighth-update block.
        if (autonomousGroups.Count == 0) return [];
        var events = new List<AutonomousWanderEvent>();
        foreach (var group in autonomousGroups.OrderBy(group => group.Definition.GroupId))
        {
            foreach (var memberId in group.MemberIds.ToArray())
            {
                if (!actorsById.TryGetValue(memberId, out var actor) || actor.IsDestroyed || actor.Seed.Team != AutonomousSpawnSeeder.InternalNeutralTeam) continue;
                if ((NextAutonomousRandom() & 3) != 0) continue;
                CellCoordinate? target = null;
                // Native code retries out-of-map candidates. A bounded loop is
                // needed for a malformed/tiny map while preserving its formula.
                for (var attempt = 0; attempt < 32; attempt++)
                {
                    var candidate = new CellCoordinate(
                        group.Definition.Origin.X + (int)(NextAutonomousRandom() & 15) - 7,
                        group.Definition.Origin.Z + (int)(NextAutonomousRandom() & 15) - 7);
                    if ((uint)candidate.X < (uint)path.Width && (uint)candidate.Z < (uint)path.Height)
                    {
                        target = candidate;
                        break;
                    }
                }
                if (target is null) continue;
                actor.MoveOrder = new ActiveMoveOrder(target.Value);
                var result = StartSegment(actor);
                events.Add(new AutonomousWanderEvent(memberId, group.Definition.GroupId, target.Value, result.StepCount > 0));
            }

            if ((NextAutonomousRandom() & 0x7f) == 0) TryRespawnAutonomousMember(group);
        }
        return events;
    }

    private uint NextAutonomousRandom()
    {
        autonomousRandomState = unchecked(autonomousRandomState * 214013 + 2531011);
        return autonomousRandomState >> 16;
    }

    private void TryRespawnAutonomousMember(AutonomousGroupRuntime group)
    {
        var live = group.MemberIds.Where(id => actorsById.TryGetValue(id, out var actor) && !actor.IsDestroyed).ToArray();
        if (live.Length >= group.Definition.DesiredPopulation) return;
        group.MemberIds.RemoveAll(id => !actorsById.TryGetValue(id, out var actor) || actor.IsDestroyed);
        var definition = EntityDefinitionFor(group.Definition.EntityId);
        var validator = new SpawnCellValidator(path, GroundOccupancy, AlternateOccupancy);
        var cell = validator.FindNearestValid(group.Definition.Origin, definition.MovementClass);
        if (cell is null) return;
        var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        var instanceId = nextActorInstanceId++;
        if (!occupancy.TryClaim(instanceId, [cell.Value])) return;
        var seed = new WorldEntity(instanceId, group.Definition.EntityId, AutonomousSpawnSeeder.InternalNeutralTeam, cell.Value,
            FixedPointPosition.AtCellCenter(cell.Value), definition.Health, group.Definition.ScenarioFlag);
        actors.Add(new SimulatedActor(seed, definition));
        actorsById.Add(instanceId, actors[^1]);
        group.MemberIds.Add(instanceId);
    }
}
