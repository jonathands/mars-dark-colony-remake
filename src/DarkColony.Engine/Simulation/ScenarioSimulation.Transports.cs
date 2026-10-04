using DarkColony.Engine.Commands;
using DarkColony.Engine.Combat;
using DarkColony.Engine.Data;
using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Movement;
using DarkColony.Engine.Scenario;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>Battlefield drop-ship and saucer transports.</summary>
public sealed partial class ScenarioSimulation
{
    private bool ResolveBattlefieldTransportImpact(
        ProjectileState projectile,
        WeaponDefinition weapon,
        CellCoordinate center,
        ICollection<BattlefieldTransportEvent> events)
    {
        if (weapon.ProjectileMode is < 5 or > 10) return false;
        if (!actorsById.TryGetValue(projectile.SourceActorInstanceId, out var source) || source.IsDestroyed) return true;

        if (weapon.ProjectileMode <= 7)
        {
            // dc.exe 0x441F29 builds two five-byte payload arrays before
            // 0x418F4C creates entity 92. Mode 5 carries 2x entity 0; mode 6
            // falls through and adds entity 2; mode 7 also adds entity 3.
            var payload = new List<int> { 0, 0 };
            if (weapon.ProjectileMode >= 6) payload.Add(2);
            if (weapon.ProjectileMode >= 7) payload.Add(3);
            StartBattlefieldTransport(source.Seed.InstanceId, source.Seed.Team, 92, center, payload, [], events);
            return true;
        }

        // Modes 8/9/10 pass radii 4/6/8 to 0x416F54, cap the scan at nine,
        // reject cooperative/unarmed/commander actors, then call 0x418F4C in
        // groups of at most three. Each call creates entity 93.
        var radius = (weapon.ProjectileMode - 6) * 2;
        var abductees = Actors
            .Where(candidate => !candidate.IsDestroyed && candidate.Seed.InstanceId != source.Seed.InstanceId)
            .Where(candidate => TeamRelations.IsHostile(source.Seed.Team, candidate.Seed.Team))
            .Where(candidate => !EffectiveDefinition(candidate).HasImmediateAreaEffect && TryGetWeapon(candidate, out _))
            .Select(candidate => new
            {
                Actor = candidate,
                Radius = Math.Max(Math.Abs(candidate.Movement.OccupiedCell.X - center.X), Math.Abs(candidate.Movement.OccupiedCell.Z - center.Z)),
            })
            .Where(candidate => candidate.Radius <= radius)
            .OrderBy(candidate => candidate.Radius)
            .ThenBy(candidate => candidate.Actor.Movement.OccupiedCell.Z)
            .ThenBy(candidate => candidate.Actor.Movement.OccupiedCell.X)
            .ThenBy(candidate => candidate.Actor.Seed.InstanceId)
            .Take(9)
            .Select(candidate => candidate.Actor)
            .ToArray();
        foreach (var group in abductees.Chunk(3))
        {
            var ids = group.Select(candidate => candidate.Seed.InstanceId).ToArray();
            StartBattlefieldTransport(source.Seed.InstanceId, source.Seed.Team, 93, center, [], ids, events);
        }
        return true;
    }

    /// <summary>
    /// <c>0x418F4C</c>. The transport is DROP (92), or SAUC (93) when the team
    /// plays race 1 (<c>0x41903B</c>). It starts one cell off the target cell
    /// center on each axis, one shared-stream draw per axis: bit 0 picks -1 or
    /// +1 (<c>0x41906F</c>). Its units belong to <paramref name="teamId"/>.
    /// </summary>
    private void StartBattlefieldTransport(
        int sourceActorInstanceId,
        int teamId,
        int transportEntityId,
        CellCoordinate target,
        IReadOnlyList<int> reinforcementEntityIds,
        IReadOnlyList<int> abducteeInstanceIds,
        ICollection<BattlefieldTransportEvent> events,
        int? corpseInstanceId = null)
    {
        var xOffset = (NextNativeRandom() & 1) == 0 ? -FixedPointPosition.One : FixedPointPosition.One;
        var zOffset = (NextNativeRandom() & 1) == 0 ? -FixedPointPosition.One : FixedPointPosition.One;
        var position = FixedPointPosition.AtCellCenter(target).AddRaw(xOffset, zOffset);
        var baseHeight = transportEntityId == 93 ? NativeSaucerBaseHeightRaw : NativeDropShipBaseHeightRaw;
        var transportDefinition = EntityDefinitionFor(transportEntityId);
        var state = new BattlefieldTransportState(nextTransportInstanceId++, sourceActorInstanceId,
            transportEntityId, teamId, target, position, baseHeight,
            transportDefinition.InitialFacing,
            reinforcementEntityIds, abducteeInstanceIds)
        {
            CorpseInstanceId = corpseInstanceId,
        };
        battlefieldTransports.Add(state);
        events.Add(new BattlefieldTransportEvent(BattlefieldTransportEventKind.Started, state.InstanceId,
            sourceActorInstanceId, transportEntityId, target, [], []));
    }

    /// <summary>
    /// Mission action <c>reinforce t x z (type count)x5</c> (<c>0x43E1A4</c>):
    /// a transport of team t's race flies the cargo in. It unloads one unit per
    /// update, then leaves.
    /// </summary>
    private void StartMissionTransport(IReadOnlyList<int> values, ICollection<BattlefieldTransportEvent> events)
    {
        var team = values[0];
        var cargo = new List<int>();
        for (var pair = 0; pair < 5; pair++)
            for (var unit = 0; unit < values[4 + pair * 2]; unit++) cargo.Add(values[3 + pair * 2]);
        var transportEntityId = teamRaces.GetValueOrDefault(team) == 1 ? 93 : 92;
        StartBattlefieldTransport(-1, team, transportEntityId, new CellCoordinate(values[1], values[2]), cargo, [], events);
    }

    private void UpdateBattlefieldTransports(ICollection<BattlefieldTransportEvent> events)
    {
        for (var index = battlefieldTransports.Count - 1; index >= 0; index--)
        {
            var transport = battlefieldTransports[index];
            if (transport.PursuitTarget is { } pursuitTarget)
            {
                if (transport.IsTurning)
                {
                    var turnSpeed = Math.Max(1, EntityDefinitionFor(transport.TransportEntityId).TurnSpeed);
                    if (!transport.Facing.Step(turnSpeed)) transport.IsTurning = false;
                    // Command 4 consumes this actor update even when it reaches
                    // the requested bearing and removes itself.
                    continue;
                }
                if (transport.HorizontalExecutionsRemaining > 0)
                {
                    transport.Position = transport.Position.AddRaw(
                        transport.HorizontalVelocityXRaw, transport.HorizontalVelocityZRaw);
                    transport.HorizontalExecutionsRemaining--;
                    continue;
                }

                // Command 5 removes itself at count zero. State 21 resumes on
                // the next update and copies its saved exact target position.
                transport.Position = pursuitTarget;
                transport.PursuitTarget = null;
                continue;
            }
            if (transport.Phase == BattlefieldTransportPhase.Descending)
            {
                if (transport.FlightCounter > 0)
                {
                    // State-22 handler 0x4183B8 uses the pre-decrement counter:
                    // height = base + 6*t*t/2.
                    var counter = transport.FlightCounter;
                    transport.HeightRaw = transport.BaseHeightRaw + 3 * counter * counter;
                    transport.FlightCounter--;
                    continue;
                }
                transport.Position = FixedPointPosition.AtCellCenter(transport.Target);
                transport.HeightRaw = transport.BaseHeightRaw;
                transport.Phase = BattlefieldTransportPhase.Delivering;
                continue;
            }

            if (transport.Phase == BattlefieldTransportPhase.Delivering)
            {
                // State-21 phase zero only advances its phase word. Payload
                // processing starts on the following actor update.
                if (!transport.DeliveryInitialized)
                {
                    transport.DeliveryInitialized = true;
                    continue;
                }

                if (transport.CorpseInstanceId is { } corpseId)
                {
                    // 0x418CAA: a first word of zero (no units) names a dying
                    // body in the next two low bytes. Its counter is set to
                    // 150, and the transport takes off. Transports update
                    // before the actors, so the body leaves in this update.
                    var collected = Array.Empty<int>();
                    if (actorsById.TryGetValue(corpseId, out var corpse) && corpse.IsDying)
                    {
                        corpse.DeathTicks = NativeDeathTicks;
                        collected = [corpseId];
                    }
                    events.Add(new BattlefieldTransportEvent(BattlefieldTransportEventKind.PayloadResolved,
                        transport.InstanceId, transport.SourceActorInstanceId, transport.TransportEntityId,
                        transport.Target, [], collected));
                    transport.Phase = BattlefieldTransportPhase.Ascending;
                    transport.FlightCounter = 0;
                    transport.HeightRaw = transport.BaseHeightRaw;
                    continue;
                }

                // Saucer payload word zero has high byte 0xff. State 21 treats
                // it as a header, advances the payload cursor, and returns
                // before inspecting the first victim on the following update.
                if (transport.Abducts && !transport.PayloadHeaderProcessed)
                {
                    transport.PayloadHeaderProcessed = true;
                    continue;
                }

                if (transport.PayloadIndex < transport.PayloadCount)
                {
                    if (!transport.Abducts)
                    {
                        var entityId = transport.PendingEntityIds[transport.PayloadIndex++];
                        var spawned = TrySpawnTransportPayload(entityId, transport.TeamId, transport.Target, out var instanceId)
                            ? new[] { instanceId }
                            : [];
                        events.Add(new BattlefieldTransportEvent(BattlefieldTransportEventKind.PayloadResolved,
                            transport.InstanceId, transport.SourceActorInstanceId, transport.TransportEntityId,
                            transport.Target, spawned, []));
                    }
                    else
                    {
                        var abducteeId = transport.PendingAbducteeInstanceIds[transport.PayloadIndex];
                        var removed = Array.Empty<int>();
                        if (actorsById.TryGetValue(abducteeId, out var abductee) && !abductee.IsDestroyed)
                        {
                            var victimPosition = abductee.Movement.VisualPosition;
                            var dx = victimPosition.XRaw - transport.Position.XRaw;
                            var dz = victimPosition.ZRaw - transport.Position.ZRaw;
                            // 0x418BDC compares Manhattan 8.8 distance with
                            // 0x100. Farther victims receive a direct 0x412388
                            // command-5 interpolation before state 21 resumes.
                            if (Math.Abs(dx) + Math.Abs(dz) > FixedPointPosition.One)
                            {
                                BeginTransportPursuit(transport, victimPosition);
                                continue;
                            }
                            RemoveActorFromWorld(abductee, carriedOff: true);
                            removed = [abducteeId];
                        }
                        transport.PayloadIndex++;
                        events.Add(new BattlefieldTransportEvent(BattlefieldTransportEventKind.PayloadResolved,
                            transport.InstanceId, transport.SourceActorInstanceId, transport.TransportEntityId,
                            transport.Target, [], removed));
                    }
                    continue;
                }

                transport.Phase = BattlefieldTransportPhase.Ascending;
                transport.FlightCounter = 0;
                transport.HeightRaw = transport.BaseHeightRaw;
                continue;
            }

            // Ascending state 22 uses the pre-increment counter and removes
            // itself when the stored value reaches 50.
            var ascentCounter = transport.FlightCounter;
            transport.FlightCounter++;
            if (transport.FlightCounter >= NativeTransportFlightTicks)
            {
                events.Add(new BattlefieldTransportEvent(BattlefieldTransportEventKind.Departed,
                    transport.InstanceId, transport.SourceActorInstanceId, transport.TransportEntityId,
                    transport.Target, [], []));
                battlefieldTransports.RemoveAt(index);
                continue;
            }
            transport.HeightRaw = transport.BaseHeightRaw + 3 * ascentCounter * ascentCounter;
        }
    }

    private void BeginTransportPursuit(BattlefieldTransportState transport, FixedPointPosition target)
    {
        var dx = target.XRaw - transport.Position.XRaw;
        var dz = target.ZRaw - transport.Position.ZRaw;
        transport.Facing.Face(NativeBearing.FromDelta(dx, dz));
        transport.IsTurning = true;
        var vector = NativeBearing.Vector(transport.Facing.Target);
        var speed = Math.Max(1, EntityDefinitionFor(transport.TransportEntityId).MovementSpeed);
        var projectedDistance = (vector.X * dx + vector.Z * dz) / NativeDirectionVector.Scale;
        transport.HorizontalExecutionsRemaining = Math.Max(0, projectedDistance / speed);
        transport.HorizontalVelocityXRaw = vector.X * speed / NativeDirectionVector.Scale;
        transport.HorizontalVelocityZRaw = vector.Z * speed / NativeDirectionVector.Scale;
        transport.PursuitTarget = target;
    }

    /// <summary>
    /// <c>0x418D5F</c>: the unit appears on the target cell when it is free,
    /// otherwise on the first free cell of the square rings (<c>0x41B4A0</c>).
    /// </summary>
    private bool TrySpawnTransportPayload(int entityId, int teamId, CellCoordinate center, out int instanceId)
    {
        instanceId = 0;
        if ((uint)entityId >= (uint)entityDefinitions.Count) return false;
        var definition = EntityDefinitionFor(entityId);
        var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        var cell = FindNativeFreeCell(center, definition.MovementClass);
        if (cell is null) return false;
        instanceId = nextActorInstanceId++;
        if (!occupancy.TryClaim(instanceId, [cell.Value])) return false;
        var seed = new WorldEntity(instanceId, entityId, teamId, cell.Value,
            FixedPointPosition.AtCellCenter(cell.Value), 0, 0);
        var actor = new SimulatedActor(seed, definition);
        actors.Add(actor);
        actorsById.Add(instanceId, actor);
        RegisterCommander(actor);
        return true;
    }
}
