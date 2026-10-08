using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    // One shadow kernel and one kernel per views variant serve every fade capacity. A recorder planned without the
    // incoming image writes a zero fade count, so a live handoff the old graph cannot hold marches and reads nothing; a
    // recorder planned with it passes the live count through.
    [InlineData(SdfWorldPackage.Parts.Shadow, 0, 0u)]
    [InlineData(SdfWorldPackage.Parts.Views, 0, 0u)]
    [InlineData(SdfWorldPackage.Parts.Shadow, 1, 1u)]
    [InlineData(SdfWorldPackage.Parts.Views, 1, 1u)]
    [Theory]
    public async Task ARecorderReadsTheLiveFadeCountOnlyWhenItsPlanHoldsTheIncomingImageAsync(string part, int plannedCapacity, uint expectedFadeCount) {
        var gpu = new UploadModelGpu();
        var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
        var lights = SdfLights.Default();

        lights.ShadowSlots.Configure(fadeCapacity: plannedCapacity, slots: 1);
        lights.ShadowSlots.SetSlot(light: 0, slot: 0);
        using var view = new SdfTestView(device: gpu, extent: Extent, hostsOnDirectX: false, pipelines: pipelines,
            residency: new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new FixedFrameSource(frame: Frame() with { Lights = lights }), height: Extent,
                kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance, pipelines: pipelines, width: Extent));
        var frame = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);

        TestLiveness.Until(step: () => view.Produce(context: in frame), reason: () => view.NotReadyReason,
            wait: view.Residency.WaitPipelineBuilds);
        var fragment = view.Passes.FragmentOf(instance: SdfTestView.Instance)!;

        Assert.Equal(expected: (plannedCapacity > 0), actual: fragment.Resources.Any(predicate: static resource => (resource.Name == SdfWorldPackage.IncomingVisibility)));
        var pass = fragment.Passes.Single(predicate: pass => (pass.Name == part));
        var context = new RenderGraphPackageRecorderContext(Device: gpu, Services: gpu.Services,
            Instance: SdfTestView.Instance, Pass: $"sdf.world${part}", Part: part, Package: RenderGraphPackageCatalog.SdfWorld,
            Pipelines: pipelines.Pipelines, HostsOnDirectX: false, InFlightFrames: 1, Width: Extent, Height: Extent,
            Parameters: SdfWorldInterfaces.WorldParameters,
            Inputs: Declarations(ports: pass.Inputs), Outputs: Declarations(ports: pass.Outputs));
        using var built = await view.Passes.BuildAsync(context: context, cancellationToken: CancellationToken.None);

        // The plan and the completed build describe the planned capacity while the live frame starts a handoff of two.
        lights.ShadowSlots.Configure(fadeCapacity: 2, slots: 1);
        lights.ShadowSlots.SetSlot(light: 0, slot: 0);
        lights.ShadowSlots.SetHandoffs(handoffs: [new SdfShadowHandoff(Incoming: 0, Outgoing: 0, Slot: 0, Weight: 0.5f)]);
        Assert.Equal(expected: 1, actual: view.Residency.Frame!.Lights.ShadowSlots.FadeCount);
        using var block = gpu.Services.BufferFactory.CreateHostVisible(name: default, sizeBytes: ((uint)SdfFrameBlock.SizeBytes), usage: GpuBufferUsage.Uniform);
        using var scratch = gpu.Services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: 4096, usage: GpuBufferUsage.Storage);
        var pool = gpu.Services.Bindings.CreatePool(name: default,
            sizes: GpuDescriptorPoolSizes.ForGroups(groups: context.Parameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute).Groups));

        try {
            using var recorder = view.Passes.Create(context: context, built: built, groups: new RenderGraphPackageGroups(
                DescriptorPool: pool, FrameBlocks: [block], OutputImages: [], PassBlocks: [block], Regions: []));
            using var counters = new GpuKernelCounters(buffers: gpu.Services.BufferFactory, slots: 1, rows: 1,
                owner: "shadow-planning-law", part: "counters");
            using var commands = gpu.Services.CommandPoolFactory.Create(name: default);
            var passBlock = new byte[SdfFrameBlock.SizeBytes];
            var recording = new RenderGraphPackageRecording(CommandBuffer: commands.CommandBufferHandle, Recorder: gpu.Services.Recorder,
                Slot: 0, Width: Extent, Height: Extent, Inputs: Resources(declarations: context.Inputs), Outputs: Resources(declarations: context.Outputs),
                PassBlock: passBlock, Leases: new LeaseRetireList(), Context: frame, MayStandIn: false, WorkCounters: counters.RowOf(row: 0, slot: 0));

            Assert.Equal(expected: RenderGraphPackageOutcome.Drew, actual: recorder.Record(recording: in recording));
            Assert.Equal(expected: SdfKernelSet.StemOf(kernel: ((part == SdfWorldPackage.Parts.Shadow) ? SdfKernel.Shadow : SdfKernel.ViewsCore)), actual: gpu.BoundPipelineName);
            var offset = ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.ShadowFadeCount));

            Assert.Equal(expected: expectedFadeCount, actual: BinaryPrimitives.ReadUInt32LittleEndian(source: passBlock.AsSpan(start: offset)));
        } finally {
            gpu.Services.Bindings.DestroyPool(poolHandle: pool);
        }

        ShaderPipelineResource[] Declarations(IReadOnlyList<ResourceReference> ports) => [.. ports.Select(selector: port =>
            fragment.Resources.Single(predicate: resource => (resource.Name == port.Name)) with { Name = $"sdf.world${port.Name}" })];
        RenderGraphPackageResource[] Resources(IReadOnlyList<ShaderPipelineResource> declarations) => [.. declarations.Select(selector: resource =>
            new RenderGraphPackageResource(Version: resource.Name, Kind: resource.Kind, Image: default,
                Buffer: ((resource.Kind == ShaderPipelineResourceKind.Buffer) ? scratch : null), Owned: null))];
    }
}
