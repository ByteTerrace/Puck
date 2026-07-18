using System.Numerics;
using System.Runtime.Versioning;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Cross-backend parity for <see cref="SdfOp.RepeatLimited"/> — the bounded domain-repeat fold, used only
/// as scaffolding elsewhere and never given a dedicated scene. RepeatLimited is <see cref="SdfOp.Repeat"/> with the
/// per-cell <c>round()</c> index clamped to ±limit per axis, so the prototype tiles a FINITE lattice; the spacing clamp
/// is host-baked but (unlike Repeat) the limit occupies <c>Data1.xyz</c>, leaving no free lane for the reciprocal, so
/// the shader keeps its per-eval divide — a distinct codegen path from Repeat's, worth its own gate. The lattice makes
/// the limit visually load-bearing: <c>limit (2, 0, 1)</c> populates exactly 5 cells in X × 1 in Y × 3 in Z = 15
/// copies, so a wrong clamp (off-by-one, or a Repeat-vs-RepeatLimited op mixup) renders extra or missing copies at the
/// lattice edge — a gross silhouette change the diff cannot miss.
/// <para>The prototype is a single-material on-center box within half-spacing per axis (the exactness contract
/// <see cref="SdfProgramBuilder.RepeatLimited"/> documents — an off-center/oversized prototype would crease the field at
/// interior cell walls with a march-holing overestimate, which is a HOLING concern, not a parity one). No per-cell
/// material stride, so there is no ownership seam — the residual is smooth-shading ±1-LSB codegen noise, the
/// <c>WorldComposite</c> posture the sibling lattice fold WorldWallpaperStage earns.</para>
/// </summary>
internal sealed class WorldRepeatLimitedStage : IPostStage {
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-repeat-limited";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // The floor is the world set (a plain Union). The bounded lattice folds the point AFTER a ResetPoint, then a
    // post-fold Translate lifts every copy uniformly off the floor (the offset applies in the shared base-cell frame, so
    // it shifts all copies identically); the box then unions on. The Y spacing (6.0) with limit 0 keeps a single Y row,
    // so only X and Z tile — a compact 5×3 slab of boxes.
    internal static SdfProgram BuildRepeatLimitedScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.5f, y: 0.52f, z: 0.58f)));
        var brick = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.8f, y: 0.4f, z: 0.28f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            .ResetPoint()
            .RepeatLimited(spacing: new Vector3(x: 1.2f, y: 6.0f, z: 1.2f), limit: new Vector3(x: 2f, y: 0f, z: 1f))
            .Translate(offset: new Vector3(x: 0f, y: 0.55f, z: 0f))
            .Box(halfExtents: new Vector3(x: 0.32f, y: 0.32f, z: 0.32f), round: 0.05f, material: brick)
            .Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // A pure geometric lattice (no per-cell recolor) of single-material boxes over a floor: the only residual is
        // smooth-shading ±1-LSB codegen noise, so the diff judges under WorldComposite (the WorldWallpaperStage posture).
        return WorldStage.RunSceneParity(
            context: context,
            prefix: "world-repeat-limited",
            program: BuildRepeatLimitedScene(),
            thresholds: ParityThresholds.WorldComposite,
            passLabel: $"{WorldWidth}x{WorldHeight} bounded 5x1x3 lattice (RepeatLimited limit 2/0/1) | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within WorldComposite thresholds"
        );
    }
}
