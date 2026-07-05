using Puck.Capture;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-B stage B7. The per-frame entity-transform channel (<c>SdfOp.TransformDynamic</c>): one program containing a
/// dynamic-slot sphere is uploaded ONCE through <see cref="Puck.SdfVm.SdfWorldEngine"/>, then two frames are rendered against
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
    // An asymmetric "beam" mover (elongated in X) so a rotation is VISIBLE — a sphere is rotation-invariant, which is
    // why the identity-only test could never exercise the quaternion path.
    private static readonly Vector3 MoverHalfExtents = new(0.8f, 0.3f, 0.3f);

    /// <inheritdoc/>
    public string Name => "dynamic-transform";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var dynamicProgram = BuildScene(bakedPosition: null, bakedOrientation: null);
        var bakedRestingProgram = BuildScene(bakedPosition: RestingPosition, bakedOrientation: null);
        var bakedMovedProgram = BuildScene(bakedPosition: MovedPosition, bakedOrientation: null);

        // ONE renderer for all dynamic frames — the program is uploaded once at construction and never re-written,
        // which is exactly the seam under test: only the 32-byte transform slot changes between frames.
        using var dynamicRenderer = new SdfWorldEngine(device: device, gpu: gpu, height: OutputHeight, kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"), options: new SdfWorldEngineOptions(DynamicTransformCapacity: 1, Program: dynamicProgram), width: OutputWidth);

        var dynamicResting = dynamicRenderer.RenderFrame(frame: BuildFrame(program: dynamicProgram, transforms: [new DynamicTransform(Position: RestingPosition, Orientation: Quaternion.Identity)]));
        var dynamicMoved = dynamicRenderer.RenderFrame(frame: BuildFrame(program: dynamicProgram, transforms: [new DynamicTransform(Position: MovedPosition, Orientation: Quaternion.Identity)]));

        using var bakedRestingRenderer = new SdfWorldEngine(device: device, gpu: gpu, height: OutputHeight, kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"), options: new SdfWorldEngineOptions(DynamicTransformCapacity: 1, Program: bakedRestingProgram), width: OutputWidth);
        using var bakedMovedRenderer = new SdfWorldEngine(device: device, gpu: gpu, height: OutputHeight, kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"), options: new SdfWorldEngineOptions(DynamicTransformCapacity: 1, Program: bakedMovedProgram), width: OutputWidth);

        var bakedResting = bakedRestingRenderer.RenderFrame(frame: BuildFrame(program: bakedRestingProgram, transforms: []));
        var bakedMoved = bakedMovedRenderer.RenderFrame(frame: BuildFrame(program: bakedMovedProgram, transforms: []));

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var artifactPath = Path.Combine(context.ArtifactsDirectory, "dynamic-transform-moved-dynamic.png");

        PngEncoder.Write(height: (int)OutputHeight, path: Path.Combine(context.ArtifactsDirectory, "dynamic-transform-resting-dynamic.png"), rgba: dynamicResting, width: (int)OutputWidth);
        PngEncoder.Write(height: (int)OutputHeight, path: Path.Combine(context.ArtifactsDirectory, "dynamic-transform-resting-baked.png"), rgba: bakedResting, width: (int)OutputWidth);
        PngEncoder.Write(height: (int)OutputHeight, path: artifactPath, rgba: dynamicMoved, width: (int)OutputWidth);
        PngEncoder.Write(height: (int)OutputHeight, path: Path.Combine(context.ArtifactsDirectory, "dynamic-transform-moved-baked.png"), rgba: bakedMoved, width: (int)OutputWidth);

        var totalPixels = (int)(OutputWidth * OutputHeight);

        // (a) The entity visibly moved: T0 vs T1 must differ meaningfully (the transform buffer actually drives pixels).
        var movedFraction = ((double)CountChangedPixels(a: dynamicResting, b: dynamicMoved, totalPixels: totalPixels) / totalPixels);

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

        // (c) The ORIENTATION half of the channel — the quaternion path the identity-only cases above never exercise.
        // Rotate the mover 90° about Z via the dynamic transform and assert it (i) matches the baked Translate+Rotate
        // equivalent within the Continuous thresholds, and (ii) visibly differs from the identity-orientation frame at
        // the same position — so a shader that silently dropped the quaternion would be caught here.
        var rotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (MathF.PI / 2f));
        var dynamicRotated = dynamicRenderer.RenderFrame(frame: BuildFrame(program: dynamicProgram, transforms: [new DynamicTransform(Position: RestingPosition, Orientation: rotation)]));
        var bakedRotatedProgram = BuildScene(bakedPosition: RestingPosition, bakedOrientation: rotation);

        using var bakedRotatedRenderer = new SdfWorldEngine(device: device, gpu: gpu, height: OutputHeight, kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"), options: new SdfWorldEngineOptions(DynamicTransformCapacity: 1, Program: bakedRotatedProgram), width: OutputWidth);

        var bakedRotated = bakedRotatedRenderer.RenderFrame(frame: BuildFrame(program: bakedRotatedProgram, transforms: []));

        PngEncoder.Write(height: (int)OutputHeight, path: Path.Combine(context.ArtifactsDirectory, "dynamic-transform-rotated-dynamic.png"), rgba: dynamicRotated, width: (int)OutputWidth);
        PngEncoder.Write(height: (int)OutputHeight, path: Path.Combine(context.ArtifactsDirectory, "dynamic-transform-rotated-baked.png"), rgba: bakedRotated, width: (int)OutputWidth);

        var rotatedFailures = ParityThresholds.Continuous.Evaluate(metrics: ParityMetrics.Compute(reference: bakedRotated, comparand: dynamicRotated, width: (int)OutputWidth, height: (int)OutputHeight));

        if (rotatedFailures.Count > 0) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"rotated frame: dynamic vs baked (Translate+Rotate) diverged — {string.Join(separator: "; ", values: rotatedFailures)}");
        }

        var rotationFraction = ((double)CountChangedPixels(a: dynamicResting, b: dynamicRotated, totalPixels: totalPixels) / totalPixels);

        if (rotationFraction < MinMovedFraction) {
            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the 90° dynamic rotation changed only {(rotationFraction * 100.0):0.###}% of pixels versus identity orientation — the transform's quaternion is not being applied");
        }

        return PostStageOutcome.Pass(
            artifactPath: artifactPath,
            detail: $"program uploaded once; entity moved via the transform buffer ({(movedFraction * 100.0):0.#}% changed) and rotated 90° ({(rotationFraction * 100.0):0.#}% changed), each matching its baked Translate / Translate+Rotate equivalent within the Continuous thresholds"
        );
    }

    // Pixels whose max RGB channel delta between two same-size frames reaches MovedChannelDelta (a clearly-not-noise
    // change). Shared by the translate and rotate "visibly changed" gates.
    private static int CountChangedPixels(byte[] a, byte[] b, int totalPixels) {
        var changed = 0;

        for (var pixel = 0; (pixel < totalPixels); pixel++) {
            var offset = (pixel * 4);
            var delta = Math.Max(
                Math.Abs(value: (a[offset] - b[offset])),
                Math.Max(
                    Math.Abs(value: (a[offset + 1] - b[offset + 1])),
                    Math.Abs(value: (a[offset + 2] - b[offset + 2]))
                )
            );

            if (delta >= MovedChannelDelta) {
                changed++;
            }
        }

        return changed;
    }

    // The scene: a ground plane, a static landmark box (which must NOT move between frames — the parity compare
    // against the baked programs covers it), and the mover BEAM (asymmetric, so a rotation is visible). With
    // bakedPosition null the beam rides dynamic slot 0; otherwise the same rigid transform is baked in — a static
    // Translate, plus a Rotate when bakedOrientation is set. The shader applies inverseRotate(Q, p - position), i.e.
    // exactly Translate THEN Rotate, so the baked op order matches. This is the oracle program.
    private static SdfProgram BuildScene(Vector3? bakedPosition, Quaternion? bakedOrientation) {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.5f, 0.55f, 0.6f)));
        var mover = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.85f, 0.25f, 0.2f)));
        var landmark = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.2f, 0.5f, 0.85f)));

        _ = builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            .Translate(offset: new Vector3(-2f, 0.6f, -1f))
            .Box(halfExtents: new Vector3(0.6f, 0.6f, 0.6f), round: 0.05f, material: landmark)
            .ResetPoint();

        if (bakedPosition is null) {
            _ = builder.TransformDynamic(slot: 0);
        } else {
            _ = builder.Translate(offset: bakedPosition.Value);

            if (bakedOrientation is Quaternion orientation) {
                _ = builder.Rotate(rotation: orientation);
            }
        }

        return builder
            .Box(halfExtents: MoverHalfExtents, round: 0.05f, material: mover)
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
