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
/// CPU path runs without a device: the one pass an upload counts, the exact counts of a first and of a still upload, that
/// the descriptor pools the tables state are the ones they create and that a device heap that cannot hold them refuses the
/// tables by name before they allocate, that a frame with no view is refused by name, what moves the revision, that
/// submission identity keeps increasing across a rebuild on the owner's ledger, and that a steady-state upload allocates
/// nothing.
/// </summary>
public sealed class SdfWorldTablesWorkLawTests {
    private const uint Extent = 64;

    [Fact]
    public void TheUploadPassIsPinned() => Assert.Equal(
        expected: ["upload"],
        actual: SdfWorldTables.PassLabels.ToArray()
    );
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
            regionCount: (brickPool ? 10 : 9),
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
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
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

    // The first upload of single-view tables, and a still one after it. Submission 7 follows the six ISA handshake
    // submissions at construction. The fake's default memory profile stages every region, so the first upload copies all
    // nine host-written tables (program, dynamic transforms, instance grid, screen surfaces, screen lights, volumes, decals,
    // screen mappings and the one-record mesh region) behind one barrier ordering the earlier views' reads of their
    // destinations before the copies write them, each binding the copy pipeline and its set with no push constants, then
    // transitions each copied buffer for its readers; the still upload repeats the first's inputs, so it owes no copy and
    // binds nothing. Outside the pass: the command buffer, and on the first upload the fillers' first transitions and
    // clears. The upload pass counts the regions' host-visible writes too: on the first upload every region's whole first
    // copy (the 820 KB decal table among them), each with its header and one run-table entry.
    private const string FirstUpload =
        "work submission=7 revision=1\nwork upload executed: dispatches=9 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=1 barriers.buffer=9 binds.pipeline=9 binds.descriptor-set=9 push-constants=0 descriptor-writes=0 uploads.host-visible=837716 clears=0 copies=0\nwork outside: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=4 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=0 clears=2 copies=0\n";
    private const string StillUpload =
        "work submission=8 revision=1\nwork upload executed: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=0 barriers.image=0 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=0 clears=0 copies=0\nwork outside: dispatches=0 dispatches.indirect=0 draws=0 render-passes=0 command-buffers=1 barriers.image=0 barriers.memory=0 barriers.buffer=0 binds.pipeline=0 binds.descriptor-set=0 push-constants=0 descriptor-writes=0 uploads.host-visible=0 clears=0 copies=0\n";

    private sealed class Rig : IDisposable {
        public Rig(GpuWorkLedger? ledger = null, int brickPoolVoxelCapacity = 0) {
            var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);

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
            // A brick pool needs its bake pipeline, so its set is built with a one-byte brick kernel.
            Pipelines = ((brickPoolVoxelCapacity == 0)
                ? SdfTestPipelines.Build(
                    device: gpu,
                    kernels: SdfTestPipelines.Kernels(),
                    ledger: owned
                )
                : SdfWorldPipelines.Build(
                    cancellationToken: CancellationToken.None,
                    device: gpu,
                    includeBrickPipelines: true,
                    kernels: (SdfTestPipelines.Kernels() with {
                        BrickBake = new byte[] { 1 },
                    }),
                    ledger: owned
                ));
            Engine = new SdfWorldTables(
                device: gpu,
                options: new SdfWorldTablesOptions(
                    BrickPoolVoxelCapacity: brickPoolVoxelCapacity,
                    Program: program,
                    WorkLedger: owned
                ),
                pipelines: Pipelines,
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

        public SdfWorldTables Engine { get; }
        public SdfFrame Frame { get; }
        public FakeGpuDevice Gpu { get; }
        public GpuPassPipeline MeshRaster { get; }
        public SdfWorldPipelines Pipelines { get; }
        public GpuPassPipeline RegionCopy { get; }

        public void Dispose() {
            Engine.Dispose();
            Pipelines.Dispose();
            RegionCopy.Dispose();
            MeshRaster.Dispose();
        }
        // Prepares a reload of the rig's pipelines and installs it, as a residency does across two produced frames.
        public int Reload(SdfWorldKernels kernels) {
            using var reload = Pipelines.PrepareReload(
                cancellationToken: CancellationToken.None,
                kernels: kernels
            );

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
