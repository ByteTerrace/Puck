using Puck.Capture;
using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. Per-object instancing/culling (the tile-mask beam prepass):
/// builds the SAME scene twice — FLAT (every shape in the WORLD set, zero instances — the linear fast path) and
/// INSTANCED (every non-ground object wrapped in <see cref="SdfProgramBuilder.BeginInstance"/>/
/// <see cref="SdfProgramBuilder.EndInstance"/>, one instance per object; the instruction streams are IDENTICAL, the
/// declarations are metadata) — renders both on BOTH backends, and asserts (a) instanced == flat, PER BACKEND:
/// BIT-IDENTICAL on Vulkan (the mask gate is an exact culling decision over a fixed SPIR-V compile — any divergence
/// is a real bug), and within <see cref="ParityThresholds.WorldLsbExact"/> on Direct3D 12 (measured: the mask-merge
/// path alone re-rolls DXC's DXIL codegen scheduling in the untouched sky background, the SAME "every delta exactly
/// ±1, widely isolated" signature <c>WorldLsbExact</c>'s doc already names for a codegen roll — not a masking bug,
/// since it disappears when the instance bound is generous enough that no ACTUAL surface differs, as the
/// bit-identical Vulkan side proves), and (b) the instanced render still holds Vulkan/Direct3D 12 cross-backend
/// parity within the calibrated <see cref="ParityThresholds.WorldComposite"/> thresholds (the instancing machinery
/// must not itself introduce a new backend divergence).
/// <para>The scene declares <b>4 hero objects + a 12×6 field of small spheres = 76 instances</b>, so the DERIVED
/// per-tile mask spans 3 words (ceil(76/32)) — the multi-word mask path (bit enumeration across word boundaries, the
/// world/visible-instance two-pointer merge, ascending-order preservation for the SmoothUnion/Subtraction blends)
/// is exercised end to end, not just the single-word case.</para>
/// </summary>
internal sealed class WorldInstancedStage : IPostStage {
    private const int FieldColumns = 12;
    private const int FieldRows = 6;
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-instanced";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    /// <summary>Builds the FLAT variant: the shared scene with every shape in the WORLD set — zero instances, the
    /// linear fast path every other world stage in the battery takes.</summary>
    internal static SdfProgram BuildFlatScene() {
        return BuildScene(instanced: false);
    }

    /// <summary>Builds the INSTANCED variant: the IDENTICAL instruction stream as <see cref="BuildFlatScene"/>, but
    /// each of the 76 non-ground objects is wrapped in its own <see cref="SdfProgramBuilder.BeginInstance"/> —
    /// exercising every one of the beam prepass's per-tile mask outcomes across all 3 derived mask words.
    /// <para>BOUND SIZING — the bit-identical contract, measured on this scene: masking an instance out of a tile is
    /// march-exact only while the masked-out shape can never INFLUENCE the field at any evaluated march point —
    /// otherwise the masked march takes bit-different steps toward the very same surface and the traveled distance
    /// (so the shading) lands ±1 LSB off. Per blend op, "influence" means: a plain UNION member influences wherever
    /// it is the running minimum, so its bound needs margin above the largest field value along any evaluated
    /// corridor — the ground plane keeps that under the camera's height (3.2) here, hence the field spheres' bound 4
    /// and the box/torus 5 (a 0.3 field bound measured maxΔ119 silhouette flips). A SUBTRACTION member only
    /// influences points INSIDE itself, so the carve keeps its tight 0.6 bound — the instance whose mask bit
    /// genuinely varies across evaluated content tiles in word 0 (the far-field rows vary words 1 and 2). A
    /// SMOOTH-blend member is NEVER maskable bit-exactly at ANY finite margin: even where its clamp saturates,
    /// <c>blendSmoothUnion</c> computes <c>lerp(candidate, current, 1) = candidate + (current - candidate)</c>,
    /// which rounds differently than leaving <c>current</c> untouched — measured as ±1 ground dither exactly over
    /// the tiles that masked the crimson sphere out — so the crimson SmoothUnion instance declares an UNMASKABLE
    /// bound (100, past every possible tile cone). That is also honest authoring guidance: smooth-blending an
    /// instance against WORLD-set geometry defeats its own per-tile cull.</para></summary>
    internal static SdfProgram BuildInstancedScene() {
        return BuildScene(instanced: true);
    }

    // The ONE scene both variants share — WorldStage's hero objects plus a 12×6 field of small spheres — emitted
    // with or without the per-object instance declarations. The instruction streams are identical either way
    // (Begin/EndInstance add metadata, not instructions), which is what makes the flat-vs-instanced comparison a
    // pure proof of the mask/merge machinery.
    private static SdfProgram BuildScene(bool instanced) {
        var builder = new SdfProgramBuilder();
        var (ground, crimson, azure, amber, jade) = WorldStage.AddHeroPalette(builder: builder);
        Span<int> fieldMaterials = [crimson, azure, amber, jade];

        // One object: its instructions bracketed SYMMETRICALLY by its own instance declaration when building the
        // instanced variant — the builder-call order (so the emitted instruction stream) is identical either way.
        static void AddObject(SdfProgramBuilder builder, bool instanced, Vector3 boundCenter, float boundRadius, Action<SdfProgramBuilder> emit) {
            if (instanced) {
                _ = builder.BeginInstance(boundCenter: boundCenter, boundRadius: boundRadius);
            }

            emit(builder);

            if (instanced) {
                _ = builder.EndInstance();
            }
        }

        // The ground plane stays in the WORLD set (unbounded — a plane can never own a finite instance bound).
        _ = builder.Plane(normal: Vector3.UnitY, offset: 0f, material: ground);

        // The smooth-blended sphere: UNMASKABLE (see BuildInstancedScene's bound-sizing contract — a smooth blend is
        // never bitwise transparent when masked out, so an instance smooth-blending against the WORLD set must stay
        // in every tile's mask).
        AddObject(builder: builder, instanced: instanced, boundCenter: new Vector3(-1.9f, 0.9f, -0.6f), boundRadius: 100f, emit: b => _ = b
            .ResetPoint()
            .Translate(offset: new Vector3(-1.9f, 0.9f, -0.6f))
            .Sphere(radius: 0.9f, material: crimson, blend: SdfBlendOp.SmoothUnion, smooth: 0.4f));

        // The rounded box: containment sphere + the union influence margin.
        AddObject(builder: builder, instanced: instanced, boundCenter: new Vector3(1.6f, 0.7f, 0.4f), boundRadius: 5f, emit: b => _ = b
            .ResetPoint()
            .Translate(offset: new Vector3(1.6f, 0.7f, 0.4f))
            .Box(halfExtents: new Vector3(0.7f, 0.7f, 0.7f), round: 0.08f, material: azure));

        // The subtraction sphere carved through the box's top — its own instance (a SEPARATE object in this port;
        // Subtraction never qualified for the intra-instance bound skip anyway). Deliberately TIGHT: a subtraction
        // only influences points inside itself, so this is the instance whose mask bit genuinely varies across the
        // evaluated content tiles.
        AddObject(builder: builder, instanced: instanced, boundCenter: new Vector3(1.6f, 1.5f, 0.4f), boundRadius: 0.6f, emit: b => _ = b
            .ResetPoint()
            .Translate(offset: new Vector3(1.6f, 1.5f, 0.4f))
            .Sphere(radius: 0.55f, material: amber, blend: SdfBlendOp.Subtraction));

        // The torus: containment sphere + the union influence margin.
        AddObject(builder: builder, instanced: instanced, boundCenter: new Vector3(-0.2f, 0.35f, 1.7f), boundRadius: 5f, emit: b => _ = b
            .ResetPoint()
            .Translate(offset: new Vector3(-0.2f, 0.35f, 1.7f))
            .Torus(majorRadius: 0.8f, minorRadius: 0.22f, material: jade));

        // The sphere field: 12×6 small spheres behind the hero objects, one instance each — instances 4..75, so the
        // mask enumeration crosses both word boundaries (bits 32 and 64) with a mix of masked-in and masked-out
        // members per tile (tiles hitting the near ground continue underground past the field and mask most of it
        // out; far-lateral horizon tiles clear the opposite rows). Bound 4 = the union influence margin over the
        // 0.18 shape (see BuildInstancedScene's bound-sizing contract — the tighter 0.3 variant measured maxΔ119
        // silhouette flips on Vulkan from bit-different march steps).
        for (var row = 0; (row < FieldRows); row++) {
            for (var column = 0; (column < FieldColumns); column++) {
                var position = new Vector3((-4.4f + (0.8f * column)), 0.18f, (-2.5f - (0.8f * row)));
                var material = fieldMaterials[(((row * FieldColumns) + column) % fieldMaterials.Length)];

                AddObject(builder: builder, instanced: instanced, boundCenter: position, boundRadius: 4f, emit: b => _ = b
                    .ResetPoint()
                    .Translate(offset: position)
                    .Sphere(radius: 0.18f, material: material));
            }
        }

        return builder.Build();
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        var flatProgram = BuildFlatScene();
        var instancedProgram = BuildInstancedScene();
        var flatFrame = WorldStage.BuildHeroFrame(program: flatProgram, width: WorldWidth, height: WorldHeight);
        var instancedFrame = WorldStage.BuildHeroFrame(program: instancedProgram, width: WorldWidth, height: WorldHeight);

        // Vulkan: both variants on the host device + its neutral compute services (SPIR-V kernels).
        var vulkanGpu = context.Resolve<IGpuComputeServices>();
        var vulkanDevice = context.RequireGpuDevice();
        var vulkanFlatPixels = RenderOnce(program: flatProgram, frame: flatFrame, device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv");
        var vulkanInstancedPixels = RenderOnce(program: instancedProgram, frame: instancedFrame, device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv");

        // Direct3D 12: both variants on the shared Tier-C device (DXIL kernels).
        var directX = context.RequireDirectXDevice();
        var directXGpu = directX.Services.GetRequiredService<IGpuComputeServices>();
        var directXFlatPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => RenderOnce(program: flatProgram, frame: flatFrame, device: directX.DeviceContext, gpu: directXGpu, bytecodeExtension: ".dxil"));
        var directXInstancedPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => RenderOnce(program: instancedProgram, frame: instancedFrame, device: directX.DeviceContext, gpu: directXGpu, bytecodeExtension: ".dxil"));

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var diffPath = Path.Combine(context.ArtifactsDirectory, "world-instanced-diff.png");

        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-instanced-vulkan.png"), rgba: vulkanInstancedPixels, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(context.ArtifactsDirectory, "world-instanced-directx.png"), rgba: directXInstancedPixels, width: (int)WorldWidth);
        ParityCheck.WriteDiffImage(comparand: directXInstancedPixels, height: (int)WorldHeight, path: diffPath, reference: vulkanInstancedPixels, width: (int)WorldWidth);

        // (a) Instanced == flat, PER BACKEND (the shared ParityCheck contract). The measured benign Direct3D 12
        // residual here is DXC's DXIL codegen redistributing pre-existing ±1-LSB rounding in the (shapeless) sky
        // background once the extra per-segment mask branch is compiled in, the same signature WorldLsbExact's doc
        // already names for a codegen roll.
        var flatContractFailure = ParityCheck.EvaluateFlatInstancedContract(
            artifactsDirectory: context.ArtifactsDirectory,
            diffPath: diffPath,
            directXFlatPixels: directXFlatPixels,
            directXInstancedPixels: directXInstancedPixels,
            height: (int)WorldHeight,
            stageName: "world-instanced",
            vulkanFlatPixels: vulkanFlatPixels,
            vulkanInstancedPixels: vulkanInstancedPixels,
            width: (int)WorldWidth
        );

        if (flatContractFailure is { } instancedFlatFailure) {
            return instancedFlatFailure;
        }

        // (b) The instanced render still holds cross-backend parity within the usual WorldComposite thresholds.
        var metrics = ParityMetrics.Compute(reference: vulkanInstancedPixels, comparand: directXInstancedPixels, width: (int)WorldWidth, height: (int)WorldHeight);
        var failures = ParityThresholds.WorldComposite.Evaluate(metrics: metrics);

        if (failures.Count != 0) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"{ParityCheck.Describe(metrics: metrics)} — {string.Join(separator: "; ", values: failures)}");
        }

        return PostStageOutcome.Pass(artifactPath: diffPath, detail: $"{WorldWidth}x{WorldHeight} {4 + (FieldColumns * FieldRows)}-instance scene (3 mask words) | instanced == flat bit-identical on Vulkan, within WorldLsbExact on Direct3D 12 | instanced cross-backend within WorldComposite thresholds | {ParityCheck.Describe(metrics: metrics)}");
    }

    private static byte[] RenderOnce(SdfProgram program, SdfFrame frame, IGpuDeviceContext device, IGpuComputeServices gpu, string bytecodeExtension) {
        using var renderer = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: WorldHeight,
            kernels: SdfWorldKernels.Load(bytecodeExtension: bytecodeExtension),
            options: new SdfWorldEngineOptions(Program: program),
            width: WorldWidth
        );

        return renderer.RenderFrame(frame: frame);
    }
}
