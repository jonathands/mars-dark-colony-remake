namespace DarkColony.Engine.World;

public readonly record struct CellCoordinate(int X, int Z)
{
    public CellCoordinate Offset(CellCoordinate delta) => new(X + delta.X, Z + delta.Z);
}
