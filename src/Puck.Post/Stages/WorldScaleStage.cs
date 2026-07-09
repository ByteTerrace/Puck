using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the <see cref="SdfOp.Scale"/> transform, which had NO stage. Scale is the one
/// transform whose correction is HOST-BAKED into two lanes — <c>Data0.xyz</c> = |scale| clamped, <c>Data0.w</c> = the
/// min axis — so the shader collapses the per-evaluation abs/max/min to one lane read and rescales the returned distance
/// by <c>min(scale)</c> (the conservative non-uniform-scale factor: <c>f(S⁻¹p)·min(s)</c> is 1-Lipschitz). Both backends
/// read the SAME baked words, so a divergence here means a codegen difference in the per-eval divide-by-scale or the
/// final min-axis multiply — not a data mismatch. The scene mixes factors above AND below 1 on shapes whose silhouette a
/// wrong reciprocal or a wrong min-axis pick would visibly move:
/// <list type="bullet">
///   <item>A box under a non-uniform Scale(1.6, 0.7, 1.0) — a wide flat slab whose 0.7 min axis sets the distance
///   rescale; a swapped or dropped min-axis would fatten or erode it.</item>
///   <item>A sphere under Scale(0.6, 1.4, 0.6) — MIXED (X/Z shrink, Y stretch) into an upright lozenge, exercising the
///   min-axis pick when the smallest and largest factors straddle 1.</item>
///   <item>A torus under Scale(1.3, 1.3, 0.5) — squashed thin in Z, so a wrong Z reciprocal breaks the ring silhouette.</item>
/// </list>
/// The shapes are non-overlapping single-material solids with union blends — no material-ownership seam, no warp — so the
/// only residual is smooth-shading ±1-LSB codegen noise, exactly the <c>WorldComposite</c> posture the hero and wallpaper
/// scenes earn (a real Scale divergence spreads or exceeds ±1).
/// </summary>
internal sealed class WorldScaleStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-scale";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // Each shape is placed then scaled about its own translated origin; every cluster unions on (Scale is a POINT op
    // applied to the shape that follows, not an accumulator read), so emission order is free.
    internal static SdfProgram BuildScaleScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.5f, 0.52f, 0.58f)));
        var brick = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.8f, 0.35f, 0.25f)));
        var teal = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.7f, 0.7f)));
        var honey = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.7f, 0.3f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // A box scaled UP wide and DOWN in Y: a flat slab whose 0.7 min axis drives the distance rescale.
            .ResetPoint()
            .Translate(offset: new Vector3(-2.2f, 0.55f, 0f))
            .Scale(scale: new Vector3(1.6f, 0.7f, 1.0f))
            .Box(halfExtents: new Vector3(0.5f, 0.5f, 0.5f), round: 0.05f, material: brick)
            // A sphere with MIXED factors (X/Z < 1, Y > 1): an upright lozenge — the min-axis pick with the smallest
            // and largest factors straddling 1.
            .ResetPoint()
            .Translate(offset: new Vector3(0.1f, 0.85f, 0f))
            .Scale(scale: new Vector3(0.6f, 1.4f, 0.6f))
            .Sphere(radius: 0.6f, material: teal)
            // A torus squashed thin in Z: a wrong Z reciprocal breaks the ring's silhouette.
            .ResetPoint()
            .Translate(offset: new Vector3(2.2f, 0.5f, 0f))
            .Scale(scale: new Vector3(1.3f, 1.3f, 0.5f))
            .Torus(majorRadius: 0.55f, minorRadius: 0.2f, material: honey)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // Non-overlapping single-material solids with union blends: no material seam, no warp — the only residual is
        // smooth-shading ±1-LSB codegen noise, so the diff judges under WorldComposite (the hero/wallpaper posture).
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-scale",
            program: BuildScaleScene(),
            thresholds: ParityThresholds.WorldComposite,
            passLabel: $"{WorldWidth}x{WorldHeight} Scale mix (factors >1 and <1, non-uniform min-axis rescale) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldComposite thresholds"
        );
    }
}
