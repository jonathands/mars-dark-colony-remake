using DarkColony.Engine.Data;
using DarkColony.Engine.Terrain;
using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// Team visibility as <c>dc.exe</c> keeps it: per-player bits in the ground
/// grid that the world update clears and restamps only when the clock is zero
/// (<c>0x41988C</c>) and every 16 updates (<c>0x419A30</c>: <c>0x4456F0</c>
/// clears, <c>0x44A6D4</c> stamps). Between refreshes every query reads the
/// last snapshot.
/// </summary>
public sealed partial class ScenarioSimulation
{
    private const int TerrainSightPasses = 1;
    private const int TerrainSightShaded = 2;

    private NativeVisionTrees visionTrees = NativeVisionTrees.Flat;
    // Per world cell (z * width + x): MAP attribute bit 7 (sight passes) and
    // bit 8 (shaded), from the loader's word `attribute << 22` (bits 29, 30).
    private byte[]? terrainSight;
    private bool[][]? teamVision;
    // Grid bit 31: every cell a team's stamps ever reached, shaded or not. The
    // original keeps it for the local player's display only; it never feeds
    // back into the simulation.
    private bool[][]? teamExplored;
    private bool[][]? teamReached;

    /// <summary>
    /// The sight radius <c>0x44A7B9</c> stamps: day and night sight blended by
    /// the lighting level (<c>world + 0x540</c>, 0 in full day, 256 at night).
    /// </summary>
    public int ObservationRange(SimulatedActor actor)
    {
        var definition = EffectiveDefinition(actor);
        var night = DayNight.LightingLevel;
        return (night * definition.NightObservation + (256 - night) * definition.DayObservation) >> 8;
    }

    /// <summary>Whether the team's bit is set in the cell's last visibility snapshot.</summary>
    public bool IsCellVisibleToTeam(int teamId, CellCoordinate cell)
    {
        if (teamId < 0 || (uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height) return false;
        var vision = EnsureVision();
        var index = cell.Z * path.Width + cell.X;
        // A player tests the grid word against its mask (player + 0x19C0): its
        // own bit plus the players it shares vision with. Teams 8 and 9 have
        // no mask; their scans use the union.
        var mask = teamId < 8 ? visionMasks[teamId] : 0xff;
        for (var player = 0; player < 8; player++)
            if ((mask & (1 << player)) != 0 && vision[player][index]) return true;
        return false;
    }

    /// <summary>
    /// Whether the team's stamps ever reached the cell: the explored-terrain
    /// memory the original draws for the local player (grid bit 31). Display only.
    /// </summary>
    public bool IsCellExploredByTeam(int teamId, CellCoordinate cell)
    {
        if (teamId is < 0 or > 7 || (uint)cell.X >= (uint)path.Width || (uint)cell.Z >= (uint)path.Height) return false;
        EnsureVision();
        return teamExplored![teamId][cell.Z * path.Width + cell.X];
    }

    /// <summary>Visibility of a live actor at its authoritative occupied cell.</summary>
    public bool IsActorVisibleToTeam(int teamId, SimulatedActor actor) =>
        !actor.IsDestroyed && IsCellVisibleToTeam(teamId, actor.Movement.OccupiedCell);

    private bool IsCellVisibleForScan(int team, CellCoordinate cell) => IsCellVisibleToTeam(team, cell);

    private void LoadTerrainSight(TerrainMap? terrain)
    {
        if (terrain is null) return;
        if (terrain.Width != path.Width || terrain.Height != path.Height)
            throw new InvalidDataException("The MAP and PTH dimensions differ.");
        terrainSight = new byte[path.Width * path.Height];
        for (var z = 0; z < path.Height; z++)
        for (var x = 0; x < path.Width; x++)
        {
            // Row ysize - 1 - z, as the loader's world mirror.
            var cell = terrain[x, path.Height - 1 - z];
            terrainSight[z * path.Width + x] = (byte)(((cell.Flags & 0x80) != 0 ? TerrainSightPasses : 0) |
                                                      ((cell.Ambient & 0x01) != 0 ? TerrainSightShaded : 0));
        }
    }

    private bool[][] EnsureVision()
    {
        if (teamVision is null) RefreshVision();
        return teamVision!;
    }

    /// <summary>
    /// <c>0x44A6D4</c>: clears every actor's revealed byte (<c>+0xCA</c>), then
    /// stamps each live actor of teams 0-7 whose radius is 1 to 12. The sight
    /// tree is walked from the actor's position cell. A cell is stamped and,
    /// for a mine detector (gamestat value 16), any mine on it is revealed to
    /// the viewer's team. Sight goes on into a node's children only through a
    /// cell with MAP attribute bit 7, unless the viewer flies (movement class
    /// nonzero). A cell with attribute bit 8 at depth 2 or more gets no team
    /// bit (shaded) but still blocks or passes sight as above.
    /// A dying actor (state 10) still stamps, with a shrinking radius. An
    /// actor waiting for contact (<c>+0xCB</c> 1 or 2) does not stamp.
    /// </summary>
    private void RefreshVision()
    {
        var cells = path.Width * path.Height;
        teamVision ??= Enumerable.Range(0, 8).Select(_ => new bool[cells]).ToArray();
        teamExplored ??= Enumerable.Range(0, 8).Select(_ => new bool[cells]).ToArray();
        teamReached ??= Enumerable.Range(0, 8).Select(_ => new bool[cells]).ToArray();
        foreach (var grid in teamVision) Array.Clear(grid);
        foreach (var grid in teamReached) Array.Clear(grid);
        foreach (var actor in actors) actor.RevealedTeamMask = 0;
        var expands = new bool[512];
        foreach (var viewer in actors)
        {
            if (!IsInWorld(viewer) || viewer.Seed.Team is < 0 or > 7 ||
                viewer.ContactRole is ContactRole.Rescue or ContactRole.Pickup) continue;
            var radius = ObservationRange(viewer);
            if (radius is < 1 or > 12) continue;
            radius = DyingObservationRange(viewer, radius);
            var nodes = visionTrees.Nodes(radius);
            if (nodes.Count > expands.Length) expands = new bool[nodes.Count];
            var definition = EffectiveDefinition(viewer);
            var flies = definition.MovementClass != 0;
            var detects = definition.DetectsMines;
            var team = viewer.Seed.Team;
            var seen = teamVision[team];
            var reached = teamReached[team];
            var origin = viewer.Movement.VisualPosition.Cell;
            for (var index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                expands[index] = false;
                if (node.Parent >= 0 && !expands[node.Parent]) continue;
                var x = origin.X + node.DeltaX;
                var z = origin.Z + node.DeltaZ;
                if ((uint)x >= (uint)path.Width || (uint)z >= (uint)path.Height) continue;
                var cellIndex = z * path.Width + x;
                var sight = terrainSight?[cellIndex] ?? TerrainSightPasses;
                if ((sight & TerrainSightShaded) == 0 || node.Depth < 2) seen[cellIndex] = true;
                reached[cellIndex] = true;
                if (detects && MineOccupancy.TryGetOwner(new CellCoordinate(x, z), out var mineId) &&
                    actorsById.TryGetValue(mineId, out var mine))
                    mine.RevealedTeamMask |= 1 << team;
                expands[index] = flies || (sight & TerrainSightPasses) != 0;
            }
        }
        // Bit 31 is set by every stamp of a player the viewer shares vision
        // with (stamping flag bit 0), shaded cells included.
        for (var player = 0; player < 8; player++)
        for (var owner = 0; owner < 8; owner++)
        {
            if ((visionMasks[player] & (1 << owner)) == 0) continue;
            var reached = teamReached[owner];
            var explored = teamExplored[player];
            for (var index = 0; index < cells; index++)
                if (reached[index]) explored[index] = true;
        }
    }
}
