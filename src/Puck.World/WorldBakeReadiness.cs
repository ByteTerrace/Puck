using Puck.World.Client;

namespace Puck.World;

/// <summary>The presentation's bake schedule as the console reads it (<c>world.wait bakes</c>).</summary>
/// <param name="schedule">The schedule the presentation pumps.</param>
public sealed class WorldBakeReadiness(WorldBakeSchedule schedule) : IWorldBakeReadiness {
    /// <inheritdoc/>
    public bool IsSettled => schedule.IsSettled;
}
