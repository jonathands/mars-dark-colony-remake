using DarkColony.Engine.Data;

namespace DarkColony.Engine.Assets;

public sealed record EntityAnimationCandidate(
    string FinPath,
    string AnimationName,
    ushort FirstFrame,
    ushort LastFrame,
    bool ExactFileStem);

public sealed record DirectionalAnimationSelection(
    EntityAnimationCandidate Candidate,
    int RequestedSector,
    int AnimationSector,
    bool ExactSector);

public sealed class EntityAnimationCatalog
{
    private readonly IReadOnlyDictionary<int, IReadOnlyList<EntityAnimationCandidate>> candidates;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> moveCandidates;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> deployCandidates;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> fireCandidates;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> hitCandidates;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<EntityAnimationCandidate>> deathCandidates;

    private EntityAnimationCatalog(
        IReadOnlyDictionary<int, IReadOnlyList<EntityAnimationCandidate>> candidates,
        IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> moveCandidates,
        IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> deployCandidates,
        IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> fireCandidates,
        IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> hitCandidates,
        IReadOnlyDictionary<int, IReadOnlyList<EntityAnimationCandidate>> deathCandidates)
    {
        this.candidates = candidates;
        this.moveCandidates = moveCandidates;
        this.deployCandidates = deployCandidates;
        this.fireCandidates = fireCandidates;
        this.hitCandidates = hitCandidates;
        this.deathCandidates = deathCandidates;
    }

    public static EntityAnimationCatalog Build(EntityCatalog entities, string animateDirectory)
    {
        var codes = entities.Entities.Select(entity => entity.Code).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(code => code.Length).ToArray();
        var byCode = codes.ToDictionary(code => code, _ => new List<EntityAnimationCandidate>(), StringComparer.OrdinalIgnoreCase);
        var movesByCode = codes.ToDictionary(code => code, _ => new List<(int Sector, EntityAnimationCandidate Candidate)>(), StringComparer.OrdinalIgnoreCase);
        var deploysByCode = codes.ToDictionary(code => code, _ => new List<(int Sector, EntityAnimationCandidate Candidate)>(), StringComparer.OrdinalIgnoreCase);
        var firesByCode = codes.ToDictionary(code => code, _ => new List<(int Sector, EntityAnimationCandidate Candidate)>(), StringComparer.OrdinalIgnoreCase);
        var hitsByCode = codes.ToDictionary(code => code, _ => new List<(int Sector, EntityAnimationCandidate Candidate)>(), StringComparer.OrdinalIgnoreCase);
        var deathsByCode = codes.ToDictionary(code => code, _ => new List<EntityAnimationCandidate>(), StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(animateDirectory, "*.fin", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            AnimationDefinition definition;
            try { definition = AnimationDefinition.Load(path); }
            catch (InvalidDataException) { continue; }
            foreach (var animation in definition.Animations)
            {
                var standCode = codes.FirstOrDefault(candidate => animation.Name.StartsWith(candidate + "STAND", StringComparison.OrdinalIgnoreCase));
                if (standCode is not null) byCode[standCode].Add(new EntityAnimationCandidate(
                    path,
                    animation.Name,
                    animation.FirstFrame,
                    animation.LastFrame,
                    Path.GetFileNameWithoutExtension(path).Equals(standCode, StringComparison.OrdinalIgnoreCase)));

                var deathCode = codes.FirstOrDefault(candidate => animation.Name.StartsWith(candidate + "DIE", StringComparison.OrdinalIgnoreCase));
                if (deathCode is not null) deathsByCode[deathCode].Add(new EntityAnimationCandidate(
                    path, animation.Name, animation.FirstFrame, animation.LastFrame,
                    Path.GetFileNameWithoutExtension(path).Equals(deathCode, StringComparison.OrdinalIgnoreCase)));

                AddDirectional("MOVE", movesByCode);
                AddDirectional("DEPLOY", deploysByCode);
                AddDirectional("FIRE", firesByCode);
                AddDirectional("HIT", hitsByCode);

                void AddDirectional(string family, Dictionary<string, List<(int Sector, EntityAnimationCandidate Candidate)>> target)
                {
                    var code = codes.FirstOrDefault(candidate => animation.Name.StartsWith(candidate + family, StringComparison.OrdinalIgnoreCase));
                    if (code is null || !TryTrailingSector(animation.Name[(code.Length + family.Length)..], out var sector)) return;
                    target[code].Add((sector, new EntityAnimationCandidate(
                        path,
                        animation.Name,
                        animation.FirstFrame,
                        animation.LastFrame,
                        Path.GetFileNameWithoutExtension(path).Equals(code, StringComparison.OrdinalIgnoreCase))));
                }
            }
        }

        var result = new Dictionary<int, IReadOnlyList<EntityAnimationCandidate>>();
        var moveResult = new Dictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>>();
        var deployResult = new Dictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>>();
        var fireResult = new Dictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>>();
        var hitResult = new Dictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>>();
        var deathResult = new Dictionary<int, IReadOnlyList<EntityAnimationCandidate>>();
        foreach (var entity in entities.Entities)
        {
            var selectedPerFile = byCode[entity.Code]
                .GroupBy(item => item.FinPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(item => item.AnimationName.Equals(entity.Code + "STAND0", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(item => item.AnimationName.EndsWith('0') ? 0 : 1)
                    .ThenBy(item => item.AnimationName, StringComparer.OrdinalIgnoreCase).First())
                .OrderBy(item => item.ExactFileStem ? 0 : 1)
                .ThenBy(item => item.FinPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.AnimationName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            result[entity.Id] = selectedPerFile;
            moveResult[entity.Id] = movesByCode[entity.Code]
                .OrderBy(item => item.Candidate.ExactFileStem ? 0 : 1).ThenBy(item => item.Candidate.FinPath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Sector).ToArray();
            deployResult[entity.Id] = deploysByCode[entity.Code]
                .OrderBy(item => item.Candidate.ExactFileStem ? 0 : 1).ThenBy(item => item.Candidate.FinPath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Sector).ToArray();
            fireResult[entity.Id] = firesByCode[entity.Code]
                // FIREA/B/C are distinct families in the installed assets.
                // Normal map firing uses the plain family when present; the
                // source ordering otherwise places A ahead of B/C. Make that
                // policy explicit instead of letting directory/frame order
                // accidentally choose Cyborg's alternate FIREB sequence.
                .OrderBy(item => item.Candidate.ExactFileStem ? 0 : 1)
                .ThenBy(item => item.Candidate.FinPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => FireVariantPriority(entity.Code, item.Candidate.AnimationName))
                .ThenBy(item => item.Sector)
                .ToArray();
            hitResult[entity.Id] = hitsByCode[entity.Code]
                .OrderBy(item => item.Candidate.ExactFileStem ? 0 : 1).ThenBy(item => item.Candidate.FinPath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Sector).ToArray();
            deathResult[entity.Id] = deathsByCode[entity.Code]
                .OrderBy(item => item.ExactFileStem ? 0 : 1)
                .ThenBy(item => item.AnimationName.EndsWith('0') ? 0 : 1)
                .ThenBy(item => item.FinPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return new EntityAnimationCatalog(result, moveResult, deployResult, fireResult, hitResult, deathResult);
    }

    public IReadOnlyList<EntityAnimationCandidate> Candidates(int entityId) => candidates.GetValueOrDefault(entityId, []);
    public EntityAnimationCandidate? Preferred(int entityId) => Candidates(entityId).FirstOrDefault();

    public DirectionalAnimationSelection? PreferredMove(int entityId, int sector)
        => PreferredDirectional(moveCandidates, entityId, sector);

    public DirectionalAnimationSelection? PreferredDeploy(int entityId, int sector)
        => PreferredDirectional(deployCandidates, entityId, sector);

    public DirectionalAnimationSelection? PreferredFire(int entityId, int sector)
        => PreferredDirectional(fireCandidates, entityId, sector);

    public DirectionalAnimationSelection? PreferredHit(int entityId, int sector)
        => PreferredDirectional(hitCandidates, entityId, sector);

    private static bool TryTrailingSector(string suffix, out int sector)
    {
        sector = 0;
        var firstDigit = suffix.Length;
        while (firstDigit > 0 && char.IsDigit(suffix[firstDigit - 1])) firstDigit--;
        return firstDigit < suffix.Length && int.TryParse(suffix[firstDigit..], out sector) && sector is >= 0 and <= 15;
    }

    private DirectionalAnimationSelection? PreferredDirectional(
        IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> source,
        int entityId,
        int sector)
    {
        if (sector is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(sector));
        var candidates = source.GetValueOrDefault(entityId, []);
        if (candidates.Count == 0) return null;
        var selected = candidates.OrderBy(item => CircularDistance(item.Sector, sector))
            .ThenBy(item => item.Candidate.ExactFileStem ? 0 : 1)
            .ThenBy(item => item.Sector)
            .First();
        return new DirectionalAnimationSelection(selected.Candidate, sector, selected.Sector, selected.Sector == sector);
    }

    public EntityAnimationCandidate? PreferredDeath(int entityId) => deathCandidates.GetValueOrDefault(entityId, []).FirstOrDefault();

    private static int CircularDistance(int left, int right)
    {
        var distance = Math.Abs(left - right);
        return Math.Min(distance, 16 - distance);
    }

    private static int FireVariantPriority(string entityCode, string animationName)
    {
        var suffix = animationName[(entityCode.Length + "FIRE".Length)..];
        var firstDigit = suffix.Length;
        while (firstDigit > 0 && char.IsDigit(suffix[firstDigit - 1])) firstDigit--;
        return suffix[..firstDigit].ToUpperInvariant() switch
        {
            "" => 0,
            "A" => 1,
            "B" => 2,
            "C" => 3,
            _ => 4,
        };
    }
}
