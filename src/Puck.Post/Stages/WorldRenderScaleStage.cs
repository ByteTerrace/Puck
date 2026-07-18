using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage — the per-view RENDER-SCALE lever's correctness tooth. Renders one scene twice on the Vulkan host:
/// native (RenderScale 1 — the bit-exact fast path the whole battery already rides) and reduced (RenderScale 0.5 —
/// Stage 1 renders the view at the integer-derived half extent, the beam/instance-cull tile coverage follows, and
/// Stage 2 bilinearly upsamples back into the full region). The reduced render must be the SAME scene, softer: a
/// broken scale path (wrong extent derivation on either stage, a tile-coverage mismatch, an upsample offset) shows
/// up as displaced/garbage/black content and blows the generous structural envelope; a correct one differs only by
/// resampling blur. Presentation-only lever, but the derivation is a cross-kernel ENGINE contract (worldRenderDims,
/// KEEP IN SYNC across sdf-world.hlsli / the beam / instance-cull / composite / SdfWorldEngine packing) — hence a
/// gate. Deterministic: fixed scene, camera, single frames at time 0.
/// </summary>
internal sealed class WorldRenderScaleStage : IPostStage {
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));
    private const uint Height = 600;
    private const float ReducedScale = 0.5f;
    private const uint Width = 960;

    // The native-vs-upsampled diff is pure resampling blur concentrated at edges. A broken scale
    // path (displaced content, black regions, a wrong extent) lifts the mean by an order of magnitude and/or the
    // spread toward the whole frame.
    private static readonly ParityThresholdSet ScaleEnvelope = new() {
        MaxChannelDelta = 255,      // disabled: an edge pixel legitimately changes by a whole silhouette class under resampling.
        MaxMeanAbsError = 3.0,      // Benign blur measured mean 0.91 on this scene (spread 45.39%, maxΔ237, edge-confined); a displaced/black render blows far past this.
        MaxPercentDiffering = 80.0, // resampling touches most edge/gradient pixels; a wrong LAYOUT still exceeds it.
        MinIsolatedFraction = 0.0,  // disabled: blur clusters along every edge by design.
        MinUnitDeltaFraction = 0.0, // disabled: blur deltas are multi-LSB by design.
    };

    /// <inheritdoc/>
    public string Name => "world-render-scale";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return RunCore(context: context);
    }

    // A compact high-structure scene: a ground plane and two contrasting spheres — enough silhouettes and gradients
    // that a displaced or truncated reduced render cannot hide.
    private static SdfProgram BuildScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.5f, y: 0.52f, z: 0.58f)));
        var red = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.85f, y: 0.2f, z: 0.15f)));
        var blue = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.15f, y: 0.35f, z: 0.85f)));

        return builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            .ResetPoint()
            .Translate(offset: new Vector3(x: -0.9f, y: 0.9f, z: 0f))
            .Sphere(radius: 0.9f, material: red)
            .ResetPoint()
            .Translate(offset: new Vector3(x: 1.1f, y: 0.6f, z: 0.4f))
            .Sphere(radius: 0.6f, material: blue)
            .Build();
    }
    private static PostStageOutcome RunCore(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var program = BuildScene();
        var nativePixels = WorldStage.RenderWorldFrame(device: device, gpu: gpu, bytecodeExtension: ".spv", frame: BuildFrame(program: program, renderScale: 1f), width: Width, height: Height);
        var reducedPixels = WorldStage.RenderWorldFrame(device: device, gpu: gpu, bytecodeExtension: ".spv", frame: BuildFrame(program: program, renderScale: ReducedScale), width: Width, height: Height);

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        return ParityCheck.WriteEvaluateReport(
            artifactsDirectory: context.ArtifactsDirectory,
            prefix: "world-render-scale",
            referencePixels: nativePixels,
            comparandPixels: reducedPixels,
            width: (int)Width,
            height: (int)Height,
            thresholds: ScaleEnvelope,
            passLabel: $"{Width}x{Height} render-scale lever | native vs RenderScale {ReducedScale} (integer-derived half extent, bilinear upsample) on the Vulkan host, within the resampling-blur envelope"
        );
    }
    private static SdfFrame BuildFrame(SdfProgram program, float renderScale) {
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(x: 0.4f, y: 1.6f, z: 5.2f),
            target: new Vector3(x: 0f, y: 0.7f, z: 0f),
            fieldOfViewRadians: FieldOfViewRadians,
            viewportWidth: Width,
            viewportHeight: Height
        );

        return new SdfFrame(
            Program: program,
            ProgramChanged: false,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f)) { RenderScale = renderScale }],
            Time: 0f,
            WarpAmount: 0f
        );
    }
}
