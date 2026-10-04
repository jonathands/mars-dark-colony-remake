using DarkColony.Engine.Scenario;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// The players' alliance and shared-vision bits: two 8×8 bit matrices
/// (<c>world + 0x471A0</c> and <c>+0x471A4</c>, one byte per player) written
/// by <c>0x41E7D8</c>. Every world update recomputes relations and vision
/// masks from them (<c>0x4198D3</c>). A pair counts only when both players
/// set the other's bit (<c>0x41E820</c>).
/// </summary>
public sealed partial class ScenarioSimulation
{
    private const int PlayerCount = 8;
    private readonly byte[] allianceBits = new byte[PlayerCount];
    private readonly byte[] visionBits = new byte[PlayerCount];
    // Player +0x19C0: bit q set when the player sees what player q stamps.
    private readonly byte[] visionMasks = new byte[PlayerCount];

    /// <summary>Whether the two players have both set each other's alliance bit.</summary>
    public bool AreAllied(int first, int second) =>
        (uint)first < PlayerCount && (uint)second < PlayerCount && Mutual(allianceBits, first, second);

    /// <summary>
    /// The Allies panel packet (<c>0x41D7B3</c>): sets or clears one direction
    /// of one matrix. A pair takes effect only once both players set it.
    /// </summary>
    public void SetAllianceBit(int player, int other, bool on) => SetBit(allianceBits, player, other, on);

    /// <inheritdoc cref="SetAllianceBit"/>
    public void SetVisionBit(int player, int other, bool on) => SetBit(visionBits, player, other, on);

    /// <summary>Whether <paramref name="player"/> has set its alliance bit for <paramref name="other"/>.</summary>
    public bool OffersAlliance(int player, int other) =>
        (uint)player < PlayerCount && (uint)other < PlayerCount && (allianceBits[player] & (1 << other)) != 0;

    private static void SetBit(byte[] bits, int player, int other, bool on)
    {
        if ((uint)player >= PlayerCount || (uint)other >= PlayerCount) return;
        if (on) bits[player] |= (byte)(1 << other);
        else bits[player] &= (byte)~(1 << other);
    }

    /// <summary>Whether <paramref name="viewer"/> sees what <paramref name="owner"/>'s units see.</summary>
    public bool SharesVision(int viewer, int owner) =>
        (uint)viewer < PlayerCount && (uint)owner < PlayerCount && (visionMasks[viewer] & (1 << owner)) != 0;

    /// <summary>
    /// SCN loader (<c>0x41BF63</c>): a team's first eight <c>%TeamAllies</c>
    /// values set its alliance bits; its own entry is set in both matrices.
    /// Every shipped SCN has all eight TEAM blocks (and no alliance flags), so
    /// the port sets every player's own bits, which also keeps fixtures
    /// without TEAM blocks from turning a team against itself.
    /// </summary>
    private void LoadAlliances(ScenarioDefinition scenario)
    {
        for (var player = 0; player < PlayerCount; player++)
        {
            allianceBits[player] |= (byte)(1 << player);
            visionBits[player] |= (byte)(1 << player);
        }
        foreach (var team in scenario.Teams.Where(team => team.TeamId is >= 0 and < PlayerCount))
            for (var other = 0; other < Math.Min(PlayerCount, team.AllianceFlags.Count); other++)
                if (team.AllianceFlags[other] != 0) allianceBits[team.TeamId] |= (byte)(1 << other);
        RecomputeAlliances();
    }

    /// <summary>
    /// <c>0x4198D3</c>, after the troop cap and before day/night: relation
    /// [p][q] (p, q &lt; 8) becomes the mutual alliance bit, and each player's
    /// vision mask its own bit plus every mutual vision bit.
    /// </summary>
    private void RecomputeAlliances()
    {
        for (var player = 0; player < PlayerCount; player++)
        {
            var mask = 0;
            for (var other = 0; other < PlayerCount; other++)
            {
                TeamRelations.SetRelation(player, other, Mutual(allianceBits, player, other) ? (byte)1 : (byte)0);
                if (Mutual(visionBits, player, other)) mask |= 1 << other;
            }
            visionMasks[player] = (byte)(mask | 1 << player);
        }
    }

    /// <summary>
    /// Mission actions <c>ally a b v</c> (<c>0x43D9ED</c>: also writes relation
    /// [a][b] directly) and <c>vision a b v</c>: both bits [a].b and [b].a are
    /// set when v is 1 and cleared otherwise (<c>0x41E7E8</c>).
    /// </summary>
    private static void SetMutualBits(byte[] bits, int first, int second, int value)
    {
        if ((uint)first >= PlayerCount || (uint)second >= PlayerCount) return;
        if (value == 1)
        {
            bits[first] |= (byte)(1 << second);
            bits[second] |= (byte)(1 << first);
        }
        else
        {
            bits[first] &= (byte)~(1 << second);
            bits[second] &= (byte)~(1 << first);
        }
    }

    private static bool Mutual(byte[] bits, int first, int second) =>
        (bits[first] & (1 << second)) != 0 && (bits[second] & (1 << first)) != 0;
}
