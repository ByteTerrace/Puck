using System.Numerics;
using Puck.Demo.Creator;
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
/// never re-derives them. Read, never modified: <see cref="CreatorSceneRenderer"/>'s bound math and
/// <see cref="AvatarDefinition.Reach"/> are the shared conservative-reach source, mirrored (not reimplemented) here so
/// a placement's footprint uses the SAME worst-case reach the renderer already trusts.
/// </summary>
public static class WorldFootprintDerivation {
    /// <summary>Derives every blocking footprint a placement contributes: per authored shape, the placement transform
    /// composed with the shape's own transform, expanded to a conservative world AABB by the primitive's unit reach
    /// times its largest scale component (mirroring <see cref="AvatarDefinition.Reach"/> — the same worst-case bound
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

        var yawRadians = float.DegreesToRadians(placement.YawDegrees ?? 0f);
        var placementRotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: yawRadians);
        var placementScale = (placement.Scale ?? 1f);
        var repeat = placement.Repeat;
        var countX = Math.Max(val1: (repeat?.CountX ?? 1), val2: 1);
        var countZ = Math.Max(val1: (repeat?.CountZ ?? 1), val2: 1);
        var spacingX = (repeat?.SpacingX ?? 0f);
        var spacingZ = (repeat?.SpacingZ ?? 0f);

        for (var copyX = 0; (copyX < countX); copyX++) {
            for (var copyZ = 0; (copyZ < countZ); copyZ++) {
                var copyOrigin = (placement.Position + new Vector3((copyX * spacingX), 0f, (copyZ * spacingZ)));

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

    /// <summary>Derives the footprint a terrain patch contributes — a slab is walk-relevant blocking geometry only
    /// when it RISES above the walk band's floor (a flat road/plaza slab at floor level blocks nothing; a raised curb
    /// or platform does).</summary>
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
    // transform, expanded by the primitive's unit-scale reach times the LARGEST scale component along either
    // transform — a sphere-of-worst-case-reach around the composed center, axis-aligned in world space. Fat by
    // design (mirrors the creator renderer's own instance-cull bound), never tight-fit per axis: a conservative
    // over-block is safe (a body simply can't stand somewhere it visually could, at the margins), an under-block
    // is not.
    private static WorldFootprint ShapeFootprint(Vector3 placementOrigin, Quaternion placementRotation, float placementScale, ShapeDocument shape) {
        var shapeScale = ((shape.Scale == default) ? Vector3.One : shape.Scale);
        var localCenter = (shape.Position * placementScale);
        var rotatedCenter = Vector3.Transform(value: localCenter, rotation: placementRotation);
        var worldCenter = (placementOrigin + rotatedCenter);
        var shapeMaxScale = MathF.Max(shapeScale.X, MathF.Max(shapeScale.Y, shapeScale.Z));
        var reach = (AvatarDefinition.Reach(type: shape.Type, scale: Vector3.One) * shapeMaxScale * placementScale);

        return new WorldFootprint(
            MaxX: (worldCenter.X + reach),
            MaxY: (worldCenter.Y + reach),
            MaxZ: (worldCenter.Z + reach),
            MinX: (worldCenter.X - reach),
            MinY: (worldCenter.Y - reach),
            MinZ: (worldCenter.Z - reach)
        );
    }
}
