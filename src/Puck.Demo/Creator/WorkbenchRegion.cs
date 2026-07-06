using System.Numerics;

namespace Puck.Demo.Creator;

/// <summary>
/// The authoring region — a bounded box around the room's center that creator shapes are clamped inside. Making the
/// region first-class (rather than clamping to the whole room) is load-bearing three ways: it is the workpiece
/// camera's orbit target, the sprite-intent backdrop anchor, and — critically — the FAT STATIC BOUND a composition
/// group's single instance declares, so a member shape can never be moved outside its group's bound and pop at a tile
/// boundary (the instance-cull contract).
/// </summary>
/// <param name="Center">The region's planar center at floor height (Y = the room's FloorY).</param>
/// <param name="HalfExtent">The horizontal half-extent on X and Z.</param>
/// <param name="MinY">The lowest shape-center height (a little above the floor so a shape never sinks).</param>
/// <param name="MaxY">The highest shape-center height.</param>
public readonly record struct WorkbenchRegion(Vector3 Center, float HalfExtent, float MinY, float MaxY) {
    /// <summary>Where the ghost (re)spawns on entering creator mode — the region's center, floating a little above
    /// the floor so it reads immediately.</summary>
    public Vector3 SpawnPosition => new(Center.X, (Center.Y + 0.7f), Center.Z);

    /// <summary>The region's mid-height point — the workpiece camera's default orbit target and the center of a
    /// composition group's static instance bound.</summary>
    public Vector3 MidPoint => new(Center.X, (0.5f * (MinY + MaxY)), Center.Z);

    /// <summary>Clamps a shape-center position inside the region.</summary>
    /// <param name="position">The candidate position.</param>
    /// <returns>The clamped position.</returns>
    public Vector3 Clamp(Vector3 position) =>
        new(
            Math.Clamp(value: position.X, max: (Center.X + HalfExtent), min: (Center.X - HalfExtent)),
            Math.Clamp(value: position.Y, max: MaxY, min: MinY),
            Math.Clamp(value: position.Z, max: (Center.Z + HalfExtent), min: (Center.Z - HalfExtent))
        );

    /// <summary>The bounding-sphere radius (about <see cref="MidPoint"/>) that covers EVERY position the region can
    /// clamp a shape to, plus the worst-case reach of a maximally-grown shape — the safe static bound for a
    /// composition group's single instance. Fat by design: a fat bound only costs a rare extra evaluation, a tight
    /// one clips blended geometry at a tile boundary.</summary>
    /// <param name="maxShapeReach">The largest possible shape reach from its center (max primitive reach × max scale).</param>
    /// <returns>The group instance bound radius.</returns>
    public float GroupBoundRadius(float maxShapeReach) {
        var horizontal = (MathF.Sqrt(2f) * HalfExtent);
        var vertical = (0.5f * (MaxY - MinY));
        var cornerDistance = MathF.Sqrt((horizontal * horizontal) + (vertical * vertical));

        return (cornerDistance + maxShapeReach);
    }
}
