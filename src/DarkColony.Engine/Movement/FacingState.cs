using DarkColony.Engine.World;

namespace DarkColony.Engine.Movement;

public sealed class FacingState
{
    public FacingState(byte initialFacing = 0)
    {
        Current = initialFacing;
        Target = initialFacing;
    }

    public byte Current { get; private set; }
    public byte Target { get; private set; }
    public int RenderSector16 => ((Current + 8) & 0xff) >> 4;

    public void Face(PathDirection direction) => Target = direction switch
    {
        PathDirection.East => 0,
        PathDirection.SouthEast => 32,
        PathDirection.South => 64,
        PathDirection.SouthWest => 96,
        PathDirection.West => 128,
        PathDirection.NorthWest => 160,
        PathDirection.North => 192,
        PathDirection.NorthEast => 224,
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    public void Face(byte bearing) => Target = bearing;

    /// <summary>
    /// Reconstructed equivalent of the native target-bearing helper used
    /// before firing. World Z grows south, matching the cardinal table above.
    /// </summary>
    public void FaceTowards(FixedPointPosition source, FixedPointPosition target)
    {
        if (source == target) return;
        Target = NativeBearing.Between(source, target);
    }

    public bool Step(int turnSpeed)
    {
        if (turnSpeed <= 0) throw new ArgumentOutOfRangeException(nameof(turnSpeed));
        var difference = Target - Current;
        if (difference > 128) difference -= 256;
        if (difference < -128) difference += 256;
        if (difference == 0) return false;
        if (Math.Abs(difference) < turnSpeed) Current = Target;
        else Current = unchecked((byte)(Current + Math.Sign(difference) * turnSpeed));
        return Current != Target;
    }
}
