namespace DarkColony.Engine.Assets;

/// <summary>
/// The FIN files <c>anim.dat</c> lists, read in its order, which is the order
/// the game loads them. A name found in several files resolves to the first
/// one, as the game's name lookup (<c>0x4254D4</c>) does. Files the list does
/// not name are never loaded by the game.
/// </summary>
public sealed class LoadedAnimations
{
    private readonly Dictionary<string, AnimationDefinition> definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Path, AnimationRange Animation)> byName = new(StringComparer.OrdinalIgnoreCase);

    private LoadedAnimations() { }

    public static LoadedAnimations Empty { get; } = new();

    /// <summary>Whether an animation of this exact name is loaded.</summary>
    public bool Contains(string name) => byName.ContainsKey(name);

    /// <summary>The first loaded animation of this exact name, with its FIN path.</summary>
    public (string Path, AnimationRange Animation)? Find(string name) => byName.TryGetValue(name, out var found) ? found : null;

    /// <summary>A loaded FIN file by its full path, or null when the game does not load it.</summary>
    public AnimationDefinition? Definition(string path) => definitions.GetValueOrDefault(Path.GetFullPath(path));

    public static LoadedAnimations Load(Data.GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var loaded = new LoadedAnimations();
        foreach (var fileName in EntityAnimationCatalog.LoadOrder(installation.DataFile("anim.dat")))
        {
            var path = Path.GetFullPath(installation.DataFile("animate", fileName));
            if (loaded.definitions.ContainsKey(path) || !File.Exists(path)) continue;
            AnimationDefinition definition;
            try { definition = AnimationDefinition.Load(path); }
            catch (InvalidDataException) { continue; }
            loaded.definitions[path] = definition;
            foreach (var animation in definition.Animations)
                if (animation.LastFrame >= animation.FirstFrame) loaded.byName.TryAdd(animation.Name, (path, animation));
        }
        return loaded;
    }
}
