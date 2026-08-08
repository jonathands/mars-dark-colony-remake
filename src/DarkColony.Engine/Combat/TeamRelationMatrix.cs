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
        // Preserve the existing port policy: a team cooperates with itself and
        // otherwise is hostile until scenario/mission initialization is traced.
        for (var team = 0; team < TeamCount; team++) values[team, team] = 1;
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
