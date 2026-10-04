namespace DarkColony.Engine.Simulation;

/// <summary>
/// The computer players (<c>ai.c</c>). Each profile (player <c>+0xBBC</c>,
/// the SCN <c>%AI</c> value) names a controller in the list at
/// <c>0x47936C</c>, whose first word points at a table of (score, action)
/// module pairs.
/// </summary>
public sealed partial class ScenarioSimulation
{
    /// <summary>Profiles the controller list holds; a larger one trips an assertion (<c>0x41AB3F</c>).</summary>
    public const int NativeAiProfileCount = 4;
    /// <summary>Profile 3 is the planner the source calls "Krusty AI"; Single Player War gives it to every computer player (<c>0x41DB12</c>).</summary>
    public const int KrustyAiProfile = 3;
    /// <summary>The score scale <c>1 / 32767</c> (double at <c>0x47380C</c>).</summary>
    private const double AiChoiceScale = 1.0 / 32767.0;

    /// <summary>Round-robin cursor, word 1 of the header at world <c>+0xB94</c>.</summary>
    private int aiThinkCursor;
    private readonly KrustyBrain?[] krustyBrains = new KrustyBrain?[PlayerCount];

    /// <summary>
    /// <c>0x41AC2C</c>, after the actors and projectiles of each update. It
    /// runs only when the update counter is a multiple of 4. On update 4 every
    /// computer player thinks; afterwards one player slot per run, in turn, so
    /// a computer player thinks every 32 updates.
    /// </summary>
    private void UpdateComputerPlayers()
    {
        var update = WorldUpdateCounter;
        if ((update & 3) != 0) return;
        if (update == 4)
        {
            for (var player = 0; player < PlayerCount; player++)
                if (aiProfiles[player] != 0) ThinkComputerPlayer(player);
            return;
        }
        aiThinkCursor = (aiThinkCursor + 1) % PlayerCount;
        if (aiProfiles[aiThinkCursor] != 0) ThinkComputerPlayer(aiThinkCursor);
    }

    /// <summary>
    /// <c>0x41AB20</c>: every module of the profile is scored, with one draw
    /// from the shared stream per module. A module replaces the current
    /// choice when its score exceeds <c>draw * running sum / 32767</c>, so it
    /// wins with probability score / sum. The chosen module's action runs.
    /// </summary>
    private void ThinkComputerPlayer(int player)
    {
        var profile = aiProfiles[player];
        if (profile is < 1 or > NativeAiProfileCount) return;
        var modules = AiModules(profile);
        var sum = 0;
        var chosen = -1;
        for (var module = 0; module < modules.Length; module++)
        {
            var score = modules[module].Score(player);
            var draw = NextNativeRandom();
            if (score > draw * (double)(sum + score) * AiChoiceScale) chosen = module;
            sum += score;
        }
        if (chosen >= 0) modules[chosen].Act(player);
    }

    private readonly record struct AiModule(Func<int, int> Score, Action<int> Act);

    /// <summary>
    /// Module tables. Profile 3 has one module whose score is always 1
    /// (<c>0x44BC5C</c>) and whose action is the Krusty planner
    /// (<c>0x44BE40</c>); profile 4 one whose score is 0 (<c>0x44D6E0</c>).
    /// Profiles 1 and 2 (five and two modules at <c>0x47B278</c> and
    /// <c>0x47B2BC</c>) appear in no shipped scenario; the port scores their
    /// modules 0, which keeps their draws from the shared stream.
    /// </summary>
    private AiModule[] AiModules(int profile) => profile switch
    {
        KrustyAiProfile => [new AiModule(_ => 1, ThinkKrusty)],
        1 => [.. Enumerable.Repeat(new AiModule(_ => 0, _ => { }), 5)],
        2 => [.. Enumerable.Repeat(new AiModule(_ => 0, _ => { }), 2)],
        _ => [new AiModule(_ => 0, _ => { })],
    };

    /// <summary>The Krusty planner's per-player state (player <c>+0xBC0</c>), created on its first think.</summary>
    public KrustyBrain? KrustyState(int player) => (uint)player < PlayerCount ? krustyBrains[player] : null;

    /// <summary><c>0x44BE40</c>: creates the state on first use (<c>0x44BD2C</c>), then plans.</summary>
    private void ThinkKrusty(int player) => KrustyBrainFor(player).Think();

    private KrustyBrain KrustyBrainFor(int player) => krustyBrains[player] ??= new KrustyBrain(this, player);

    /// <summary>
    /// <c>0x41AD68</c> calls the controller's message method at once; Krusty's
    /// (<c>0x44BF54</c>) creates the state if needed. Other profiles ignore it.
    /// </summary>
    private void DeliverAiMessage(int player, IReadOnlyList<int> values)
    {
        if (aiProfiles[player] == KrustyAiProfile) KrustyBrainFor(player).HandleMessage(values);
    }
}
