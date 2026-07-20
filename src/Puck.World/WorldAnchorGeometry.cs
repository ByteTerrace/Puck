using System.Numerics;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The one shared static resolver for a <see cref="WorldAnchor"/>'s STAMPED-transform geometry — the placement math both
/// the audio director (speaker poses) and the camera path (binder offscreen views + the main-window composer) read, so
/// cameras and speakers resolve a placement anchor through the SAME code (P9). This is exactly the seam whose absence
/// forced the validator to reject placement-anchored cameras; with it, that rejection is gone.
/// </summary>
internal static class WorldAnchorGeometry {
    /// <summary>The stamped world position of a placement anchor: the placement's root position, or — when a shape id is
    /// given — the root composed with the shape's scaled local position under the placement's yaw. Zero when the
    /// placement (or its creation) does not resolve.</summary>
    /// <param name="definition">The live definition (placement + creation rows).</param>
    /// <param name="placementId">The referenced placement id.</param>
    /// <param name="shapeId">The referenced creation shape id, or <see langword="null"/> for the placement root.</param>
    /// <returns>The stamped world position.</returns>
    public static Vector3 StaticPlacementPosition(WorldDefinition definition, string placementId, int? shapeId) {
        if (definition is null) {
            return Vector3.Zero;
        }

        foreach (var placement in definition.Placements) {
            if (!string.Equals(a: placement.Id, b: placementId, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            var creation = WorldPlacementStamper.FindCreation(creations: definition.Creations, id: placement.CreationId);

            return ((creation is null) ? placement.Position : StaticShapePosition(placement: placement, creation: creation, shapeId: shapeId));
        }

        return Vector3.Zero;
    }

    /// <summary>The stamped world position of one shape within a placement (root ∘ scale · local under the yaw), or the
    /// placement root when no shape id resolves.</summary>
    /// <param name="placement">The placement row.</param>
    /// <param name="creation">The placement's creation.</param>
    /// <param name="shapeId">The shape id, or <see langword="null"/>.</param>
    /// <returns>The stamped world position.</returns>
    public static Vector3 StaticShapePosition(WorldPlacement placement, WorldCreation creation, int? shapeId) {
        if (shapeId is not { } targetShapeId) {
            return placement.Position;
        }

        foreach (var shape in (creation.Document.Shapes ?? [])) {
            if (shape.Id == targetShapeId) {
                var rotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (placement.YawDegrees * (MathF.PI / 180f)));

                return (placement.Position + Vector3.Transform(value: (shape.Position * placement.Scale), rotation: rotation));
            }
        }

        return placement.Position;
    }
}
