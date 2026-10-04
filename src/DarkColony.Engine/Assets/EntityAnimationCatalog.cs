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
    bool ExactSector,
    int NativeSelector);

public sealed class EntityAnimationCatalog
{
    private readonly IReadOnlyDictionary<int, IReadOnlyList<EntityAnimationCandidate>> candidates;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> moveCandidates;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> deployCandidates;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> retractCandidates;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> fireCandidates;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> hitCandidates;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<EntityAnimationCandidate>> deathCandidates;

    private EntityAnimationCatalog(
        IReadOnlyDictionary<int, IReadOnlyList<EntityAnimationCandidate>> candidates,
        IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> moveCandidates,
        IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> deployCandidates,
        IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> retractCandidates,
        IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> fireCandidates,
        IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> hitCandidates,
        IReadOnlyDictionary<int, IReadOnlyList<EntityAnimationCandidate>> deathCandidates)
    {
        this.candidates = candidates;
        this.moveCandidates = moveCandidates;
        this.deployCandidates = deployCandidates;
        this.retractCandidates = retractCandidates;
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
        var retractsByCode = codes.ToDictionary(code => code, _ => new List<(int Sector, EntityAnimationCandidate Candidate)>(), StringComparer.OrdinalIgnoreCase);
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
                AddDirectional("RETRACT", retractsByCode);
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
        var retractResult = new Dictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>>();
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
            retractResult[entity.Id] = retractsByCode[entity.Code]
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

        return new EntityAnimationCatalog(result, moveResult, deployResult, retractResult, fireResult, hitResult, deathResult);
    }

    public IReadOnlyList<EntityAnimationCandidate> Candidates(int entityId) => candidates.GetValueOrDefault(entityId, []);
    public EntityAnimationCandidate? Preferred(int entityId) => Candidates(entityId).FirstOrDefault();

    public DirectionalAnimationSelection? PreferredMove(int entityId, int sector)
        => PreferredDirectional(moveCandidates, entityId, sector);

    public DirectionalAnimationSelection? PreferredDeploy(int entityId, int sector)
        => PreferredDirectional(deployCandidates, entityId, sector);

    public DirectionalAnimationSelection? PreferredRetract(int entityId, int sector)
        => PreferredDirectional(retractCandidates, entityId, sector);

    public DirectionalAnimationSelection? PreferredFire(int entityId, int sector)
        => PreferredFire(entityId, sector, 0);

    /// <summary>
    /// Mirrors the native animation-pointer loader at 0x43b970: FIREA replaces
    /// plain FIRE in slot zero when both exist, then FIREB and FIREC append.
    /// The common firing routine chooses one slot by a random roll modulo the
    /// resulting count.
    /// </summary>
    public DirectionalAnimationSelection? PreferredFire(int entityId, int sector, int variantRoll)
    {
        if (sector is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(sector));
        var source = fireCandidates.GetValueOrDefault(entityId, []);
        if (source.Count == 0) return null;
        var variants = source
            .GroupBy(item => FireVariant(item.Candidate.AnimationName))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>)group.ToArray());
        var orderedKeys = new List<string>();
        if (variants.ContainsKey("A")) orderedKeys.Add("A");
        else if (variants.ContainsKey("")) orderedKeys.Add("");
        if (variants.ContainsKey("B")) orderedKeys.Add("B");
        if (variants.ContainsKey("C")) orderedKeys.Add("C");
        orderedKeys.AddRange(variants.Keys.Where(key => !orderedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        var key = orderedKeys[Math.Abs(variantRoll % orderedKeys.Count)];
        return PreferredDirectional(variants[key], sector);
    }

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
        return PreferredDirectional(source.GetValueOrDefault(entityId, []), sector);
    }

    public EntityAnimationCandidate? PreferredDeath(int entityId) => deathCandidates.GetValueOrDefault(entityId, []).FirstOrDefault();

    /// <summary>
    /// Mirrors <c>0x4260a8</c>. The loader first resolves suffixes in its
    /// rotated <c>(12 - index) &amp; 15</c> order, then fills a 32-entry doubled
    /// selector array by trying the offsets at <c>0x47950c</c>. This is the
    /// native sparse-sector/mirroring policy: it is not a nearest-angle
    /// fallback and it applies to every directional animation family.
    /// </summary>
    private static DirectionalAnimationSelection? PreferredDirectional(
        IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)> candidates,
        int sector)
    {
        if (sector is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(sector));
        if (candidates.Count == 0) return null;
        var bySector = candidates
            .GroupBy(item => item.Sector)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.Candidate.ExactFileStem ? 0 : 1)
                    .ThenBy(item => item.Candidate.FinPath, StringComparer.OrdinalIgnoreCase)
                    .First().Candidate);
        var selector = sector * 2;
        for (var attempt = 0; attempt < NativeDirectionalFallbackOffsets.Length; attempt++)
        {
            var candidateIndex = ((selector + NativeDirectionalFallbackOffsets[attempt]) & 0x1f) >> 1;
            var animationSector = (12 - candidateIndex) & 0x0f;
            if (!bySector.TryGetValue(animationSector, out var candidate)) continue;
            return new DirectionalAnimationSelection(candidate, sector, animationSector, attempt == 0, selector);
        }
        // Some one-direction action families (notably FIRE/HIT) are loaded by
        // the same object constructor but do not expose any of the numbered
        // selector slots. Their resolved base frame remains the native action
        // fallback; this is intentionally after, never instead of, the slot
        // table used by MOVE and genuinely directional families.
        var baseCandidate = bySector.GetValueOrDefault(0) ?? bySector.OrderBy(pair => pair.Key).First().Value;
        return new DirectionalAnimationSelection(baseCandidate, sector, 0, false, selector);
    }

    // Executable dwords at 0x47950c, consumed in sequence by 0x42624a.
    private static readonly int[] NativeDirectionalFallbackOffsets =
    [3, -2, 3, -2, 3, -2, 3, 0, 0, -1, 0, -1, -1, 0, -1, 0,
     0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 0, -1, -1, 0, -1, 0];

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

    private static string FireVariant(string animationName)
    {
        var fire = animationName.IndexOf("FIRE", StringComparison.OrdinalIgnoreCase);
        if (fire < 0) return string.Empty;
        var suffix = animationName[(fire + "FIRE".Length)..];
        var firstDigit = suffix.Length;
        while (firstDigit > 0 && char.IsDigit(suffix[firstDigit - 1])) firstDigit--;
        return suffix[..firstDigit].ToUpperInvariant();
    }
}
