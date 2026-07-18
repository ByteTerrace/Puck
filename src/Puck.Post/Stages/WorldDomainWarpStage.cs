using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the DOMAIN-WARP point op (<see cref="SdfOp.DomainWarp"/>): a torus's sample
/// point is perturbed by a bounded, cross-coupled sinusoidal field
/// (<c>x' = x + amplitude·sin(fx·y)</c>, etc.) BEFORE the shape evaluates, giving the torus an organic wobble instead
/// of its usual perfect roundness. The cross-coupled basis is float trig (deterministic across both backends, like
/// the twist/bend warps), so the wobble's high-frequency shading gradient carries the usual ±1-LSB seam. DomainWarp is
/// NOT an isometry (the metric stretches by up to <c>amplitude·‖frequency‖</c>); the scene keeps that product moderate
/// (~0.4) so the clamped march stays decisive rather than a knife-edge — mirroring how
/// <see cref="WorldWarpSolidityStage"/>/<see cref="WorldCellJitterSolidityStage"/> pick their own margins.
/// </summary>
internal sealed class WorldDomainWarpStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-domain-warp";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // A torus wobbled by DomainWarp (frequency 2.0/2.0/2.0, amplitude 0.12 -> amplitude*|frequency| ~= 0.42): a
    // moderate, clearly organic distortion of the torus's otherwise perfectly round profile.
    internal static SdfProgram BuildDomainWarpScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.42f, y: 0.46f, z: 0.52f)));
        var teal = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.2f, y: 0.7f, z: 0.75f), Emissive: 0.3f));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            .Translate(offset: new Vector3(x: 0f, y: 0.9f, z: 0f))
            .DomainWarp(frequency: new Vector3(x: 2.0f, y: 2.0f, z: 2.0f), amplitude: 0.12f)
            .Torus(majorRadius: 0.9f, minorRadius: 0.28f, material: teal)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // The wobble's high-frequency shading gradient carries the usual float-trig ±1-LSB seam, and its organic
        // silhouette folds create isolated high-contrast winner flips at grazing angles, so WorldHighContrast is this
        // scene's posture.
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-domain-warp",
            program: BuildDomainWarpScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} DomainWarp-wobbled torus (freq 2.0/2.0/2.0, amp 0.12) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds"
        );
    }
}
