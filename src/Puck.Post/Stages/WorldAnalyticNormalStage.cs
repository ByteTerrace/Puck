using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the ANALYTIC surface normal — the forward-mode gradient dual
/// (sdf-vm.hlsli's <c>mapGradMasked</c>) that, by DEFAULT, replaces the 4-tap finite-difference probe as the lit
/// surface normal. A scene built to exercise the gradient CHAIN, where finite differences visibly banded and the
/// chain rule matters: a TWISTED box column (the twist Jacobian carries sin/cos into the gradient), a bounded
/// REPEATED sphere cluster (the fold is identity in the Jacobian, but the transported leaf gradient must still be
/// right per copy), a SCOPED ONION sphere whose first member is a SmoothUnion (the scope save/restore of the gradient
/// lane, the onion's sign flip, and the smooth blend's gradient lerp all in one cluster), and a SMOOTH-UNION blob
/// pair. Because the analytic normal shades EVERY lit pixel, this is the cross-backend proof for the whole dual path.
/// The scoped onion and the twist put fresh sin/cos and a divide (the gradient normalize) into the differential path
/// and the scope introduces a sharp material seam, so the diff judges under the <c>WorldHighContrast</c> posture — the
/// isolated boundary-pixel material flip the other hard-edged world scenes earn, on top of the ±1-LSB codegen noise.
/// </summary>
internal sealed class WorldAnalyticNormalStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-analytic-normal";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // The op-chain scene the analytic normal is built to get right. Each cluster resets the point chain first, so the
    // Jacobian transport (RESET restores identity) starts clean; the scoped cluster keeps the field op off the floor.
    internal static SdfProgram BuildAnalyticNormalScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.5f, 0.52f, 0.58f)));
        var brick = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.8f, 0.35f, 0.25f)));
        var copper = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.85f, 0.45f, 0.15f), Emissive: 0.25f));
        var jade = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.72f, 0.45f)));
        var azure = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.45f, 0.85f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // A twisted box column — the twist Jacobian is the sharpest analytic-vs-FD divergence: FD taps across the
            // twisting cross-section band; the analytic chain rule carries the exact rotated gradient.
            .ResetPoint()
            .Translate(offset: new Vector3(-2.4f, 1.0f, 0f))
            .TwistY(rate: 1.4f)
            .Box(halfExtents: new Vector3(0.5f, 1.0f, 0.5f), round: 0.05f, material: brick)
            // A bounded 5x1x5 repeated sphere cluster (on-center prototype within half-spacing — the exactness rule):
            // the fold is identity in the Jacobian, so this checks the transported leaf gradient per copy.
            .ResetPoint()
            .Translate(offset: new Vector3(2.4f, 0.28f, 0f))
            .RepeatLimited(spacing: new Vector3(0.72f, 2.0f, 0.72f), limit: new Vector3(2f, 0f, 2f))
            .Sphere(radius: 0.2f, material: jade)
            // A SCOPED ONION sphere whose first member is a SmoothUnion — the scope saves/restores the gradient lane,
            // the onion flips it by sign(d), and the smooth blend lerps it: three gradient paths in one cluster.
            .ResetPoint()
            .PushField(compose: SdfBlendOp.Union)
            .Translate(offset: new Vector3(0f, 0.95f, 0f))
            .Sphere(radius: 0.62f, material: copper, blend: SdfBlendOp.SmoothUnion, smooth: 0.2f)
            .Onion(thickness: 0.06f)
            .PopField()
            // A smooth-union two-sphere blob (the gradient lerp on a soft seam).
            .ResetPoint()
            .Translate(offset: new Vector3(-0.2f, 0.45f, 2.1f))
            .Sphere(radius: 0.42f, material: azure)
            .ResetPoint()
            .Translate(offset: new Vector3(0.7f, 0.45f, 2.1f))
            .Sphere(radius: 0.42f, material: azure, blend: SdfBlendOp.SmoothUnion, smooth: 0.28f)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-analytic-normal",
            program: BuildAnalyticNormalScene(),
            thresholds: ParityThresholds.WorldHighContrast,
            passLabel: $"{WorldWidth}x{WorldHeight} analytic forward-mode normals over a twist/repeat/scoped-onion/smooth op chain | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldHighContrast thresholds"
        );
    }
}
