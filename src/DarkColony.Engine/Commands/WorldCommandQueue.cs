namespace DarkColony.Engine.Commands;

/// <summary>Orders player/system intents reproducibly before actor dispatch.</summary>
public sealed class WorldCommandQueue
{
    private readonly SortedDictionary<ulong, List<ScheduledWorldCommand>> byTick = [];
    private ulong nextSequence;

    public int Count { get; private set; }

    public ScheduledWorldCommand Enqueue(ulong currentTick, ulong targetTick, WorldCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (targetTick <= currentTick) throw new ArgumentOutOfRangeException(nameof(targetTick), "Commands must target a future tick.");
        var scheduled = new ScheduledWorldCommand(targetTick, nextSequence++, command);
        if (!byTick.TryGetValue(targetTick, out var commands)) byTick[targetTick] = commands = [];
        commands.Add(scheduled);
        Count++;
        return scheduled;
    }

    public IReadOnlyList<ScheduledWorldCommand> Dequeue(ulong tick)
    {
        if (!byTick.Remove(tick, out var commands)) return [];
        Count -= commands.Count;
        return commands;
    }
}
