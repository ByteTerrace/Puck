using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Resolves entity-part anchors through the part table published by the entity's active compiled look.</summary>
public static class WorldEntityPartResolver {
    /// <summary>Resolves the current authored pose when no packed transform buffer is available.</summary>
    public static bool TryAuthoredPose(WorldClient client, WorldStampPool stamps, int entityIndex, string partId, out SdfAnchor pose) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stamps);

        if (!client.IsActive(index: entityIndex)) {
            pose = default;

            return false;
        }

        if (stamps.HasBodyRegistration(bodyIndex: entityIndex)) {
            return stamps.TryBodyPartAuthoredPose(
                bodyIndex: entityIndex,
                client: client,
                partId: partId,
                pose: out pose
            );
        }

        var look = client.Look(index: entityIndex);

        if (!WorldAvatarCatalog.TryPartOffset(
            avatar: entityIndex,
            partId: partId,
            rig: WorldAvatarCatalog.RigFor(
                look: look,
                catalogRig: client.CatalogRig(index: entityIndex)
            ),
            scale: look.Scale,
            offset: out var offset
        )) {
            pose = default;

            return false;
        }

        var orientation = client.Orientation(index: entityIndex);

        pose = new SdfAnchor(
            Position: (client.Position(index: entityIndex) + Vector3.Transform(
                rotation: orientation,
                value: offset
            )),
            Orientation: orientation
        );

        return true;
    }
    /// <summary>Resolves a live part pose from a span-backed composed transform buffer.</summary>
    public static bool TryPackedPose(WorldClient client, WorldStampPool stamps, int entityIndex, string partId, ReadOnlySpan<DynamicTransform> transforms, out SdfAnchor pose) {
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

        return WorldAvatarCatalog.TryPartPose(
            avatar: entityIndex,
            partId: partId,
            rig: WorldAvatarCatalog.RigFor(
                look: look,
                catalogRig: client.CatalogRig(index: entityIndex)
            ),
            transforms: transforms,
            pose: out pose
        );
    }
    /// <summary>Resolves a live part pose from a list-backed composed transform buffer.</summary>
    public static bool TryPackedPose(WorldClient client, WorldStampPool stamps, int entityIndex, string partId, IReadOnlyList<DynamicTransform> transforms, out SdfAnchor pose) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stamps);
        ArgumentNullException.ThrowIfNull(transforms);

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

        return WorldAvatarCatalog.TryPartPose(
            avatar: entityIndex,
            partId: partId,
            rig: WorldAvatarCatalog.RigFor(
                look: look,
                catalogRig: client.CatalogRig(index: entityIndex)
            ),
            transforms: transforms,
            pose: out pose
        );
    }
}
