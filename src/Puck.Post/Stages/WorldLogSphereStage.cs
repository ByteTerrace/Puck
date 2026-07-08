using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for the D2 LOG-SPHERICAL domain warp (<see cref="SdfOp.LogSphere"/>): a ground
/// plane and a Droste showcase — one torus prototype tiled by a log-spherical fold into a spiral of self-similar
/// nested rings (shellRatio 2, a per-shell Z-spin), plus a box tiled with no spin (concentric shells) — the
/// max-visual-density-per-instruction pitch rendered on both backends.
/// <para>The fold puts <c>log</c>/<c>exp</c>/<c>round</c>/<c>cos</c>/<c>sin</c> into the differential path and is not an
/// isometry, so — exactly like <see cref="WorldWarpStage"/>'s twist/bend — DXC's SPIR-V and DXIL codegen re-roll the
/// benign ±1-LSB noise and the many self-similar shell edges dither along gradient bands. The diff therefore judges
/// under <c>WorldLsbExact</c> (the every-delta-exactly-±1 family that survives codegen redistribution), staying within
/// an EXISTING threshold family — no threshold moved.</para>
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
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.5f, 0.52f, 0.58f)));
        var brick = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.8f, 0.35f, 0.25f)));
        var teal = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.7f, 0.7f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            // A torus tiled into a Droste spiral of self-similar nested rings (per-shell Z-spin).
            .ResetPoint()
            .Translate(offset: new Vector3(-1.5f, 1.4f, 0.0f))
            .LogSphere(shellRatio: 2.0f, twist: 0.6f)
            .Torus(majorRadius: 0.7f, minorRadius: 0.16f, material: brick)
            // A box tiled into concentric self-similar shells (no spin).
            .ResetPoint()
            .Translate(offset: new Vector3(1.7f, 1.2f, 0.2f))
            .LogSphere(shellRatio: 1.9f)
            .Box(halfExtents: new Vector3(0.55f, 0.2f, 0.55f), round: 0.05f, material: teal)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // The log-spherical fold puts log/exp/round/cos/sin into the differential path and is not an isometry, so the
        // diff judges under WorldLsbExact — the every-delta-exactly-±1 signature that survives codegen redistribution
        // (the WorldWarpStage precedent). An existing family; no threshold moved.
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-log-sphere",
            program: BuildLogSphereScene(),
            thresholds: ParityThresholds.WorldLsbExact,
            passLabel: $"{WorldWidth}x{WorldHeight} log-spherical Droste warp (spinning + concentric shells) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldLsbExact thresholds"
        );
    }
}
