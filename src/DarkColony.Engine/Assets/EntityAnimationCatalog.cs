using System.Collections.Concurrent;
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
    private Dictionary<int, EntityAnimationCandidate> buildCandidates = [];

    // The selections are fixed once the catalog is built, and the renderer
    // asks for them for every actor on every frame. Each family keeps its 16
    // sectors per entity, resolved on first use; fire keeps one such table
    // per variant, in the native slot order.
    private enum Family { Move, Deploy, Retract, Hit }
    private readonly ConcurrentDictionary<(Family Family, int EntityId), DirectionalAnimationSelection?[]> sectorTables = new();
    private readonly ConcurrentDictionary<int, DirectionalAnimationSelection?[][]> fireTables = new();

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

    /// <summary>The FIN files <c>anim.dat</c> lists, in order: the order the game loads them.</summary>
    public static IReadOnlyList<string> LoadOrder(string animDatPath) => File.Exists(animDatPath)
        ? File.ReadAllLines(animDatPath).Select(line => line.Trim()).Where(line => line.Length != 0).ToArray()
        : [];

    /// <param name="loadOrder">
    /// The <c>anim.dat</c> file order. When given, a name found in several FIN
    /// files resolves to the first one loaded, as the game's name lookup does;
    /// without it, a file named after the entity code wins, then file name order.
    /// </param>
    public static EntityAnimationCatalog Build(EntityCatalog entities, string animateDirectory, IReadOnlyList<string>? loadOrder = null)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (loadOrder is not null)
            for (var index = 0; index < loadOrder.Count; index++) rank.TryAdd(Path.GetFileName(loadOrder[index]), index);
        int FileRank(EntityAnimationCandidate candidate) => loadOrder is null
            ? (candidate.ExactFileStem ? 0 : 1)
            : rank.GetValueOrDefault(Path.GetFileName(candidate.FinPath), int.MaxValue);
        var codes = entities.Entities.Select(entity => entity.Code).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(code => code.Length).ToArray();
        var byCode = codes.ToDictionary(code => code, _ => new List<EntityAnimationCandidate>(), StringComparer.OrdinalIgnoreCase);
        var movesByCode = codes.ToDictionary(code => code, _ => new List<(int Sector, EntityAnimationCandidate Candidate)>(), StringComparer.OrdinalIgnoreCase);
        var deploysByCode = codes.ToDictionary(code => code, _ => new List<(int Sector, EntityAnimationCandidate Candidate)>(), StringComparer.OrdinalIgnoreCase);
        var retractsByCode = codes.ToDictionary(code => code, _ => new List<(int Sector, EntityAnimationCandidate Candidate)>(), StringComparer.OrdinalIgnoreCase);
        var firesByCode = codes.ToDictionary(code => code, _ => new List<(int Sector, EntityAnimationCandidate Candidate)>(), StringComparer.OrdinalIgnoreCase);
        var hitsByCode = codes.ToDictionary(code => code, _ => new List<(int Sector, EntityAnimationCandidate Candidate)>(), StringComparer.OrdinalIgnoreCase);
        var deathsByCode = codes.ToDictionary(code => code, _ => new List<EntityAnimationCandidate>(), StringComparer.OrdinalIgnoreCase);
        var builds = new Dictionary<string, List<EntityAnimationCandidate>>(StringComparer.OrdinalIgnoreCase);
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

                if (animation.Name.EndsWith("BUILDSTAND0", StringComparison.OrdinalIgnoreCase) || animation.Name.EndsWith("BUILD0", StringComparison.OrdinalIgnoreCase))
                {
                    if (!builds.TryGetValue(animation.Name, out var named)) builds[animation.Name] = named = [];
                    named.Add(new EntityAnimationCandidate(path, animation.Name, animation.FirstFrame, animation.LastFrame, false));
                }

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
                .OrderBy(item => FileRank(item))
                .ThenBy(item => item.FinPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.AnimationName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            result[entity.Id] = selectedPerFile;
            moveResult[entity.Id] = movesByCode[entity.Code]
                .OrderBy(item => FileRank(item.Candidate)).ThenBy(item => item.Candidate.FinPath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Sector).ToArray();
            deployResult[entity.Id] = deploysByCode[entity.Code]
                .OrderBy(item => FileRank(item.Candidate)).ThenBy(item => item.Candidate.FinPath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Sector).ToArray();
            retractResult[entity.Id] = retractsByCode[entity.Code]
                .OrderBy(item => FileRank(item.Candidate)).ThenBy(item => item.Candidate.FinPath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Sector).ToArray();
            fireResult[entity.Id] = firesByCode[entity.Code]
                // FIREA/B/C are distinct families in the installed assets.
                // Normal map firing uses the plain family when present; the
                // source ordering otherwise places A ahead of B/C. Make that
                // policy explicit instead of letting directory/frame order
                // accidentally choose Cyborg's alternate FIREB sequence.
                .OrderBy(item => FileRank(item.Candidate))
                .ThenBy(item => item.Candidate.FinPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => FireVariantPriority(entity.Code, item.Candidate.AnimationName))
                .ThenBy(item => item.Sector)
                .ToArray();
            hitResult[entity.Id] = hitsByCode[entity.Code]
                .OrderBy(item => FileRank(item.Candidate)).ThenBy(item => item.Candidate.FinPath, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Sector).ToArray();
            deathResult[entity.Id] = deathsByCode[entity.Code]
                .OrderBy(item => FileRank(item))
                .ThenBy(item => item.AnimationName.EndsWith('0') ? 0 : 1)
                .ThenBy(item => item.FinPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var buildResult = new Dictionary<int, EntityAnimationCandidate>();
        foreach (var entity in entities.Entities)
        {
            // 0x43C18C: <code>BUILDSTAND, else <code>BUILD, the first one loaded.
            var named = builds.GetValueOrDefault(entity.Code + "BUILDSTAND0") ?? builds.GetValueOrDefault(entity.Code + "BUILD0");
            if (named is not null) buildResult[entity.Id] = named.OrderBy(FileRank).ThenBy(item => item.FinPath, StringComparer.OrdinalIgnoreCase).First();
        }

        return new EntityAnimationCatalog(result, moveResult, deployResult, retractResult, fireResult, hitResult, deathResult) { buildCandidates = buildResult };
    }

    /// <summary>
    /// The troop's build animation (entity <c>+0x98</c>), which a producing
    /// city building plays once: in <c>hubu.fin</c> and <c>albu.fin</c> the door
    /// opening and the troop walking out. See <see cref="TroopBuildTimings"/>.
    /// </summary>
    public EntityAnimationCandidate? PreferredBuild(int entityId) => buildCandidates.GetValueOrDefault(entityId);

    public IReadOnlyList<EntityAnimationCandidate> Candidates(int entityId) => candidates.GetValueOrDefault(entityId, []);
    public EntityAnimationCandidate? Preferred(int entityId) => Candidates(entityId).FirstOrDefault();

    public DirectionalAnimationSelection? PreferredMove(int entityId, int sector)
        => PreferredDirectional(Family.Move, moveCandidates, entityId, sector);

    public DirectionalAnimationSelection? PreferredDeploy(int entityId, int sector)
        => PreferredDirectional(Family.Deploy, deployCandidates, entityId, sector);

    public DirectionalAnimationSelection? PreferredRetract(int entityId, int sector)
        => PreferredDirectional(Family.Retract, retractCandidates, entityId, sector);

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
        var variants = FireTables(entityId);
        if (variants.Length == 0) return null;
        return variants[Math.Abs(variantRoll % variants.Length)][sector];
    }

    /// <summary>The entity's fire families (runtime <c>+0xE4</c>), which a presentation draw is taken modulo.</summary>
    public int FireVariantCount(int entityId) => FireTables(entityId).Length;

    private DirectionalAnimationSelection?[][] FireTables(int entityId) => fireTables.GetOrAdd(entityId, id =>
    {
        var (variants, orderedKeys) = FireVariants(id);
        return orderedKeys.Select(key => SectorTable(variants[key])).ToArray();
    });

    private (Dictionary<string, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> Variants, List<string> OrderedKeys) FireVariants(int entityId)
    {
        var source = fireCandidates.GetValueOrDefault(entityId, []);
        var variants = source
            .GroupBy(item => FireVariant(item.Candidate.AnimationName))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>)group.ToArray());
        var orderedKeys = new List<string>();
        if (variants.ContainsKey("A")) orderedKeys.Add("A");
        else if (variants.ContainsKey("")) orderedKeys.Add("");
        if (variants.ContainsKey("B")) orderedKeys.Add("B");
        if (variants.ContainsKey("C")) orderedKeys.Add("C");
        // FIREA took the plain family's slot, so plain FIRE is not a variant of its own
        // (ATRIL: atril.fin has FIREA, the unloaded tmp.fin a plain FIRE whose sprite is not shipped).
        orderedKeys.AddRange(variants.Keys.Where(key => !orderedKeys.Contains(key, StringComparer.OrdinalIgnoreCase) &&
                !(key.Length == 0 && orderedKeys.Contains("A")))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
        return (variants, orderedKeys);
    }

    public DirectionalAnimationSelection? PreferredHit(int entityId, int sector)
        => PreferredDirectional(Family.Hit, hitCandidates, entityId, sector);

    private static bool TryTrailingSector(string suffix, out int sector)
    {
        sector = 0;
        var firstDigit = suffix.Length;
        while (firstDigit > 0 && char.IsDigit(suffix[firstDigit - 1])) firstDigit--;
        return firstDigit < suffix.Length && int.TryParse(suffix[firstDigit..], out sector) && sector is >= 0 and <= 15;
    }

    private DirectionalAnimationSelection? PreferredDirectional(
        Family family,
        IReadOnlyDictionary<int, IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)>> source,
        int entityId,
        int sector)
    {
        if (sector is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(sector));
        return sectorTables.GetOrAdd((family, entityId), static (key, candidates) => SectorTable(candidates.GetValueOrDefault(key.EntityId, [])), source)[sector];
    }

    private static DirectionalAnimationSelection?[] SectorTable(IReadOnlyList<(int Sector, EntityAnimationCandidate Candidate)> candidates)
    {
        var table = new DirectionalAnimationSelection?[16];
        for (var sector = 0; sector < table.Length; sector++) table[sector] = PreferredDirectional(candidates, sector);
        return table;
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
                // The lists are already in file preference order (see Build).
                group => group.First().Candidate);
        var selector = sector * 2;
        for (var attempt = 0; attempt < NativeDirectionalFallbackOffsets.Count; attempt++)
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

    /// <summary>
    /// The executable's dwords at <c>0x47950C</c>, which <c>0x42624A</c> tries
    /// in turn: the selector itself, then ever farther neighbours, 1, -1, 2,
    /// -2 ... 15, -15, 16. A direction the FIN names therefore always shows
    /// its own animation. (Until 2026-10-06 the port had misread them as
    /// 3, -2, 3, -2 ..., which turned 16-direction units a sector and showed
    /// the Sarge's one-frame in-between directions while it walked.)
    /// </summary>
    public static IReadOnlyList<int> NativeDirectionalFallbackOffsets { get; } =
        [0, .. Enumerable.Range(1, 15).SelectMany(step => new[] { step, -step }), 16];

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
