using System.Numerics;
using Puck.Demo.Overworld;
using Puck.SdfVm;

namespace Puck.Demo.Gravity;

/// <summary>
/// Draws the gravity scenario's avatar token: a small standing capsule riding a dynamic-transform slot
/// packed every frame from <see cref="OverworldWorld.FieldWalkerTransform"/> — the SAME "bake position into the
/// program once, then move it for free via <see cref="SdfProgramBuilder.TransformDynamic"/>" shape
/// <c>PlayerBoxEmitter</c> uses for every room player, rather than <see cref="Puck.Demo.Rts.RtsUnitInstanceEmitter"/>'s
/// per-tick rebuild (a pool of many units needs a fresh position baked per slot each rebuild; ONE walker instead rides
/// one stable slot for the composition's whole lifetime).
/// <para>
/// THE PROBE CONTRACT: <see cref="DynamicSlotCount"/> is always 1 (there is only ever one walker), so the probe form
/// (always <see langword="true"/> active) trivially dominates the live form (active only once <c>planet.spawn</c> has
/// run) — no separate worst-case sizing concern, unlike a pool.
/// </para>
/// </summary>
public sealed class WalkerInstanceEmitter : ISdfSceneEmitter {
    private static readonly Vector3 WalkerColor = new(x: 0.85f, y: 0.78f, z: 0.20f);

    private const float CapsuleHeight = 1.3f;
    private const float CapsuleRadius = 0.28f;

    private readonly OverworldWorld m_world;

    /// <summary>Wraps the sim world whose <see cref="OverworldWorld.FieldWalkerActive"/>/<see cref="OverworldWorld.FieldWalkerTransform"/>
    /// this draws.</summary>
    /// <param name="world">The live sim world.</param>
    public WalkerInstanceEmitter(OverworldWorld world) {
        ArgumentNullException.ThrowIfNull(argument: world);

        m_world = world;
    }

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        var walkerMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: WalkerColor, Specular: 0.25f, Shininess: 24f));
        var active = (context.Probe || m_world.FieldWalkerActive);
        var slot = context.SlotBase;

        // A standing capsule authored along the DYNAMIC transform's local +Y (the walker's own published "up" — see
        // FieldWalkerBody's orientation-publish remarks: local Y = Up, local Z = facing), from the transform's origin
        // (feet, matching where FieldWalkerBody.Position sits — the body's Position IS its foot contact point, per its
        // ground-snap step) up to CapsuleHeight.
        _ = builder.BeginInstanceDynamic(slot: slot, boundOffset: new Vector3(x: 0f, y: (CapsuleHeight * 0.5f), z: 0f), boundRadius: ((CapsuleHeight * 0.5f) + CapsuleRadius), active: active);
        _ = builder.ResetPoint().TransformDynamic(slot: slot).Capsule(endpoint: new Vector3(x: 0f, y: CapsuleHeight, z: 0f), radius: CapsuleRadius, material: walkerMaterial);
        _ = builder.EndInstance();
    }

    /// <inheritdoc/>
    public int DynamicSlotCount => 1;

    /// <inheritdoc/>
    public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) {
        slots[context.SlotBase] = m_world.FieldWalkerTransform(renderOrigin: GravityScenario.PlanetCenter, alpha: context.InterpolationAlpha);
    }
}
