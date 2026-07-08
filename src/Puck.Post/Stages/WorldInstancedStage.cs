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
    /// SMOOTH-blend member is now maskable bit-exactly too: <c>blendSmoothUnion</c> is written far-exact
    /// (<c>lerp(a, b, 1 - h)</c>), so once the seam saturates past the blend radius k it returns the accumulator TO
    /// THE BIT rather than <c>candidate + (current - candidate)</c> — and <see cref="SdfProgram"/> inflates a smooth
    /// instance's cull bound by k so every tile that masks it out sits in that saturated region. Hence the crimson
    /// SmoothUnion sphere carries a FINITE bound (5, the union influence margin; the packer adds the k = 0.4 halo) and
    /// still renders bit-identical to flat — what used to force an unmaskable bound.</para></summary>
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

        // The smooth-blended sphere: now CULLABLE with a finite bound. blendSmoothUnion is far-exact (returns the
        // accumulator TO THE BIT once the seam saturates past the blend radius k), and PackInstances auto-inflates a
        // smooth instance's bound by k (0.4 here), so a masked-out tile skips it EXACTLY — the same bit-identical
        // instanced == flat contract a plain-union member gets. Bound 5 = the union influence margin (as the box/torus);
        // the packer adds the k halo on top.
        AddObject(builder: builder, instanced: instanced, boundCenter: new Vector3(-1.9f, 0.9f, -0.6f), boundRadius: 5f, emit: b => _ = b
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

    /// <summary>The INTERSECTION-GUARD scene: a ground plane and a box in the WORLD set, plus one sphere carrying an
    /// <see cref="SdfBlendOp.Intersection"/> blend, declared (when <paramref name="instanced"/>) inside an instance whose
    /// bound hugs the sphere.
    /// <para>An intersection has GLOBAL reach — <c>mapCore</c> never resets the accumulator, only the point, so
    /// <c>max(accumulator, candidate)</c> intersects against every shape emitted before it. No bounding sphere can cull
    /// that: masking the instance out of a tile leaves the accumulator (ground plane ∪ box), while evaluating it leaves
    /// their intersection with the sphere. Rendered, the bug is unmistakable — a ground plane that exists only in the
    /// tiles where the instance happened to be culled.</para>
    /// <para>NO FINITE BOUND IS CORRECT for an intersection member. A Union member influences the running minimum only
    /// where it is nearest, so a generous bound covers it (hence the 5s above, for 0.9-radius spheres); an intersection
    /// influences the running MAXIMUM wherever <c>candidate &gt; accumulator</c> — which is everywhere outside its own
    /// shape. <see cref="SdfProgram"/> therefore OVERRIDES the authored bound with its unmaskable sentinel.</para>
    /// <para>This scene proves that override by authoring a bound (0.15) deliberately SMALLER than the sphere it
    /// declares (0.6), so the mask would clear across most of the frame. Note a merely-tight-but-covering bound would
    /// NOT expose the bug: the beam prepass cone-marches the UNMASKED field, and an intersection annihilates everything
    /// outside its own shape, so every tile the cone march leaves non-empty is a tile whose cone passes through the
    /// shape — and therefore through any bound that contains it. The bug hides behind the cull that precedes it. With
    /// the sentinel the render is bit-identical to flat on Vulkan; without it, ~14k bytes differ (the ground plane
    /// reappears in exactly the tiles that culled the instance).</para>
    /// <para>The FIELD ops (<see cref="SdfOp.Onion"/>, <see cref="SdfOp.Dilate"/>, <see cref="SdfOp.Displace"/>) are the
    /// same hazard and are MORE visible: they mutate the running accumulator outright, so an <c>Onion</c> inside an
    /// instance shells the ground plane. Masking the instance out of a tile un-shells it there — and unlike the
    /// intersection, the beam prepass does NOT hide this, because the shelled ground is non-empty everywhere, so Stage 1
    /// runs in exactly the tiles whose masked field has no onion. Creator mode emits <c>Onion</c> inside
    /// <c>BeginInstanceDynamic</c> per placed shape, so this is live. The onion variant of this scene therefore uses an
    /// HONEST bound: it needs no under-covering trick to expose the bug.</para></summary>
    /// <param name="instanced">Whether to wrap the offending shape in an instance declaration.</param>
    /// <param name="fieldOp">Use an <see cref="SdfOp.Onion"/> field op instead of an intersection blend.</param>
    /// <returns>The scene program.</returns>
    internal static SdfProgram BuildUnmaskableGuardScene(bool instanced, bool fieldOp) {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.42f, 0.46f, 0.52f)));
        var amber = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.9f, 0.6f, 0.2f), Emissive: 0.25f));
        var sphereCenter = new Vector3(0.35f, 0.5f, 0f);

        _ = builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            .Translate(offset: new Vector3(0f, 0.5f, 0f))
            .Box(halfExtents: new Vector3(0.5f, 0.5f, 0.5f), round: 0f, material: amber);

        // The intersection variant authors a bound deliberately SMALLER than the sphere it declares, so the mask would
        // clear across most of the frame (a covering bound hides the intersection bug behind the cone march — see the
        // summary). The field-op variant needs no such trick: an HONEST bound still exposes it.
        if (instanced) {
            _ = builder.BeginInstance(boundCenter: sphereCenter, boundRadius: (fieldOp ? 0.7f : 0.15f));
        }

        _ = builder.ResetPoint().Translate(offset: sphereCenter);
        _ = fieldOp
            ? builder.Sphere(radius: 0.6f, material: amber).Onion(thickness: 0.05f)
            : builder.Sphere(radius: 0.6f, material: amber, blend: SdfBlendOp.Intersection);

        if (instanced) {
            _ = builder.EndInstance();
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

        // (c) The UNMASKABLE-COMPOSE guard. An op that READS the running accumulator has global reach, so the packer must
        // make its instance unmaskable. Two families qualify: the intersection blends (max(accumulator, candidate)) and
        // the FIELD ops (Onion/Dilate/Displace, which mutate the accumulator outright). Rendered on Vulkan — where the
        // mask gate is an exact culling decision — instanced must be BIT-IDENTICAL to flat in both cases.
        foreach (var fieldOp in new[] { false, true }) {
            var guardFlatProgram = BuildUnmaskableGuardScene(instanced: false, fieldOp: fieldOp);
            var guardInstancedProgram = BuildUnmaskableGuardScene(instanced: true, fieldOp: fieldOp);
            var guardFlatPixels = RenderOnce(program: guardFlatProgram, frame: WorldStage.BuildHeroFrame(program: guardFlatProgram, width: WorldWidth, height: WorldHeight), device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv");
            var guardInstancedPixels = RenderOnce(program: guardInstancedProgram, frame: WorldStage.BuildHeroFrame(program: guardInstancedProgram, width: WorldWidth, height: WorldHeight), device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv");
            var guardDiffering = 0;

            for (var index = 0; (index < guardFlatPixels.Length); index++) {
                if (guardFlatPixels[index] != guardInstancedPixels[index]) {
                    guardDiffering++;
                }
            }

            if (guardDiffering != 0) {
                var kind = (fieldOp ? "an Onion field op" : "an Intersection blend");
                var guardName = (fieldOp ? "onion" : "intersection");
                var guardPath = Path.Combine(context.ArtifactsDirectory, $"world-instanced-unmaskable-{guardName}-guard.png");

                PngEncoder.Write(height: (int)WorldHeight, path: guardPath, rgba: guardInstancedPixels, width: (int)WorldWidth);

                return PostStageOutcome.Fail(artifactPath: guardPath, detail: $"an instance carrying {kind} rendered {guardDiffering} bytes differently from the flat stream — it reads the running accumulator, so its influence is unbounded and its instance must be packed UNMASKABLE (SdfProgram.UnmaskableBoundRadius) rather than culled per tile");
            }
        }

        return PostStageOutcome.Pass(artifactPath: diffPath, detail: $"{WorldWidth}x{WorldHeight} {4 + (FieldColumns * FieldRows)}-instance scene (3 mask words) | instanced == flat bit-identical on Vulkan, within WorldLsbExact on Direct3D 12 | instanced cross-backend within WorldComposite thresholds | Intersection- and Onion-carrying instances are unmaskable and render flat-identical | {ParityCheck.Describe(metrics: metrics)}");
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
