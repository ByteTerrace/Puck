using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>Prepared detail metadata shares the node's waited counter slots, budget and completed ledger.</summary>
public sealed class RenderGraphWorkDetailLawTests {
    private const string Package = "test.details";
    private static ShaderPipelineRenderNode Node(FakePipelineGpu gpu, Model model) {
        var catalog = new RenderGraphPackageCatalog(packages: [new RenderGraphPackage(Id: Package,
            Inputs: [], Outputs: [RenderGraphPackagePort.Image(RenderGraphPortAccess.ComputeWrite)],
            Members: ShaderWorkCounters.Members, Summary: "Counts named work.")]);
        var recorders = new RenderGraphPackageRecorders();
        recorders.Register(Package, model);
        var node = new ShaderPipelineRenderNode("details", gpu, new GpuPassPipelineCache(), false, 32, 32, packages: recorders);
        var graph = new RenderGraphDefinition(Schema: RenderGraphSchemas.Graph, Name: "details", Outputs: ["out"],
            Resources: [new ShaderPipelineResource(Name: "out", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative())],
            Packages: [new RenderGraphPackagePass(Name: "sky", Package: Package, Outputs: ["out"])]);
        node.Swap(new CompiledShaderPipeline(new RenderGraphCompiler(catalog).Compile(graph).Pipeline,
            new Dictionary<string, CompiledShader>()));
        return node;
    }
    [Fact]
    public void AWaitedSlotGrowsBeforeItsRecordingAndKeepsSubmittedNames() {
        var gpu = new FakePipelineGpu { QueueHeld = true, Recording = true };
        var model = new Model { Names = ["stars"] };
        using var node = Node(gpu, model);
        node.ProduceUntilInstalled();
        node.ProduceFrame(default); node.ProduceFrame(default);
        var previous = model.Buffers.ToArray();
        var bytes = node.OwnedBytes;
        gpu.Events.Clear();
        model.Names = ["clouds", "aurora", "panorama"];
        node.ProduceFrame(default);
        var slot = model.LastSlot;
        Assert.NotEqual(previous[slot], model.Buffers[slot]);
        for (var other = 0; other < 3; other++) { if (other != slot) { Assert.Equal(previous[other], model.Buffers[other]); } }
        Assert.Equal(1U, model.FirstRow);
        Assert.Equal(3, model.DetailCount);
        Assert.Equal(bytes + 160UL, node.OwnedBytes);
        Assert.Equal(node.OwnedBytes, node.InstalledAccount.SteadyBytes);
        var wait = gpu.Events.FindIndex(static item => item.StartsWith("wait fence", StringComparison.Ordinal));
        var create = gpu.Events.FindIndex(static item => item.StartsWith("create buffer", StringComparison.Ordinal));
        Assert.True(wait >= 0 && create > wait, string.Join("\n", gpu.Events));
        var sample = new GpuWorkSample();
        Assert.True(node.TryReadCompleted(sample));
        Assert.Equal(1, sample.PassCount);
        Assert.Equal("stars", Assert.Single(sample.Details.ToArray()).Detail);
        model.Names = ["moon"];
        node.ProduceFrame(default);
        Assert.Equal("stars", sample.Details[0].Detail);
    }
    [Fact]
    public void DetailGrowthRefusesItsPeakBeforeCreatingAndRetriesAtTheExactBudget() {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model);
        node.ProduceUntilInstalled();
        var bytes = node.OwnedBytes;
        var creations = gpu.CreationCount;
        var replacement = 4UL * 40 * 2;
        model.Names = ["stars", "clouds", "aurora"];
        node.BudgetCapBytes = bytes + replacement - 1;
        var refusal = Assert.Throws<InvalidOperationException>(() => node.ProduceFrame(default));
        Assert.Contains("details", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("budget", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(creations, gpu.CreationCount);
        Assert.Equal(bytes, node.OwnedBytes);
        node.BudgetCapBytes = bytes + replacement;
        gpu.ResetPeakBytes();
        node.ProduceFrame(default);
        Assert.Equal(bytes + replacement, gpu.PeakLiveBytes);
        Assert.Equal(bytes + replacement - 80, node.OwnedBytes);
        Assert.Equal(node.OwnedBytes, node.InstalledAccount.SteadyBytes);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void AFailedPairAllocationLeavesTheOldSlotOwnedAndRetryable(int failedCreation) {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model);
        node.ProduceUntilInstalled();
        var bytes = node.OwnedBytes;
        var live = gpu.LiveBytes;
        model.Names = ["stars", "clouds"];
        gpu.FailAtCreation = gpu.CreationCount + failedCreation;
        Assert.Throws<InvalidOperationException>(() => node.ProduceFrame(default));
        Assert.Equal(bytes, node.OwnedBytes);
        Assert.Equal(live, gpu.LiveBytes);
        node.ProduceFrame(default);
        Assert.Equal(bytes + 160UL, node.OwnedBytes);
        node.Dispose();
        Assert.All(gpu.CreatedObjects, item => Assert.Equal(1, item.DisposeCount));
    }
    [Fact]
    public void RetainedDetailCapacityAndMetadataAllocateNothingAfterWarmup() {
        var gpu = new FakePipelineGpu();
        var model = new Model { Names = ["stars", "clouds"] };
        using var node = Node(gpu, model);
        node.ProduceUntilInstalled();
        for (var frame = 0; frame < 30; frame++) { node.ProduceFrame(default); }
        var creations = gpu.CreationCount;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 100; frame++) { node.ProduceFrame(default); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(creations, gpu.CreationCount);
        var cpu = node.KernelCounterCpuBytes;
        Assert.True(cpu > 0);
        model.Names = [];
        for (var frame = 0; frame < 5; frame++) { node.ProduceFrame(default); }
        Assert.Equal(creations, gpu.CreationCount);
        var sample = new GpuWorkSample();
        Assert.True(node.TryReadCompleted(sample));
        Assert.Empty(sample.Details.ToArray());
    }
    private sealed class Model : IRenderGraphPackageFactory {
        public string[] Names = [];
        public readonly nint[] Buffers = new nint[3];
        public int LastSlot;
        public uint FirstRow;
        public int DetailCount;
        public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => null;
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(this);
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
