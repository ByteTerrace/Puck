using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Navigation;

namespace Puck.World.Client;

/// <summary>A body as a path is planned for it: where it stands and the volume it sweeps.</summary>
/// <param name="Position">The body's foot point.</param>
/// <param name="Orientation">The body's local-to-world orientation.</param>
/// <param name="StepHeight">The tallest riser, in metres, the body walks up onto.</param>
/// <param name="Volumes">The body's collider volumes at its live scale; empty for a body with no collider.</param>
public readonly record struct WorldSeatWalker(FixedVector3 Position, FixedQuaternion Orientation, FixedQ4816 StepHeight, ReadOnlyMemory<FixedBodyColliderVolume> Volumes);
/// <summary>Where a planned path ends: inside a place's volume, or at a point.</summary>
/// <param name="Place">The place's volume, or <see langword="null"/> for a point.</param>
/// <param name="Point">The point; ignored for a place.</param>
public readonly record struct WorldSeatGoal(FixedSpatialShape? Place, FixedVector3 Point);
/// <summary>The outcome of planning a path.</summary>
/// <param name="Refusal">Why no path was planned, or <see langword="null"/> when one was.</param>
/// <param name="StartsOnCell">Whether the path's first corner is the walkable cell nearest the body, which the body
/// steps onto before steering on: it cannot walk straight to the corner after it and keep the margin.</param>
public readonly record struct WorldSeatPlan(string? Refusal, bool StartsOnCell) {
    /// <summary>Gets whether a path was planned.</summary>
    public bool Found => (Refusal is null);
}
/// <summary>How far a walk from a point reaches a goal, as <see cref="IWorldSeatNavigation.Reach"/> measures it.</summary>
public enum WorldSeatReachKind : byte {
    /// <summary>No baked domain holds a walkable cell at the start or at the goal, so the navigation cannot say.</summary>
    Uncovered = 0,
    /// <summary>A walk reaches the goal within the limit.</summary>
    Within = 1,
    /// <summary>Every walk to the goal is longer than the limit, or none reaches it.</summary>
    Beyond = 2,
}
/// <summary>A walk's reach to a goal.</summary>
/// <param name="Kind">Whether the walk reaches the goal within the limit.</param>
/// <param name="Metres">The walk's length, in metres, for <see cref="WorldSeatReachKind.Within"/>; otherwise zero.</param>
public readonly record struct WorldSeatReach(WorldSeatReachKind Kind, float Metres);
/// <summary>
/// Path planning for a seat over the world its client was delivered: the navigation domains of one world document,
/// planned against the static geometry the same document describes. The seat driver holds one for the world its seat
/// stands in, and reads no other geometry.
/// </summary>
public interface IWorldSeatNavigation {
    /// <summary>Gets whether every domain is baked, so a plan waits on no bake.</summary>
    bool Ready { get; }
    /// <summary>Gets the baked and declared domain counts, for a caller that reports a wait.</summary>
    (int Baked, int Declared) Domains { get; }

    /// <summary>Starts every domain's bake off the calling thread; a no-op for domains baked or baking.</summary>
    void Prepare();
    /// <summary>Plans the path a body steers along to a goal: the route across the domains from the walkable cell the
    /// body stands on, or the nearest one it can reach, straightened to the corners a walker turns at.</summary>
    /// <param name="walker">The body.</param>
    /// <param name="goal">Where the path ends.</param>
    /// <param name="margin">The clearance, in metres, a straight segment keeps from the edge of the walkable cells.</param>
    /// <param name="corners">Receives the steering points in order, cleared first; a point goal is the last.</param>
    /// <param name="route">Receives the foot point of every cell the unstraightened route crosses, cleared first.</param>
    /// <returns>The outcome.</returns>
    WorldSeatPlan Plan(in WorldSeatWalker walker, in WorldSeatGoal goal, FixedQ4816 margin, List<FixedVector3> corners, List<FixedVector3> route);
    /// <summary>Measures how far a walk from a point reaches a goal across the domains, up to a limit: the cheapest
    /// route's cost, which is its length plus the clearance surcharge near walls, so never less than its length. The
    /// search settles only the cells within the limit of the start and the goal. Answers
    /// <see cref="WorldSeatReachKind.Uncovered"/> while a domain is unbaked (it never bakes on the calling thread;
    /// <see cref="Prepare"/> starts the bakes), or where no domain holds a walkable cell near the start or near
    /// <paramref name="goalProbe"/>.</summary>
    /// <param name="from">The start, a body's foot.</param>
    /// <param name="goal">The cells the walk may end at.</param>
    /// <param name="goalProbe">A point standing over the goal's cells, which says whether any domain covers them.</param>
    /// <param name="limit">The longest walk measured, in metres.</param>
    /// <returns>The reach.</returns>
    WorldSeatReach Reach(in FixedVector3 from, INavigationRouteGoal goal, in FixedVector3 goalProbe, FixedQ4816 limit);
}
