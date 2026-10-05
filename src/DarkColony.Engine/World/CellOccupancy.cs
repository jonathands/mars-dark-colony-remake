namespace DarkColony.Engine.World;

/// <summary>
/// Authoritative cell ownership. Multi-cell claims are atomic. Each owner's
/// cells are also kept by owner, so finding or releasing an actor's cells
/// does not scan the whole grid.
/// </summary>
public sealed class CellOccupancy
{
    private readonly Dictionary<CellCoordinate, int> owners = [];
    private readonly Dictionary<int, HashSet<CellCoordinate>> cellsByOwner = [];

    public int Count => owners.Count;
    public IEnumerable<KeyValuePair<CellCoordinate, int>> Claims => owners;

    public bool IsOccupied(CellCoordinate cell) => owners.ContainsKey(cell);

    public bool TryGetOwner(CellCoordinate cell, out int entityInstanceId) =>
        owners.TryGetValue(cell, out entityInstanceId);

    /// <summary>The cells an owner holds, in no particular order.</summary>
    public IReadOnlyCollection<CellCoordinate> CellsOf(int entityInstanceId) =>
        cellsByOwner.TryGetValue(entityInstanceId, out var cells) ? cells : [];

    public bool TryClaim(int entityInstanceId, IEnumerable<CellCoordinate> cells)
    {
        var claim = cells.Distinct().ToArray();
        if (claim.Any(cell => owners.TryGetValue(cell, out var owner) && owner != entityInstanceId)) return false;
        foreach (var cell in claim) Set(cell, entityInstanceId);
        return true;
    }

    public bool TryMove(int entityInstanceId, CellCoordinate source, CellCoordinate destination)
    {
        if (!owners.TryGetValue(source, out var sourceOwner) || sourceOwner != entityInstanceId) return false;
        if (owners.TryGetValue(destination, out var destinationOwner) && destinationOwner != entityInstanceId) return false;
        Remove(source);
        Set(destination, entityInstanceId);
        return true;
    }

    /// <summary>SCN object construction writes its handle without an emptiness test.</summary>
    public void ReplaceClaims(int entityInstanceId, IEnumerable<CellCoordinate> cells)
    {
        foreach (var cell in cells.Distinct()) Set(cell, entityInstanceId);
    }

    public void ReleaseCell(CellCoordinate cell) => Remove(cell);

    public void Release(int entityInstanceId)
    {
        if (!cellsByOwner.Remove(entityInstanceId, out var cells)) return;
        foreach (var cell in cells) owners.Remove(cell);
    }

    private void Set(CellCoordinate cell, int owner)
    {
        if (owners.TryGetValue(cell, out var previous))
        {
            if (previous == owner) return;
            Forget(previous, cell);
        }
        owners[cell] = owner;
        if (!cellsByOwner.TryGetValue(owner, out var cells)) cellsByOwner[owner] = cells = [];
        cells.Add(cell);
    }

    private void Remove(CellCoordinate cell)
    {
        if (owners.Remove(cell, out var owner)) Forget(owner, cell);
    }

    private void Forget(int owner, CellCoordinate cell)
    {
        if (cellsByOwner.TryGetValue(owner, out var cells) && cells.Remove(cell) && cells.Count == 0) cellsByOwner.Remove(owner);
    }
}
