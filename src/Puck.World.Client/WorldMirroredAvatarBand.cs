using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>The pieces a mirrored (session/adjacency) avatar copy shares with the boot copy's own presentation-only
/// movement-driven gait: the distance clamp and cadence that turn walked distance into limb-swing phase, and the
/// per-avatar palette registration (rig/scale/gait-amplitude identity plus its two catalog materials).</summary>
internal static class WorldMirroredAvatarBand {
    /// <summary>The walked distance one frame may add to gait phase — clamps a teleport/server snap so it cannot
    /// spin the limbs through dozens of cycles in one frame.</summary>
    public const float MaxGaitTravelPerFrame = 0.25f;
    /// <summary>The walked-distance-to-phase cadence: phase advances by distance, not wall time, so idle avatars
    /// hold their pose and walking speed controls the swing rate.</summary>
    public const float GaitCadence = 8.0f;

    /// <summary>Advances one avatar's gait phase by its walked distance since the last call, or reseeds (phase 0,
    /// no travel charged) when the entity address changed — a body index reused by a different inhabitant, or this
    /// is the first frame the entity is live.</summary>
    /// <param name="seeded">Whether a prior call has latched an address for this slot; set <see langword="true"/>
    /// on return.</param>
    /// <param name="lastAddress">The address latched by the previous call; updated on a reseed.</param>
    /// <param name="lastPosition">The position latched by the previous call; updated every call.</param>
    /// <param name="gaitPhase">The running gait phase; advanced on a match, reset to 0 on a reseed.</param>
    /// <param name="address">This call's entity address.</param>
    /// <param name="position">This call's render position.</param>
    public static void AdvanceGait(ref bool seeded, ref WorldEntityAddress lastAddress, ref Vector3 lastPosition, ref float gaitPhase, WorldEntityAddress address, Vector3 position) {
        if (
            seeded &&
            (lastAddress == address)
        ) {
            var travelled = MathF.Min(
                x: Vector3.Distance(
                    value1: position,
                    value2: lastPosition
                ),
                y: MaxGaitTravelPerFrame
            );

            gaitPhase += (travelled * GaitCadence);
        } else {
            seeded = true;
            gaitPhase = 0f;
            lastAddress = address;
        }

        lastPosition = position;
    }
    /// <summary>Registers one avatar's rig/scale/gait-amplitude identity and its two catalog materials.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="look">The entity's resolved look.</param>
    /// <param name="bodyColor">The entity's body color.</param>
    /// <param name="catalogRig">The entity's own carried catalog rig — <see cref="WorldAvatarCatalog.RigFor"/>'s
    /// fallback for an unpinned look.</param>
    /// <param name="noseFactor">The accent-material color multiplier (<c>playerDefaults.noseFactor</c>).</param>
    /// <param name="identityIndex">The slot to write <paramref name="emittedRigs"/>/<paramref name="emittedScales"/>/
    /// <paramref name="emittedGaitAmplitudes"/> at.</param>
    /// <param name="emittedRigs">The resolved catalog rig per identity slot.</param>
    /// <param name="emittedScales">The resolved uniform render scale per identity slot.</param>
    /// <param name="emittedGaitAmplitudes">The resolved gait amplitude per identity slot.</param>
    /// <param name="materialIndex">The slot to write <paramref name="bodyMaterials"/>/<paramref name="accentMaterials"/>
    /// at — the palette array's own index, which may differ from <paramref name="identityIndex"/> when the palette
    /// is reused per band.</param>
    /// <param name="bodyMaterials">The body material id per material slot.</param>
    /// <param name="accentMaterials">The accent material id per material slot.</param>
    public static void EmitPalette(SdfProgramBuilder builder, WorldLook look, Vector3 bodyColor, byte catalogRig, float noseFactor, int identityIndex, int[] emittedRigs, float[] emittedScales, float[] emittedGaitAmplitudes, int materialIndex, int[] bodyMaterials, int[] accentMaterials) {
        emittedRigs[identityIndex] = WorldAvatarCatalog.RigFor(
            catalogRig: catalogRig,
            look: look
        );
        emittedScales[identityIndex] = look.Scale;
        emittedGaitAmplitudes[identityIndex] = look.Motion.GaitAmplitude;
        bodyMaterials[materialIndex] = builder.AddMaterial(material: new SdfMaterial(Albedo: bodyColor));
        accentMaterials[materialIndex] = builder.AddMaterial(material: new SdfMaterial(Albedo: (bodyColor * noseFactor)));
    }
}
