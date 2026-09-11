using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldStampPool {
    /// <summary>Composes the rest pose onto a placement slot that carries a domain frame instead of a shape frame.</summary>
    public SdfAnchor ResolvePlacementSlotPose(string placementId, int? shapeId, WorldClient client, DynamicTransform transform) {
        var live = FindRow(placementId);
        if (live is null && client.TryInhabitantBody(placementId: placementId, index: out var bodyIndex)) {
            live = FindBody(bodyIndex);
        }
        var shape = live?.Creation.EngineDocument.Shapes?.FirstOrDefault(s => s.Id == shapeId);
        if (shape is { Domain.Count: > 0 }) {
            return new(transform.Position + Vector3.Transform(shape.Position.Value * live!.Scale, transform.Orientation),
                Quaternion.Normalize(transform.Orientation * shape.Rotation.Value));
        }
        return new(transform.Position, transform.Orientation);
    }
}
