using System.Numerics;

using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage for the carve-bake sampled-field brick (<see cref="SdfShapeType.SampledRegion"/>).
/// <para>
/// On the Vulkan host, this stage drives the GPU bake pipeline (<see cref="SdfWorldEngine.RequestBrickBake"/>
/// + the sliced <c>sdf-brick-bake.comp</c> baker, then the trilinear <c>sdfSampledRegion</c> sample in the views/beam
/// kernels):
/// <list type="number">
/// <item>Bake and sample determinism: two independently constructed
/// engines each bake the identical <see cref="BrickBakeRequest"/> to completion and render the identical scene; their
/// readbacks must be bit-identical.</item>
/// <item>The brick affects the field: the baked scene must differ from the same subject rendered without the brick, so a
/// silently-inert brick (a zeroed pool, an unbound buffer) fails loudly instead of passing as "identical to uncarved".</item>
/// <item>Both renders clear a coverage floor, so a black or unframed render is reported as an infrastructure failure.</item>
/// </list>
/// </para>
/// <para>
/// This stage does not cover Vulkan-to-Direct3D-12 parity for the baked scene or the hull fallback used by kernels
/// compiled without <c>SDF_SAMPLED_REGIONS</c>.
/// </para>
/// <para>Deterministic: a fixed scene, camera, and single settled frame at time 0 — no wall-clock, no RNG.</para>
/// </summary>
internal sealed class WorldSampledRegionStage : IPostStage {
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));
    private const uint Height = 600;
    private const uint Width = 960;
    // λ = √3 is folded into the stored brick values for step safety. Keep in sync with
    // SdfCarveBakePlanner.InvLambda and the √3 the sdf-brick-bake.comp baker + sdfSampledRegion assume.
    private const float InverseLambda = 0.5773502691896258f;   // 1/√3
    // A bake completes one ≤256K-voxel slice per RenderFrame; the test brick is ~75K voxels (one slice), so a handful of
    // frames always suffices. The cap guards against a wedged bake (never Ready) turning the drive loop infinite.
    private const int MaxBakeDriveFrames = 32;
    // A carved render fills a large fraction of the frame with the lit subject; well below its true coverage but far
    // above a black/unframed render, so a broken pipeline reads as INFRA.
    private const int MinCoveragePixels = 40_000;
    // Two renders "differ" meaningfully when this many pixels changed — the carve cavity is a big chunk of the subject,
    // so a real bake moves far more than this; a threshold this low only rejects a truly inert (identical) brick.
    private const int MinCarveDeltaPixels = 3_000;

    /// <inheritdoc/>
    public string Name => "world-sampled-region";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var carves = BuildCarveCluster();
        var brick = ComputeBrickGeometry(carves: carves);
        var request = new BrickBakeRequest(
            BoxMin: brick.BoxMin,
            CellSize: brick.CellSize,
            Carves: carves,
            DimX: brick.DimX,
            DimY: brick.DimY,
            DimZ: brick.DimZ,
            InverseLambda: InverseLambda
        );

        var uncarved = RenderScene(device: device, gpu: gpu, program: BuildScene(brick: null), request: null);
        var baked1 = RenderScene(device: device, gpu: gpu, program: BuildScene(brick: brick), request: request);
        var baked2 = RenderScene(device: device, gpu: gpu, program: BuildScene(brick: brick), request: request);

        var coverage = LitPixels(pixels: baked1);

        if (coverage < MinCoveragePixels) {
            return PostStageOutcome.Infra(detail: $"the baked render is near-empty ({coverage} lit px < {MinCoveragePixels}) — the scene never framed or the render failed");
        }

        if (!PixelsEqual(a: baked1, b: baked2)) {
            var drift = PixelDelta(a: baked1, b: baked2);

            return PostStageOutcome.Fail(detail: $"bake+sample NON-DETERMINISM: two independent bakes of one request rendered {drift} differing px (must be 0) — the bake kernel or the trilinear sample diverged run-to-run");
        }

        var carveDelta = PixelDelta(a: uncarved, b: baked1);

        if (carveDelta < MinCarveDeltaPixels) {
            return PostStageOutcome.Fail(detail: $"the brick is INERT: the baked scene differs from the uncarved subject by only {carveDelta} px (< {MinCarveDeltaPixels}) — the SampledRegion did not carve (zeroed pool / unbound buffer?)");
        }

        return PostStageOutcome.Pass(detail: $"{Width}x{Height} baked SampledRegion on Vulkan | {carves.Length} carves -> 1 brick ({brick.DimX}x{brick.DimY}x{brick.DimZ} voxels, cell {brick.CellSize:0.0000}) | two independent bakes bit-identical ({coverage} lit px), carved vs uncarved {carveDelta} px");
    }

    // A tight cluster of hard carves on the +Z cap of the subject sphere — dense enough to bite a visible cavity the
    // hero camera sees, small enough to bake in one slice. Deterministic (a fixed 3x3x1 lattice, no RNG).
    private static Vector4[] BuildCarveCluster() {
        var carves = new List<Vector4>(capacity: 9);

        for (var iy = -1; (iy <= 1); iy++) {
            for (var ix = -1; (ix <= 1); ix++) {
                carves.Add(item: new Vector4(w: 0.35f, x: (ix * 0.32f), y: (0.9f + (iy * 0.32f)), z: 1.0f));
            }
        }

        return carves.ToArray();
    }

    // The brick box/cell/dims the planner would derive for this cluster (mirrors SdfCarveBakePlanner.TryRequestBake): a
    // tight AABB grown by margin m = maxRadius + 2h, cubic cell h (enlarged only to keep dims <= BrickDim).
    private static (Vector3 BoxMin, float CellSize, int DimX, int DimY, int DimZ, float BoundaryFloor) ComputeBrickGeometry(ReadOnlySpan<Vector4> carves) {
        var h = (SdfCarveBakePlanner.BinEdge / SdfBrickPoolLayout.BrickDim);
        var min = new Vector3(value: float.PositiveInfinity);
        var max = new Vector3(value: float.NegativeInfinity);
        var maxRadius = 0f;

        foreach (var carve in carves) {
            var center = new Vector3(x: carve.X, y: carve.Y, z: carve.Z);

            min = Vector3.Min(value1: min, value2: (center - new Vector3(value: carve.W)));
            max = Vector3.Max(value1: max, value2: (center + new Vector3(value: carve.W)));
            maxRadius = MathF.Max(x: maxRadius, y: carve.W);
        }

        var margin = (maxRadius + (2f * h));
        var boxMin = (min - new Vector3(margin));
        var boxMax = (max + new Vector3(margin));
        var extent = (boxMax - boxMin);
        var cell = MathF.Max(h, (MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) / SdfBrickPoolLayout.BrickDim));

        return (
            boxMin,
            cell,
            Math.Clamp(value: (int)MathF.Ceiling(x: (extent.X / cell)), min: 1, max: SdfBrickPoolLayout.BrickDim),
            Math.Clamp(value: (int)MathF.Ceiling(x: (extent.Y / cell)), min: 1, max: SdfBrickPoolLayout.BrickDim),
            Math.Clamp(value: (int)MathF.Ceiling(x: (extent.Z / cell)), min: 1, max: SdfBrickPoolLayout.BrickDim),
            (margin * InverseLambda)
        );
    }

    // The scene: a ground plane + a subject sphere; when a brick is supplied, one SampledRegion instance subtracts the
    // baked carve cluster from it (slot 0). brick == null renders the uncarved subject (the "the brick actually carves"
    // reference).
    private static SdfProgram BuildScene((Vector3 BoxMin, float CellSize, int DimX, int DimY, int DimZ, float BoundaryFloor)? brick) {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.5f, y: 0.52f, z: 0.58f)));
        var body = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.8f, y: 0.45f, z: 0.3f)));

        _ = builder.Plane(normal: Vector3.UnitY, offset: 0f, material: ground);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0f, y: 0.9f, z: 0f)).Sphere(radius: 1.1f, material: body);

        if (brick is { } b) {
            var extent = new Vector3(x: (b.DimX * b.CellSize), y: (b.DimY * b.CellSize), z: (b.DimZ * b.CellSize));
            var center = (b.BoxMin + (0.5f * extent));

            builder.BeginInstance(boundCenter: center, boundRadius: (0.5f * extent.Length()));
            _ = builder.ResetPoint().SampledRegion(
                boxMin: b.BoxMin,
                cellSize: b.CellSize,
                dimX: b.DimX,
                dimY: b.DimY,
                dimZ: b.DimZ,
                brickWordOffset: SdfBrickPoolLayout.SlotWordOffset(slot: 0),
                boundaryFloor: b.BoundaryFloor,
                material: body
            );
            builder.EndInstance();
        }

        return builder.Build();
    }

    // Builds an engine, optionally requests + drives the sliced bake to Ready, then renders one settled frame and reads
    // it back. A fresh engine per call so the two determinism renders share NO GPU state.
    private static byte[] RenderScene(IGpuDeviceContext device, IGpuComputeServices gpu, SdfProgram program, BrickBakeRequest? request) {
        using var engine = new SdfWorldEngine(
            device: device,
            gpu: gpu,
            height: Height,
            kernels: SdfWorldKernels.Load(bytecodeExtension: ".spv"),
            options: new SdfWorldEngineOptions(Program: program),
            width: Width
        );

        var frame = BuildFrame(program: program);

        if (request is { } bake) {
            engine.RequestBrickBake(slot: 0, request: bake);

            // One ≤256K-voxel slice records per RenderFrame; drive until the slot flips Ready, then render the settled
            // frame that samples the completed brick.
            for (var index = 0; ((index < MaxBakeDriveFrames) && (engine.GetBrickState(slot: 0).State != BrickBakeState.Ready)); index++) {
                _ = engine.RenderFrame(frame: frame);
            }
        }

        return engine.RenderFrame(frame: frame);
    }
    private static SdfFrame BuildFrame(SdfProgram program) {
        var camera = CameraSnapshot.LookAt(
            position: new Vector3(x: 0.4f, y: 2.6f, z: 6.5f),
            target: new Vector3(x: 0f, y: 0.9f, z: 0.4f),
            fieldOfViewRadians: FieldOfViewRadians,
            viewportWidth: Width,
            viewportHeight: Height
        );

        return new SdfFrame(
            Program: program,
            ProgramChanged: false,
            Time: 0f,
            Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
            WarpAmount: 0f
        );
    }

    // Count non-background pixels (any channel meaningfully lit) — a coarse coverage proxy for the vacuity floor.
    private static int LitPixels(byte[] pixels) {
        var count = 0;

        for (var index = 0; ((index + 2) < pixels.Length); index += 4) {
            if ((pixels[index] > 24) || (pixels[(index + 1)] > 24) || (pixels[(index + 2)] > 24)) {
                count++;
            }
        }

        return count;
    }
    private static bool PixelsEqual(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(other: b.AsSpan());

    // The count of pixels whose RGBA differs between two equal-length readbacks.
    private static int PixelDelta(byte[] a, byte[] b) {
        var count = 0;
        var length = Math.Min(val1: a.Length, val2: b.Length);

        for (var index = 0; ((index + 3) < length); index += 4) {
            if ((a[index] != b[index]) || (a[(index + 1)] != b[(index + 1)]) || (a[(index + 2)] != b[(index + 2)]) || (a[(index + 3)] != b[(index + 3)])) {
                count++;
            }
        }

        return count;
    }
}
