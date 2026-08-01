namespace DarkColony.Engine.World;

/// <summary>Signed 8.8 world-space position used by the original simulation.</summary>
public readonly record struct FixedPointPosition(int XRaw, int ZRaw)
{
    public const int FractionBits = 8;
    public const int One = 1 << FractionBits;
    public const int Half = One / 2;

    public static FixedPointPosition AtCellCenter(CellCoordinate cell) =>
        new(checked(cell.X * One + Half), checked(cell.Z * One + Half));

    public CellCoordinate Cell => new(XRaw >> FractionBits, ZRaw >> FractionBits);

    public float X => XRaw / (float)One;
    public float Z => ZRaw / (float)One;

    public FixedPointPosition AddRaw(int xDelta, int zDelta) =>
        new(checked(XRaw + xDelta), checked(ZRaw + zDelta));
}
