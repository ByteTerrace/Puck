using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

/// <summary>A contact field that needs the durable identity of the body being resolved. WorldBody supplies the
/// entity index only to this refinement (<see cref="WorldBody.SweepContact"/>); ordinary terrain fields remain unaware
/// of population addressing.</summary>
internal interface IEntityContactField : IContactField {
    ContactResolution ResolveEntitySweep(int entityIndex, in FixedVector3 previousPosition, ref FixedVector3 position,
        ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up);
}

/// <summary>The analytic collider vocabulary's live document census, measured from the definition by
/// <see cref="WorldColliderSet.Measure"/> whichever contact provider the world selects.</summary>
/// <param name="SphereCount">All sphere colliders.</param>
/// <param name="BoxCount">All box colliders.</param>
/// <param name="CylinderCount">All circular cylinder colliders.</param>
/// <param name="PlaneCount">All half-space colliders.</param>
/// <param name="PlacementSphereCount">Sphere colliders derived from creation placements.</param>
/// <param name="PlacementBoxCount">Box colliders derived from creation placements.</param>
/// <param name="PlacementCylinderCount">Cylinder colliders derived from creation placements.</param>
/// <param name="PlacementPlaneCount">Half-space colliders derived from creation placements.</param>
/// <param name="UnsupportedPlacementCount">Placement primitive copies with no analytic representation.</param>
/// <param name="SolidBakeHash">The hash of everything the solid field's distance grid is baked from — the compiled
/// program's packed words, the grid cell size, and the contact reach the band derives from — so two fields with the
/// same hash carry the same bake; 0 under the analytic provider or when no grid is authored.</param>
public readonly record struct WorldContactCensus(
    long SphereCount,
    long BoxCount,
    long CylinderCount,
    long PlaneCount,
    long PlacementSphereCount,
    long PlacementBoxCount,
    long PlacementCylinderCount,
    long PlacementPlaneCount,
    long UnsupportedPlacementCount,
    ulong SolidBakeHash = 0UL
) {
    /// <summary>Gets the field colliders derived from placements: one per stamped copy of a relieved creation.</summary>
    public long PlacementFieldCount { get; init; }
    /// <summary>Gets all analytic colliders derived from placements.</summary>
    public long PlacementColliderCount => ((((PlacementSphereCount + PlacementBoxCount) + PlacementCylinderCount) + PlacementPlaneCount) + PlacementFieldCount);
    /// <summary>Gets all analytic colliders.</summary>
    public long SolidCount => ((((SphereCount + BoxCount) + CylinderCount) + PlaneCount) + PlacementFieldCount);
}
