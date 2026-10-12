using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    private const int ChainInstructions = 150;

    [Fact]
    public void AMeasuredShadeIsPricedAtItsCountedVisitsWithoutItsConservativeRecordBounds() {
        var layout = new SdfIndirectLayout(tier: SdfIndirectTier.Medium);
        var units = SdfIndirectCost.ShadeUnits(frame: CostFrame(shapes: 1), layout: layout);
        var measured = (units with { MeasuredFieldCost = 19 });

        // Before a measurement the continuation search and the reduction are priced at their bounds; once the kernel has
        // counted its visits, record loads and reduction included, the count is the price.
        Assert.True(condition: (units.ItemCost(instructionCount: ChainInstructions) >= (((long)layout.RaysPerProbe) * ((8L * layout.RaysPerProbe) + SdfIndirectLayout.IrradianceTexels))));
        Assert.Equal(expected: (layout.RaysPerProbe * 19L), actual: measured.ItemCost(instructionCount: ChainInstructions));
        // The parity world's solve at Medium: every allocated probe shaded once for the direct sweep and once for each
        // of the two feedback sweeps, at the visits the RTX 2060 counted there, takes few produced frames' chunks.
        var probes = (((layout.BounceLimit + 1) * layout.BrickCapacity) * SdfIndirectLayout.ProbesPerBrick);
        var chunks = SdfIndirectCost.Admit(count: probes, instructionCount: ChainInstructions, limit: SdfIndirectCost.FrameCost(layout: layout), units: measured);

        Assert.InRange(actual: chunks.Count, high: (SdfIndirectCache.FinishFrameBound / 16), low: 1);
    }
    [Fact]
    public void ANarrowChunkCostsItsLongestLaneAndAWideOneItsVisits() {
        var units = (SdfIndirectCost.TraceUnits with { MeasuredFieldCost = 100 });
        var lane = units.CostOf(chunk: new SdfIndirectChunk(ItemFirst: 0, UnitCount: 1, UnitFirst: 0), instructionCount: ChainInstructions);

        // A lane alone takes as long as its whole chain, far longer than its visits at the device's throughput; lanes
        // beside it add nothing until their visits outlast it.
        Assert.True(condition: (lane > (64L * 100L)), userMessage: $"{lane}");
        Assert.Equal(expected: lane, actual: units.CostOf(chunk: new SdfIndirectChunk(ItemFirst: 0, UnitCount: 64, UnitFirst: 0), instructionCount: ChainInstructions));
        var wide = new SdfIndirectChunk(ItemFirst: 0, UnitCount: checked((int)(((lane / 100L) * 4L))), UnitFirst: 0);

        Assert.Equal(expected: (wide.UnitCount * 100L), actual: units.CostOf(chunk: wide, instructionCount: ChainInstructions));
        // A shading lane runs a probe's rays in turn, so its chain is that many rays of the same price.
        var layout = new SdfIndirectLayout(tier: SdfIndirectTier.High);
        var shade = (SdfIndirectCost.ShadeUnits(frame: CostFrame(shapes: 1), layout: layout) with { MeasuredFieldCost = 100 });
        var shaded = shade.CostOf(chunk: new SdfIndirectChunk(ItemFirst: 0, UnitCount: 1, UnitFirst: 0), instructionCount: ChainInstructions);

        Assert.Equal(expected: (lane * (layout.RaysPerProbe / IrradianceLattice.RaysPerStratum)), actual: shaded);
        Assert.Equal(expected: shaded, actual: shade.CostOf(chunk: new SdfIndirectChunk(ItemFirst: 0, UnitCount: layout.RaysPerProbe, UnitFirst: 0), instructionCount: ChainInstructions));
    }
    [Fact]
    public void AChunkWhoseLongestLaneOutlastsTheSliceFillsTheLanesBesideIt() {
        var layout = new SdfIndirectLayout(tier: SdfIndirectTier.High);
        var slice = SdfIndirectCost.FrameCost(layout: layout);
        var units = (SdfIndirectCost.TraceUnits with { MeasuredFieldCost = 10_000 });
        var lane = units.CostOf(chunk: new SdfIndirectChunk(ItemFirst: 0, UnitCount: 1, UnitFirst: 0), instructionCount: ChainInstructions);

        Assert.True(condition: (lane > slice), userMessage: "The fixture's single ray must outlast the slice.");
        var chunks = SdfIndirectCost.Admit(count: 512, instructionCount: ChainInstructions, limit: slice, units: units);

        // Every chunk but the last holds more visits than the slice, the rays that run beside its longest one, and no
        // chunk takes more device time than that one ray.
        Assert.All(collection: chunks.Take(count: (chunks.Count - 1)), action: chunk => Assert.True(condition: ((chunk.UnitCount * 10_000L) > slice), userMessage: $"{chunk}"));
        Assert.All(collection: chunks, action: chunk => Assert.Equal(expected: lane, actual: units.CostOf(chunk: chunk, instructionCount: ChainInstructions)));
        Assert.Equal(expected: (512 * IrradianceLattice.RaysPerStratum), actual: chunks.Sum(selector: chunk => chunk.UnitCount));
        // A light unit's chunk fills the slice with visits instead.
        var light = (SdfIndirectCost.TraceUnits with { MeasuredFieldCost = 10 });

        Assert.All(collection: SdfIndirectCost.Admit(count: 512, instructionCount: ChainInstructions, limit: slice, units: light),
            action: chunk => Assert.InRange(actual: light.CostOf(chunk: chunk, instructionCount: ChainInstructions), high: slice, low: 1));
    }
    [Fact]
    public async Task APartitionChunkDispatchesAWorkgroupPerCellAsync() {
        var source = Frame() with { FarDistance = 1f, IndirectTier = SdfIndirectTier.Medium };
        var pipelines = SdfTestPipelines.Cache();
        var gpu = new FakeGpuDevice();
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-partition-groups", pipelines: pipelines, width: Extent);
        var context = ContextOf(gpu: gpu);

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var cache = residency.Tables!.Indirect!;
        var views = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        using var factory = new SdfIndirectPasses(views: views);

        factory.Register(name: residency.IndirectInstanceName, residency: residency);
        var fragment = factory.FragmentOf(instance: residency.IndirectInstanceName)!;
        var pass = fragment.Passes.Single(predicate: candidate => (candidate.Name == SdfWorldPackage.IndirectClassify));
        var recorderContext = new RenderGraphPackageRecorderContext(Device: gpu, Services: gpu.Services,
            Instance: residency.IndirectInstanceName, Pass: SdfWorldPackage.IndirectClassify, Part: SdfWorldPackage.IndirectClassify,
            Package: RenderGraphPackageCatalog.Indirect, Pipelines: pipelines.Pipelines, HostsOnDirectX: false,
            InFlightFrames: 1, Width: 1, Height: 1, Parameters: SdfWorldInterfaces.IndirectParameters,
            Inputs: Declarations(ports: pass.Inputs), Outputs: Declarations(ports: pass.Outputs));
        var built = await factory.BuildAsync(context: recorderContext, cancellationToken: CancellationToken.None);
        using var block = gpu.Services.BufferFactory.CreateHostVisible(name: default,
            sizeBytes: recorderContext.Parameters.SizeBytes, usage: GpuBufferUsage.Uniform);
        var pool = gpu.Services.Bindings.CreatePool(name: default,
            sizes: GpuDescriptorPoolSizes.ForGroups(groups: recorderContext.Parameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute).Groups));
        var partitions = 0;

        try {
            using var recorder = factory.Create(context: recorderContext, built: built, groups: new RenderGraphPackageGroups(
                DescriptorPool: pool, FrameBlocks: [block], OutputImages: [], PassBlocks: [block], Regions: []));
            using var counters = new GpuKernelCounters(buffers: gpu.Services.BufferFactory, slots: 1, rows: 1,
                owner: "indirect-partition-groups", part: "counters");
            using var commands = gpu.Services.CommandPoolFactory.Create(name: default);
            var dispatches = new List<(uint X, uint Y, uint Z)>();

            TestLiveness.Within(frames: 256, step: () => {
                residency.BeginFrame();
                Assert.True(condition: residency.Prepare(context: context));
                for (var passIndex = 0; (passIndex < 4); passIndex++) { Assert.Same(residency.Tables, residency.Submit(context: context)); }
                if (cache.TransportChunk(part: SdfWorldPackage.IndirectClassify) is { } chunk) {
                    var recording = new RenderGraphPackageRecording(CommandBuffer: commands.CommandBufferHandle, Recorder: gpu.Services.Recorder,
                        Slot: 0, Width: 1, Height: 1, Inputs: Resources(declarations: recorderContext.Inputs), Outputs: Resources(declarations: recorderContext.Outputs),
                        PassBlock: new byte[recorderContext.Parameters.SizeBytes], Leases: new LeaseRetireList(), Context: context, MayStandIn: false,
                        WorkCounters: counters.RowOf(row: 0, slot: 0));

                    dispatches.Clear();
                    gpu.OnDispatch = (x, y, z) => dispatches.Add(item: (x, y, z));
                    Assert.Equal(expected: RenderGraphPackageOutcome.Drew, actual: recorder.Record(recording: in recording));
                    gpu.OnDispatch = null;
                    // A cell's corner pairs march on lanes of its own workgroup, so its chunk runs a workgroup per cell.
                    Assert.Equal(expected: (((uint)chunk.UnitCount), 1u, 1u), actual: Assert.Single(collection: dispatches));
                    partitions++;
                }
                cache.Submitted();
                return cache.TransportComplete;
            }, building: () => false, reason: () => "The transport schedule has not completed its partitions.");
        } finally {
            gpu.OnDispatch = null;
            gpu.Services.Bindings.DestroyPool(poolHandle: pool);
        }
        Assert.True(condition: (partitions > 0), userMessage: "The fixture's transport must partition at least one chunk of cells.");

        ShaderPipelineResource[] Declarations(IReadOnlyList<ResourceReference> ports) => [.. ports.Select(selector: port =>
            fragment.Resources.Single(predicate: resource => (resource.Name == port.Name)))];
        RenderGraphPackageResource[] Resources(IReadOnlyList<ShaderPipelineResource> declarations) => [.. declarations.Select(selector: resource =>
            new RenderGraphPackageResource(Version: resource.Name, Kind: resource.Kind, Image: default, Buffer: cache.Buffer, Owned: null))];
    }
}
