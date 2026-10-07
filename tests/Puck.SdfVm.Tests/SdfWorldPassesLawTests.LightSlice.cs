using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    // Two complete regions plus the existing graph installation/retirement allowance.
    private static int LightCompletionFrames() => (16 + (2 * (SdfIndirectLightLayout.Resolution /
        SdfIndirectLightViews.RowsPerSubmission(layout: new SdfIndirectLayout(tier: SdfIndirectTier.Medium)))));

    [InlineData(SdfIndirectTier.Medium)]
    [InlineData(SdfIndirectTier.High)]
    [Theory]
    public async Task RecordedLightDepthDispatchMatchesItsPackedRowIntervalAsync(SdfIndirectTier tier) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 2);
        builder.Sphere(material: material, radius: 1);
        builder.EndInstance();
        var lights = SdfLights.Default();

        lights.ShadowSlots.SetOwner(owner: "sun", slot: 0);
        var frame = Frame() with { Program = builder.Build(), Lights = lights, IndirectTier = tier, FarDistance = 12 };
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: frame), height: Extent, width: Extent, name: "light-slice",
            kernels: SdfTestPipelines.Kernels(), pipelines: pipelines);
        var context = ContextOf(gpu: gpu);

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var views = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        const string Instance = "light-slice.camera";

        views.RegisterLightView(name: Instance, residency: residency);
        var fragment = views.FragmentOf(instance: Instance)!;
        var pass = fragment.Passes.Single(predicate: candidate => (candidate.Name == SdfWorldPackage.LightDepth));
        var recorderContext = new RenderGraphPackageRecorderContext(Device: gpu, Services: gpu.Services,
            Instance: Instance, Pass: SdfWorldPackage.LightDepth, Part: SdfWorldPackage.LightDepth,
            Package: RenderGraphPackageCatalog.SdfWorld, Pipelines: pipelines.Pipelines, HostsOnDirectX: false,
            InFlightFrames: 1, Width: 512, Height: 512, Parameters: SdfWorldInterfaces.WorldParameters,
            Inputs: Declarations(ports: pass.Inputs), Outputs: Declarations(ports: pass.Outputs));
        var built = await views.BuildAsync(recorderContext, CancellationToken.None);
        var depth = views.BorrowedBuffer(recorderContext, built, recorderContext.Outputs[0])!;
        using var block = gpu.Services.BufferFactory.CreateHostVisible(name: default,
            sizeBytes: recorderContext.Parameters.SizeBytes, usage: GpuBufferUsage.Uniform);
        var pool = gpu.Services.Bindings.CreatePool(name: default,
            sizes: GpuDescriptorPoolSizes.ForGroups(recorderContext.Parameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute).Groups));

        try {
            using var recorder = views.Create(recorderContext, built, new RenderGraphPackageGroups(
                DescriptorPool: pool, FrameBlocks: [block], OutputImages: [], PassBlocks: [block], Regions: []));
            using var counters = new GpuKernelCounters(gpu.Services.BufferFactory, slots: 1, rows: 1, owner: Instance, part: "counters");
            using var commands = gpu.Services.CommandPoolFactory.Create(name: default);
            var dispatches = new List<(uint X, uint Y, uint Z)>();

            for (var index = 0; (index < 4); index++) {
                views.BeginFrame(context: context);
                Assert.False(condition: recorder.Skips(context: context));
                _ = residency.Submit(context: context);
                var planned = residency.IndirectLightViews.Slice;
                var bytes = new byte[recorderContext.Parameters.SizeBytes];
                var recording = new RenderGraphPackageRecording(CommandBuffer: commands.CommandBufferHandle, Recorder: gpu.Services.Recorder,
                    Slot: 0, Width: 512, Height: 512,
                    Inputs: [.. recorderContext.Inputs.Select(selector: resource => new RenderGraphPackageResource(resource.Name, resource.Kind,
                        default, residency.Tables!.Indirect!.Buffer, null))],
                    Outputs: [new RenderGraphPackageResource(Buffer: depth, Image: default, Kind: ShaderPipelineResourceKind.Buffer, Owned: null, Version: SdfWorldPackage.IndirectLightDepth)],
                    PassBlock: bytes, Leases: new LeaseRetireList(), Context: context, MayStandIn: false, WorkCounters: counters.RowOf(row: 0, slot: 0));

                dispatches.Clear();
                gpu.OnDispatch = (x, y, z) => dispatches.Add(item: (x, y, z));
                Assert.Equal(RenderGraphPackageOutcome.Drew, recorder.Record(recording: recording));
                gpu.OnDispatch = null;
                var dispatched = Assert.Single(collection: dispatches);
                var word = BinaryPrimitives.ReadUInt32LittleEndian(source: bytes.AsSpan(start: ((int)recorderContext.Parameters.BlockOffsetOf(member: SdfWorldPackage.LightSlice))));

                Assert.Equal(actual: word, expected: planned);
                Assert.True(condition: SdfIndirectLightLayout.TryUnpackSlice(columnCount: out var columns, firstColumn: out _, firstRow: out _, map: out var map, rowCount: out var rowCount, slice: word));
                Assert.Equal(actual: dispatched, expected: (((uint)(columns / 8)), ((uint)(rowCount / 8)), 1u));
                Assert.InRange(rowCount, 8, SdfIndirectLightViews.RowsPerSubmission(layout: residency.Tables!.Indirect!.Layout));
                Assert.False(condition: residency.IndirectLightViews.Snapshot(index: map).Valid);
                recorder.Submitted();
            }
        } finally {
            gpu.OnDispatch = null;
            gpu.Services.Bindings.DestroyPool(poolHandle: pool);
        }

        ShaderPipelineResource[] Declarations(IReadOnlyList<ResourceReference> ports) => [.. ports.Select(selector: port =>
            fragment.Resources.Single(predicate: resource => (resource.Name == port.Name)))];
    }
}
