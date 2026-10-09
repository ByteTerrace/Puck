using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [InlineData(SdfIndirectTier.Medium)]
    [InlineData(SdfIndirectTier.High)]
    [Theory]
    public async Task AProducedFrameBoundsAllChunksIncludingTheObservedParityProofAllowanceAsync(SdfIndirectTier tier) {
        var result = await RunFrameBudgetSolve(tier, budgeted: true);
        var slice = SdfIndirectCost.FrameCost(layout: new SdfIndirectLayout(tier: tier));

        // The auxiliary allowance holds the receivers; the device allowance holds every shade chunk after a frame's first.
        // This fake device measures nothing, so the allowance stays one submission's cap rather than the tier's slice.
        Assert.All(result.FrameBudgets, frame => {
            Assert.InRange(actual: frame.Auxiliary, high: SdfIndirectFrameBudget.AuxiliaryLimit, low: 0);
            Assert.Contains(collection: new[] { slice, SdfIndirectCost.SubmissionCostLimit }, expected: frame.Limit);
            Assert.True(condition: ((frame.Device <= frame.Limit) || (frame.Chunks == 1)),
                userMessage: $"A frame of {frame.Chunks} chunks reserved {frame.Device} visits past its {frame.Limit}-visit slice.");
        });
        Assert.True(condition: (result.Frames > 1), userMessage: "A large solve must leave whole chunks for later produced frames.");
    }
    [Fact]
    public async Task ADeferredFiniteSolveCompletesOnlyAfterItsLastChunkAndFenceAsync() {
        var result = await RunFrameBudgetSolve(SdfIndirectTier.Medium, budgeted: true);

        Assert.Equal(result.Chunks.Count, result.Frames);
        Assert.Equal(3, result.Sweeps);
        Assert.True(condition: (result.Frames > 1), userMessage: "Readiness must remain incomplete across the finite solve's produced frames.");
    }
    [Fact]
    public async Task CompletedBudgetedAndOneFrameSolvesHaveIdenticalOrderedInputsAndBankResultsAsync() {
        var whole = await RunFrameBudgetSolve(SdfIndirectTier.Medium, budgeted: false);
        var amortized = await RunFrameBudgetSolve(SdfIndirectTier.Medium, budgeted: true);

        Assert.Equal(1, whole.Frames);
        Assert.True(condition: (amortized.Frames > 1), userMessage: "The comparison must actually spread the same finite solve across frames.");
        Assert.Equal(whole.Chunks.Count, amortized.Chunks.Count);
        for (var index = 0; (index < whole.Chunks.Count); index++) { Assert.Equal(whole.Chunks[index], amortized.Chunks[index]); }
        Assert.Equal(whole.Generation, amortized.Generation);
        Assert.Equal(whole.Publication, amortized.Publication);
        Assert.Equal(whole.Sweeps, amortized.Sweeps);
        var referenceWhole = SolveReference(budgeted: false);
        var referenceBudgeted = SolveReference(budgeted: true);

        Assert.Equal(actual: referenceWhole.Frames, expected: 1);
        Assert.True(condition: (referenceBudgeted.Frames > 1));
        Assert.Equal(actual: referenceBudgeted.Chunks, expected: referenceWhole.Chunks);
        Assert.Equal(actual: referenceBudgeted.Radiance, expected: referenceWhole.Radiance);
        Assert.Equal(actual: referenceBudgeted.Irradiance, expected: referenceWhole.Irradiance);
        Assert.Contains(collection: referenceWhole.Radiance, filter: value => (value is { X: > 0 }));
    }

    private static (int Frames, List<IrradianceProbeKey> Chunks, List<Double3?> Radiance, List<Double3?> Irradiance) SolveReference(bool budgeted) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.Translate(offset: new Vector3(value: 1.5f));
        builder.Box(halfExtents: new Vector3(x: 1.95f, y: 1.45f, z: 1.95f), material: material, round: 0);
        builder.ResetPoint();
        builder.Translate(offset: new Vector3(value: 1.5f));
        builder.Box(halfExtents: new Vector3(x: 1.75f, y: 1.25f, z: 1.75f), material: material, blend: SdfBlendOp.Subtraction, round: 0);
        var model = new IrradianceCacheModel(new IrradianceField(program: builder.Build()),
            new IrradianceSurfaces(albedo: static _ => new Double3(X: 0.6, Y: 0.5, Z: 0.4), emission: static _ => new Double3(X: 0.75, Y: 0.5, Z: 0.25)),
            [new IrradianceLevel(Name: "room", Radius: 0, Reach: 0, Spacing: 1, Strata: 2)], new IrradianceModelOptions(100));

        model.Allocate(0, Double3.Zero, Double3.Zero);
        model.Classify();
        model.Trace();
        var layout = new SdfIndirectLayout(tier: SdfIndirectTier.Medium);
        var source = CostFrame(shapes: 168);
        var probeBudget = SdfIndirectWork.ShadeProbeBudget(frame: source, layout: layout);

        model.BeginSolve(layout.BounceLimit, probesPerStep: probeBudget);
        var budget = new SdfIndirectFrameBudget();
        var chunks = new List<IrradianceProbeKey>();
        var probes = new HashSet<IrradianceProbeKey>();
        var frames = 0;

        while (!model.SolveComplete) {
            budget.BeginFrame();
            model.BeginFrame();
            frames++;
            Assert.True(condition: budget.TryAdmit(chunk: model, cost: SdfIndirectCost.EstimateCost(instructionCount: 168, queries: 399_440)));
            while (model.PlanSolve() is { } batch) {
                var cost = SdfIndirectCost.SubmissionCostLimit;

                if (budgeted && !budget.TryAdmitDevice(chunk: batch, cost: cost)) { break; }
                chunks.AddRange(collection: batch.Probes);
                probes.UnionWith(other: batch.Probes);
                model.SubmittedSolve();
            }
        }
        var radiance = probes.Order().SelectMany(selector: key => Enumerable.Range(0, layout.RaysPerProbe).Select(selector: ray => model.RadianceOf(key: key, ray: ray))).ToList();
        var irradiance = probes.Order().Select(selector: key => model.ProbeIrradianceOf(key: key, normal: new Double3(X: 0, Y: 1, Z: 0))).ToList();

        return (frames, chunks, radiance, irradiance);
    }

    private sealed record FrameBudgetSolve(int Frames, int Sweeps, int Generation, uint Publication, List<(long Device, long Auxiliary, long Limit, int Chunks)> FrameBudgets, List<byte[]> Chunks);

    private static async Task<FrameBudgetSolve> RunFrameBudgetSolve(SdfIndirectTier tier, bool budgeted) {
        var source = CostFrame(shapes: 168) with { IndirectTier = tier };

        Assert.Equal(168, source.Program.InstructionCount);
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "frame-budget", pipelines: pipelines, width: Extent);
        var context = ContextOf(gpu: gpu);

        TestLiveness.Until(() => { residency.BeginFrame(); return residency.Prepare(context: context); },
            () => residency.NotReadyReason, residency.WaitPipelineBuilds);
        var cache = residency.Tables!.Indirect!;

        TestLiveness.Within(128, () => { residency.BeginFrame(); residency.Submit(context: context); cache.Submitted(); return cache.IsComplete; },
            () => false, () => "Transport has not completed.");
        var views = new SdfWorldPasses(_ => new SdfWorldView(Residency: residency, View: 0));
        using var factory = new SdfIndirectPasses(views: views);

        factory.Register(name: residency.IndirectInstanceName, residency: residency);
        var fragment = factory.FragmentOf(instance: residency.IndirectInstanceName)!;
        var pass = fragment.Passes.Single(predicate: candidate => (candidate.Name == SdfWorldPackage.IndirectShade));
        var recorderContext = new RenderGraphPackageRecorderContext(Device: gpu, Services: gpu.Services,
            Instance: residency.IndirectInstanceName, Pass: SdfWorldPackage.IndirectShade, Part: SdfWorldPackage.IndirectShade,
            Package: RenderGraphPackageCatalog.Indirect, Pipelines: pipelines.Pipelines, HostsOnDirectX: false,
            InFlightFrames: 1, Width: 1, Height: 1, Parameters: SdfWorldInterfaces.IndirectParameters,
            Inputs: Declarations(ports: pass.Inputs), Outputs: Declarations(ports: pass.Outputs));
        var built = await factory.BuildAsync(recorderContext, CancellationToken.None);
        using var block = gpu.Services.BufferFactory.CreateHostVisible(name: default, sizeBytes: recorderContext.Parameters.SizeBytes, usage: GpuBufferUsage.Uniform);
        var pool = gpu.Services.Bindings.CreatePool(name: default,
            sizes: GpuDescriptorPoolSizes.ForGroups(recorderContext.Parameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute).Groups));

        try {
            using var recorder = factory.Create(recorderContext, built, new RenderGraphPackageGroups(DescriptorPool: pool, FrameBlocks: [block], OutputImages: [], PassBlocks: [block], Regions: []));
            using var counters = new GpuKernelCounters(gpu.Services.BufferFactory, 1, 1, "frame-budget", "counters");
            using var commands = gpu.Services.CommandPoolFactory.Create(name: default);
            var budgets = new List<(long Device, long Auxiliary, long Limit, int Chunks)>();
            var chunks = new List<byte[]>();
            var frames = 0;
            var receiver = new object();
            var frameBound = IndirectCompletionFrames(cache: cache, source: source);

            TestLiveness.Within(frameBound, () => {
                residency.BeginFrame();
                residency.Submit(context: context);
                frames++;
                _ = recorder.Skips(context: context);
                var cost = SdfIndirectCost.EstimateCost(instructionCount: 168, queries: 399_440);

                Assert.True(condition: residency.IndirectFrameBudget.TryAdmit(chunk: receiver, cost: cost));
                var drawn = 0;

                while (!cache.LightingComplete) {
                    var skips = recorder.Skips(context: context);

                    if (budgeted && skips) { break; }
                    Assert.False(condition: residency.IsIndirectReady);
                    var count = cache.ShadeCount;

                    Assert.True(condition: (count > 0));
                    var bytes = new byte[recorderContext.Parameters.SizeBytes];
                    var recording = new RenderGraphPackageRecording(commands.CommandBufferHandle, gpu.Services.Recorder, 0, 1, 1,
                        Resources(declarations: recorderContext.Inputs), Resources(declarations: recorderContext.Outputs), bytes, new LeaseRetireList(), context,
                        MayStandIn: false, WorkCounters: counters.RowOf(row: 0, slot: 0));

                    Assert.Equal(RenderGraphPackageOutcome.Drew, recorder.Record(recording: recording));
                    var chunk = new byte[(16 + (count * 16))];

                    foreach (var (member, offset) in new[] { (SdfWorldPackage.IndirectReadGeneration, 0), (SdfWorldPackage.IndirectWriteGeneration, 4),
                        (SdfWorldPackage.IndirectFeedback, 8), (SdfWorldPackage.IndirectWritePublication, 12) }) {
                        bytes.AsSpan(((int)recorderContext.Parameters.BlockOffsetOf(member: member)), 4).CopyTo(destination: chunk.AsSpan(start: offset));
                    }
                    cache.Regions[4].Contents[..(count * 16)].CopyTo(destination: chunk.AsSpan(start: 16));
                    chunks.Add(item: chunk);
                    drawn++;
                    recorder.Submitted();
                }
                budgets.Add(item: (residency.IndirectFrameBudget.DeviceCost, residency.IndirectFrameBudget.AuxiliaryCost, residency.IndirectFrameBudget.DeviceLimit, drawn));
                Assert.False(condition: residency.IsIndirectReady);
                return cache.LightingComplete;
            }, () => false, () => "The deferred finite solve must complete after a bounded number of produced frames.");
            Assert.Equal((cache.Layout.BounceLimit + 1), cache.CompletedSweeps);
            return new(frames, cache.CompletedSweeps, cache.PublishedGeneration, cache.PublishedStamp, budgets, chunks);
        } finally { gpu.Services.Bindings.DestroyPool(poolHandle: pool); }

        ShaderPipelineResource[] Declarations(IReadOnlyList<ResourceReference> ports) => [.. ports.Select(selector: port => fragment.Resources.Single(predicate: resource => (resource.Name == port.Name)))];
        RenderGraphPackageResource[] Resources(IReadOnlyList<ShaderPipelineResource> declarations) => [.. declarations.Select(selector: resource =>
            new RenderGraphPackageResource(resource.Name, resource.Kind, default, cache.Buffer, null))];
    }
}
