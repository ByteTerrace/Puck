using System.Numerics;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>Resolves entity-part anchors through the part table published by the entity's active compiled look.</summary>
internal static class WorldEntityPartResolver {
    /// <summary>Resolves a live part pose from a span-backed composed transform buffer.</summary>
    public static bool TryPackedPose(WorldClient client, WorldStampPool stamps, int entityIndex, string partId, ReadOnlySpan<DynamicTransform> transforms, out SdfAnchor pose) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stamps);

        if (!client.IsActive(index: entityIndex)) {
            pose = default;

            return false;
        }

        if (stamps.HasBodyRegistration(bodyIndex: entityIndex)) {
            return stamps.TryBodyPartPose(bodyIndex: entityIndex, partId: partId, transforms: transforms, pose: out pose);
        }

        var look = client.Look(index: entityIndex);

        return WorldAvatarCatalog.TryPartPose(avatar: entityIndex, partId: partId, rig: CatalogRig(look: look), transforms: transforms, pose: out pose);
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
            if (!stamps.TryBodyPartTransformSlot(bodyIndex: entityIndex, partId: partId, transformSlot: out var stampSlot) ||
                ((uint)stampSlot >= (uint)transforms.Count)) {
                pose = default;

                return false;
            }

            var stampTransform = transforms[stampSlot];

            pose = new SdfAnchor(Position: stampTransform.Position, Orientation: stampTransform.Orientation);

            return true;
        }

        var look = client.Look(index: entityIndex);

        return WorldAvatarCatalog.TryPartPose(avatar: entityIndex, partId: partId, rig: CatalogRig(look: look), transforms: transforms, pose: out pose);
    }

    /// <summary>Resolves the current authored pose when no packed transform buffer is available.</summary>
    public static bool TryAuthoredPose(WorldClient client, WorldStampPool stamps, int entityIndex, string partId, out SdfAnchor pose) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stamps);

        if (!client.IsActive(index: entityIndex)) {
            pose = default;

            return false;
        }

        if (stamps.HasBodyRegistration(bodyIndex: entityIndex)) {
            return stamps.TryBodyPartAuthoredPose(bodyIndex: entityIndex, partId: partId, client: client, pose: out pose);
        }

        var look = client.Look(index: entityIndex);

        if (!WorldAvatarCatalog.TryPartOffset(avatar: entityIndex, partId: partId, rig: CatalogRig(look: look), scale: look.Scale, offset: out var offset)) {
            pose = default;

            return false;
        }

        var orientation = client.Orientation(index: entityIndex);

        pose = new SdfAnchor(
            Position: (client.Position(index: entityIndex) + Vector3.Transform(value: offset, rotation: orientation)),
            Orientation: orientation
        );

        return true;
    }

    private static int CatalogRig(WorldLook look) =>
        ((look.Source is WorldLookSource.Catalog { Index: { } pinned }) ? pinned : -1);
}
