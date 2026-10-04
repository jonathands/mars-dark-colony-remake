using DarkColony.Engine.Data;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// One computer player's Krusty planner (<c>krusty.c</c> and its modules;
/// 0x6C40 bytes at player <c>+0xBC0</c>, allocated by <c>0x44BD2C</c>).
/// </summary>
/// <remarks>
/// <para>
/// A think (<c>0x44BE40</c>) runs, in order: on the first think only, the unit
/// assignment (<c>0x457568</c>); the influence map (<c>0x456AD0</c>); the
/// census and goal walk (<c>0x457940</c>); then each of the four groups'
/// methods pre, purge, update and post.
/// </para>
/// <para>
/// Group 0 harvests (<c>0x4598B0</c>). Group 1 guards: task 0 roams near home
/// and one task holds each harvested vent's region (<c>0x4593A8</c>). Group 2
/// attacks: its tasks pick enemy regions within reach of the guarded ones
/// (<c>0x458B44</c>). Group 3 scouts and bombs (<c>0x459F80</c>). Units are
/// split by category: harvesters to group 0, fliers to group 3, the rest one
/// quarter to group 1 and three quarters to group 2 (ratio <c>0xC0</c>).
/// </para>
/// </remarks>
public sealed partial class KrustyBrain
{
    internal const int ZoneCount = 256;
    internal const int GroupCount = 4;
    internal const int TaskCount = 16;
    internal const int CategoryCount = 9;
    internal const int NoCategory = 8;
    internal const int HarvesterCategory = 6;
    internal const int GoalSlotCount = 0x20;
    internal const int NoOwner = 0xFF;
    internal const int HarvestGroup = 0;
    internal const int GuardGroup = 1;
    internal const int AttackGroup = 2;
    internal const int ScoutGroup = 3;
    /// <summary>Troop items the purchase loops consider (<c>0..0x6D</c>).</summary>
    private const int TroopItemLimit = 0x6E;
    /// <summary>The "no choice yet" score of the army purchase and the task choosers.</summary>
    private const int NoChoiceScore = 10000;

    private readonly ScenarioSimulation world;
    private readonly KrustyGoal?[] goals = new KrustyGoal?[GoalSlotCount];
    private readonly bool[] allies = new bool[8];
    /// <summary>
    /// Category weights of the army purchase (<c>+0x6C18..+0x6C30</c>), indexed
    /// by category; harvesters weigh 10000 so the army goal never buys one.
    /// </summary>
    private readonly int[] categoryWeights = [1, 2, 1, 2, 4, 2, NoChoiceScore, 4, 0];
    private readonly Dictionary<int, (int Group, int Task)> membership = [];

    internal KrustyBrain(ScenarioSimulation world, int player)
    {
        this.world = world;
        Player = player;
        Zones = [.. Enumerable.Range(0, ZoneCount).Select(_ => new KrustyZone())];
        Groups = [.. Enumerable.Range(0, GroupCount).Select(index => new KrustyGroup(index))];
        // 0x44BD2C: the player is its own only ally, then the regions, the
        // groups (2, 1, 0, 3), the constants, and the default goals.
        allies[player] = true;
        InitializeZones();
        CreateArmyTask(AttackGroup, 0);
        Groups[AttackGroup].Tasks[0].Mode = KrustyTaskMode.Idle;
        CreateArmyTask(AttackGroup, 1);
        Groups[AttackGroup].Tasks[1].Mode = KrustyTaskMode.Idle;
        CreateArmyTask(GuardGroup, 0);
        Groups[HarvestGroup].Tasks[0].Active = true;
        Groups[ScoutGroup].Tasks[0].Active = true;
        if (world.KrustyTables is { } tables)
            for (var index = 0; index < tables.DefaultGoals.Count && index < GoalSlotCount; index++)
                goals[index] = tables.DefaultGoals[index];
    }

    public int Player { get; }

    /// <summary>State byte 0: set by the setup, cleared after the first think's extra assignment.</summary>
    public bool FirstThinkPending { get; private set; } = true;

    /// <summary>Number of times the planner has run.</summary>
    public int Thinks { get; private set; }

    /// <summary>Share of the non-specialist units kept in group 1, out of 256 (<c>+0x6C14</c>).</summary>
    public int GuardShare { get; private set; } = 0xC0;

    public IReadOnlyList<KrustyZone> Zones { get; }
    public IReadOnlyList<KrustyGroup> Groups { get; }

    /// <summary>The group and task an actor of this player belongs to, if any (actor links <c>+0xD2/+0xD4</c>).</summary>
    public (int Group, int Task)? MembershipOf(int instanceId) =>
        membership.TryGetValue(instanceId, out var entry) ? entry : null;

    internal void Think()
    {
        Thinks++;
        if (FirstThinkPending)
        {
            AssignUnits();
            FirstThinkPending = false;
        }
        UpdateInfluence();
        TakeCensus();
        for (var group = 0; group < GroupCount; group++)
        {
            PreGroup(group);
            PurgeGroup(group);
            UpdateGroup(group);
            PostGroup(group);
        }
    }

    /// <summary>
    /// <c>0x4563F0</c>: a unit's category. Human types 0-7 and Gray types 8-15
    /// map to 0-7 (6 is the harvester, 5 the flier), types 49/50 to 7, the
    /// deployed towers 41/42 to 1, anything else to 8 (none).
    /// </summary>
    internal static int Category(int type) => type switch
    {
        < 0 => NoCategory,
        < 8 => type,
        < 16 => type - 8,
        0x31 or 0x32 => 7,
        0x29 or 0x2A => 1,
        _ => NoCategory,
    };

    private int TypeOf(SimulatedActor actor) => world.EffectiveDefinition(actor).Id;

    /// <summary>A unit the planner counts (actor state neither 0 nor 10).</summary>
    private static bool IsActive(SimulatedActor actor) => !actor.IsDestroyed;

    private static CellCoordinate CellOf(SimulatedActor actor) => actor.Movement.VisualPosition.Cell;

    private int RegionOf(CellCoordinate cell) =>
        (uint)cell.X < (uint)world.KrustyRegions.Width && (uint)cell.Z < (uint)world.KrustyRegions.Height
            ? world.KrustyRegions.RegionAt(cell)
            : 0;

    // ---- census and goals (krusty_general.c) ----

    /// <summary>
    /// <c>0x457568</c>: units without a group are counted by category, given
    /// group quotas, then handed out in slot order, each to the first group
    /// with quota left, at the task that group's accept method picks.
    /// </summary>
    private void AssignUnits()
    {
        var available = new int[CategoryCount];
        var actors = world.ActorsInNativeSlotOrder().ToArray();
        foreach (var actor in actors)
        {
            if (actor.Seed.Team != Player || membership.ContainsKey(actor.Seed.InstanceId) || !IsActive(actor)) continue;
            var type = TypeOf(actor);
            if (type is 0x29 or 0x2A) continue;
            available[Category(type)]++;
        }
        var quotas = new int[GroupCount, CategoryCount];
        quotas[HarvestGroup, HarvesterCategory] = available[HarvesterCategory];
        available[HarvesterCategory] = 0;
        quotas[GuardGroup, 1] = available[1];
        available[1] = 0;
        // Fliers always go to the scouts. Without fliers, plain infantry joins
        // the scouts while the scouts are under a quarter of the armies.
        if (world.NativeTroopBuildable(Player, 0x0A, out _, out _) || world.NativeTroopBuildable(Player, 0x18, out _, out _) || available[5] > 0)
        {
            quotas[ScoutGroup, 5] = available[5];
            available[5] = 0;
        }
        else if (available[0] > 0 &&
                 Groups[ScoutGroup].Counts[0] * 4 <= Groups[GuardGroup].Counts[0] + Groups[AttackGroup].Counts[0])
        {
            quotas[ScoutGroup, 0] = available[0];
            available[0] = 0;
        }
        for (var category = 0; category < CategoryCount; category++)
            for (var left = available[category]; left > 0; left--)
            {
                var guard = GuardShare != 0x100 &&
                            Groups[GuardGroup].Counts[category] * GuardShare <= (0x100 - GuardShare) * Groups[AttackGroup].Counts[category];
                var group = guard ? GuardGroup : AttackGroup;
                quotas[group, category]++;
                Groups[group].Counts[category] = (ushort)(Groups[group].Counts[category] + 1);
            }
        foreach (var actor in actors)
        {
            // City buildings (slots below 0x78) are never assigned.
            if (world.IsCityBuildingActor(actor) || actor.Seed.Team != Player ||
                membership.ContainsKey(actor.Seed.InstanceId) || !IsActive(actor)) continue;
            var category = Category(TypeOf(actor));
            for (var group = 0; group < GroupCount; group++)
            {
                if (quotas[group, category] == 0) continue;
                var task = AcceptUnit(group, category);
                quotas[group, category]--;
                Insert(actor.Seed.InstanceId, group, task);
                break;
            }
        }
    }

    /// <summary>
    /// <c>0x457940</c>: assignment, the group counts, plus the troops still in
    /// the production queues, then the goal walk.
    /// </summary>
    private void TakeCensus()
    {
        AssignUnits();
        var census = new int[CategoryCount + 1];
        for (var group = 0; group < GroupCount; group++)
        {
            CountGroup(group);
            for (var category = 0; category < CategoryCount; category++) census[category] += Groups[group].Counts[category];
        }
        foreach (var type in world.QueuedTroopTypes(Player)) census[Category(type)]++;
        WalkGoals(census);
    }

    /// <summary><c>0x4578D0</c>: the first unsatisfied goal acts, and the walk stops.</summary>
    private void WalkGoals(int[] census)
    {
        foreach (var goal in goals)
        {
            if (goal is not { } current) return;
            if (GoalSatisfied(current, census)) continue;
            ActOnGoal(current, census);
            return;
        }
    }

    private bool GoalSatisfied(KrustyGoal goal, int[] census) => goal.Kind switch
    {
        // 0x45642C
        KrustyGoalKind.Harvesters => goal.Parameter <= census[HarvesterCategory],
        // 0x4564C8: an item that cannot (or need not) be built counts as done.
        KrustyGoalKind.Building => world.NativeBuildingStatus(Player, BuildingItem(goal.Parameter), out _, out _) != 1,
        // 0x456664: a full troop cap, or an army (categories 0, 2-5) of at least p.
        KrustyGoalKind.Army => world.PlayerStatistic(Player, 6) >= world.TroopCap ||
                               census[0] + census[2] + census[3] + census[4] + census[5] >= goal.Parameter,
        _ => false,
    };

    private int BuildingItem(int parameter) => world.KrustyTables?.BuildingItem(parameter, world.TeamRace(Player)) ?? -1;

    private void ActOnGoal(KrustyGoal goal, int[] census)
    {
        switch (goal.Kind)
        {
            case KrustyGoalKind.Harvesters:
                // 0x456448: one of every buildable harvester troop, while affordable.
                for (var item = 0; item < TroopItemLimit; item++)
                {
                    if (!world.NativeTroopBuildable(Player, item, out var type, out var cost) || Category(type) != HarvesterCategory) continue;
                    if (!world.TrySpendP7(Player, cost)) return;
                    world.QueueNativeTroop(Player, item, type);
                }
                break;
            case KrustyGoalKind.Building:
            {
                // 0x456550
                var item = BuildingItem(goal.Parameter);
                if (world.NativeItemCost(item) > world.P7(Player)) return;
                if (!world.TrySpendP7(Player, world.NativeItemCost(item))) return;
                world.QueueNativeBuild(Player, item);
                break;
            }
            case KrustyGoalKind.Army:
            {
                // 0x4566AC: the buildable troop whose category has the lowest
                // census times weight (first one on ties).
                var best = NoChoiceScore;
                var bestItem = -1;
                var bestType = -1;
                for (var item = 0; item < TroopItemLimit; item++)
                {
                    if (!world.NativeTroopBuildable(Player, item, out var type, out _)) continue;
                    var category = Category(type);
                    if (category == NoCategory) continue;
                    var weight = census[category] * categoryWeights[category];
                    if (category == HarvesterCategory) weight = NoChoiceScore;
                    if (best <= weight) continue;
                    best = weight;
                    bestItem = item;
                    bestType = type;
                }
                if (bestType == -1 || !world.NativeTroopBuildable(Player, bestItem, out _, out var price)) return;
                if (world.TrySpendP7(Player, price)) world.QueueNativeTroop(Player, bestItem, bestType);
                break;
            }
        }
    }

    // ---- mission messages ----

    /// <summary>
    /// <c>0x44BF54</c>, the controller method an <c>aimsg</c> calls with
    /// <c>values[0]</c> selecting the setting. The shipped corpus sends only 1-4.
    /// Settings 6-8 (protected regions) and 13 (the cautious attack search)
    /// are not ported.
    /// </summary>
    internal bool HandleMessage(IReadOnlyList<int> values)
    {
        if (values.Count < 2) return false;
        var value = values[1];
        switch (values[0])
        {
            case 0: GuardShare = (value << 8) / 100; return true;
            case 1: categoryWeights[0] = value; return true;
            case 2: categoryWeights[2] = value; return true;
            case 3: categoryWeights[3] = value; return true;
            case 4: categoryWeights[4] = value; return true;
            case 5: categoryWeights[5] = value; return true;
            case 9 or 10 or 11 or 12 when values.Count >= 3:
            {
                // 0x457A60/0x457AF8/0x457B90/0x457C28: set or clear flag 2
                // (rated as half reach) or 4 (unexplored) of the region at (x, z).
                var region = RegionOf(new CellCoordinate(value, values[2]));
                var zone = Zones[region];
                zone.Flags = values[0] switch
                {
                    9 => zone.Flags | 2,
                    10 => zone.Flags & ~2,
                    11 => zone.Flags | 4,
                    _ => zone.Flags & ~4,
                };
                return true;
            }
            case 14 when values.Count >= 3 && (uint)value < (uint)allies.Length:
                allies[value] = values[2] != 0;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>One region's record (18 bytes from the state's start).</summary>
public sealed class KrustyZone
{
    /// <summary>Byte <c>+2</c>/<c>+3</c>: a cell of the region nearest its centroid.</summary>
    public CellCoordinate Centroid { get; internal set; }
    /// <summary>Byte <c>+0xD</c>: hops from the home region, 0xFF when unreachable.</summary>
    public int Distance { get; internal set; } = KrustyBrain.NoOwner;
    /// <summary>Byte <c>+0x12</c>: bit 0 contested, bit 1 half reach, bit 2 unexplored.</summary>
    public int Flags { get; internal set; }
    /// <summary>Byte <c>+4</c>/word <c>+6</c>: owner and strength against fliers.</summary>
    public int OwnerA { get; internal set; } = KrustyBrain.NoOwner;
    public int StrengthA { get; internal set; }
    /// <summary>Byte <c>+8</c>/word <c>+0xA</c>: owner and strength against ground units.</summary>
    public int OwnerB { get; internal set; } = KrustyBrain.NoOwner;
    public int StrengthB { get; internal set; }
    /// <summary>Byte <c>+0xC</c>/word <c>+0xE</c>: owner and strength of fliers.</summary>
    public int OwnerC { get; internal set; } = KrustyBrain.NoOwner;
    public int StrengthC { get; internal set; }
    /// <summary>Word <c>+0x10</c>: unarmed enemy objects (1 for an unexplored region).</summary>
    public int Count { get; internal set; }
}

public enum KrustyTaskMode
{
    /// <summary>Moving on a chosen enemy region.</summary>
    Attacking = 0,
    /// <summary>Gathering reinforcements; only set by code the executable never reaches.</summary>
    Gathering = 1,
    /// <summary>Looking for a target.</summary>
    Idle = 2,
    /// <summary>Holding a guarded region while it looks for a target.</summary>
    Guarding = 3,
}

/// <summary>One of a group's 16 tasks (<c>0x12C</c> bytes from group <c>+0x10</c>).</summary>
public sealed class KrustyTask
{
    internal readonly List<int> MemberList = [];
    internal readonly int[] CountsByCategory = new int[KrustyBrain.CategoryCount];

    /// <summary>Byte <c>+1</c>.</summary>
    public bool Active { get; internal set; }
    /// <summary>Members from the list head (actor <c>+0xD2</c> links); new members join at the head.</summary>
    public IReadOnlyList<int> Members => MemberList;
    /// <summary>Words <c>+0x11A</c>: members by category at the last count.</summary>
    public IReadOnlyList<int> Counts => CountsByCategory;
    /// <summary>Dword <c>+8</c>: the region the task's units were last sent to.</summary>
    public int Current { get; internal set; }
    /// <summary>Dword <c>+0xC</c>: the region the task heads for.</summary>
    public int Target { get; internal set; }
    /// <summary>Byte <c>+0x10</c> (group 2).</summary>
    public KrustyTaskMode Mode { get; internal set; }
    /// <summary>Word <c>+0x12</c>: the next step of <see cref="Route"/>.</summary>
    public int RouteIndex { get; internal set; }
    /// <summary>Bytes <c>+0x14</c>: regions from the current one to the target.</summary>
    internal byte[] Route { get; } = new byte[256];
}

/// <summary>One of the four groups (stride <c>0x12FC</c> from state <c>+0x1E84</c>).</summary>
public sealed class KrustyGroup
{
    internal readonly int[] Counts = new int[KrustyBrain.CategoryCount];

    internal KrustyGroup(int index)
    {
        Index = index;
        Tasks = [.. Enumerable.Range(0, KrustyBrain.TaskCount).Select(_ => new KrustyTask())];
    }

    public int Index { get; }
    public IReadOnlyList<KrustyTask> Tasks { get; }
    /// <summary>Words <c>+0x12D0</c>: members by category.</summary>
    public IReadOnlyList<int> CategoryCounts => Counts;
    /// <summary>Dword <c>+8</c>: cleared by groups 0-2 each think; the scouts add to it and nothing reads it.</summary>
    public int Budget { get; internal set; }
}
