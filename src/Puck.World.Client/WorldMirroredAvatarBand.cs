using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>One catalog avatar's presentation-only gait: the pose stream it follows (the entity address and the pose
/// epoch its source last declared), its last render position, and the limb-swing phase walked distance drives.</summary>
internal struct WorldAvatarGait {
    /// <summary>Whether a pose has been followed since the last cut.</summary>
    public bool Seeded;
    /// <summary>The entity address of the stream followed.</summary>
    public WorldEntityAddress Address;
    /// <summary>The pose epoch of the stream followed.</summary>
    public int Epoch;
    /// <summary>The last render position followed.</summary>
    public Vector3 Position;
    /// <summary>The running gait phase, radians of swing.</summary>
    public float Phase;

    /// <summary>Returns whether a pose cannot continue the followed stream: nothing followed yet, another inhabitant
    /// at the body index, or a jump its source declared (a teleport or an activation moves the pose epoch).</summary>
    /// <param name="address">The pose's entity address.</param>
    /// <param name="epoch">The pose's epoch.</param>
    /// <returns><see langword="true"/> when the pose starts a new stream.</returns>
    public readonly bool Breaks(WorldEntityAddress address, int epoch) => (!Seeded || (Address != address) || (Epoch != epoch));
    /// <summary>Follows one pose: a continuing pose advances the phase by its walked distance, clamped so a snap cannot
    /// spin the limbs through many cycles in one frame; a pose that breaks the stream reseeds the phase at zero.</summary>
    /// <param name="address">The pose's entity address.</param>
    /// <param name="epoch">The pose's epoch.</param>
    /// <param name="position">The pose's render position.</param>
    /// <param name="footfall">Whether a continuing pose crossed a half cycle of phase: one footfall per π, a stride
    /// swinging one leg through.</param>
    /// <returns><see langword="true"/> when the pose continued the stream; <see langword="false"/> when it reseeded.</returns>
    public bool Follow(WorldEntityAddress address, int epoch, Vector3 position, out bool footfall) {
        var continues = !Breaks(address: address, epoch: epoch);

        footfall = false;
        if (continues) {
            var travelled = MathF.Min(
                x: Vector3.Distance(
                    value1: position,
                    value2: Position
                ),
                y: WorldMirroredAvatarBand.MaxGaitTravelPerFrame
            );
            var previous = Phase;

            Phase += (travelled * WorldMirroredAvatarBand.GaitCadence);
            footfall = (((int)(Phase / MathF.PI)) > ((int)(previous / MathF.PI)));
        } else {
            Seeded = true;
            Phase = 0f;
            Address = address;
            Epoch = epoch;
        }

        Position = position;
        return continues;
    }
}
/// <summary>What every catalog avatar band shares, the boot scene's and the mirrored session and adjacency copies': the
/// distance clamp and cadence that turn walked distance into limb-swing phase, the per-avatar look identity and
/// catalog materials, and the mirrored copies' per-frame repack.</summary>
internal static class WorldMirroredAvatarBand {
    /// <summary>The walked-distance-to-phase cadence: phase advances by distance, not wall time, so idle avatars
    /// hold their pose and walking speed controls the swing rate.</summary>
    public const float GaitCadence = 8.0f;
    /// <summary>The walked distance one frame may add to gait phase — clamps a teleport/server snap so it cannot
    /// spin the limbs through dozens of cycles in one frame.</summary>
    public const float MaxGaitTravelPerFrame = 0.25f;

    /// <summary>Wakes one mirrored avatar's transform owner for its interpolated pose and follows the pose with its
    /// gait. A mirrored avatar is drawn at its source's pose with no follower, so it steps by a whole frame.</summary>
    /// <param name="owners">The band's transform owners.</param>
    /// <param name="owner">The avatar's owner index in the band.</param>
    /// <param name="gait">The avatar's gait.</param>
    /// <param name="moved">The frame's moved-transform journal.</param>
    /// <param name="address">The pose's entity address.</param>
    /// <param name="epoch">The pose's epoch.</param>
    /// <param name="position">The interpolated render position.</param>
    /// <param name="orientation">The interpolated render orientation.</param>
    /// <returns><see langword="true"/> when the avatar must repack (<see cref="Pack"/>); <see langword="false"/> when its
    /// pose holds still.</returns>
    public static bool Wake(WorldTransformOwners owners, int owner, ref WorldAvatarGait gait, SdfMovedTransforms moved, WorldEntityAddress address, int epoch, Vector3 position, Quaternion orientation) {
        if (!owners.Wake(
            deltaSeconds: 1f,
            discontinuity: gait.Breaks(address: address, epoch: epoch),
            moved: moved,
            orientation: orientation,
            owner: owner,
            position: position
        )) {
            return false;
        }

        _ = gait.Follow(address: address, epoch: epoch, footfall: out _, position: position);
        return true;
    }
    /// <summary>Packs one woken mirrored avatar's catalog body with its root at the pose its band draws it at, and
    /// settles its owner.</summary>
    /// <param name="owners">The band's transform owners.</param>
    /// <param name="owner">The avatar's owner index in the band.</param>
    /// <param name="gait">The avatar's gait.</param>
    /// <param name="moved">The frame's moved-transform journal.</param>
    /// <param name="table">The frame's transform table.</param>
    /// <param name="firstSlot">The body's first catalog slot in its band.</param>
    /// <param name="avatar">The body's catalog avatar index.</param>
    /// <param name="catalogBase">The band's first dynamic-transform slot.</param>
    /// <param name="rig">The body's emitted catalog rig.</param>
    /// <param name="scale">The body's emitted render scale.</param>
    /// <param name="gaitAmplitude">The body's emitted gait amplitude, scaling its phase.</param>
    /// <param name="rootPosition">The root's position in the band's frame.</param>
    /// <param name="rootOrientation">The root's orientation in the band's frame.</param>
    public static void Pack(WorldTransformOwners owners, int owner, in WorldAvatarGait gait, SdfMovedTransforms moved, Span<DynamicTransform> table, int firstSlot, int avatar, int catalogBase, int rig, float scale, float gaitAmplitude, Vector3 rootPosition, Quaternion rootOrientation) =>
        owners.Settle(
            deltaSeconds: 1f,
            moved: WorldTransformOwners.PackBody(
                firstSlot: firstSlot,
                avatar: avatar,
                catalogBase: catalogBase,
                gaitPhase: (gait.Phase * gaitAmplitude),
                moved: moved,
                rig: rig,
                rootOrientation: rootOrientation,
                rootPosition: rootPosition,
                reseat: owners.Reseats(owner: owner),
                scale: scale,
                table: table
            ),
            owner: owner
        );
    /// <summary>Latches one avatar's look identity: its catalog rig, uniform render scale and gait amplitude.</summary>
    /// <param name="look">The entity's resolved look.</param>
    /// <param name="catalogRig">The entity's own carried catalog rig — <see cref="WorldRigCatalog.RigFor"/>'s
    /// fallback for an unpinned look.</param>
    /// <param name="identityIndex">The slot to write the identity at.</param>
    /// <param name="emittedRigs">The resolved catalog rig per identity slot.</param>
    /// <param name="emittedScales">The resolved uniform render scale per identity slot.</param>
    /// <param name="emittedGaitAmplitudes">The resolved gait amplitude per identity slot.</param>
    public static void LatchIdentity(WorldLook look, byte catalogRig, int identityIndex, Span<int> emittedRigs, Span<float> emittedScales, Span<float> emittedGaitAmplitudes) {
        emittedRigs[identityIndex] = WorldRigCatalog.RigFor(
            catalogRig: catalogRig,
            look: look
        );
        emittedScales[identityIndex] = look.Scale;
        emittedGaitAmplitudes[identityIndex] = look.Motion.GaitAmplitude;
    }
    /// <summary>Registers one avatar's two catalog materials, its body color and the accent beside it.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="bodyColor">The entity's body color.</param>
    /// <param name="noseFactor">The accent-material color multiplier (<c>playerDefaults.noseFactor</c>).</param>
    /// <param name="materialIndex">The slot to write the material ids at.</param>
    /// <param name="bodyMaterials">The body material id per material slot.</param>
    /// <param name="accentMaterials">The accent material id per material slot.</param>
    public static void EmitMaterials(SdfProgramBuilder builder, Vector3 bodyColor, float noseFactor, int materialIndex, Span<int> bodyMaterials, Span<int> accentMaterials) {
        bodyMaterials[materialIndex] = builder.AddMaterial(material: new SdfMaterial(Albedo: bodyColor));
        accentMaterials[materialIndex] = builder.AddMaterial(material: new SdfMaterial(Albedo: (bodyColor * noseFactor)));
    }
    /// <summary>Latches one mirrored avatar's look identity (<see cref="LatchIdentity"/>) and registers its catalog
    /// materials (<see cref="EmitMaterials"/>).</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="look">The entity's resolved look.</param>
    /// <param name="bodyColor">The entity's body color.</param>
    /// <param name="catalogRig">The entity's own carried catalog rig.</param>
    /// <param name="noseFactor">The accent-material color multiplier (<c>playerDefaults.noseFactor</c>).</param>
    /// <param name="identityIndex">The slot to write the identity at.</param>
    /// <param name="emittedRigs">The resolved catalog rig per identity slot.</param>
    /// <param name="emittedScales">The resolved uniform render scale per identity slot.</param>
    /// <param name="emittedGaitAmplitudes">The resolved gait amplitude per identity slot.</param>
    /// <param name="materialIndex">The slot to write the material ids at — the palette array's own index, which may
    /// differ from <paramref name="identityIndex"/> when the palette is reused per band.</param>
    /// <param name="bodyMaterials">The body material id per material slot.</param>
    /// <param name="accentMaterials">The accent material id per material slot.</param>
    public static void EmitPalette(SdfProgramBuilder builder, WorldLook look, Vector3 bodyColor, byte catalogRig, float noseFactor, int identityIndex, int[] emittedRigs, float[] emittedScales, float[] emittedGaitAmplitudes, int materialIndex, int[] bodyMaterials, int[] accentMaterials) {
        LatchIdentity(catalogRig: catalogRig, emittedGaitAmplitudes: emittedGaitAmplitudes, emittedRigs: emittedRigs, emittedScales: emittedScales, identityIndex: identityIndex, look: look);
        EmitMaterials(accentMaterials: accentMaterials, bodyColor: bodyColor, bodyMaterials: bodyMaterials, builder: builder, materialIndex: materialIndex, noseFactor: noseFactor);
    }
}
