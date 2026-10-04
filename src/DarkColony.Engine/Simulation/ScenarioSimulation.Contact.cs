using DarkColony.Engine.World;

namespace DarkColony.Engine.Simulation;

/// <summary>
/// Actor byte <c>+0xCB</c>, set from the SCN flag column by the actor
/// constructor (<c>0x41B321</c>): a placement that waits for contact instead
/// of acting. Units spawned during play get 0.
/// </summary>
public enum ContactRole : byte
{
    None = 0,
    /// <summary>Joins player 0 when a player-0 unit comes within two cells.</summary>
    Rescue = 1,
    /// <summary>Pays its health to the first player whose unit comes within two cells, then dies.</summary>
    Pickup = 2,
}

public sealed partial class ScenarioSimulation
{
    private const int ScriptWordCount = 8;
    private const int ContactRadius = 2;

    /// <summary>The script array <c>u(0..7)</c> (dwords at <c>0x4FE04C</c>, zeroed at script load).</summary>
    private readonly int[] scriptWords = new int[ScriptWordCount];

    public IReadOnlyList<int> ScriptWords => scriptWords;

    /// <summary>
    /// The idle command's branch for <c>+0xCB</c> 1 and 2 (<c>0x4148E2</c> ->
    /// <c>0x4140DC</c>). Every fourth phase tick it walks the 5 x 5 square
    /// around the actor, x outer and z inner, probing the ground grid and
    /// then the alternate grid of each cell, and skips occupants that are
    /// waiting for contact themselves. The branch replaces the rest of the
    /// idle command, so these actors never scan for targets or take orders.
    /// <list type="bullet">
    /// <item>Rescue: a player-0 occupant turns the actor into a normal player-0
    /// unit, adds 1 to <c>u(0)</c> (<c>0x43FC24</c>), and ends the walk.</item>
    /// <item>Pickup: an occupant of a player 0-7 adds the actor's health to that
    /// player's P7 (<c>+0xBAC</c>), and the actor is killed (<c>0x416308</c>).</item>
    /// </list>
    /// </summary>
    private void UpdateContact(SimulatedActor actor, TickEvents events)
    {
        if ((DayNight.PhaseTicks & 3) != 0) return;
        var center = actor.Movement.VisualPosition.Cell;
        for (var x = center.X - ContactRadius; x <= center.X + ContactRadius; x++)
        {
            if ((uint)x >= (uint)path.Width) continue;
            for (var z = center.Z - ContactRadius; z <= center.Z + ContactRadius; z++)
            {
                if ((uint)z >= (uint)path.Height) continue;
                foreach (var grid in (CellOccupancy[])[GroundOccupancy, AlternateOccupancy])
                {
                    if (!grid.TryGetOwner(new CellCoordinate(x, z), out var occupantId) ||
                        !actorsById.TryGetValue(occupantId, out var occupant) || occupant.ContactRole != ContactRole.None) continue;
                    var team = occupant.Seed.Team;
                    if (actor.ContactRole == ContactRole.Rescue)
                    {
                        if (team != 0) continue;
                        actor.ContactRole = ContactRole.None;
                        actor.Seed = actor.Seed with { Team = 0 };
                        scriptWords[0]++;
                        events.ContactResolutions.Add(new ContactResolvedEvent(actor.Seed.InstanceId, ContactRole.Rescue,
                            occupant.Seed.InstanceId, 0, 0));
                        return;
                    }
                    if (actor.ContactRole != ContactRole.Pickup || (uint)team >= PlayerCount) continue;
                    var amount = actor.Health;
                    if (teamEconomies.TryGetValue(team, out var economy)) economy.AddP7(amount);
                    actor.ContactRole = ContactRole.None;
                    events.ContactResolutions.Add(new ContactResolvedEvent(actor.Seed.InstanceId, ContactRole.Pickup,
                        occupant.Seed.InstanceId, team, amount));
                    Destroy(actor, events.Destroyed);
                    return;
                }
            }
        }
    }
}

/// <summary>
/// A waiting placement met a unit: a rescue (now player 0) or a pickup that
/// paid <see cref="Amount"/> P7 to <see cref="Team"/>.
/// </summary>
public sealed record ContactResolvedEvent(int ActorInstanceId, ContactRole Role, int ByActorInstanceId, int Team, int Amount);
