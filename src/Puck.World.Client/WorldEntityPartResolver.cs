using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Resolves entity-part anchors through the part table published by the entity's active compiled look.</summary>
public static class WorldEntityPartResolver {
    /// <summary>Resolves a live part pose from the frame's composed transform buffer.</summary>
    public static bool TryPackedPose(WorldClient client, WorldCreationInstances stamps, int entityIndex, string partId, ReadOnlySpan<DynamicTransform> transforms, out SdfAnchor pose) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stamps);

        if (!client.IsActive(index: entityIndex)) {
            pose = default;

            return false;
        }

        if (stamps.HasBodyRegistration(bodyIndex: entityIndex)) {
            return stamps.TryBodyPartPose(
                bodyIndex: entityIndex,
                partId: partId,
                pose: out pose,
                transforms: transforms
            );
        }

        var look = client.Look(index: entityIndex);

        return stamps.TryCatalogPartPose(
            body: entityIndex,
            part: partId,
            rig: WorldRigCatalog.RigFor(
                look: look,
                catalogRig: client.CatalogRig(index: entityIndex)
            ),
            transforms: transforms,
            pose: out pose,
            scale: look.Scale
        );
    }
}
