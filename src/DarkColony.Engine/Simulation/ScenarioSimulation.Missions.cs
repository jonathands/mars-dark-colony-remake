using DarkColony.Engine.Economy;
using DarkColony.Engine.Time;
using DarkColony.Engine.Missions;
using DarkColony.Engine.Movement;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// A mission's end: <c>bail a b</c> stores a in stat (0,0) and b in stat (7,0).
/// In the corpus a = 0 is a victory (text <c>.001</c>) and a = 1 a defeat whose
/// text is <c>.00b</c>.
/// </summary>
public sealed record MissionOutcome(int Result, int OutcomeText, ulong EndsAtTick)
{
    public bool Victory => Result == 0;
}

/// <summary>A <c>msg</c> action: line <c>Index</c> of the scenario's <c>.msg</c> text and its display fields.</summary>
public sealed record MissionMessageEvent(int Index, int Kind, int Field3, int Field4);

/// <summary>An action whose native effect the port does not model yet.</summary>
public sealed record MissionUnmodeledActionEvent(int TriggerSlot, MissionActionType Type, IReadOnlyList<int> Values);

/// <summary>
/// The mission trigger runtime (<c>trigger.c</c>). Every eighth world update
/// (<c>world + 0x94C &amp; 7</c>, <c>0x419A4E</c>) the norm runner <c>0x43E4D0</c>
/// walks slots 0-127; a trigger with lives left whose condition holds runs its
/// actions (<c>0x43D814</c>) and loses one life. A trip trigger runs the same
/// way (<c>0x43E530</c>) when a unit's path step reserves a cell carrying its
/// ID in the trip map, with that unit as <c>S</c>/<c>t</c>.
/// </summary>
public sealed partial class ScenarioSimulation
{
    /// <summary>Statistics per player (<c>0x4956E0</c>, 12 words) used by <c>s(p,k)</c>.</summary>
    public const int PlayerStatCount = 12;
    /// <summary>Entity types per player in the type statistics (<c>0x495860</c>).</summary>
    public const int TypeStatEntities = 0x6e;
    /// <summary><c>bail</c> sets the game-over time 10,000 ms ahead (<c>0x43D978</c>); 152 world updates of 66 ms.</summary>
    public const int BailDelayTicks = 152;

    public MissionOutcome? Outcome { get; private set; }
    public IReadOnlyList<MissionMessageEvent> LastMissionMessages { get; private set; } = [];
    public IReadOnlyList<MissionUnmodeledActionEvent> LastUnmodeledMissionActions { get; private set; } = [];
    /// <summary>Remaining lives per trigger slot (byte <c>+8</c> of each table entry).</summary>
    public IReadOnlyList<int> MissionLives => missionLives.Select(lives => (int)lives).ToArray();
    public bool NoUndeploy { get; private set; }

    public int PlayerStatistic(int player, int stat) =>
        (uint)player < 8 && (uint)stat < PlayerStatCount ? playerStats[player, stat] : 0;

    public int TypeStatistic(int player, int stat, int entityType) =>
        (uint)player < 8 && (uint)stat < 4 && (uint)entityType < TypeStatEntities ? typeStats[player, entityType, stat] : 0;

    private void InitializeMission(MissionScript? script)
    {
        missionScript = script;
        if (script is null) return;
        foreach (var trigger in script.Triggers.Where(trigger => trigger.Slot is >= 0 and < MissionScript.TriggerSlots))
        {
            missionTriggers[trigger.Slot] = trigger;
            missionLives[trigger.Slot] = trigger.Lives;
        }
    }

    /// <summary>
    /// Start of the world update (<c>0x4196F4</c>): per-type live counts (stat 1
    /// of <c>0x495860</c>) and the unit count (player stat 6) are rebuilt from
    /// every actor outside the city slots.
    /// </summary>
    private void RecountMissionStatistics()
    {
        for (var player = 0; player < 8; player++)
        {
            playerStats[player, 6] = 0;
            for (var type = 0; type < TypeStatEntities; type++) typeStats[player, type, 1] = 0;
        }
        var cityActors = cityBuildings.Values.ToHashSet();
        foreach (var actor in actors)
        {
            if (actor.IsDestroyed || cityActors.Contains(actor.Seed.InstanceId) || actor.Seed.Team is < 0 or >= 8) continue;
            var type = EffectiveDefinition(actor).Id;
            if (type < TypeStatEntities) typeStats[actor.Seed.Team, type, 1]++;
            playerStats[actor.Seed.Team, 6]++;
        }
    }

    /// <summary>
    /// <c>0x441930</c> on a kill: a different player's killer gains stat 2 and
    /// a type-3 count; the victim's player gains stat 3 and a type-0 count.
    /// </summary>
    private void RecordMissionKill(SimulatedActor victim, int? attackerTeam)
    {
        var victimType = EffectiveDefinition(victim).Id;
        if (attackerTeam is { } killer and >= 0 and < 8 && killer != victim.Seed.Team)
        {
            playerStats[killer, 2]++;
            if (victimType < TypeStatEntities) typeStats[killer, victimType, 3]++;
        }
        if (victim.Seed.Team is >= 0 and < 8)
        {
            playerStats[victim.Seed.Team, 3]++;
            if (victimType < TypeStatEntities) typeStats[victim.Seed.Team, victimType, 0]++;
        }
    }

    /// <summary>Player stat 1 accumulates every Petra-7 credit (start money, passive and vent income).</summary>
    private void RecordP7Earned(int team, int amount)
    {
        if ((uint)team < 8) playerStats[team, 1] += amount;
    }

    /// <summary><c>0x413B9C</c>: stat 5 counts the harvester pulses that paid their owner.</summary>
    private void RecordHarvestPulse(int team) => AddPlayerStatistic(team, 5, 1);

    /// <summary>
    /// <c>0x419D8C</c> adds to a player statistic. Besides the kill and P7
    /// counters: stat 8 counts projectiles launched (constructor
    /// <c>0x441710</c>, <c>0x44178F</c>) and stat 9 the health a healer
    /// restores to the healed actor's player (<c>0x413F04</c>, <c>0x413FD8</c>).
    /// </summary>
    private void AddPlayerStatistic(int team, int stat, int amount)
    {
        if ((uint)team < 8) playerStats[team, stat] += amount;
    }

    /// <summary>The trigger block of the world update (<c>0x419A4E</c>), every eighth tick.</summary>
    private void RunNormTriggers(List<MissionMessageEvent> messages, List<MissionUnmodeledActionEvent> unmodeled)
    {
        if (missionScript is null || (WorldUpdateCounter & 7) != 0) return;
        // 0x419A5B: player 0 stat 10 holds 1 - night (+0x53C), player 1 stat 10
        // the phase counter * 256 / the cycle limit (+0x530 << 8 / +0x534).
        playerStats[0, 10] = DayNight.Phase == DayNightPhase.Night ? 0 : 1;
        playerStats[1, 10] = (DayNight.PhaseTicks << 8) / Math.Max(1, DayNight.CycleTickLimit);
        for (var slot = 0; slot < MissionScript.TriggerSlots; slot++)
        {
            if (missionTriggers[slot] is not { Trip: false } trigger || missionLives[slot] == 0) continue;
            if (TriggerExpression.Evaluate(trigger.Condition, new MissionContext(this, null)) == 0) continue;
            ExecuteMissionActions(trigger, messages, unmodeled);
            missionLives[slot]--;
        }
    }

    /// <summary><c>0x43E530</c>, called by the path step <c>0x415E6E</c> after it reserves a cell.</summary>
    private void RunTripTrigger(SimulatedActor unit, CellCoordinate cell)
    {
        if (missionScript?.TripMap?.TriggerAt(cell) is not { } slot || slot == 0) return;
        if (missionTriggers[slot] is not { Trip: true } trigger || missionLives[slot] == 0) return;
        if (TriggerExpression.Evaluate(trigger.Condition, new MissionContext(this, unit)) == 0) return;
        ExecuteMissionActions(trigger, pendingMissionMessages, pendingUnmodeledMissionActions);
        missionLives[slot]--;
    }

    private void ExecuteMissionActions(MissionTrigger trigger, List<MissionMessageEvent> messages, List<MissionUnmodeledActionEvent> unmodeled)
    {
        foreach (var action in trigger.Actions)
        {
            var v = action.Values;
            switch (action.Type)
            {
                case MissionActionType.Ai:
                    // Player +0xBBC is the SCN %AI value; nonzero marks a computer player.
                    if ((uint)v[0] < 8)
                    {
                        if (v[1] != 0) computerTeams.Add(v[0]);
                        else computerTeams.Remove(v[0]);
                    }
                    break;
                case MissionActionType.Bail:
                    playerStats[0, 0] = v[0];
                    playerStats[7, 0] = v[1];
                    Outcome = new MissionOutcome(v[0], v[1], simulationTicks + BailDelayTicks);
                    break;
                case MissionActionType.SetLifes:
                    if ((uint)v[0] < MissionScript.TriggerSlots)
                        missionLives[v[0]] = unchecked((byte)Evaluate(action));
                    break;
                case MissionActionType.SetArray:
                    if ((uint)v[0] < TypeStatEntities) typeStats[0, v[0], 2] = (short)Evaluate(action);
                    break;
                case MissionActionType.Ally:
                    if ((uint)v[0] < TeamRelationMatrixSize && (uint)v[1] < TeamRelationMatrixSize)
                        TeamRelations.SetRelation(v[0], v[1], (byte)v[2]);
                    break;
                case MissionActionType.DependFiddle:
                    if (teamEconomies.TryGetValue(v[0], out var fiddled)) fiddled.SetItemDisabled(v[1], v[2] != 0);
                    break;
                case MissionActionType.ExoMoney:
                    if ((uint)v[0] < 8) passiveRates[v[0]] = v[1];
                    break;
                case MissionActionType.Message:
                    messages.Add(new MissionMessageEvent(v[2], v[0], v[3], v[4]));
                    break;
                case MissionActionType.Waypoint:
                    AssignScriptWaypoints(v);
                    break;
                case MissionActionType.Reinforce2:
                case MissionActionType.Reinforce:
                    // reinforce lands a transport (0x418F4C, command 13) that
                    // unloads later; the port places its cargo at once.
                    if (action.Type == MissionActionType.Reinforce)
                        unmodeled.Add(new MissionUnmodeledActionEvent(trigger.Slot, action.Type, v));
                    SpawnScriptUnits(v);
                    break;
                case MissionActionType.NewRate:
                    SetVentRate(new CellCoordinate(v[1], v[2]), v[0]);
                    break;
                case MissionActionType.NewRate2:
                    SetVentRate(new CellCoordinate(v[0], v[1]), Evaluate(action));
                    break;
                case MissionActionType.SetMoney:
                    SetVentReservoir(new CellCoordinate(v[0], v[1]), Evaluate(action));
                    break;
                case MissionActionType.NewType:
                    ChangeScriptActorType(new CellCoordinate(v[0], v[1]), v[2]);
                    break;
                case MissionActionType.NoUndeploy:
                    NoUndeploy = true;
                    break;
                case MissionActionType.NoPickup:
                case MissionActionType.Vision:
                case MissionActionType.Abduct:
                case MissionActionType.Artifact:
                case MissionActionType.AiMessage:
                    unmodeled.Add(new MissionUnmodeledActionEvent(trigger.Slot, action.Type, v));
                    break;
                case MissionActionType.Die:
                    break;
            }
        }
    }

    private const int TeamRelationMatrixSize = 10;

    private int Evaluate(MissionAction action) => TriggerExpression.Evaluate(action.Expression!, new MissionContext(this, null));

    /// <summary>
    /// <c>waypoint x z n ...</c> (<c>0x43D764</c>): the first actor standing on
    /// (x, z) gets up to eight points and native state 9. The port gives it
    /// a move order through the points in turn.
    /// </summary>
    private void AssignScriptWaypoints(IReadOnlyList<int> values)
    {
        var cell = new CellCoordinate(values[0], values[1]);
        var actor = actors.FirstOrDefault(candidate => !candidate.IsDestroyed && candidate.Movement.OccupiedCell == cell &&
                                                       EffectiveDefinition(candidate).MovementSpeed > 0);
        if (actor is null) return;
        var order = new ActiveMoveOrder(new CellCoordinate(values[3], values[4]));
        for (var point = 1; point < values[2]; point++)
            order.TryAppendWaypoint(new CellCoordinate(values[3 + point * 2], values[4 + point * 2]));
        actor.Playback?.Cancel();
        actor.Playback = null;
        actor.AttackTargetInstanceId = null;
        actor.MoveOrder = order;
        _ = StartSegment(actor);
    }

    /// <summary>
    /// <c>reinforce2 team x z (type count) x5</c>: each unit is created by
    /// <c>0x41B634</c> on the first free cell of the square rings around (x, z)
    /// (<c>0x41B4A0</c>: x outer, z inner).
    /// </summary>
    private void SpawnScriptUnits(IReadOnlyList<int> values)
    {
        var team = values[0];
        var origin = new CellCoordinate(values[1], values[2]);
        for (var pair = 0; pair < 5; pair++)
        {
            var entityId = values[3 + pair * 2];
            var count = values[4 + pair * 2];
            if ((uint)entityId >= (uint)entityDefinitions.Count) continue;
            var definition = EntityDefinitionFor(entityId);
            for (var unit = 0; unit < count; unit++)
            {
                if (FindNativeFreeCell(origin, definition.MovementClass) is not { } cell) break;
                var occupancy = definition.MovementClass == 0 ? GroundOccupancy : AlternateOccupancy;
                var instanceId = nextActorInstanceId++;
                occupancy.ReplaceClaims(instanceId, [cell]);
                var seed = new WorldEntity(instanceId, entityId, team, cell, FixedPointPosition.AtCellCenter(cell), 0, 0);
                var actor = new SimulatedActor(seed, definition);
                actors.Add(actor);
                actorsById.Add(instanceId, actor);
            }
        }
    }

    private CellCoordinate? FindNativeFreeCell(CellCoordinate origin, int movementClass)
    {
        var occupancy = movementClass == 0 ? GroundOccupancy : AlternateOccupancy;
        var limit = Math.Max(path.Width, path.Height);
        for (var radius = 0; radius < limit; radius++)
        for (var x = origin.X - radius; x <= origin.X + radius; x++)
        {
            if ((uint)x >= (uint)path.Width) continue;
            for (var z = origin.Z - radius; z <= origin.Z + radius; z++)
            {
                if ((uint)z >= (uint)path.Height) continue;
                var cell = new CellCoordinate(x, z);
                if (occupancy.IsOccupied(cell)) continue;
                if (movementClass == 0 && path.RegionAt(cell) == 0) continue;
                return cell;
            }
        }
        return null;
    }

    /// <summary>
    /// <c>newrate</c>/<c>newrate2</c>: the vent at (x, z) pays rate times the
    /// session rate option (player 1 stat 0) &gt;&gt; 8 per pulse.
    /// </summary>
    private void SetVentRate(CellCoordinate cell, int rate)
    {
        if (PetraVents.FirstOrDefault(vent => vent.Position == cell) is { } vent)
            vent.Rate = PetraFlowRules.NativeSigned8_8Multiply(rate, playerStats[1, 0]);
    }

    /// <summary>
    /// <c>setmoney</c>: the vent at (x, z) holds reservoir times the session
    /// money option (player 2 stat 0) &gt;&gt; 8; a vent with rate 0 is created if
    /// none is there.
    /// </summary>
    private void SetVentReservoir(CellCoordinate cell, int reservoir)
    {
        reservoir = PetraFlowRules.NativeSigned8_8Multiply(reservoir, playerStats[2, 0]);
        var vent = PetraVents.FirstOrDefault(candidate => candidate.Position == cell);
        if (vent is null)
        {
            vent = new PetraVent(PetraVents.Count, cell, 0, reservoir);
            PetraVents = [.. PetraVents, vent];
        }
        vent.RemainingReservoir = reservoir;
    }

    /// <summary>
    /// <c>newtype x z t</c>: the first ground-class actor on (x, z) becomes
    /// entity t (byte <c>+6</c>), keeping its health and identity.
    /// </summary>
    private void ChangeScriptActorType(CellCoordinate cell, int entityId)
    {
        if ((uint)entityId >= (uint)entityDefinitions.Count) return;
        var actor = actors.FirstOrDefault(candidate => !candidate.IsDestroyed && candidate.Movement.OccupiedCell == cell &&
                                                       EffectiveDefinition(candidate).MovementClass == 0);
        if (actor is not null) actor.DeployedEntityId = entityId;
    }

    private sealed class MissionContext(ScenarioSimulation simulation, SimulatedActor? unit) : ITriggerExpressionContext
    {
        public int Clock => (int)(simulation.WorldUpdateCounter >> 4);
        public int NextRandom() => (int)simulation.NextNativeRandom();
        public (int EntityType, int Team)? Unit => unit is null ? null : (simulation.EffectiveDefinition(unit).Id, unit.Seed.Team);
        public int SlotHealth(int player, int slot) => simulation.CityBuilding(player, slot)?.Health ?? 0;
        public bool CellVisible(int x, int z, int player) =>
            (uint)player < 8 && (uint)x < (uint)simulation.path.Width && (uint)z < (uint)simulation.path.Height &&
            simulation.IsCellVisibleToTeam(player, new CellCoordinate(x, z));
        public int PlayerStat(int player, int stat) => simulation.PlayerStatistic(player, stat);
        public int TypeStat(int player, int stat, int entityType) => simulation.TypeStatistic(player, stat, entityType);
        public bool MineAlive(int x, int z) =>
            simulation.PetraVents.Any(vent => vent.Position.X == x && vent.Position.Z == z && vent.RemainingReservoir > 0);
        public int ScriptWord(int byteOffset) => 0;
    }
}
