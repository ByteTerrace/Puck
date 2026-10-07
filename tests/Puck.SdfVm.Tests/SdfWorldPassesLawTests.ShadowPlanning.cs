using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [InlineData(SdfWorldPackage.Parts.Shadow)]
    [InlineData(SdfWorldPackage.Parts.Views)]
    [Theory]
    public async Task ShadowRecorderKeepsPlannedFadeCapacityWhenLivePolicyChangesAsync(string part) {
        var gpu = new UploadModelGpu();
        var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
        var lights = SdfLights.Default();

        lights.ShadowSlots.Configure(fadeCapacity: 1, slots: 1);
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
        var pass = fragment.Passes.Single(predicate: pass => (pass.Name == part));
        var context = new RenderGraphPackageRecorderContext(Device: gpu, Services: gpu.Services,
            Instance: SdfTestView.Instance, Pass: $"sdf.world${part}", Part: part, Package: RenderGraphPackageCatalog.SdfWorld,
            Pipelines: pipelines.Pipelines, HostsOnDirectX: false, InFlightFrames: 1, Width: Extent, Height: Extent,
            Parameters: SdfWorldInterfaces.WorldFadeParameters[1],
            Inputs: Declarations(ports: pass.Inputs), Outputs: Declarations(ports: pass.Outputs));
        using var built = await view.Passes.BuildAsync(context: context, cancellationToken: CancellationToken.None);

        // The plan and completed build still describe R8 while the residency now requests two channels.
        lights.ShadowSlots.Configure(fadeCapacity: 2, slots: 1);
        lights.ShadowSlots.SetSlot(light: 0, slot: 0);
        Assert.Equal(expected: 2, actual: view.Residency.Frame!.Lights.ShadowSlots.FadeCapacity);
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
            var recording = new RenderGraphPackageRecording(CommandBuffer: commands.CommandBufferHandle, Recorder: gpu.Services.Recorder,
                Slot: 0, Width: Extent, Height: Extent, Inputs: Resources(declarations: context.Inputs), Outputs: Resources(declarations: context.Outputs),
                PassBlock: new byte[SdfFrameBlock.SizeBytes], Leases: new LeaseRetireList(), Context: frame, MayStandIn: false, WorkCounters: counters.RowOf(row: 0, slot: 0));

            Assert.Equal(expected: RenderGraphPackageOutcome.Drew, actual: recorder.Record(recording: in recording));
            Assert.Equal(expected: SdfKernelSet.StemOf(kernel: ((part == SdfWorldPackage.Parts.Shadow) ? SdfKernel.ShadowFade1 : SdfKernel.ViewsCoreFade1)), actual: gpu.BoundPipelineName);
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
