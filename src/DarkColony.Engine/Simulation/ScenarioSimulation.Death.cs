using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// The dying state (state 10). <c>0x416308</c> empties a killed actor's
/// command stack and pushes the dying command (10, <c>0x416460</c>) with a
/// zero counter; its caller then clears the actor from the grids
/// (<c>0x434D48</c>). The actor stays in the update list until the counter
/// reaches 150, so the statistics recount, the troop cap, and vision still
/// count it.
/// </summary>
public sealed partial class ScenarioSimulation
{
    /// <summary>The dying counter value that takes the actor out of the world (<c>0x416521</c>).</summary>
    public const int NativeDeathTicks = 0x96;
    /// <summary>The SCN loader sets world byte 0 for this tileset (<c>0x41BB37</c>, case-sensitive).</summary>
    private const string NoPickupTileset = "atlantis.bts";

    private readonly bool[] noPickupPlayers = new bool[PlayerCount];
    private bool noPickupWorld;

    /// <summary>Alive, or dying and still counted (state not 0).</summary>
    private static bool IsInWorld(SimulatedActor actor) => !actor.IsDestroyed || actor.IsDying;

    /// <summary>
    /// <c>0x416308</c> on a kill. A commander's body (runtime <c>+0x100</c>,
    /// the Inspire target limit, nonzero only for entities 69-76) is collected by a
    /// transport of its own team (<c>0x418F4C</c> with an all-zero first
    /// payload word and the actor index after it), unless the map's tileset
    /// is <c>atlantis.bts</c> or the player ran <c>nopickup</c>.
    /// </summary>
    private void BeginDeath(SimulatedActor actor)
    {
        var position = actor.Movement.VisualPosition.Cell;
        actor.DeathTicks = 0;
        var team = actor.Seed.Team;
        if (EffectiveDefinition(actor).ImmediateAreaTargetLimit == 0 || noPickupWorld ||
            (uint)team < PlayerCount && noPickupPlayers[team]) return;
        var transportEntityId = teamRaces.GetValueOrDefault(team) == 1 ? 93 : 92;
        StartBattlefieldTransport(-1, team, transportEntityId, position, [], [], pendingMissionTransports,
            corpseInstanceId: actor.Seed.InstanceId);
    }

    /// <summary>
    /// The dying command (<c>0x416460</c>), in the actor's own update. Its
    /// first run draws a death animation from the shared stream. A
    /// commander's counter then stays at 1 until its pickup transport sets
    /// it to 150; any other actor's counter counts up. At 150 the actor
    /// leaves the update list (state 0).
    /// </summary>
    private void UpdateDeath(SimulatedActor actor)
    {
        var ticks = actor.DeathTicks!.Value;
        if (ticks == 0) _ = NextNativeRandom();
        if (EffectiveDefinition(actor).ImmediateAreaTargetLimit == 0) ticks++;
        else if (ticks == 0) ticks = 1;
        actor.DeathTicks = ticks == NativeDeathTicks ? null : ticks;
    }

    /// <summary>
    /// A dying actor sees <c>(150 - t) * r / 150</c> cells, at least 1, where
    /// t is its counter (<c>0x445D05</c>). The 1-12 range test comes first.
    /// </summary>
    private static int DyingObservationRange(SimulatedActor actor, int radius) =>
        actor.DeathTicks is { } ticks ? Math.Max(1, (NativeDeathTicks - ticks) * radius / NativeDeathTicks) : radius;
}
