using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;
using Puck.SdfVm.Debug;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. The DRIFT MONOLITH — one hand-authored scene (<see cref="SdfDriftMonolith"/>, shared verbatim with
/// the demo gallery's monolith exhibit) that deliberately STACKS every known cross-backend parity amplifier into a
/// single frame, so the battery carries a constructed worst-case whose measured divergence is the calibration
/// CEILING for real content. Where the other world stages each isolate ONE phenomenon under its own tight family,
/// this one fires all of them at once:
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
        // Accurate fold-boundary marching can legitimately
        // flips a boundary sample's SHELL on a ±1-ULP backend difference (ParityThresholds.WorldFoldBoundary carries
        // the full evidence), and this scene deliberately STACKS a Droste region on the other amplifiers — measured
        // mean 0.53 with both backends visually correct and artifact-free. 2.0 keeps this the battery's generous
        // ceiling (= the fold family's cap); a missing/relocated/recolored region still blows far past it.
        MaxMeanAbsError = 2.0,
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

    // The fixed monolith frame: one full-region viewport, high and pulled back on +Z (fov 60°) so all five amplifier
    // regions sit inside the frustum at once and the far wall + ground horizon fall in the grazing band. Time 0, no
    // dynamic entities — only the scene varies from the other world stages.
    private static SdfFrame BuildMonolithFrame(SdfProgram program) {
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(x: 0f, y: 3.6f, z: 9.6f),
            target: new Vector3(x: 0f, y: 1f, z: 0f),
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
        var frame = BuildMonolithFrame(program: SdfDriftMonolith.Build());

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
