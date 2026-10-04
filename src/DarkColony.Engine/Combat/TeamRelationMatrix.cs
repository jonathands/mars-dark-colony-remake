namespace DarkColony.Engine.Combat;

/// <summary>
/// Deterministic counterpart to the executable's 10-by-10 team-relation table.
/// The recovered attack scan accepts a candidate only when its relation byte is
/// zero; nonzero is cooperative/non-hostile for the movement-yield path.
/// </summary>
public sealed class TeamRelationMatrix
{
    public const int TeamCount = 10;
    private readonly byte[,] values = new byte[TeamCount, TeamCount];

    private TeamRelationMatrix()
    {
        // An unset native matrix is not evidence that all teams cooperate.
        // The SCN loader 0x41B920 zeroes the matrix (1 = cooperative, 0 =
        // hostile), sets each player's own entry through 0x41E7D8, and then
        // (0x41C00E) marks every player and critter team 9 mutually cooperative.
        for (var team = 0; team < TeamCount; team++) values[team, team] = 1;
        for (var player = 0; player < 8; player++)
        {
            values[player, 9] = 1;
            values[9, player] = 1;
        }
    }

    public static TeamRelationMatrix CreateDefault() => new();

    public bool IsHostile(int sourceTeam, int candidateTeam) => Relation(sourceTeam, candidateTeam) == 0;

    public byte Relation(int sourceTeam, int candidateTeam)
    {
        ValidateTeam(sourceTeam);
        ValidateTeam(candidateTeam);
        return values[sourceTeam, candidateTeam];
    }

    /// <summary>Sets one directed native relation byte; callers may create asymmetric alliances.</summary>
    public void SetRelation(int sourceTeam, int candidateTeam, byte relation)
    {
        ValidateTeam(sourceTeam);
        ValidateTeam(candidateTeam);
        values[sourceTeam, candidateTeam] = relation;
    }

    private static void ValidateTeam(int team)
    {
        if ((uint)team >= TeamCount) throw new ArgumentOutOfRangeException(nameof(team));
    }
}
