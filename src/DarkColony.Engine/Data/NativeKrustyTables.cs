using System.Buffers.Binary;

namespace DarkColony.Engine.Data;

/// <summary>The four kinds of planner goal, by their (satisfied, act) function pair.</summary>
public enum KrustyGoalKind
{
    /// <summary><c>0x45642C</c>/<c>0x456448</c>: at least <c>p</c> harvesters, buying every buildable one.</summary>
    Harvesters,
    /// <summary><c>0x4564C8</c>/<c>0x456550</c>: building <c>p</c> of the race's list, bought when it can be.</summary>
    Building,
    /// <summary><c>0x456664</c>/<c>0x4566AC</c>: an army of at least <c>p</c> (or a full troop cap), buying the scarcest troop.</summary>
    Army,
    /// <summary><c>0x456868</c>/<c>0x456874</c>: never satisfied and does nothing, so the walk stops there.</summary>
    Stop,
}

public readonly record struct KrustyGoal(KrustyGoalKind Kind, int Parameter);

/// <summary>
/// The planner tables of the computer player (<c>krusty_general.c</c>), read
/// from the user's <c>dc.exe</c>: the building item of each (goal parameter,
/// race) at <c>0x488FF4</c> (dword <c>p * 2 + race</c>), and the 18 default
/// goals the setup (<c>0x45687C</c>) copies from <c>0x48903C</c>, 12 bytes each:
/// satisfied function, act function, parameter.
/// </summary>
public sealed class NativeKrustyTables
{
    public const uint BuildingItemTableAddress = 0x488FF4;
    public const uint DefaultGoalTableAddress = 0x48903C;
    public const int BuildingItemCount = 18;
    public const int DefaultGoalCount = 18;

    private static readonly IReadOnlyDictionary<(uint Satisfied, uint Act), KrustyGoalKind> GoalFunctions =
        new Dictionary<(uint, uint), KrustyGoalKind>
        {
            [(0x45642C, 0x456448)] = KrustyGoalKind.Harvesters,
            [(0x4564C8, 0x456550)] = KrustyGoalKind.Building,
            [(0x456664, 0x4566AC)] = KrustyGoalKind.Army,
            [(0x456868, 0x456874)] = KrustyGoalKind.Stop,
        };

    private NativeKrustyTables(IReadOnlyList<int> buildingItems, IReadOnlyList<KrustyGoal> defaultGoals)
    {
        BuildingItems = buildingItems;
        DefaultGoals = defaultGoals;
    }

    /// <summary>Dependency item of building goal <c>p</c> for race <c>r</c> at index <c>p * 2 + r</c>.</summary>
    public IReadOnlyList<int> BuildingItems { get; }
    public IReadOnlyList<KrustyGoal> DefaultGoals { get; }

    public int BuildingItem(int parameter, int race) =>
        (uint)(parameter * 2 + race) < (uint)BuildingItems.Count ? BuildingItems[parameter * 2 + race] : -1;

    public static NativeKrustyTables Load(string executablePath) => FromImage(PeImage.Load(executablePath));

    public static NativeKrustyTables FromImage(PeImage image)
    {
        var items = image.AtVirtualAddress(BuildingItemTableAddress, BuildingItemCount * 4);
        var buildingItems = new int[BuildingItemCount];
        for (var index = 0; index < buildingItems.Length; index++)
            buildingItems[index] = BinaryPrimitives.ReadInt32LittleEndian(items[(index * 4)..]);
        var goalBytes = image.AtVirtualAddress(DefaultGoalTableAddress, DefaultGoalCount * 12);
        var goals = new KrustyGoal[DefaultGoalCount];
        for (var index = 0; index < goals.Length; index++)
        {
            var entry = goalBytes[(index * 12)..];
            var satisfied = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var act = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            if (!GoalFunctions.TryGetValue((satisfied, act), out var kind))
                throw new InvalidDataException($"Planner goal {index} uses unknown functions 0x{satisfied:X}/0x{act:X}.");
            goals[index] = new KrustyGoal(kind, BinaryPrimitives.ReadInt32LittleEndian(entry[8..]));
        }
        return new NativeKrustyTables(buildingItems, goals);
    }

    /// <summary>Builds tables from explicit values, for checks without an installation.</summary>
    public static NativeKrustyTables FromValues(IEnumerable<int> buildingItems, IEnumerable<KrustyGoal> defaultGoals) =>
        new([.. buildingItems], [.. defaultGoals]);
}
