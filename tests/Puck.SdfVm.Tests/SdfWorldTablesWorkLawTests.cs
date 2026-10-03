using System.Numerics;
using System.Text;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Abstractions.Counting;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for the GPU work <see cref="SdfWorldTables"/> counts, driven over <see cref="FakeGpuDevice"/> so the tables' whole
/// CPU path runs without a device: the passes an upload counts, the exact counts of a first and of a still upload, that
/// the descriptor pools the tables state are the ones they create and that a device heap that cannot hold them refuses the
/// tables by name before they allocate, that a frame with no view is refused by name, what moves the revision, that
/// submission identity keeps increasing across a rebuild on the owner's ledger, and that a steady-state upload allocates
/// nothing.
/// </summary>
public sealed partial class SdfWorldTablesWorkLawTests {
    private const uint Extent = 64;

    [Fact]
    public void TheUploadPassesArePinned() {
        Assert.Equal(
            expected: ["fillers", "bricks", "upload"],
            actual: SdfWorldTables.PassLabels.ToArray()
        );
        Assert.Equal(
            expected: [WorkClass.Deterministic, WorkClass.PerBackendDeterministic, WorkClass.PerBackendDeterministic],
            actual: SdfWorldTables.PassClasses.ToArray()
        );
    }
    [Fact]
    public void AViewsCadenceSignatureTracksOnlyItsOwnQuality() {
        using var rig = new Rig();
        var views = new[] { rig.Frame.Views[0], rig.Frame.Views[0] };
        var frame = rig.Frame with { Views = views };
        var first = rig.Engine.ViewSignature(frame: frame, view: 0);

        Assert.Equal(actual: rig.Engine.ViewSignature(frame: frame, view: 1), expected: first);
        views[1] = views[1] with { Quality = new SdfViewQuality { DisableAmbientOcclusion = true } };
        var second = rig.Engine.ViewSignature(frame: frame, view: 1);

        Assert.NotEqual(actual: second, expected: first);
        Assert.Equal(actual: rig.Engine.ViewSignature(frame: frame, view: 0), expected: first);
        views[0] = views[0] with { Quality = new SdfViewQuality { UseFastSoftShadowMarch = true } };
        Assert.NotEqual(actual: rig.Engine.ViewSignature(frame: frame, view: 0), expected: first);
        Assert.Equal(actual: rig.Engine.ViewSignature(frame: frame, view: 1), expected: second);
    }
    [InlineData(0)]
    [InlineData(SdfWorldTables.DefaultBrickPoolVoxelCapacity)]
    [Theory]
    public void TheDescriptorPoolsTheTablesStateAreThePoolsTheyCreate(int brickPoolVoxelCapacity) {
        using var rig = new Rig(brickPoolVoxelCapacity: brickPoolVoxelCapacity);

        // The tables reserve every region's copy sets first, in one pool, the mesh region's and, with a brick pool, the
        // brick staging's included, then create their own pool; no region creates one. The two pools are the ones they
        // state, in the other order.
        var brickPool = (brickPoolVoxelCapacity > 0);
        var copyPool = GpuRegionCopyPool.SizesOf(
            regionCount: (brickPool ? 14 : 13),
            slotCount: SdfWorldTables.FrameRingSize
        );

        Assert.Equal(
            expected: [copyPool, SdfWorldTables.DescriptorPoolSizes(brickPool: brickPool)],
            actual: rig.Gpu.PoolsCreated
        );
        Assert.Equal(
            expected: [SdfWorldTables.DescriptorPoolSizes(brickPool: brickPool), copyPool],
            actual: SdfWorldTables.DescriptorPools(brickPool: brickPool)
        );
    }
    [Fact]
    public void TablesTheDeviceHeapCannotHoldAreRefusedByNameBeforeTheyAllocate() {
        var gpu = new FakeGpuDevice();
        var demand = SdfWorldTables.DescriptorPools(brickPool: false).Aggregate(
            func: static (sum, pool) => (sum + pool.HeapDescriptors),
            seed: 0U
        );
        // The handshake's pass set holds the samplers, so the tables also need sampler descriptors.
        var samplers = SdfWorldTables.DescriptorPools(brickPool: false).Aggregate(
            func: static (sum, pool) => (sum + pool.SamplerHeapDescriptors),
            seed: 0U
        );
        var options = new SdfWorldTablesOptions(
            BrickPoolVoxelCapacity: 0,
            Program: Program()
        );

        GpuDescriptorHeapBudget Heap(uint views) => new(capabilities: (GpuDeviceCapabilities.FromDirectX(
            resourceBindingTier: 3,
            rootSignatureVersion: "1.1",
            samplerHeapSize: 0,
            shaderModel: "6.6",
            staticSamplerHeapSize: 0,
            viewHeapSize: 0
        ) with {
            ViewHeapSize = views,
        }));

        gpu.DescriptorHeap = Heap(views: (demand - 1U));

        var refusal = Assert.Throws<GpuDescriptorHeapRefusalException>(testCode: () => SdfWorldTables.CheckAdmission(
            device: gpu,
            options: options
        ));

        Assert.StartsWith(
            actualString: refusal.Message,
            expectedStartString: $"[{GpuDescriptorHeapBudget.RefusalCode}] 'SDF world tables' needs {demand} view and {samplers} sampler descriptors in 2 pool(s) and is refused: "
        );
        Assert.Empty(collection: gpu.PoolsCreated);

        gpu.DescriptorHeap = Heap(views: demand);
        SdfWorldTables.CheckAdmission(
            device: gpu,
            options: options
        );
        Assert.Equal(
            actual: (gpu.DescriptorHeap.FreeViewDescriptors, gpu.DescriptorHeap.LivePools),
            expected: (demand, 0)
        );
    }
    [Fact]
    public void AFirstUploadCountsEveryCopyExactlyAndAStillOneCountsNothing() {
        using var rig = new Rig();

        // An upload is published by the next one, which observes its fence.
        rig.Render();
        rig.Render();
        Assert.Equal(
            expected: FirstUpload,
            actual: rig.Report()
        );

        rig.Render();
        Assert.Equal(
            expected: StillUpload,
            actual: rig.Report()
        );
    }
    // A queued host-baked brick is written under the bricks pass: its staging region's host writes and copy, and the
    // pool's barrier on either side of it, none of which the upload pass or the work outside every pass counts.
    [Fact]
    public void AQueuedBrickCountsUnderTheBricksPass() {
        using var rig = new Rig(brickPoolVoxelCapacity: SdfWorldTables.DefaultBrickPoolVoxelCapacity);

        rig.Render();
        rig.Render();
        rig.Engine.UploadBrick(
            dimX: 2,
            dimY: 2,
            dimZ: 2,
            slot: 0,
            voxels: new float[8]
        );
        rig.Render();
        rig.Render();

        Assert.Equal(
            expected: BrickUpload,
            actual: rig.Report()
        );
    }
    // The tables render no view of their own, so a frame of no views is refused by name before anything is packed, and the
    // next frame packs as the first would have.
    [Fact]
    public void AFrameWithNoViewsIsRefusedByName() {
        using var rig = new Rig();

        var refusal = Assert.Throws<ArgumentException>(testCode: () => rig.Engine.Pack(frame: rig.Frame with {
            Views = [],
        }));

        Assert.StartsWith(
            actualString: refusal.Message,
            expectedStartString: "A frame renders at least one view."
        );

        rig.Render();
        rig.Render();
        Assert.Equal(
            expected: FirstUpload,
            actual: rig.Report()
        );
    }
    [Fact]
    public void ProgramUploadsAndInstalledKernelReloadsMoveTheRevision() {
        using var rig = new Rig();
        var sample = new GpuWorkSample();

        rig.Render();
        rig.Render();
        Assert.True(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        Assert.Equal(expected: 1L, actual: sample.Revision);

        rig.Engine.UploadProgram(program: rig.Frame.Program);
        Assert.False(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        rig.Render();
        rig.Render();
        Assert.True(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        Assert.Equal(expected: 2L, actual: sample.Revision);

        // Unchanged bytecode installs nothing and keeps the revision; a changed kernel installs and moves it.
        Assert.Equal(expected: 0, actual: rig.Reload(kernels: SdfTestPipelines.Kernels(beam: 1)));
        rig.Render();
        rig.Render();
        Assert.True(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        Assert.Equal(expected: 2L, actual: sample.Revision);

        Assert.Equal(expected: 1, actual: rig.Reload(kernels: SdfTestPipelines.Kernels(beam: 2)));
        Assert.False(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        rig.Render();
        rig.Render();
        Assert.True(condition: rig.Engine.Work.TryReadCompleted(sample: sample));
        Assert.Equal(expected: 3L, actual: sample.Revision);
    }
    [Fact]
    public void SubmissionIdentityKeepsIncreasingAcrossARebuildOnTheOwnersLedger() {
        var ledger = new GpuWorkLedger(
            framesInFlight: SdfWorldTables.FrameRingSize,
            name: "gpu.test"
        );
        var sample = new GpuWorkSample();
        long before;

        using (var rig = new Rig(ledger: ledger)) {
            rig.Render();
            rig.Render();
            Assert.True(condition: ledger.TryReadCompleted(sample: sample));
            before = sample.Submission;
        }

        // What an owner does when it drops the tables: nothing reads available until the rebuilt tables complete.
        ledger.Invalidate();
        Assert.False(condition: ledger.TryReadCompleted(sample: sample));

        using (var rig = new Rig(ledger: ledger)) {
            rig.Render();
            rig.Render();
            Assert.True(condition: ledger.TryReadCompleted(sample: sample));
            Assert.True(
                condition: (sample.Submission > before),
                userMessage: $"The rebuilt tables' submission {sample.Submission} did not follow {before}."
            );
        }
    }
    [Fact]
    public void AFencedUploadPublishesOnTheNextAndASteadyStateUploadAllocatesNothing() {
        using var rig = new Rig();
        var sample = new GpuWorkSample();

        void Upload() {
            rig.Render();
            _ = rig.Engine.Work.TryReadCompleted(sample: sample);
        }

        for (var warm = 0; (warm < 4); warm++) {
            Upload();
        }

        var submission = sample.Submission;

        Upload();
        Assert.Equal(expected: (submission + 1L), actual: sample.Submission);
        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: Upload));
    }

    private static SdfProgram Program() {
        var builder = new SdfProgramBuilder();

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            radius: 1f
        );

        return builder.Build();
    }

    // The first upload of single-view tables, and a still one after it: the tables' first two submissions. The fake's default memory profile stages every region, so the first upload copies all
    // thirteen host-written tables (program, dynamic transforms, instance grid, screen surfaces, screen lights, volumes,
    // decals, screen mappings, the one-record mesh region, the lights and the sky's block, stops and softboxes) behind one barrier ordering the earlier views' reads of their
    // destinations before the copies write them, each binding the copy pipeline and its set with no push constants, then
    // transitions each copied buffer for its readers; the still upload repeats the first's inputs, so it owes no copy and
    // binds nothing. The fillers pass runs on the first upload alone, the fillers' first transitions and clears, and the
    // bricks pass on neither, since the tables have no brick pool. Outside every pass: the command buffer, and on the first
    // upload the first write of both ring slots' World sets. The upload pass counts the regions' host-visible writes too:
    // on the first upload every region's whole first copy (the 820 KB decal table among them), each with its header and one
    // run-table entry. The previous-transform table is seeded device-to-device behind four buffer barriers, adding one
    // counted copy and no host-visible bytes. This fixture has no mesh draws to seed.
    private const string FirstUpload =
        "work submission=1 revision=1\nwork fillers executed: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=4 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=0 clears=2 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0\nwork bricks skipped\nwork upload executed: dispatches=13 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=1 barriers.buffer=17 binds.pipeline=13 binds.descriptor-set=13 push-constants=0 descriptor-writes=26 uploads.host-visible=838668 clears=0 copies=1 march.steps=0 texels.written=0 copies.buffer-bytes=48 sky.evaluations=0\nwork outside: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=0 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=0 clears=0 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0\n";
    // A still upload that writes one queued 2x2x2 brick: the bricks pass binds the copy pipeline and the brick staging's
    // set and dispatches its copy between the pool's two barriers, and counts the staging's host writes, the eight voxels
    // behind their header and one run-table entry.
    private const string BrickUpload =
        "work submission=3 revision=1\nwork fillers skipped\nwork bricks executed: dispatches=1 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=2 binds.pipeline=1 binds.descriptor-set=1 push-constants=0 descriptor-writes=2 uploads.host-visible=56 clears=0 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0\nwork upload executed: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=0 clears=0 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0\nwork outside: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=0 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=0 clears=0 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0\n";
    private const string StillUpload =
        "work submission=2 revision=1\nwork fillers skipped\nwork bricks skipped\nwork upload executed: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=0 clears=0 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0\nwork outside: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=0 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=0 clears=0 copies=0 march.steps=0 texels.written=0 copies.buffer-bytes=0 sky.evaluations=0\n";

    private sealed class Rig : IDisposable {
        public Rig(GpuWorkLedger? ledger = null, int brickPoolVoxelCapacity = 0) {
            var gpu = new FakeGpuDevice();

            Gpu = gpu;

            var program = Program();
            var owned = (ledger ?? new GpuWorkLedger(
                framesInFlight: SdfWorldTables.FrameRingSize,
                name: "gpu.sdf-tables"
            ));

            RegionCopy = SdfTestPipelines.RegionCopy(
                device: gpu,
                ledger: owned
            );
            MeshRaster = SdfTestPipelines.MeshRaster(
                device: gpu,
                ledger: owned
            );
            ImpostorRaster = SdfTestPipelines.ImpostorRaster(
                device: gpu,
                ledger: owned
            );
            // A brick pool needs its bake pipeline, so its set is built with a one-byte brick kernel.
            Pipelines = ((brickPoolVoxelCapacity == 0)
                ? SdfTestPipelines.Build(
                    device: gpu,
                    kernels: SdfTestPipelines.Kernels(),
                    cache: Cache
                )
                : SdfTestPipelines.Build(
                    device: gpu,
                    includeBrickPipelines: true,
                    kernels: SdfTestPipelines.Kernels().With(bytecode: new byte[] { 1 }, kernel: SdfKernel.BrickBake),
                    cache: Cache
                ));
            Engine = new SdfWorldTables(
                device: gpu,
                options: new SdfWorldTablesOptions(
                    BrickPoolVoxelCapacity: brickPoolVoxelCapacity,
                    Program: program,
                    WorkLedger: owned
                ),
                pipelines: Pipelines,
                impostorRaster: ImpostorRaster,
                meshRaster: MeshRaster,
                regionCopy: RegionCopy.Compute!
            );
            Frame = new SdfFrame(
                Program: program,
                ProgramChanged: false,
                Time: 0f,
                Views: [new SdfViewSnapshot(
                    Camera: CameraSnapshot.LookAt(
                        fieldOfViewRadians: 1f,
                        position: new Vector3(x: 0f, y: 0f, z: -5f),
                        target: Vector3.Zero,
                        viewportHeight: Extent,
                        viewportWidth: Extent
                    ),
                    Region: new NormalizedRect(
                        Height: 1f,
                        Width: 1f,
                        X: 0f,
                        Y: 0f
                    )
                )]
            );
        }

        public GpuPassPipelineCache Cache { get; } = new();
        public SdfWorldTables Engine { get; }
        public SdfFrame Frame { get; }
        public FakeGpuDevice Gpu { get; }
        public GpuPassPipeline ImpostorRaster { get; }
        public GpuPassPipeline MeshRaster { get; }
        public SdfWorldPipelines Pipelines { get; }
        public GpuPassPipeline RegionCopy { get; }

        public void Dispose() {
            Engine.Dispose();
            Pipelines.Dispose();
            RegionCopy.Dispose();
            MeshRaster.Dispose();
            ImpostorRaster.Dispose();
        }
        // Prepares a reload of the rig's pipelines and installs it, as a residency does across two produced frames.
        public int Reload(SdfKernelSet kernels) {
            using var reflector = SdfTestPipelines.Reflector();
            using var reload = Pipelines.PrepareReload(
                cache: Cache,
                device: Gpu,
                kernels: kernels,
                reflector: reflector
            );

            reload.WaitAsync(cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

            return Engine.InstallReload(reload: reload);
        }
        // Packs the rig's frame and submits its upload, as a view's first pass of a frame does.
        public void Render() {
            Engine.Pack(frame: Frame);
            Engine.SubmitUpload();
        }
        public string Report() {
            var text = new StringBuilder();

            _ = GpuWorkReport.AppendCompleted(
                builder: text,
                sample: new GpuWorkSample(),
                source: Engine.Work
            );

            return text.ToString();
        }
    }
}
