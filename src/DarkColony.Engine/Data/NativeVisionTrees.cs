using System.Buffers.Binary;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Data;

/// <summary>
/// The sight trees of <c>dc.exe</c>. <c>0x488FC8</c> holds one root pointer per
/// sight radius; a node is <c>{ int dx, int dz, int childBytes, int child[8] }</c>
/// with <c>childBytes = (children - 1) * 4</c>. The stamping routines
/// (<c>0x449D20</c> and its variants) walk the tree depth first from the
/// viewer's cell and descend into a node's children only when its cell lets
/// sight through. A node's level is its depth (<c>0x47B098</c> maps level d to
/// d + 1). For radii 1 to 10 the tree visits exactly the cells with
/// dx² + dz² ≤ r², each once. Radius 0 is never stamped, and the shipped
/// gamestat sight values stop at 10. The tables are read from the user's
/// <c>dc.exe</c>.
/// </summary>
public sealed class NativeVisionTrees
{
    public const uint RootTableAddress = 0x488FC8;
    public const int MaximumRadius = 10;
    private const int NodeSize = 12 + 8 * 4;
    private const int MaximumNodes = 4096;

    private readonly VisionNode[][] trees;

    private NativeVisionTrees(VisionNode[][] trees) => this.trees = trees;

    /// <summary>
    /// The nodes of the radius's tree in depth-first order (a parent precedes
    /// its children); <see cref="VisionNode.Parent"/> is -1 for the root.
    /// </summary>
    public IReadOnlyList<VisionNode> Nodes(int radius) =>
        radius is >= 1 and <= MaximumRadius ? trees[radius] : [];

    public static NativeVisionTrees Load(string executablePath) => FromImage(PeImage.Load(executablePath));

    public static NativeVisionTrees FromImage(PeImage image)
    {
        var roots = image.AtVirtualAddress(RootTableAddress, (MaximumRadius + 1) * 4);
        var trees = new VisionNode[MaximumRadius + 1][];
        trees[0] = [];
        for (var radius = 1; radius <= MaximumRadius; radius++)
        {
            var nodes = new List<VisionNode>();
            var pending = new Stack<(uint Address, int Depth, int Parent)>();
            pending.Push((BinaryPrimitives.ReadUInt32LittleEndian(roots[(radius * 4)..]), 0, -1));
            while (pending.Count > 0)
            {
                var (address, depth, parent) = pending.Pop();
                if (nodes.Count >= MaximumNodes) throw new InvalidDataException($"Sight tree {radius} does not end.");
                var node = image.AtVirtualAddress(address, NodeSize);
                var index = nodes.Count;
                nodes.Add(new VisionNode(BinaryPrimitives.ReadInt32LittleEndian(node), BinaryPrimitives.ReadInt32LittleEndian(node[4..]),
                    depth, parent));
                var children = BinaryPrimitives.ReadInt32LittleEndian(node[8..]) / 4 + 1;
                if (children is < 0 or > 8) throw new InvalidDataException($"Sight tree {radius} has a node with {children} children.");
                for (var child = children - 1; child >= 0; child--)
                {
                    var childAddress = BinaryPrimitives.ReadUInt32LittleEndian(node[(12 + child * 4)..]);
                    if (childAddress != 0) pending.Push((childAddress, depth + 1, index));
                }
            }
            var cells = nodes.Select(node => new CellCoordinate(node.DeltaX, node.DeltaZ)).ToHashSet();
            if (cells.Count != nodes.Count || !cells.SetEquals(Disc(radius)))
                throw new InvalidDataException($"Sight tree {radius} does not cover the radius-{radius} disc once.");
            trees[radius] = [.. nodes];
        }
        return new NativeVisionTrees(trees);
    }

    /// <summary>
    /// For checks without an installation: each disc cell hangs directly from
    /// the viewer's cell, so only that cell's opacity can block sight.
    /// </summary>
    public static NativeVisionTrees Flat { get; } = new(
        Enumerable.Range(0, MaximumRadius + 1).Select(radius => radius == 0
            ? Array.Empty<VisionNode>()
            : Disc(radius).OrderBy(cell => cell != default).ThenBy(cell => cell.Z).ThenBy(cell => cell.X)
                .Select(cell => cell == default ? new VisionNode(0, 0, 0, -1) : new VisionNode(cell.X, cell.Z, 1, 0)).ToArray()).ToArray());

    private static IEnumerable<CellCoordinate> Disc(int radius)
    {
        for (var dz = -radius; dz <= radius; dz++)
        for (var dx = -radius; dx <= radius; dx++)
            if (dx * dx + dz * dz <= radius * radius) yield return new CellCoordinate(dx, dz);
    }
}

/// <summary>One cell of a sight tree, relative to the viewer.</summary>
public readonly record struct VisionNode(int DeltaX, int DeltaZ, int Depth, int Parent);
