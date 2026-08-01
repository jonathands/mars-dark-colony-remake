using DarkColony.Engine.Commands;

namespace DarkColony.Engine.Simulation;

public sealed class WorldSimulation
{
    public ulong TickCount { get; private set; }
    public WorldCommandQueue Commands { get; } = new();
    public IReadOnlyList<ScheduledWorldCommand> LastCommands { get; private set; } = [];

    public void Step()
    {
        TickCount++;
        LastCommands = Commands.Dequeue(TickCount);
    }
}
