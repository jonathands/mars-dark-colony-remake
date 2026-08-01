namespace DarkColony.Engine.World;

/// <summary>Authoritative cell ownership. Multi-cell claims are atomic.</summary>
public sealed class CellOccupancy
{
    private readonly Dictionary<CellCoordinate, int> owners = [];

    public int Count => owners.Count;
    public IEnumerable<KeyValuePair<CellCoordinate, int>> Claims => owners;

    public bool IsOccupied(CellCoordinate cell) => owners.ContainsKey(cell);

    public bool TryGetOwner(CellCoordinate cell, out int entityInstanceId) =>
        owners.TryGetValue(cell, out entityInstanceId);

    public bool TryClaim(int entityInstanceId, IEnumerable<CellCoordinate> cells)
    {
        var claim = cells.Distinct().ToArray();
        if (claim.Any(cell => owners.TryGetValue(cell, out var owner) && owner != entityInstanceId)) return false;
        foreach (var cell in claim) owners[cell] = entityInstanceId;
        return true;
    }

    public bool TryMove(int entityInstanceId, CellCoordinate source, CellCoordinate destination)
    {
        if (!owners.TryGetValue(source, out var sourceOwner) || sourceOwner != entityInstanceId) return false;
        if (owners.TryGetValue(destination, out var destinationOwner) && destinationOwner != entityInstanceId) return false;
        owners.Remove(source);
        owners[destination] = entityInstanceId;
        return true;
    }

    /// <summary>SCN object construction writes its handle without an emptiness test.</summary>
    public void ReplaceClaims(int entityInstanceId, IEnumerable<CellCoordinate> cells)
    {
        foreach (var cell in cells.Distinct()) owners[cell] = entityInstanceId;
    }

    public void Release(int entityInstanceId)
    {
        foreach (var cell in owners.Where(pair => pair.Value == entityInstanceId).Select(pair => pair.Key).ToArray())
            owners.Remove(cell);
    }
}
