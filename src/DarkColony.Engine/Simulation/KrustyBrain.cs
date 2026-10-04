namespace DarkColony.Engine.Simulation;

/// <summary>
/// One computer player's Krusty planner state (<c>krusty.c</c>; 0x6C40
/// bytes at player <c>+0xBC0</c>, allocated by <c>0x44BD2C</c>).
/// </summary>
/// <remarks>
/// The native think (<c>0x44BE40</c>) runs, in order: the first-think setup
/// (<c>0x457568</c>, once), the general planner (<c>0x456AD0</c>: economy,
/// goals and purchases), <c>0x457940</c>, then the four task groups (stride
/// <c>0x12FC</c>; methods at <c>+0x3168</c>, <c>+0x3174</c>,
/// <c>+0x316C</c>, <c>+0x3170</c>) set up as group 2 (<c>0x45913C</c>),
/// group 1 (<c>0x459860</c>), group 0 (<c>0x459E48</c>), and group 3
/// (<c>0x45A72C</c>). The port fills these in step by step; until a step is
/// ported its native effect is absent.
/// </remarks>
public sealed class KrustyBrain
{
    private readonly ScenarioSimulation simulation;

    internal KrustyBrain(ScenarioSimulation simulation, int player)
    {
        this.simulation = simulation;
        Player = player;
    }

    public int Player { get; }

    /// <summary>State byte 0: set by the setup, cleared after the first think's extra pass.</summary>
    public bool FirstThinkPending { get; private set; } = true;

    /// <summary>Number of times the planner has run.</summary>
    public int Thinks { get; private set; }

    internal void Think()
    {
        Thinks++;
        if (FirstThinkPending) FirstThinkPending = false;
        _ = simulation;
    }
}
