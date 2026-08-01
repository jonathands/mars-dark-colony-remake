using DarkColony.Engine.World;

namespace DarkColony.Engine.Movement;

/// <summary>Native nibble values from executable table 0x47A984/0x479208.</summary>
public enum PathDirection : byte
{
    NorthWest = 0,
    North = 1,
    NorthEast = 2,
    West = 3,
    East = 4,
    SouthWest = 5,
    South = 6,
    SouthEast = 7,
}

public static class PathDirectionExtensions
{
    public static CellCoordinate Delta(this PathDirection direction) => direction switch
    {
        PathDirection.NorthWest => new(-1, -1),
        PathDirection.North => new(0, -1),
        PathDirection.NorthEast => new(1, -1),
        PathDirection.West => new(-1, 0),
        PathDirection.East => new(1, 0),
        PathDirection.SouthWest => new(-1, 1),
        PathDirection.South => new(0, 1),
        PathDirection.SouthEast => new(1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };
}
