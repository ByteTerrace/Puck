using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Resolves positional lights through the same entity, part, and placement frames as other consumers.</summary>
public static class WorldLightAnchorResolver {
    /// <summary>Returns a current pose or null for an unavailable target.</summary>
    public static SdfAnchor? Resolve(WorldAnchor anchor, WorldClient client, WorldStampPool stamps, ReadOnlySpan<DynamicTransform> transforms) {
        switch (anchor) {
            case WorldAnchor.Entity entity:
                if (!client.IsActive(entity.Index)) { return null; }
                if (stamps.TryBodyTransformSlot(entity.Index, out var root) && (uint)root < (uint)transforms.Length) {
                    return new(transforms[root].Position, transforms[root].Orientation);
                }
                return new(client.Position(entity.Index), client.Orientation(entity.Index));
            case WorldAnchor.EntityPart part:
                return WorldEntityPartResolver.TryPackedPose(client, stamps, part.Index, part.PartId, transforms, out var pose) ? pose : null;
            case WorldAnchor.Placement placement:
                return ResolvePlacement(placement, client, stamps, transforms);
            default:
                return null;
        }
    }
    private static SdfAnchor? ResolvePlacement(WorldAnchor.Placement anchor, WorldClient client, WorldStampPool stamps, ReadOnlySpan<DynamicTransform> transforms) {
        if (stamps.TryShapeTransformSlot(anchor.PlacementId, anchor.ShapeId, client, out var slot) && (uint)slot < (uint)transforms.Length) {
            return stamps.ResolvePlacementSlotPose(anchor.PlacementId, anchor.ShapeId, client, transforms[slot]);
        }
        var placement = client.Definition.Placements.FirstOrDefault(p => p.Id == anchor.PlacementId);
        if (placement is null) { return null; }
        var creation = WorldDefinitionRows.FindCreation(client.Definition.Creations, placement.PrototypeId);
        if (creation is null || !WorldPlacementStamper.IsStaticStamp(placement, creation)) { return null; }
        var frame = WorldDefinitionRows.ResolvedFrame(client.Definition, placement);
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, frame.YawDegrees * (MathF.PI / 180f));
        if (anchor.ShapeId is not { } shapeId) { return new(frame.Position, rotation); }
        var shape = creation.EngineDocument.Shapes?.FirstOrDefault(s => s.Id == shapeId);
        if (shape is null) { return null; }
        return new(frame.Position + Vector3.Transform(shape.Position.Value * placement.Scale, rotation),
            Quaternion.Normalize(rotation * shape.Rotation.Value));
    }
}
