using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    // Keep the existing transport, graph installation and readback allowance. Add the actual finite lighting
    // submissions: a batch cannot cross a level, and every requested sweep visits every allocated probe.
    private static int IndirectCompletionFrames(SdfIndirectCache cache, SdfFrame source) {
        var budget = SdfIndirectWork.ShadeProbeBudget(layout: cache.Layout, frame: source);
        var batches = cache.Snapshot().Bricks.GroupBy(keySelector: brick => brick.Key.Level).Sum(selector: level =>
            ((((level.Count() * SdfIndirectLayout.ProbesPerBrick) + budget) - 1) / budget));
        var bounces = ((((source.IndirectSources & SdfIndirectSources.Feedback) != 0) && (source.IndirectGains.Feedback != 0f))
            ? Math.Min(val1: (source.IndirectBounces ?? cache.Layout.BounceLimit), val2: cache.Layout.BounceLimit) : 0);

        return checked((64 + (Math.Max(val1: 1, val2: batches) * (bounces + 1))));
    }

    [InlineData(SdfIndirectTier.Medium, true)]
    [InlineData(SdfIndirectTier.Medium, false)]
    [InlineData(SdfIndirectTier.High, true)]
    [InlineData(SdfIndirectTier.High, false)]
    [Theory]
    public async Task PinnedDirectionalSourcesBoundEveryRecordedShadeDispatchAsync(SdfIndirectTier tier, bool direct) {
        var source = Frame() with { FarDistance = 1f, IndirectTier = tier, IndirectSources = SdfIndirectSources.Emission | SdfIndirectSources.Feedback | (direct ? SdfIndirectSources.Direct : 0) };
        var pipelines = SdfTestPipelines.Cache();
        var fenceWaits = 0;
        var gpu = new FakeGpuDevice {
            OnCall = call => { if (call == "IGpuSubmissionFence.Wait") { Interlocked.Increment(location: ref fenceWaits); } },
        };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-shade-budget", pipelines: pipelines, width: Extent);
        var context = ContextOf(gpu: gpu);

        TestLiveness.Until(step: () => { residency.BeginFrame(); return residency.Prepare(context: context); },
            reason: () => residency.NotReadyReason, wait: residency.WaitPipelineBuilds);
        var cache = residency.Tables!.Indirect!;
        var fallbackBudget = SdfIndirectWork.ShadeProbeBudget(layout: cache.Layout, lights: source.Lights);
        var expectedBudget = SdfIndirectWork.ShadeProbeBudget(layout: cache.Layout, frame: source);
        var views = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: residency, View: 0));
        using var factory = new SdfIndirectPasses(views: views);

        factory.Register(name: residency.IndirectInstanceName, residency: residency);
        var fragment = factory.FragmentOf(instance: residency.IndirectInstanceName)!;
        var pass = fragment.Passes.Single(predicate: candidate => (candidate.Name == SdfWorldPackage.IndirectShade));
        var recorderContext = new RenderGraphPackageRecorderContext(Device: gpu, Services: gpu.Services,
            Instance: residency.IndirectInstanceName, Pass: SdfWorldPackage.IndirectShade, Part: SdfWorldPackage.IndirectShade,
            Package: RenderGraphPackageCatalog.Indirect, Pipelines: pipelines.Pipelines, HostsOnDirectX: false,
            InFlightFrames: 1, Width: 1, Height: 1, Parameters: SdfWorldInterfaces.IndirectParameters,
            Inputs: Declarations(ports: pass.Inputs), Outputs: Declarations(ports: pass.Outputs));
        var built = await factory.BuildAsync(context: recorderContext, cancellationToken: CancellationToken.None);
        using var block = gpu.Services.BufferFactory.CreateHostVisible(name: default,
            sizeBytes: recorderContext.Parameters.SizeBytes, usage: GpuBufferUsage.Uniform);
        var pool = gpu.Services.Bindings.CreatePool(name: default,
            sizes: GpuDescriptorPoolSizes.ForGroups(groups: recorderContext.Parameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute).Groups));
        var batches = 0;
        var largestBatch = 0u;

        try {
            using var recorder = factory.Create(context: recorderContext, built: built, groups: new RenderGraphPackageGroups(
                DescriptorPool: pool, FrameBlocks: [block], OutputImages: [], PassBlocks: [block], Regions: []));
            using var counters = new GpuKernelCounters(buffers: gpu.Services.BufferFactory, slots: 1, rows: 1,
                owner: "indirect-shade-budget", part: "counters");
            using var commands = gpu.Services.CommandPoolFactory.Create(name: default);
            var dispatches = new List<(uint X, uint Y, uint Z)>();

            TestLiveness.Within(frames: 128, step: () => {
                var submissions = gpu.Submissions;
                var waits = Volatile.Read(location: ref fenceWaits);

                residency.BeginFrame();
                Assert.True(condition: residency.Prepare(context: context));
                for (var passIndex = 0; (passIndex < 4); passIndex++) { Assert.Same(residency.Tables, residency.Submit(context: context)); }
                if (cache.ShadeCount > 0) {
                    batches++;
                    var bytes = new byte[recorderContext.Parameters.SizeBytes];
                    var recording = new RenderGraphPackageRecording(CommandBuffer: commands.CommandBufferHandle, Recorder: gpu.Services.Recorder,
                        Slot: 0, Width: 1, Height: 1, Inputs: Resources(declarations: recorderContext.Inputs), Outputs: Resources(declarations: recorderContext.Outputs),
                        PassBlock: bytes, Leases: new LeaseRetireList(), Context: context, MayStandIn: false, WorkCounters: counters.RowOf(row: 0, slot: 0));

                    dispatches.Clear();
                    gpu.OnDispatch = (x, y, z) => dispatches.Add(item: (x, y, z));
                    Assert.Equal(expected: RenderGraphPackageOutcome.Drew, actual: recorder.Record(recording: in recording));
                    gpu.OnDispatch = null;
                    var dispatched = Assert.Single(collection: dispatches);
                    var cost = (SdfIndirectCost.EstimateCost((((long)dispatched.X) * SdfIndirectCost.ShadeQueries(cache.Layout, source)), source.Program.InstructionCount)
                        + (dispatched.X * SdfIndirectCost.ShadeCacheCost(layout: cache.Layout)));

                    Assert.InRange(actual: cost, high: SdfIndirectCost.SubmissionCostLimit, low: 1);

                    largestBatch = Math.Max(val1: largestBatch, val2: dispatched.X);

                    Assert.Equal(expected: (((uint)cache.ShadeCount), 1u, 1u), actual: dispatched);
                    Assert.InRange(actual: dispatched.X, high: ((uint)expectedBudget), low: 1u);
                    var countOffset = ((int)recorderContext.Parameters.BlockOffsetOf(member: SdfWorldPackage.IndirectShadeCount));

                    Assert.Equal(expected: dispatched.X, actual: BinaryPrimitives.ReadUInt32LittleEndian(source: bytes.AsSpan(start: countOffset)));
                }
                Assert.Equal(expected: 1, actual: (gpu.Submissions - submissions));
                Assert.InRange(actual: (Volatile.Read(location: ref fenceWaits) - waits), low: 0, high: 1);
                cache.Submitted();
                recorder.Submitted();
                return (cache.IsComplete && cache.LightingComplete);
            }, building: () => false, reason: () => "The bounded shade schedule has not completed its finite sweeps.");
        } finally {
            gpu.OnDispatch = null;
            gpu.Services.Bindings.DestroyPool(poolHandle: pool);
        }

        if (!direct) { Assert.True(condition: (largestBatch > fallbackBudget)); }
        Assert.True(condition: (batches > cache.CompletedSweeps));
        Assert.Equal(expected: (cache.Layout.BounceLimit + 1), actual: cache.CompletedSweeps);
        Assert.NotNull(@object: cache.PublishedLightingSource);

        ShaderPipelineResource[] Declarations(IReadOnlyList<ResourceReference> ports) => [.. ports.Select(selector: port =>
            fragment.Resources.Single(predicate: resource => (resource.Name == port.Name)))];
        RenderGraphPackageResource[] Resources(IReadOnlyList<ShaderPipelineResource> declarations) => [.. declarations.Select(selector: resource =>
            new RenderGraphPackageResource(Version: resource.Name, Kind: resource.Kind, Image: default, Buffer: cache.Buffer, Owned: null))];
    }
}
