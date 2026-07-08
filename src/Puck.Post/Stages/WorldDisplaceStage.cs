using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the DISPLACE field op (<see cref="SdfOp.Displace"/>): a large smooth
/// sphere gets a bounded sinusoidal relief (<c>amplitude·sin(fx·x)sin(fy·y)sin(fz·z)</c>) added to its field, turning
/// a bald sphere into a visibly corrugated, bumpy shell — REAL geometry that shadows and self-occludes, not a normal
/// map. The separable sin-product basis is float trig (deterministic across both backends, like the twist/bend
/// warps), so a per-pixel ±1-LSB seam is expected under the relief's high-frequency shading gradient. Displace is
/// NOT 1-Lipschitz (the relief's gradient reaches <c>amplitude·‖frequency‖</c>); the scene keeps that product
/// moderate (~0.5) so the clamped march stays decisive rather than a knife-edge — mirroring how
/// <see cref="WorldWarpSolidityStage"/>/<see cref="WorldCellJitterSolidityStage"/> pick their own margins.
/// </summary>
internal sealed class WorldDisplaceStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-displace";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // A large sphere corrugated by Displace (frequency 2.5/2.5/2.5, amplitude 0.12 -> amplitude*|frequency| ~= 0.52):
    // a moderate, clearly-visible bumpy relief with ample step budget under the baked clamp.
    internal static SdfProgram BuildDisplaceScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.42f, 0.46f, 0.52f)));
        var coral = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.4f, 0.25f), Emissive: 0.3f));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            .Translate(offset: new Vector3(0f, 1.3f, 0f))
            .Sphere(radius: 1.3f, material: coral)
            .Displace(frequency: new Vector3(2.5f, 2.5f, 2.5f), amplitude: 0.12f)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // The relief's high-frequency shading gradient carries the usual float-trig ±1-LSB seam, and its bumps create
        // isolated high-contrast winner flips at grazing silhouette folds, so WorldHighContrast (not the plainer
        // WorldComposite) is this scene's posture.
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-displace",
            program: BuildDisplaceScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} Displace-corrugated sphere (freq 2.5/2.5/2.5, amp 0.12) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds"
        );
    }
}
