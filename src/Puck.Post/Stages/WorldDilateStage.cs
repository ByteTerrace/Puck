using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for <see cref="SdfOp.Dilate"/> — the field op that inflates (rounds and fattens)
/// the ENTIRE accumulator before it, previously covered only INCIDENTALLY (the scoped-reach margin test). Like
/// <see cref="SdfOp.Onion"/>/<see cref="SdfOp.Displace"/>, Dilate reads the running field, so it is subject to the
/// accumulator rule; this scene exercises BOTH composition paths in one frame:
/// <list type="bullet">
///   <item>A SCOPED dilate (<see cref="SdfProgramBuilder.PushField"/>/<see cref="SdfProgramBuilder.PopField"/> composed
///   back with a Union): a box + sphere turret inflated by 0.12 INSIDE the scope, so the dilation touches only its own
///   two shapes and the Union compose melds the fattened turret onto the floor WITHOUT inflating it — the far-neutral,
///   still-maskable path (a scoped field op keeps its instance cullable). If the scope leaked, the floor would balloon
///   and the cross-backend diff would spike.</item>
///   <item>An UNSCOPED dilate exercising the UNMASKABLE path: the same turret dilated with no scope. Because an unscoped
///   field op reads the WHOLE accumulator, it must be emitted FIRST (against the empty <c>SDF_FAR_DISTANCE</c>
///   accumulator) so it inflates only its own shapes and not the floor emitted after it — the same ordering discipline
///   WorldWarpStage's onion and WorldChamferStage's intersection follow. This is the path an instance can no longer be
///   masked through.</item>
/// </list>
/// Dilate is 1-Lipschitz (it shifts the field by a constant, leaving the gradient untouched), so this is a pure PARITY
/// gate, not a solidity one. Each cluster is single-material with union blends and rounded (not seamed) geometry, so the
/// residual is smooth-shading ±1-LSB codegen noise — the <c>WorldComposite</c> posture the hero scene earns.
/// </summary>
internal sealed class WorldDilateStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-dilate";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // The UNSCOPED-dilate turret goes FIRST, against the empty accumulator, so its Dilate inflates only its own two
    // shapes — emitted later (after the floor) it would fatten the ground plane too, the unscoped field op's failure
    // mode. The floor then unions on top, and the SCOPED-dilate turret composes far-neutrally: the scope frees it to sit
    // anywhere without eating the floor.
    internal static SdfProgram BuildDilateScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.5f, 0.52f, 0.58f)));
        var copper = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.82f, 0.45f, 0.2f)));
        var jade = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.25f, 0.72f, 0.5f)));

        return builder
            // UNSCOPED dilate (the unmaskable path), emitted FIRST so it inflates only its own turret, not the floor.
            .Translate(offset: new Vector3(-1.8f, 0.6f, 0f))
            .Box(halfExtents: new Vector3(0.42f, 0.42f, 0.42f), round: 0f, material: copper)
            .ResetPoint()
            .Translate(offset: new Vector3(-1.8f, 1.15f, 0f))
            .Sphere(radius: 0.34f, material: copper)
            .Dilate(radius: 0.12f)
            // The world floor unions on top of the finished (dilated) turret — it stays un-inflated.
            .ResetPoint()
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // SCOPED dilate: an identical turret inflated INSIDE a scope, so the dilation touches only its own shapes and
            // the Union compose leaves the floor intact — the far-neutral, still-maskable path.
            .ResetPoint()
            .PushField(compose: SdfBlendOp.Union)
            .Translate(offset: new Vector3(1.8f, 0.6f, 0f))
            .Box(halfExtents: new Vector3(0.42f, 0.42f, 0.42f), round: 0f, material: jade)
            .ResetPoint()
            .Translate(offset: new Vector3(1.8f, 1.15f, 0f))
            .Sphere(radius: 0.34f, material: jade)
            .Dilate(radius: 0.12f)
            .PopField()
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // Single-material rounded turrets over a floor, all union blends — the only residual is smooth-shading ±1-LSB
        // codegen noise, so the diff judges under WorldComposite (the hero posture).
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-dilate",
            program: BuildDilateScene(),
            thresholds: ParityThresholds.WorldComposite,
            passLabel: $"{WorldWidth}x{WorldHeight} scoped + unscoped Dilate turrets over an intact floor | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldComposite thresholds"
        );
    }
}
