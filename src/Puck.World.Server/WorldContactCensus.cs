using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>A contact field that needs the durable identity of the body being resolved. WorldBody supplies the
/// entity index only to this refinement; ordinary terrain fields remain unaware of population addressing.</summary>
internal interface IEntityContactField : IContactField {
    ContactResolution ResolveEntity(int entityIndex, ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up);
    ContactResolution ResolveEntitySweep(int entityIndex, in FixedVector3 previousPosition, ref FixedVector3 position,
        ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        ResolveEntity(
            entityIndex: entityIndex,
            orientation: in orientation,
            position: ref position,
            up: in up,
            velocity: ref velocity,
            volumes: volumes
        );
}

/// <summary>The analytic collider vocabulary's live document census, measured from the definition by
/// <see cref="WorldColliderSet.Measure"/> whichever contact provider the world selects.</summary>
/// <param name="SphereCount">All sphere colliders.</param>
/// <param name="BoxCount">All axis-aligned box colliders.</param>
/// <param name="PlaneCount">All half-space colliders.</param>
/// <param name="PlacementSphereCount">Sphere colliders derived from creation placements.</param>
/// <param name="PlacementBoxCount">Box colliders derived from creation placements.</param>
/// <param name="PlacementPlaneCount">Half-space colliders derived from creation placements.</param>
/// <param name="UnsupportedPlacementCount">Placement primitive copies with no analytic representation.</param>
public readonly record struct WorldContactCensus(
    long SphereCount,
    long BoxCount,
    long PlaneCount,
    long PlacementSphereCount,
    long PlacementBoxCount,
    long PlacementPlaneCount,
    long UnsupportedPlacementCount
) {
    /// <summary>Gets all analytic colliders derived from placements.</summary>
    public long PlacementColliderCount => ((PlacementSphereCount + PlacementBoxCount) + PlacementPlaneCount);
    /// <summary>Gets all analytic colliders.</summary>
    public long SolidCount => ((SphereCount + BoxCount) + PlaneCount);
}
