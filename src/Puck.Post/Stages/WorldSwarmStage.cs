using Puck.Capture;
using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Instancing at SCALE — the standing evidence for the "hundreds of objects" claim: a swarm scene of
/// <b>300 instances</b> (a 16×16 field of varied primitives + 44 movers riding dynamic transform slots) over a
/// WORLD-set ground plane, so the DERIVED per-tile mask spans <b>10 words</b> (ceil(300/32)) and the beam prepass,
/// the multi-word bit enumeration, and the world/visible-instance two-pointer merge all run far past the single- and
/// three-word cases the <c>world-instanced</c> stage covers. Every instance blends plain <see cref="SdfBlendOp.Union"/>
/// against the world with a generous influence-margin bound (see <see cref="WorldInstancedStage.BuildInstancedScene"/>'s
/// bound-sizing contract — a masked-out plain-Union member is march-exact only while its bound clears the largest
/// field value along any evaluated corridor, which the ground plane keeps under the camera's height), so mask-culling
/// stays exact and the flat-vs-instanced comparison is a pure proof of the mask/merge machinery at scale.
/// <para>Asserts, per the <c>world-instanced</c> conventions: (a) instanced == flat PER BACKEND — BIT-IDENTICAL on
/// Vulkan, within <see cref="ParityThresholds.WorldLsbExact"/> on Direct3D 12 (the mask-merge path re-rolls DXC's
/// DXIL codegen, the documented benign ±1 signature); (b) DETERMINISM — the same program + the same transforms
/// rendered twice produce bit-identical pixels on BOTH backends; (c) the instanced render holds Vulkan/Direct3D 12
/// cross-backend parity within the calibrated <see cref="ParityThresholds.WorldComposite"/> thresholds. The Vulkan
/// renders run with the opt-in GPU timestamp bracketing (the PUCK_TIMING plumbing GpuBudgetStage proves), and the
/// pass detail reports the beam/views/composite GPU-ms of the flat and instanced swarm renders — informational
/// evidence, no budget (per GpuBudgetStage, a tight budget needs a per-machine calibration that does not exist yet).</para>
/// </summary>
internal sealed class WorldSwarmStage : IPostStage {
    private const int FieldColumns = 16;
    private const int FieldRows = 16;
    private const int MoverColumns = 11;
    private const int MoverCount = 44;
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-swarm";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // The mover transforms: one rigid transform per dynamic slot, a 4×11 formation hovering above the static field,
    // each rotated about Y by a slot-derived angle so the dynamic quaternion path is live at scale. Deterministic by
    // construction (pure functions of the slot index), built once and shared by every render on both backends.
    private static DynamicTransform[] BuildMoverTransforms() {
        var transforms = new DynamicTransform[MoverCount];

        for (var mover = 0; (mover < MoverCount); mover++) {
            transforms[mover] = new DynamicTransform(
                Position: new Vector3((-4f + (0.8f * (mover % MoverColumns))), 1.5f, (-3.4f - (1.2f * (mover / MoverColumns)))),
                Orientation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (0.35f * mover))
            );
        }

        return transforms;
    }

    // The ONE swarm scene both variants share — a 16×16 static field of varied primitives (spheres/boxes/capsules,
    // four materials) plus 44 dynamic movers, over a WORLD-set ground plane — emitted with or without the per-object
    // instance declarations (Begin/EndInstance add metadata, not instructions, so the instruction streams are
    // identical either way). Every bound is 4: the union influence margin over the largest shape extent (~0.45) with
    // the corridor field values under the camera's height (3.2), the same sizing the world-instanced stage measured.
    private static SdfProgram BuildScene(bool instanced) {
        var builder = new SdfProgramBuilder();
        var (ground, crimson, azure, amber, jade) = WorldStage.AddHeroPalette(builder: builder);
        Span<int> palette = [crimson, azure, amber, jade];

        // The ground plane stays in the WORLD set (unbounded — a plane can never own a finite instance bound).
        _ = builder.Plane(normal: Vector3.UnitY, offset: 0f, material: ground);

        // The static field: 16×16 varied primitives, one plain-Union instance each (instances 0..255 — every bit of
        // mask words 0..7 is a real object).
        for (var row = 0; (row < FieldRows); row++) {
            for (var column = 0; (column < FieldColumns); column++) {
                var index = ((row * FieldColumns) + column);
                var material = palette[(index % palette.Length)];
                var position = new Vector3((-6f + (0.8f * column)), 0f, (-2.5f - (0.8f * row)));

                if (instanced) {
                    _ = builder.BeginInstance(boundCenter: position, boundRadius: 4f);
                }

                _ = builder.ResetPoint();

                switch (index % 3) {
                    case 0:
                        _ = builder
                            .Translate(offset: (position + new Vector3(0f, 0.18f, 0f)))
                            .Sphere(radius: 0.18f, material: material);
                        break;
                    case 1:
                        _ = builder
                            .Translate(offset: (position + new Vector3(0f, 0.16f, 0f)))
                            .Box(halfExtents: new Vector3(0.16f, 0.16f, 0.16f), round: 0.03f, material: material);
                        break;
                    default:
                        _ = builder
                            .Translate(offset: (position + new Vector3(0f, 0.12f, 0f)))
                            .Capsule(endpoint: new Vector3(0f, 0.32f, 0f), radius: 0.12f, material: material);
                        break;
                }

                if (instanced) {
                    _ = builder.EndInstance();
                }
            }
        }

        // The movers: 44 dynamic instances (256..299 — mask words 8 and 9), each riding its OWN dynamic transform
        // slot: the instance bound tracks the slot's per-frame position on the GPU, and the instance's instructions
        // apply the same slot's full rigid transform.
        for (var mover = 0; (mover < MoverCount); mover++) {
            var material = palette[((mover + 1) % palette.Length)];

            if (instanced) {
                _ = builder.BeginInstanceDynamic(slot: mover, boundOffset: Vector3.Zero, boundRadius: 4f);
            }

            _ = builder
                .ResetPoint()
                .TransformDynamic(slot: mover);

            switch (mover % 3) {
                case 0:
                    _ = builder.Sphere(radius: 0.24f, material: material);
                    break;
                case 1:
                    _ = builder.Box(halfExtents: new Vector3(0.2f, 0.2f, 0.2f), round: 0.04f, material: material);
                    break;
                default:
                    _ = builder.Capsule(endpoint: new Vector3(0.3f, 0f, 0f), radius: 0.14f, material: material);
                    break;
            }

            if (instanced) {
                _ = builder.EndInstance();
            }
        }

        return builder.Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        var flatProgram = BuildScene(instanced: false);
        var instancedProgram = BuildScene(instanced: true);
        var transforms = BuildMoverTransforms();
        // The hero camera (WorldStage.BuildHeroFrame — the corridor field values the bound margins were sized
        // against) plus the per-slot mover transforms.
        var flatFrame = WorldStage.BuildHeroFrame(program: flatProgram, width: WorldWidth, height: WorldHeight) with { DynamicTransforms = transforms };
        var instancedFrame = WorldStage.BuildHeroFrame(program: instancedProgram, width: WorldWidth, height: WorldHeight) with { DynamicTransforms = transforms };

        // Vulkan: both variants on the host device + its neutral compute services (SPIR-V kernels), with the opt-in
        // timestamp bracketing live — each variant renders TWICE on its one engine (the determinism pair; the second
        // frame is also the steady-state cost the timing evidence reads, per GpuBudgetStage).
        var vulkanGpu = context.Resolve<IGpuComputeServices>();
        var vulkanDevice = context.RequireGpuDevice();
        var timingFactory = context.Resolve<IGpuTimingPoolFactory>();
        var timingRecorder = context.Resolve<IGpuTimingRecorder>();
        byte[] vulkanFlatPixels;
        byte[] vulkanFlatPixelsRepeat;
        byte[] vulkanInstancedPixels;
        byte[] vulkanInstancedPixelsRepeat;
        var flatTimingDigest = "n/a";
        var instancedTimingDigest = "n/a";

        using (var flatRenderer = CreateEngine(program: flatProgram, device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv", timingFactory: timingFactory, timingRecorder: timingRecorder)) {
            vulkanFlatPixels = flatRenderer.RenderFrame(frame: flatFrame);
            vulkanFlatPixelsRepeat = flatRenderer.RenderFrame(frame: flatFrame);

            if (flatRenderer.TryReadPassTimings(beam: out var beam, views: out var views, composite: out var composite, frame: out var frameMs)) {
                flatTimingDigest = $"beam {beam:0.###} + views {views:0.###} + composite {composite:0.###} = {frameMs:0.###} ms";
            }
        }

        using (var instancedRenderer = CreateEngine(program: instancedProgram, device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv", timingFactory: timingFactory, timingRecorder: timingRecorder)) {
            vulkanInstancedPixels = instancedRenderer.RenderFrame(frame: instancedFrame);
            vulkanInstancedPixelsRepeat = instancedRenderer.RenderFrame(frame: instancedFrame);

            if (instancedRenderer.TryReadPassTimings(beam: out var beam, views: out var views, composite: out var composite, frame: out var frameMs)) {
                instancedTimingDigest = $"beam {beam:0.###} + views {views:0.###} + composite {composite:0.###} = {frameMs:0.###} ms";
            }
        }

        // Direct3D 12: both variants on the shared Tier-C device (DXIL kernels), the instanced one twice (the
        // determinism pair on the second backend).
        var directX = context.RequireDirectXDevice();
        var directXGpu = directX.Services.GetRequiredService<IGpuComputeServices>();
        byte[] directXFlatPixels = [];
        byte[] directXInstancedPixels = [];
        byte[] directXInstancedPixelsRepeat = [];

        _ = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => {
            using (var flatRenderer = CreateEngine(program: flatProgram, device: directX.DeviceContext, gpu: directXGpu, bytecodeExtension: ".dxil", timingFactory: null, timingRecorder: null)) {
                directXFlatPixels = flatRenderer.RenderFrame(frame: flatFrame);
            }

            using (var instancedRenderer = CreateEngine(program: instancedProgram, device: directX.DeviceContext, gpu: directXGpu, bytecodeExtension: ".dxil", timingFactory: null, timingRecorder: null)) {
                directXInstancedPixels = instancedRenderer.RenderFrame(frame: instancedFrame);
                directXInstancedPixelsRepeat = instancedRenderer.RenderFrame(frame: instancedFrame);
            }

            return directXInstancedPixels;
        });

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var diffPath = Path.Combine(context.ArtifactsDirectory, "world-swarm-diff.png");

        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-swarm-vulkan.png"), rgba: vulkanInstancedPixels, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-swarm-directx.png"), rgba: directXInstancedPixels, width: (int)WorldWidth);
        ParityCheck.WriteDiffImage(comparand: directXInstancedPixels, height: (int)WorldHeight, path: diffPath, reference: vulkanInstancedPixels, width: (int)WorldWidth);

        // (a) Determinism, PER BACKEND: the same program + the same transforms rendered twice on the same engine are
        // bit-identical — a fixed pipeline over fixed inputs has no legitimate variance.
        if (!vulkanFlatPixels.AsSpan().SequenceEqual(other: vulkanFlatPixelsRepeat) || !vulkanInstancedPixels.AsSpan().SequenceEqual(other: vulkanInstancedPixelsRepeat)) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: "two identical Vulkan renders diverged (flat and/or instanced) — the swarm render is nondeterministic");
        }

        if (!directXInstancedPixels.AsSpan().SequenceEqual(other: directXInstancedPixelsRepeat)) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: "two identical Direct3D 12 instanced renders diverged — the swarm render is nondeterministic");
        }

        // (b) Instanced == flat, PER BACKEND (the shared ParityCheck contract — see the world-instanced stage's
        // measurement for the benign Direct3D 12 codegen-roll signature WorldLsbExact absorbs).
        var flatContractFailure = ParityCheck.EvaluateFlatInstancedContract(
            artifactsDirectory: context.ArtifactsDirectory,
            diffPath: diffPath,
            directXFlatPixels: directXFlatPixels,
            directXInstancedPixels: directXInstancedPixels,
            height: (int)WorldHeight,
            stageName: "world-swarm",
            vulkanFlatPixels: vulkanFlatPixels,
            vulkanInstancedPixels: vulkanInstancedPixels,
            width: (int)WorldWidth
        );

        if (flatContractFailure is { } swarmFlatFailure) {
            return swarmFlatFailure;
        }

        // (c) The instanced swarm still holds cross-backend parity within the usual WorldComposite thresholds.
        var metrics = ParityMetrics.Compute(reference: vulkanInstancedPixels, comparand: directXInstancedPixels, width: (int)WorldWidth, height: (int)WorldHeight);
        var failures = ParityThresholds.WorldComposite.Evaluate(metrics: metrics);

        if (failures.Count != 0) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"{ParityCheck.Describe(metrics: metrics)} — {string.Join(separator: "; ", values: failures)}");
        }

        return PostStageOutcome.Pass(artifactPath: diffPath, detail: $"{WorldWidth}x{WorldHeight} {(FieldColumns * FieldRows) + MoverCount}-instance swarm ({instancedProgram.InstanceMaskWordCount} mask words, {MoverCount} dynamic slots) | deterministic on both backends | instanced == flat bit-identical on Vulkan, within WorldLsbExact on Direct3D 12 | cross-backend within WorldComposite thresholds | {ParityCheck.Describe(metrics: metrics)} | Vulkan GPU-ms flat: {flatTimingDigest}; instanced: {instancedTimingDigest}");
    }

    private static SdfWorldEngine CreateEngine(SdfProgram program, IGpuDeviceContext device, IGpuComputeServices gpu, string bytecodeExtension, IGpuTimingPoolFactory? timingFactory, IGpuTimingRecorder? timingRecorder) {
        return new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: WorldHeight,
            kernels: SdfWorldKernels.Load(bytecodeExtension: bytecodeExtension),
            options: new SdfWorldEngineOptions(DynamicTransformCapacity: MoverCount, Program: program, TimingFactory: timingFactory, TimingRecorder: timingRecorder),
            width: WorldWidth
        );
    }
}
