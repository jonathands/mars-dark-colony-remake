using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DarkColony.Engine.Combat;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// Canonical description of a simulation's observable state, used to prove
/// determinism and to detect unintended behavior changes.
/// </summary>
/// <remarks>
/// The text is built from public properties only, so a refactor that keeps
/// behavior and the public API produces identical digests even when private
/// fields or subsystem classes are reorganized. New public state is picked up
/// automatically. Definition records from <c>DarkColony.Engine.Data</c> are
/// reduced to their identity: catalog content is installation data with its
/// own reader checks. Sets and dictionaries are sorted, so a change of
/// collection type cannot alter the digest.
/// </remarks>
public static class SimulationDigest
{
    private const int MaximumDepth = 16;
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertiesByType = new();

    /// <summary>Multi-line canonical text, one actor or section per line, for diffing.</summary>
    public static string Describe(ScenarioSimulation simulation)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        var text = new StringBuilder();
        foreach (var property in PublicProperties(typeof(ScenarioSimulation)))
        {
            var value = property.GetValue(simulation);
            switch (value)
            {
                case IReadOnlyList<SimulatedActor> actors:
                    foreach (var actor in actors.OrderBy(actor => actor.Seed.InstanceId))
                    {
                        text.Append("actor ");
                        AppendValue(text, actor, 0, []);
                        text.Append('\n');
                    }
                    break;
                case CellOccupancy occupancy:
                    text.Append(property.Name).Append(' ');
                    AppendValue(text, occupancy.Claims, 0, []);
                    text.Append('\n');
                    break;
                default:
                    text.Append(property.Name).Append('=');
                    AppendValue(text, value, 0, []);
                    text.Append('\n');
                    break;
            }
        }

        foreach (var team in simulation.TeamResources.Keys.Order())
        {
            text.Append("economy ").Append(team.ToString(CultureInfo.InvariantCulture)).Append(' ');
            AppendValue(text, simulation.EconomyForTeam(team), 0, []);
            text.Append('\n');
        }

        text.Append("relations ");
        for (var source = 0; source < TeamRelationMatrix.TeamCount; source++)
        {
            if (source != 0) text.Append('/');
            for (var candidate = 0; candidate < TeamRelationMatrix.TeamCount; candidate++)
                text.Append(simulation.TeamRelations.Relation(source, candidate).ToString("x", CultureInfo.InvariantCulture));
        }
        text.Append('\n');
        return text.ToString();
    }

    /// <summary>Short stable hash of <see cref="Describe"/>.</summary>
    public static string Hash(ScenarioSimulation simulation) => HashText(Describe(simulation));

    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>
    /// Folds one completed tick's full description into a running hash, so a
    /// divergence is detected at the tick where it first becomes observable.
    /// </summary>
    public sealed class Accumulator
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Add(ScenarioSimulation simulation) => hash.AppendData(Encoding.UTF8.GetBytes(Describe(simulation)));

        /// <summary>Current running value; the accumulator keeps going afterward.</summary>
        public string Current => Convert.ToHexString(hash.GetCurrentHash())[..16].ToLowerInvariant();
    }

    private static void AppendValue(StringBuilder text, object? value, int depth, HashSet<object> path)
    {
        switch (value)
        {
            case null:
                text.Append("null");
                return;
            case string literal:
                text.Append('"').Append(literal.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                return;
            case bool flag:
                text.Append(flag ? "true" : "false");
                return;
            case Enum enumeration:
                text.Append(enumeration.ToString());
                return;
            case IFormattable formattable when value.GetType().IsPrimitive || value is decimal:
                text.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
        }

        var type = value.GetType();
        if (depth >= MaximumDepth)
        {
            text.Append('…');
            return;
        }

        if (type.Namespace == "DarkColony.Engine.Data")
        {
            // Catalog definitions are constant installation content.
            text.Append(type.Name);
            var id = type.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            if (id?.GetValue(value) is { } identity) text.Append('#').Append(Convert.ToString(identity, CultureInfo.InvariantCulture));
            return;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            AppendValue(text, type.GetProperty("Key")!.GetValue(value), depth + 1, path);
            text.Append("=>");
            AppendValue(text, type.GetProperty("Value")!.GetValue(value), depth + 1, path);
            return;
        }

        if (value is IEnumerable sequence)
        {
            var items = new List<string>();
            foreach (var item in sequence)
            {
                var itemText = new StringBuilder();
                AppendValue(itemText, item, depth + 1, path);
                items.Add(itemText.ToString());
            }
            if (IsUnordered(type)) items.Sort(StringComparer.Ordinal);
            text.Append('[').AppendJoin(',', items).Append(']');
            return;
        }

        if (!type.IsValueType && !path.Add(value))
        {
            text.Append('^').Append(type.Name);
            return;
        }

        text.Append(type.Name).Append('{');
        var first = true;
        foreach (var property in PublicProperties(type))
        {
            if (!first) text.Append(';');
            first = false;
            text.Append(property.Name).Append('=');
            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch (TargetInvocationException error)
            {
                text.Append('!').Append(error.InnerException?.GetType().Name ?? "error");
                continue;
            }
            AppendValue(text, propertyValue, depth + 1, path);
        }
        text.Append('}');
        if (!type.IsValueType) path.Remove(value);
    }

    private static bool IsUnordered(Type type) =>
        type.GetInterfaces().Any(contract => contract.IsGenericType &&
            (contract.GetGenericTypeDefinition() == typeof(IReadOnlySet<>) ||
             contract.GetGenericTypeDefinition() == typeof(ISet<>) ||
             contract.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) ||
             contract.GetGenericTypeDefinition() == typeof(IDictionary<,>))) ||
        (type.IsGenericType && type.GetGenericArguments().Length == 1 &&
         type.GetGenericArguments()[0].IsGenericType &&
         type.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(KeyValuePair<,>)) ||
        type.GetInterfaces().Any(contract => contract.IsGenericType &&
            contract.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
            contract.GetGenericArguments()[0].IsGenericType &&
            contract.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(KeyValuePair<,>));

    private static PropertyInfo[] PublicProperties(Type type) => PropertiesByType.GetOrAdd(type, static candidate =>
        candidate.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0 && property.CanRead)
            // Span-like properties cannot be boxed through reflection.
            .Where(property => !property.PropertyType.IsByRefLike)
            // Compiler-generated record equality contract.
            .Where(property => property.Name != "EqualityContract")
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray());
}
