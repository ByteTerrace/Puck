using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the log-spherical domain warp (<see cref="SdfOp.LogSphere"/>): a ground
/// plane and a Droste showcase — one torus prototype tiled by a log-spherical fold into a spiral of self-similar
/// nested rings (shellRatio 2, a per-shell Z-spin), plus a box tiled with no spin (concentric shells) — the
/// max-visual-density-per-instruction pitch rendered on both backends.
/// <para>The diff judges under <c>WorldFoldBoundary</c>: the fold-safe step bound makes marchers stop and
/// resample at every shell boundary instead of striding through, so a ±1-ULP cross-backend difference at a boundary
/// sample legitimately flips which SHELL the pixel resolves — a whole material/shading class, not ±1-LSB dither. This
/// A parity check alone cannot detect a defect shared by both backends, so correctness is gated by
/// <see cref="WorldDrosteSolidityStage"/> and <see cref="WorldLogSphereSolidityStage"/>, and this diff now guards only
/// region/layout-level divergence.</para>
/// </summary>
internal sealed class WorldLogSphereStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-log-sphere";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    /// <summary>The Droste showcase: a torus tiled by a spinning log-spherical fold (a spiral of nested rings) and a
    /// box tiled by a concentric fold, both from a handful of instructions — the log-spherical warp's expressiveness
    /// pitch. Content is kept radially centred in its shell cell (the Repeat contract) so the single-cell field stays
    /// conservative.</summary>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildLogSphereScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.5f, y: 0.52f, z: 0.58f)));
        var brick = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.8f, y: 0.35f, z: 0.25f)));
        var teal = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.2f, y: 0.7f, z: 0.7f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // A torus tiled into a Droste spiral of self-similar nested rings (per-shell Z-spin).
            .ResetPoint()
            .Translate(offset: new Vector3(x: -1.5f, y: 1.4f, z: 0.0f))
            .LogSphere(shellRatio: 2.0f, twist: 0.6f)
            .Torus(majorRadius: 0.7f, minorRadius: 0.16f, material: brick)
            // A box tiled into concentric self-similar shells (no spin).
            .ResetPoint()
            .Translate(offset: new Vector3(x: 1.7f, y: 1.2f, z: 0.2f))
            .LogSphere(shellRatio: 1.9f)
            .Box(halfExtents: new Vector3(x: 0.55f, y: 0.2f, z: 0.55f), round: 0.05f, material: teal)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // Judged under WorldFoldBoundary (see the family's calibration note): honest fold-boundary marching makes a
        // ±1-ULP backend difference legitimately flip a boundary sample's SHELL, so this diff guards region/layout
        // divergence only — the solidity stages carry the correctness teeth for this scene class.
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-log-sphere",
            program: BuildLogSphereScene(),
            thresholds: ParityThresholds.WorldFoldBoundary,
            passLabel: $"{WorldWidth}x{WorldHeight} log-spherical Droste warp (spinning + concentric shells) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldFoldBoundary thresholds"
        );
    }
}
