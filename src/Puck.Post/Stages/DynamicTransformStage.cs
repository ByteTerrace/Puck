using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-B stage B7. The per-frame entity-transform channel (<c>SdfOp.TransformDynamic</c>): one program containing a
/// dynamic-slot sphere is uploaded ONCE through <see cref="PostWorldRenderer"/>, then two frames are rendered against
/// it with different <see cref="SdfFrame.DynamicTransforms"/> — T0 at the resting position, T1 moved +1.5 world units
/// in X — so the entity moves purely by rewriting the small per-frame transform buffer. The stage asserts (a) the
/// entity visibly moved (a meaningful fraction of pixels differs between T0 and T1) and (b) each dynamic frame matches
/// its BAKED equivalent — a second program with the same transform baked as a static <c>Translate</c> — within the
/// <c>Continuous</c> parity thresholds copied from the demo's calibrated <c>ParityThresholds</c> (the dynamic opcode
/// with an identity orientation is arithmetically the immediate translate, so any divergence past ±1-LSB noise means
/// the channel's buffer indexing, packing, or opcode broke). Artifacts: the dynamic and baked renders of both frames.
/// </summary>
internal sealed class DynamicTransformStage : IPostStage {
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));
    private const uint OutputHeight = 128;
    private const uint OutputWidth = 256;
    // The "visibly moved" gate for T0 vs T1: at least this fraction of pixels must differ by a clearly-not-noise delta.
    private const int MovedChannelDelta = 8;
    private const double MinMovedFraction = 0.01;

    private static readonly Vector3 RestingPosition = new(0f, 0.8f, 0f);
    private static readonly Vector3 MovedPosition = new(1.5f, 0.8f, 0f);

    /// <inheritdoc/>
    public string Name => "dynamic-transform";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var dynamicProgram = BuildScene(bakedPosition: null);
        var bakedRestingProgram = BuildScene(bakedPosition: RestingPosition);
        var bakedMovedProgram = BuildScene(bakedPosition: MovedPosition);

        // ONE renderer for both dynamic frames — the program is uploaded once at construction and never re-written,
        // which is exactly the seam under test: only the 32-byte transform slot changes between T0 and T1.
        using var dynamicRenderer = new PostWorldRenderer(bytecodeExtension: ".spv", device: device, dynamicTransformCapacity: 1, gpu: gpu, height: OutputHeight, program: dynamicProgram, width: OutputWidth);

        var dynamicResting = dynamicRenderer.RenderFrame(frame: BuildFrame(program: dynamicProgram, transforms: [new DynamicTransform(Position: RestingPosition, Orientation: Quaternion.Identity)]));
        var dynamicMoved = dynamicRenderer.RenderFrame(frame: BuildFrame(program: dynamicProgram, transforms: [new DynamicTransform(Position: MovedPosition, Orientation: Quaternion.Identity)]));

        using var bakedRestingRenderer = new PostWorldRenderer(bytecodeExtension: ".spv", device: device, dynamicTransformCapacity: 1, gpu: gpu, height: OutputHeight, program: bakedRestingProgram, width: OutputWidth);
        using var bakedMovedRenderer = new PostWorldRenderer(bytecodeExtension: ".spv", device: device, dynamicTransformCapacity: 1, gpu: gpu, height: OutputHeight, program: bakedMovedProgram, width: OutputWidth);

        var bakedResting = bakedRestingRenderer.RenderFrame(frame: BuildFrame(program: bakedRestingProgram, transforms: []));
        var bakedMoved = bakedMovedRenderer.RenderFrame(frame: BuildFrame(program: bakedMovedProgram, transforms: []));

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var artifactPath = Path.Combine(context.ArtifactsDirectory, "dynamic-transform-moved-dynamic.png");

        PngImage.Write(height: (int)OutputHeight, path: Path.Combine(context.ArtifactsDirectory, "dynamic-transform-resting-dynamic.png"), rgba: dynamicResting, width: (int)OutputWidth);
        PngImage.Write(height: (int)OutputHeight, path: Path.Combine(context.ArtifactsDirectory, "dynamic-transform-resting-baked.png"), rgba: bakedResting, width: (int)OutputWidth);
        PngImage.Write(height: (int)OutputHeight, path: artifactPath, rgba: dynamicMoved, width: (int)OutputWidth);
        PngImage.Write(height: (int)OutputHeight, path: Path.Combine(context.ArtifactsDirectory, "dynamic-transform-moved-baked.png"), rgba: bakedMoved, width: (int)OutputWidth);

        // (a) The entity visibly moved: T0 vs T1 must differ meaningfully (the transform buffer actually drives pixels).
        var movedPixels = 0;
        var totalPixels = (int)(OutputWidth * OutputHeight);

        for (var pixel = 0; (pixel < totalPixels); pixel++) {
            var offset = (pixel * 4);
            var delta = Math.Max(
                Math.Abs(value: (dynamicResting[offset] - dynamicMoved[offset])),
                Math.Max(
                    Math.Abs(value: (dynamicResting[offset + 1] - dynamicMoved[offset + 1])),
                    Math.Abs(value: (dynamicResting[offset + 2] - dynamicMoved[offset + 2]))
                )
            );

            if (delta >= MovedChannelDelta) {
                movedPixels++;
            }
        }

        var movedFraction = ((double)movedPixels / totalPixels);

        if (movedFraction < MinMovedFraction) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"only {(movedFraction * 100.0):0.###}% of pixels changed (>= {MovedChannelDelta} LSB) between the resting and moved frames — the dynamic-transform buffer did not move the entity");
        }

        // (b) Each dynamic frame matches its baked-equivalent render within the Continuous thresholds (the shared
        // ParityCheck substrate; values copied from the demo's calibrated ParityThresholds).
        var restingFailures = ParityThresholds.Continuous.Evaluate(metrics: ParityMetrics.Compute(reference: bakedResting, comparand: dynamicResting, width: (int)OutputWidth, height: (int)OutputHeight));

        if (restingFailures.Count > 0) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"resting frame: dynamic vs baked diverged — {string.Join(separator: "; ", values: restingFailures)}");
        }

        var movedFailures = ParityThresholds.Continuous.Evaluate(metrics: ParityMetrics.Compute(reference: bakedMoved, comparand: dynamicMoved, width: (int)OutputWidth, height: (int)OutputHeight));

        if (movedFailures.Count > 0) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"moved frame: dynamic vs baked diverged — {string.Join(separator: "; ", values: movedFailures)}");
        }

        return PostStageOutcome.Pass(
            artifactPath: artifactPath,
            detail: $"program uploaded once; entity moved via the transform buffer ({(movedFraction * 100.0):0.#}% of pixels changed) and both frames match their baked-Translate equivalents within the Continuous thresholds"
        );
    }

    // The scene: a ground plane, a static landmark box (which must NOT move between frames — the parity compare
    // against the baked programs covers it), and the mover sphere. With bakedPosition null the sphere rides dynamic
    // slot 0; otherwise the same rigid transform is baked in as a static Translate — the oracle program.
    private static SdfProgram BuildScene(Vector3? bakedPosition) {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.5f, 0.55f, 0.6f)));
        var mover = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.85f, 0.25f, 0.2f)));
        var landmark = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.5f, 0.85f)));

        _ = builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            .Translate(offset: new Vector3(-2f, 0.6f, -1f))
            .Box(halfExtents: new Vector3(0.6f, 0.6f, 0.6f), round: 0.05f, material: landmark)
            .ResetPoint();
        _ = (bakedPosition is null)
            ? builder.TransformDynamic(slot: 0)
            : builder.Translate(offset: bakedPosition.Value);

        return builder
            .Sphere(radius: 0.8f, material: mover)
            .Build();
    }

    // One fixed full-screen camera framing both entity positions, so only the entity's motion changes the image.
    private static SdfFrame BuildFrame(SdfProgram program, IReadOnlyList<DynamicTransform> transforms) {
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(0.75f, 3.5f, 7.5f),
            target: new Vector3(0.75f, 0.8f, 0f),
            fieldOfViewRadians: FieldOfViewRadians,
            viewportWidth: OutputWidth,
            viewportHeight: OutputHeight
        );

        return new SdfFrame(
            Program: program,
            ProgramChanged: false,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            Time: 0f,
            WarpAmount: 0f
        ) {
            DynamicTransforms = transforms,
        };
    }

}
