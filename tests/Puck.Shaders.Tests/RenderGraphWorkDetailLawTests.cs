using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>Prepared detail metadata shares the node's waited counter slots, budget and completed ledger.</summary>
public sealed class RenderGraphWorkDetailLawTests {
    private const string Package = "test.details";

    private static ShaderPipelineRenderNode Node(FakePipelineGpu gpu, Model model) {
        var catalog = new RenderGraphPackageCatalog(packages: [new RenderGraphPackage(Id: Package,
            Inputs: [], Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
            Members: ShaderWorkCounters.Members, Summary: "Counts named work.")]);
        var recorders = new RenderGraphPackageRecorders();

        recorders.Register(factory: model, package: Package);
        var node = new ShaderPipelineRenderNode("details", gpu, new GpuPassPipelineCache(), false, 32, 32, packages: recorders);
        var graph = new RenderGraphDefinition(Schema: RenderGraphSchemas.Graph, Name: "details", Outputs: ["out"],
            Resources: [new ShaderPipelineResource(Name: "out", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative())],
            Packages: [new RenderGraphPackagePass(Name: "sky", Package: Package, Outputs: ["out"])]);

        node.Swap(pipeline: new CompiledShaderPipeline(plan: new RenderGraphCompiler(catalog).Compile(definition: graph).Pipeline,
            shaders: new Dictionary<string, CompiledShader>()));
        return node;
    }

    [Fact]
    public void AWaitedSlotGrowsBeforeItsRecordingAndKeepsSubmittedNames() {
        var gpu = new FakePipelineGpu { QueueHeld = true, Recording = true };
        var model = new Model { Names = ["stars"] };
        using var node = Node(gpu: gpu, model: model);

        node.ProduceUntilInstalled();
        node.ProduceFrame(context: default); node.ProduceFrame(context: default);
        var previous = model.Buffers.ToArray();
        var bytes = node.OwnedBytes;

        gpu.Events.Clear();
        model.Names = ["clouds", "aurora", "panorama"];
        node.ProduceFrame(context: default);
        var slot = model.LastSlot;

        Assert.NotEqual(previous[slot], model.Buffers[slot]);
        for (var other = 0; (other < 3); other++) { if (other != slot) { Assert.Equal(previous[other], model.Buffers[other]); } }
        Assert.Equal(actual: model.FirstRow, expected: 1U);
        Assert.Equal(actual: model.DetailCount, expected: 3);
        Assert.Equal((bytes + 160UL), node.OwnedBytes);
        Assert.Equal(node.OwnedBytes, node.InstalledAccount.SteadyBytes);
        var wait = gpu.Events.FindIndex(match: static item => item.StartsWith(comparisonType: StringComparison.Ordinal, value: "wait fence"));
        var create = gpu.Events.FindIndex(match: static item => item.StartsWith(comparisonType: StringComparison.Ordinal, value: "create buffer"));

        Assert.True(condition: ((wait >= 0) && (create > wait)), userMessage: string.Join(separator: "\n", values: gpu.Events));
        var sample = new GpuWorkSample();

        Assert.True(condition: node.TryReadCompleted(sample: sample));
        Assert.Equal(1, sample.PassCount);
        Assert.Equal("stars", Assert.Single(collection: sample.Details.ToArray()).Detail);
        model.Names = ["moon"];
        node.ProduceFrame(context: default);
        Assert.Equal("stars", sample.Details[0].Detail);
    }
    [Fact]
    public void DetailGrowthRefusesItsPeakBeforeCreatingAndRetriesAtTheExactBudget() {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu: gpu, model: model);

        node.ProduceUntilInstalled();
        var bytes = node.OwnedBytes;
        var creations = gpu.CreationCount;
        var replacement = ((4UL * 40) * 2);

        model.Names = ["stars", "clouds", "aurora"];
        node.BudgetCapBytes = ((bytes + replacement) - 1);
        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceFrame(context: default));

        Assert.Contains("details", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("budget", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(creations, gpu.CreationCount);
        Assert.Equal(bytes, node.OwnedBytes);
        node.BudgetCapBytes = (bytes + replacement);
        gpu.ResetPeakBytes();
        node.ProduceFrame(context: default);
        Assert.Equal((bytes + replacement), gpu.PeakLiveBytes);
        Assert.Equal(((bytes + replacement) - 80), node.OwnedBytes);
        Assert.Equal(node.OwnedBytes, node.InstalledAccount.SteadyBytes);
    }
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void AFailedPairAllocationLeavesTheOldSlotOwnedAndRetryable(int failedCreation) {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu: gpu, model: model);

        node.ProduceUntilInstalled();
        var bytes = node.OwnedBytes;
        var live = gpu.LiveBytes;

        model.Names = ["stars", "clouds"];
        gpu.FailAtCreation = (gpu.CreationCount + failedCreation);
        Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceFrame(context: default));
        Assert.Equal(bytes, node.OwnedBytes);
        Assert.Equal(live, gpu.LiveBytes);
        node.ProduceFrame(context: default);
        Assert.Equal((bytes + 160UL), node.OwnedBytes);
        node.Dispose();
        Assert.All(gpu.CreatedObjects, item => Assert.Equal(1, item.DisposeCount));
    }
    [Fact]
    public void RetainedDetailCapacityAndMetadataAllocateNothingAfterWarmup() {
        var gpu = new FakePipelineGpu();
        var model = new Model { Names = ["stars", "clouds"] };
        using var node = Node(gpu: gpu, model: model);

        node.ProduceUntilInstalled();
        for (var frame = 0; (frame < 30); frame++) { node.ProduceFrame(context: default); }
        var creations = gpu.CreationCount;
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var frame = 0; (frame < 100); frame++) { node.ProduceFrame(context: default); }
        Assert.Equal(0, (GC.GetAllocatedBytesForCurrentThread() - before));
        Assert.Equal(creations, gpu.CreationCount);
        var cpu = node.KernelCounterCpuBytes;

        Assert.True(condition: (cpu > 0));
        model.Names = [];
        for (var frame = 0; (frame < 5); frame++) { node.ProduceFrame(context: default); }
        Assert.Equal(creations, gpu.CreationCount);
        var sample = new GpuWorkSample();

        Assert.True(condition: node.TryReadCompleted(sample: sample));
        Assert.Empty(collection: sample.Details.ToArray());
    }

    private sealed class Model : IRenderGraphPackageFactory {
        public string[] Names = [];
        public readonly nint[] Buffers = new nint[3];

        public int LastSlot;
        public uint FirstRow;
        public int DetailCount;

        public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => null;
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(model: this);

        private sealed class Recorder(Model model) : IRenderGraphPackageRecorder {
            public IReadOnlyList<string> WorkDetails(in FrameContext context) => model.Names;
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                model.LastSlot = recording.Slot;
                model.Buffers[recording.Slot] = recording.WorkCounters!.Value.Buffer.BufferHandle;
                model.FirstRow = recording.WorkDetailRow;
                model.DetailCount = recording.WorkDetailCount;
                return RenderGraphPackageOutcome.Drew;
            }
            public void Dispose() { }
        }
    }
}
