using System.Numerics;
using Puck.Authoring;
using Puck.Demo.Forge;

namespace Puck.Demo.World;

/// <summary>One world-space XZ footprint rectangle, plus the vertical span it occupies — the shape
/// <see cref="WalkGridBaker"/> consumes to mark cells blocked. A footprint only blocks the walk grid when its
/// vertical span intersects the walk band (see <see cref="WalkGridBaker"/>), so a lamp head overhanging the band
/// harmlessly leaves the floor beneath it walkable.</summary>
/// <param name="MinX">The rectangle's minimum X (world units).</param>
/// <param name="MinZ">The rectangle's minimum Z.</param>
/// <param name="MaxX">The rectangle's maximum X.</param>
/// <param name="MaxZ">The rectangle's maximum Z.</param>
/// <param name="MinY">The lowest world Y the geometry reaches.</param>
/// <param name="MaxY">The highest world Y the geometry reaches.</param>
public readonly record struct WorldFootprint(float MinX, float MinZ, float MaxX, float MaxZ, float MinY, float MaxY);

/// <summary>
/// Derives conservative world-space XZ footprints from a <see cref="WorldDocument"/>'s placements (resolved against
/// their referenced <see cref="CreationDocument"/>s) and terrain patches. Authoring-side only: every input and output
/// here is float — the seam into fixed point is <see cref="WalkGridBaker"/>, which consumes these footprints and
/// never re-derives them. Each shape's blocking box uses <see cref="AvatarDefinition.AxisExtents"/> — the SAME
/// canonical per-primitive dimensions the creator/forge trust — expanded PER AXIS at the shape's own scale, not
/// <see cref="AvatarDefinition.Reach"/>'s isotropic worst-case sphere (which is the right call for a rotation-blind
/// render-cull bound, but explodes a tall thin post's footprint into a wide one — see <see cref="ShapeFootprint"/>).
/// </summary>
public static class WorldFootprintDerivation {
    /// <summary>Derives every blocking footprint a placement contributes: per authored shape, the placement transform
    /// composed with the shape's own transform, expanded to a conservative world AABB via the primitive's PER-AXIS
    /// canonical half-extents (mirroring <see cref="AvatarDefinition.AxisExtents"/> — the same tight per-axis bound
    /// the creator's own renderer trusts). A repeat block stamps one footprint set per copy
    /// (<see cref="PlacementRepeatDocument.CountX"/> × <see cref="PlacementRepeatDocument.CountZ"/> at
    /// <see cref="PlacementRepeatDocument.SpacingX"/>/<see cref="PlacementRepeatDocument.SpacingZ"/>).</summary>
    /// <param name="placement">The placement to derive footprints for.</param>
    /// <param name="creation">The placement's resolved creation (its shapes, in the creation's local frame), or
    /// <see langword="null"/> when unresolved (yields no footprints — a dangling placement blocks nothing rather than
    /// throwing; the store's load-time hash check is the loud failure for that case).</param>
    /// <returns>The world-space footprints this placement (and its repeats) contribute.</returns>
    public static IEnumerable<WorldFootprint> ForPlacement(PlacementDocument placement, CreationDocument? creation) {
        ArgumentNullException.ThrowIfNull(argument: placement);

        if ((creation?.Shapes is not { Count: > 0 } shapes)) {
            yield break;
        }

        var yawRadians = float.DegreesToRadians(degrees: (placement.YawDegrees ?? 0f));
        var placementRotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: yawRadians);
        var placementScale = (placement.Scale ?? 1f);
        var repeat = placement.Repeat;
        var countX = Math.Max(val1: (repeat?.CountX ?? 1), val2: 1);
        var countZ = Math.Max(val1: (repeat?.CountZ ?? 1), val2: 1);
        var spacingX = (repeat?.SpacingX ?? 0f);
        var spacingZ = (repeat?.SpacingZ ?? 0f);

        for (var copyX = 0; (copyX < countX); copyX++) {
            for (var copyZ = 0; (copyZ < countZ); copyZ++) {
                var copyOrigin = (placement.Position + new Vector3(x: (copyX * spacingX), y: 0f, z: (copyZ * spacingZ)));

                foreach (var shape in shapes) {
                    yield return ShapeFootprint(
                        placementOrigin: copyOrigin,
                        placementRotation: placementRotation,
                        placementScale: placementScale,
                        shape: shape
                    );
                }
            }
        }
    }

    /// <summary>Derives the footprint a terrain patch WOULD contribute if fed into the walk-block pass — NOT
    /// currently called by <see cref="WorldWalkGridBake"/> (see its terrain remark): a road/plaza/sidewalk slab is
    /// walkable dressing by definition, and the floor-plane occlusion math makes "rises above the band but stays
    /// invisible" a contradiction in terms for a flush slab, so terrain never blocks today. Kept for a future raised
    /// kind (a curb or dais) that opts back in explicitly.</summary>
    /// <param name="patch">The terrain patch.</param>
    /// <returns>The patch's world-space footprint.</returns>
    public static WorldFootprint ForTerrainPatch(TerrainPatchDocument patch) {
        ArgumentNullException.ThrowIfNull(argument: patch);

        return new WorldFootprint(
            MaxX: (patch.Center.X + patch.HalfExtents.X),
            MaxY: (patch.Center.Y + patch.HalfExtents.Y),
            MaxZ: (patch.Center.Z + patch.HalfExtents.Z),
            MinX: (patch.Center.X - patch.HalfExtents.X),
            MinY: (patch.Center.Y - patch.HalfExtents.Y),
            MinZ: (patch.Center.Z - patch.HalfExtents.Z)
        );
    }

    // One authored shape's conservative world AABB: the placement transform composed with the shape's own local
    // transform, expanded PER AXIS by the primitive's own canonical half-extents (AvatarDefinition.AxisExtents) at
    // the shape's actual per-axis scale, then re-expressed as a world-axis-aligned box via the standard rotated-AABB
    // formula (world half-extent on axis i = Σ_j |R_ij| · local half-extent_j, R = the shape's own rotation composed
    // with the placement's). Fat by design (never tighter than the true rotated box: a conservative over-block is
    // safe, an under-block is not) — but NOT isotropic: an earlier revision used a single "reach" (unit-scale
    // diagonal length × the LARGEST scale component, applied to every axis) which explodes for aspect-heavy
    // geometry — a tall thin pillar's height got applied as extra WIDTH too, ballooning a 0.14-half-extent post into
    // a several-unit blocking radius. Per-axis keeps a tall post's footprint tall, not wide.
    private static WorldFootprint ShapeFootprint(Vector3 placementOrigin, Quaternion placementRotation, float placementScale, ShapeDocument shape) {
        var shapeScale = ((shape.Scale == default) ? Vector3.One : shape.Scale);
        var localCenter = (shape.Position * placementScale);
        var rotatedCenter = Vector3.Transform(value: localCenter, rotation: placementRotation);
        var worldCenter = (placementOrigin + rotatedCenter);
        var localHalf = ((AvatarDefinition.AxisExtents(type: shape.Type) * shapeScale) * placementScale);
        var rotation = Matrix4x4.CreateFromQuaternion(quaternion: Quaternion.Concatenate(value1: shape.Rotation, value2: placementRotation));
        var worldHalf = new Vector3(
            x: (((MathF.Abs(x: rotation.M11) * localHalf.X) + (MathF.Abs(x: rotation.M21) * localHalf.Y)) + (MathF.Abs(x: rotation.M31) * localHalf.Z)),
            y: (((MathF.Abs(x: rotation.M12) * localHalf.X) + (MathF.Abs(x: rotation.M22) * localHalf.Y)) + (MathF.Abs(x: rotation.M32) * localHalf.Z)),
            z: (((MathF.Abs(x: rotation.M13) * localHalf.X) + (MathF.Abs(x: rotation.M23) * localHalf.Y)) + (MathF.Abs(x: rotation.M33) * localHalf.Z))
        );

        return new WorldFootprint(
            MaxX: (worldCenter.X + worldHalf.X),
            MaxY: (worldCenter.Y + worldHalf.Y),
            MaxZ: (worldCenter.Z + worldHalf.Z),
            MinX: (worldCenter.X - worldHalf.X),
            MinY: (worldCenter.Y - worldHalf.Y),
            MinZ: (worldCenter.Z - worldHalf.Z)
        );
    }
}
