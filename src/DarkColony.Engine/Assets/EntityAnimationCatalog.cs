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

    private EntityAnimationCatalog(
        IReadOnlyDictionary<int, IReadOnlyList<EntityAnimationCandidate>> candidates,
        IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> moveCandidates)
    {
        this.candidates = candidates;
        this.moveCandidates = moveCandidates;
    }

    public static EntityAnimationCatalog Build(EntityCatalog entities, string animateDirectory)
    {
        var codes = entities.Entities.Select(entity => entity.Code).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(code => code.Length).ToArray();
        var byCode = codes.ToDictionary(code => code, _ => new List<EntityAnimationCandidate>(), StringComparer.OrdinalIgnoreCase);
        var movesByCode = codes.ToDictionary(code => code, _ => new List<(int Sector, EntityAnimationCandidate Candidate)>(), StringComparer.OrdinalIgnoreCase);
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

                var moveCode = codes.FirstOrDefault(candidate => animation.Name.StartsWith(candidate + "MOVE", StringComparison.OrdinalIgnoreCase));
                if (moveCode is null) continue;
                var suffix = animation.Name[(moveCode.Length + 4)..];
                if (!int.TryParse(suffix, out var sector) || sector is < 0 or > 15) continue;
                movesByCode[moveCode].Add((sector, new EntityAnimationCandidate(
                    path,
                    animation.Name,
                    animation.FirstFrame,
                    animation.LastFrame,
                    Path.GetFileNameWithoutExtension(path).Equals(moveCode, StringComparison.OrdinalIgnoreCase))));
            }
        }

        var result = new Dictionary<int, IReadOnlyList<EntityAnimationCandidate>>();
        var moveResult = new Dictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>>();
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
                .OrderBy(item => item.Candidate.ExactFileStem ? 0 : 1)
                .ThenBy(item => item.Candidate.FinPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Sector)
                .ToArray();
        }

        return new EntityAnimationCatalog(result, moveResult);
    }

    public IReadOnlyList<EntityAnimationCandidate> Candidates(int entityId) => candidates.GetValueOrDefault(entityId, []);
    public EntityAnimationCandidate? Preferred(int entityId) => Candidates(entityId).FirstOrDefault();

    public DirectionalAnimationSelection? PreferredMove(int entityId, int sector)
    {
        if (sector is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(sector));
        var candidates = moveCandidates.GetValueOrDefault(entityId, []);
        if (candidates.Count == 0) return null;
        var selected = candidates.OrderBy(item => CircularDistance(item.Sector, sector))
            .ThenBy(item => item.Candidate.ExactFileStem ? 0 : 1)
            .ThenBy(item => item.Sector)
            .First();
        return new DirectionalAnimationSelection(selected.Candidate, sector, selected.Sector, selected.Sector == sector);
    }

    private static int CircularDistance(int left, int right)
    {
        var distance = Math.Abs(left - right);
        return Math.Min(distance, 16 - distance);
    }
}
