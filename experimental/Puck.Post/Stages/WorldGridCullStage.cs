using Puck.Capture;
using System.Numerics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage. The world-space UNIFORM-GRID instance cull (<see cref="SdfInstanceGrid"/>): the beam prepass walks a
/// frozen grid of the tile's cone footprint instead of testing every instance in every tile
/// This stage proves the grid mask equals the flat mask by
/// rendering the SAME program two ways — grid ON (<see cref="SdfProgramBuilder.Build(bool)"/> default) and grid SUPPRESSED
/// (<c>Build(buildInstanceGrid: false)</c>, the pre-grid flat per-instance loop over the identical instances) — and
/// asserting they are pixel-identical.
/// <para>The scene is a destructible slab: a floor + a subject slab in the WORLD set, <b>196 scattered
/// SUBTRACTION carves</b> (static, maskable — the grid BINS them; each cuts a divot in the slab, so a mask MISS shows
/// as a solid bump and a spurious mask hit as an extra hole — a superset violation cannot hide), plus the two
/// instance classes the frozen grid cannot bin: one DYNAMIC floating sphere (its centre resolves per frame into the
/// live grid), one UNMASKABLE intersection sphere (its 1e30 bound rides the always-tested list), and one PARKED
/// dynamic sphere (its negative bound excludes it entirely). So the mask spans <b>7 words</b> (ceil(199/32)), and the
/// grid cell walk, always-list, and parked exclusion are exercised end to end.</para>
/// <para>Asserts, per the <c>world-instanced</c> conventions: (a) grid == flat PER BACKEND — BIT-IDENTICAL on Vulkan
/// (the mask is an exact culling decision over a fixed SPIR-V compile; ANY divergence is a hole-in-the-world superset
/// bug, not a tolerance), within <see cref="ParityThresholds.WorldLsbExact"/> on Direct3D 12 (the benign DXIL codegen
/// redistribution the flat/instanced contract already documents); (b) the grid render holds Vulkan/Direct3D 12
/// cross-backend parity within the calibrated <see cref="ParityThresholds.WorldComposite"/> thresholds (the grid walk
/// must not itself introduce a backend divergence).</para>
/// </summary>
internal sealed class WorldGridCullStage : IPostStage {
    private const int CarveColumns = 14;
    private const int CarveRows = 14;
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "world-grid-cull";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    // The ONE destructible-slab scene, built with the grid ON or SUPPRESSED. The instruction stream and instances are
    // identical either way (buildInstanceGrid only packs/omits the grid side table), so grid-vs-flat is a pure proof of
    // the cell walk + always-list against the flat per-instance loop.
    private static SdfProgram BuildScene(bool grid) {
        var builder = new SdfProgramBuilder();

        var (ground, crimson, azure, amber, jade) = WorldStage.AddHeroPalette(builder: builder);

        // The UNMASKABLE always-list member: an intersection sphere authored FIRST, against the empty accumulator (so it
        // is just the sphere — the accumulator rule), carrying an Intersection blend that makes the packer override its
        // bound with the 1e30 sentinel. The frozen grid cannot bin it, so it rides the always-tested list every tile,
        // exactly as the flat loop's 1e30 bound passes every tile.
        _ = builder.BeginInstance(boundCenter: new Vector3(x: -2.6f, y: 1.4f, z: -1.2f), boundRadius: 0.5f);
        _ = builder
            .ResetPoint()
            .Translate(offset: new Vector3(x: -2.6f, y: 1.4f, z: -1.2f))
            .Sphere(radius: 0.45f, material: jade, blend: SdfBlendOp.Intersection);
        _ = builder.EndInstance();

        // The WORLD set: the floor and a wide, shallow subject slab the carves bite into.
        _ = builder
            .Plane(normal: Vector3.UnitY, offset: 0f, material: ground)
            .ResetPoint()
            .Translate(offset: new Vector3(x: 0f, y: 0.5f, z: 0f))
            .Box(halfExtents: new Vector3(x: 3.2f, y: 0.5f, z: 3.2f), round: 0.05f, material: azure);

        // The scattered SUBTRACTION carves (static, maskable → the grid bins them): a 14×14 field of divots across the
        // slab's top, deterministically jittered so their cells vary. Each is its own instance with a tight bound (a
        // subtraction only influences points inside itself). A masked-out carve leaves a solid bump — visible.
        Span<int> palette = [crimson, azure, amber, jade];

        for (var row = 0; (row < CarveRows); row++) {
            for (var column = 0; (column < CarveColumns); column++) {
                var index = ((row * CarveColumns) + column);
                // Deterministic jitter (pure function of the index — no RNG): a small offset from the lattice point.
                var jitterX = (0.12f * MathF.Sin(x: (index * 12.9898f)));
                var jitterZ = (0.12f * MathF.Sin(x: (index * 78.233f)));
                var position = new Vector3(
                    x: ((-2.9f + ((5.8f / (CarveColumns - 1)) * column)) + jitterX),
                    y: 1.02f,
                    z: ((-2.9f + ((5.8f / (CarveRows - 1)) * row)) + jitterZ)
                );

                _ = builder.BeginInstance(boundCenter: position, boundRadius: 0.32f);
                _ = builder
                    .ResetPoint()
                    .Translate(offset: position)
                    .Sphere(radius: 0.22f, material: palette[(index % palette.Length)], blend: SdfBlendOp.Subtraction);
                _ = builder.EndInstance();
            }
        }

        // The DYNAMIC always-list member: a floating sphere on transform slot 0. Its centre resolves per frame, so the
        // frozen grid cannot bin it and it rides the always-tested list, exactly as the flat loop tests it every tile.
        _ = builder.BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 0.6f);
        _ = builder
            .ResetPoint()
            .TransformDynamic(slot: 0)
            .Sphere(radius: 0.5f, material: crimson);
        _ = builder.EndInstance();

        // The PARKED member: its authored transform would put a large amber sphere conspicuously above the slab if
        // either grid path leaked it. The negative packed radius must exclude it from both the grid and always-list.
        _ = builder.BeginInstanceDynamic(slot: 1, boundOffset: Vector3.Zero, boundRadius: 0.8f, active: false);
        _ = builder
            .ResetPoint()
            .TransformDynamic(slot: 1)
            .Sphere(radius: 0.7f, material: amber);
        _ = builder.EndInstance();

        return builder.Build(buildInstanceGrid: grid);
    }
    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        var flatProgram = BuildScene(grid: false); // grid SUPPRESSED — the flat per-instance reference
        var gridProgram = BuildScene(grid: true);  // grid ON — the cell walk + always-list
        // The one dynamic transform slot 0 uses (a floating sphere above the slab). Deterministic.
        DynamicTransform[] transforms = [
            new DynamicTransform(Position: new Vector3(x: 0.6f, y: 2.4f, z: 0.4f), Orientation: Quaternion.Identity),
            new DynamicTransform(Position: new Vector3(x: 0f, y: 2.2f, z: 0f), Orientation: Quaternion.Identity),
        ];
        var flatFrame = WorldStage.BuildHeroFrame(program: flatProgram, width: WorldWidth, height: WorldHeight) with { DynamicTransforms = transforms };
        var gridFrame = WorldStage.BuildHeroFrame(program: gridProgram, width: WorldWidth, height: WorldHeight) with { DynamicTransforms = transforms };

        // Vulkan: both variants on the host device + its neutral compute services (SPIR-V kernels).
        var vulkanGpu = context.Resolve<IGpuComputeServices>();
        var vulkanDevice = context.RequireGpuDevice();
        var vulkanFlatPixels = RenderOnce(program: flatProgram, frame: flatFrame, device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv");
        var vulkanGridPixels = RenderOnce(program: gridProgram, frame: gridFrame, device: vulkanDevice, gpu: vulkanGpu, bytecodeExtension: ".spv");

        // Direct3D 12: both variants on the shared Tier-C device (DXIL kernels).
        var directX = context.RequireDirectXDevice();
        var directXGpu = directX.Services.GetRequiredService<IGpuComputeServices>();
        var directXFlatPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => RenderOnce(program: flatProgram, frame: flatFrame, device: directX.DeviceContext, gpu: directXGpu, bytecodeExtension: ".dxil"));
        var directXGridPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => RenderOnce(program: gridProgram, frame: gridFrame, device: directX.DeviceContext, gpu: directXGpu, bytecodeExtension: ".dxil"));

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var diffPath = Path.Combine(path1: context.ArtifactsDirectory, path2: "world-grid-cull-diff.png");

        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: context.ArtifactsDirectory, path2: "world-grid-cull-vulkan.png"), rgba: vulkanGridPixels, width: (int)WorldWidth);
        PngEncoder.Write(height: (int)WorldHeight, path: Path.Combine(path1: context.ArtifactsDirectory, path2: "world-grid-cull-directx.png"), rgba: directXGridPixels, width: (int)WorldWidth);
        ParityCheck.WriteDiffImage(comparand: directXGridPixels, height: (int)WorldHeight, path: diffPath, reference: vulkanGridPixels, width: (int)WorldWidth);

        // (a) grid == flat, PER BACKEND (the shared flat/instanced contract: Vulkan bit-identical, Direct3D 12 within
        // WorldLsbExact). A hole-in-the-world superset violation surfaces here as a non-identical Vulkan pair.
        var contractFailure = ParityCheck.EvaluateFlatInstancedContract(
            artifactsDirectory: context.ArtifactsDirectory,
            diffPath: diffPath,
            directXFlatPixels: directXFlatPixels,
            directXInstancedPixels: directXGridPixels,
            height: (int)WorldHeight,
            stageName: "world-grid-cull",
            vulkanFlatPixels: vulkanFlatPixels,
            vulkanInstancedPixels: vulkanGridPixels,
            width: (int)WorldWidth
        );

        if (contractFailure is { } gridFlatFailure) {
            return gridFlatFailure;
        }

        // (b) The grid render still holds cross-backend parity within the usual WorldComposite thresholds.
        var metrics = ParityMetrics.Compute(reference: vulkanGridPixels, comparand: directXGridPixels, width: (int)WorldWidth, height: (int)WorldHeight);
        var failures = ParityThresholds.WorldComposite.Evaluate(metrics: metrics);

        if (failures.Count != 0) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"{ParityCheck.Describe(metrics: metrics)} — {string.Join(separator: "; ", values: failures)}");
        }

        var carves = (CarveColumns * CarveRows);

        return PostStageOutcome.Pass(artifactPath: diffPath, detail: $"{WorldWidth}x{WorldHeight} {carves}-carve destructible slab (7 mask words) + 1 dynamic + 1 unmaskable always-list + 1 parked instance | grid == flat bit-identical on Vulkan, within WorldLsbExact on Direct3D 12 | grid cross-backend within WorldComposite thresholds | {ParityCheck.Describe(metrics: metrics)}");
    }
    private static byte[] RenderOnce(SdfProgram program, SdfFrame frame, IGpuDeviceContext device, IGpuComputeServices gpu, string bytecodeExtension) {
        using var renderer = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: WorldHeight,
            kernels: SdfWorldKernels.Load(bytecodeExtension: bytecodeExtension),
            options: new SdfWorldEngineOptions(DynamicTransformCapacity: 1, Program: program),
            width: WorldWidth
        );

        return renderer.RenderFrame(frame: frame);
    }
}
