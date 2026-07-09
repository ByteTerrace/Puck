using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. The DRIFT MONOLITH — one hand-authored scene that deliberately STACKS every known cross-backend
/// parity amplifier into a single frame, so the battery carries a constructed worst-case whose measured divergence is
/// the calibration CEILING for real content. Where the other world stages each isolate ONE phenomenon under its own
/// tight family, this one fires all of them at once:
/// <list type="bullet">
///   <item>a <b>LogSphere Droste region</b> (left) — <c>log</c>/<c>exp</c>/<c>cos</c>/<c>sin</c> in the per-sample path
///   and self-similar shell boundaries where a 1-ulp radial step flips which shell a march lands in (the
///   transcendental + field-discontinuity classes, <see cref="WorldLogSphereStage"/>'s pitch);</item>
///   <item>a <b>wallpaper-folded region</b> (right) — a P6M kaleidoscope whose fold branches and parity-material
///   stride recolor cells, so a 1-ulp fold decision flips a cell's winner (the fold-discontinuity class);</item>
///   <item>a <b>near-tie material seam</b> (center) — two equal-radius EMISSIVE spheres of DISTINCT high-contrast
///   materials smooth-blended so their distances tie to within ~1 ulp along a whole vertical strip, where the
///   strict-<c>&lt;</c> material tie-break flips the winning material backend-to-backend (the material-winner-flip
///   class, <see cref="ParityThresholds.WorldHighContrast"/>'s phenomenon, made a whole strip instead of a stray pixel);</item>
///   <item>a <b>deep smooth/chamfer chain</b> (front) — a row of spheres each blended into the last with alternating
///   smooth-min and √2-chamfer seams, deep enough that the nested <c>smin</c>/chamfer arithmetic accumulates LSB
///   noise the two codegens round differently;</item>
///   <item>a <b>thin far grazing wall</b> (back) plus the ground horizon — a razor-thin slab receding toward
///   <c>MaxDistance</c> whose silhouette grazes under footprint-adaptive termination (the grazing-silhouette class).</item>
/// </list>
/// <para><b>Framing.</b> The camera sits high and pulled back on +Z looking at the origin (fov 60°) so all five
/// regions fall inside the frustum at once: the Droste torus/box left of center, the emissive seam and smooth chain
/// through the middle, the wallpaper lattice right of center, and — behind the origin — the thin wall and the ground
/// horizon receding to the far grazing band. A finite <c>stepScale</c> WILL be baked (LogSphere, the chamfer seams,
/// and the far reach all clamp the march); that is expected and fine — the scene is meant to exercise the clamped
/// march, not dodge it.</para>
/// <para><b>Threshold intent — the CEILING, not a tight gate.</b> This stage is judged against a deliberately
/// GENEROUS envelope (<see cref="DriftCeiling"/>): stacking every amplifier means it legitimately trips every
/// fine-grained benign signature simultaneously (deltas off ±1 at the seam flips, clustered differing pixels at the
/// shell/fold discontinuities, wide spread from the transcendental dither), so guarding those here would make the
/// worst-case-by-construction the TIGHTEST gate in the battery — the exact inversion of its job. Only the two guards a
/// real GROSS divergence cannot dodge stay live (mean and spread). The standing finding this stage exists to surface:
/// if this monolith ever measures LESS drift than a real-content world stage, that real stage has a bug the tight
/// families should have caught; and any real-content stage measuring ABOVE this ceiling is over the honest budget.</para>
/// </summary>
internal sealed class WorldDriftMonolithStage : IPostStage {
    private const float FieldOfViewRadians = (60f * (MathF.PI / 180f));
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    // The deliberately GENEROUS measured-ceiling envelope, defined LOCALLY (not in ParityThresholds) because it has no
    // demo counterpart to keep in sync and it is intentionally looser than every shared family. It is >= the relaxed
    // default posture on every guard, so it can never gate tighter than the rest of the battery does by default.
    private static readonly ParityThresholdSet DriftCeiling = new() {
        MaxChannelDelta = 255,       // disabled: material-winner + shell-boundary flips are legitimately large here.
        MaxMeanAbsError = 0.35,      // the relaxed ceiling; a missing/relocated/recolored region blows far past it.
        MaxPercentDiffering = 25.0,  // generous: every amplifier redistributes ±1 dither widely; a wrong LAYOUT still trips it.
        MinIsolatedFraction = 0.0,   // disabled: seams and discontinuities cluster the differing pixels by design.
        MinUnitDeltaFraction = 0.0,  // disabled: material flips + discontinuity marching produce multi-LSB deltas by design.
    };

    /// <inheritdoc/>
    public string Name => "world-drift-monolith";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    /// <summary>Builds the stacked-amplifier scene. Every region composes through the UNION family (smooth/chamfer
    /// union, plain union) so nothing annihilates its neighbours (the accumulator rule); the emissive seam is emitted
    /// LAST so its material-winner flip is always present regardless of what precedes it.</summary>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildMonolithScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.5f, 0.52f, 0.58f)));
        var brick = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.8f, 0.35f, 0.25f)));
        var teal = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.7f, 0.7f)));
        var rose = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.35f, 0.45f)));
        // The two hex-stride rows are reached ONLY through the wallpaper chain's 3-coloring — never named directly.
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.4f, 0.85f, 0.6f)));
        _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.65f, 0.35f, 0.8f)));
        var jade = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.7f, 0.45f)));
        var wall = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.45f, 0.48f, 0.52f)));
        // The seam pair: two DISTINCT high-contrast EMISSIVE materials, so a winner flip along the tie strip is a large,
        // obvious cross-backend delta (the WorldHighContrast phenomenon, escalated to a whole strip).
        var seamCream = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.96f, 0.9f, 0.72f), Emissive: 0.85f));
        var seamAzure = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.25f, 0.5f, 0.96f), Emissive: 0.85f));

        _ = builder.Plane(normal: Vector3.UnitY, offset: 0f, material: ground);

        // LEFT — LogSphere Droste: a torus tiled into a spinning spiral of self-similar shells (per-shell Z-spin) and a
        // box tiled into concentric shells. Transcendental fold + discontinuous shell boundaries.
        _ = builder
            .ResetPoint()
            .Translate(offset: new Vector3(-2.7f, 1.5f, -0.3f))
            .LogSphere(shellRatio: 2.0f, twist: 0.6f)
            .Torus(majorRadius: 0.62f, minorRadius: 0.15f, material: brick)
            .ResetPoint()
            .Translate(offset: new Vector3(-2.7f, 1.5f, -0.3f))
            .LogSphere(shellRatio: 1.9f)
            .Box(halfExtents: new Vector3(0.5f, 0.18f, 0.5f), round: 0.05f, material: teal);

        // RIGHT — wallpaper fold: a P6M hex kaleidoscope over an ASYMMETRIC motif (cone + off-center sphere) with a
        // 3-coloring parity stride, so a flipped mirror or a trunc-vs-floor cell key changes pixels on one backend.
        // The motif sits well clear of cell boundaries/seams so every fold branch is an exact isometry.
        _ = builder
            .ResetPoint()
            .Translate(offset: new Vector3(2.7f, 0f, 0.2f))
            .WallpaperFold(group: SdfWallpaperGroup.P6M, cell: new Vector2(0.85f, 0.85f), limit: new Vector2(2f, 2f), materialStride: 1)
            .Translate(offset: new Vector3(0.05f, 0f, 0.1f))
            .RoundCone(lowerRadius: 0.13f, upperRadius: 0.04f, height: 0.28f, material: rose)
            .ResetPoint()
            .Translate(offset: new Vector3(2.7f, 0f, 0.2f))
            .WallpaperFold(group: SdfWallpaperGroup.P6M, cell: new Vector2(0.85f, 0.85f), limit: new Vector2(2f, 2f), materialStride: 1)
            .Translate(offset: new Vector3(-0.07f, 0.05f, 0.02f))
            .Sphere(radius: 0.07f, material: rose);

        // FRONT — deep smooth/chamfer chain: a row of overlapping spheres, the first a plain union, each subsequent one
        // blended into the running field with an ALTERNATING smooth-min / √2-chamfer seam. Deep nesting so the smin and
        // chamfer arithmetic accumulates the LSB noise the two codegens contract differently.
        _ = builder.ResetPoint().Translate(offset: new Vector3(-1.05f, 0.55f, 2.5f)).Sphere(radius: 0.4f, material: jade);

        for (var link = 1; (link < 7); link++) {
            var blend = (((link & 1) == 0) ? SdfBlendOp.ChamferUnion : SdfBlendOp.SmoothUnion);

            _ = builder
                .ResetPoint()
                .Translate(offset: new Vector3((-1.05f + (link * 0.34f)), (0.55f + (((link & 1) == 0) ? 0.06f : -0.03f)), 2.5f))
                .Sphere(radius: 0.38f, material: jade, blend: blend, smooth: 0.3f);
        }

        // BACK — thin far grazing wall: a wide, tall, razor-thin slab set far behind the origin so its top and side
        // silhouettes graze near MaxDistance under footprint-adaptive termination (the ground horizon does the same).
        _ = builder.ResetPoint().Translate(offset: new Vector3(0f, 2f, -6.5f)).Box(halfExtents: new Vector3(7f, 2f, 0.03f), round: 0f, material: wall);

        // CENTER (emitted LAST) — near-tie material seam: two equal-radius emissive spheres centered symmetrically about
        // x = 0, smooth-unioned so along the x = 0 strip their distances tie within ~1 ulp and the strict-< material
        // tie-break flips the winner backend-to-backend across the whole strip.
        _ = builder
            .ResetPoint()
            .Translate(offset: new Vector3(-0.62f, 1.05f, 0.7f))
            .Sphere(radius: 0.92f, material: seamCream, blend: SdfBlendOp.SmoothUnion, smooth: 0.5f)
            .ResetPoint()
            .Translate(offset: new Vector3(0.62f, 1.05f, 0.7f))
            .Sphere(radius: 0.92f, material: seamAzure, blend: SdfBlendOp.SmoothUnion, smooth: 0.5f);

        return builder.Build();
    }

    // The fixed monolith frame: one full-region viewport, high and pulled back on +Z (fov 60°) so all five amplifier
    // regions sit inside the frustum at once and the far wall + ground horizon fall in the grazing band. Time 0, no
    // dynamic entities — only the scene varies from the other world stages.
    private static SdfFrame BuildMonolithFrame(SdfProgram program) {
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(0f, 3.6f, 9.6f),
            target: new Vector3(0f, 1f, 0f),
            fieldOfViewRadians: FieldOfViewRadians,
            viewportWidth: WorldWidth,
            viewportHeight: WorldHeight
        );

        return new SdfFrame(
            Program: program,
            ProgramChanged: false,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            Time: 0f,
            WarpAmount: 0f
        );
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        var frame = BuildMonolithFrame(program: BuildMonolithScene());

        // Vulkan reference (SPIR-V) on the host device; Direct3D 12 comparand (DXIL) on the shared Tier-C device — the
        // identical engine and frame, only the backend differs. The WriteEvaluateReport tail puts the drift metrics
        // (diff%, maxΔ, isolated%, unitΔ) into the pass line via ParityCheck.Describe — this stage's whole point.
        var vulkanPixels = WorldStage.RenderWorldFrame(device: context.RequireGpuDevice(), gpu: context.Resolve<IGpuComputeServices>(), bytecodeExtension: ".spv", frame: frame, width: WorldWidth, height: WorldHeight);
        var directX = context.RequireDirectXDevice();
        var directXPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => WorldStage.RenderWorldFrame(device: directX.DeviceContext, gpu: directX.Services.GetRequiredService<IGpuComputeServices>(), bytecodeExtension: ".dxil", frame: frame, width: WorldWidth, height: WorldHeight));

        return ParityCheck.WriteEvaluateReport(
            artifactsDirectory: context.ArtifactsDirectory,
            comparandPixels: directXPixels,
            height: (int)WorldHeight,
            passLabel: $"{WorldWidth}x{WorldHeight} DRIFT MONOLITH (LogSphere Droste + P6M wallpaper + near-tie emissive material seam + deep smooth/chamfer chain + far grazing wall) — the stacked-amplifier parity CEILING | Vulkan (SPIR-V) vs Direct3D 12 (DXIL) within the generous drift-ceiling envelope",
            prefix: "world-drift-monolith",
            referencePixels: vulkanPixels,
            thresholds: DriftCeiling,
            width: (int)WorldWidth
        );
    }
}
