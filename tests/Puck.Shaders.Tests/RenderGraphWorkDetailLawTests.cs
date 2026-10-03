using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>Named counter rows follow completed frame slots and the node's memory account.</summary>
public sealed class RenderGraphWorkDetailLawTests {
    private const string Package = "test.details";

    [Fact]
    public void DetailGrowthWaitsForTheSlotAndRetainsSubmittedIdentities() {
        var gpu = new FakePipelineGpu { QueueHeld = true, Recording = true };
        var factory = new DetailsFactory { Labels = ["stars"] };
        using var node = Node(factory: factory, gpu: gpu);

        node.ProduceUntilInstalled();
        node.ProduceFrame(context: default);
        node.ProduceFrame(context: default);
        var previous = factory.Buffers.ToArray();
        var bytes = node.OwnedBytes;

        gpu.Events.Clear();
        factory.Labels = ["stars", "clouds", "aurora"];
        node.ProduceFrame(context: default);
        var slot = factory.Slot;

        Assert.NotEqual(expected: previous[slot], actual: factory.Buffers[slot]);
        for (var index = 0; (index < previous.Length); index++) {
            if (index != slot) { Assert.Equal(expected: previous[index], actual: factory.Buffers[index]); }
        }
        Assert.Equal(actual: factory.FirstRow, expected: 2u);
        Assert.Equal(expected: (bytes + ((ulong)(4 * GpuKernelCounters.RowBytes))), actual: node.OwnedBytes);
        Assert.Equal(expected: node.OwnedBytes, actual: node.InstalledAccount.SteadyBytes);
        var wait = gpu.Events.FindIndex(match: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "wait fence"));
        var create = gpu.Events.FindIndex(match: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "create buffer"));

        Assert.True(condition: ((wait >= 0) && (create > wait)), userMessage: string.Join(separator: "\n", values: gpu.Events));
        var sample = new GpuWorkSample();

        Assert.True(condition: node.TryReadCompleted(sample: sample));
        Assert.Equal(expected: new[] { "plain", "stars" }, actual: sample.Details.ToArray().Select(selector: static detail => detail.Detail));
    }
    [Fact]
    public void DetailGrowthRefusesItsPeakAndRetriesWithoutLosingTheOldBuffers() {
        var gpu = new FakePipelineGpu();
        var factory = new DetailsFactory();
        using var node = Node(factory: factory, gpu: gpu);

        node.ProduceUntilInstalled();
        var bytes = node.OwnedBytes;
        var creations = gpu.CreationCount;

        factory.Labels = ["stars", "clouds"];
        var replacement = ((ulong)(8 * GpuKernelCounters.RowBytes));

        node.BudgetCapBytes = ((bytes + replacement) - 1);
        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceFrame(context: default));

        Assert.Contains(expectedSubstring: "kernel work details", actualString: refusal.Message);
        Assert.Equal(expected: creations, actual: gpu.CreationCount);
        Assert.Equal(expected: bytes, actual: node.OwnedBytes);
        node.BudgetCapBytes = (bytes + replacement);
        node.ProduceFrame(context: default);
        Assert.Equal(expected: (bytes + ((ulong)(6 * GpuKernelCounters.RowBytes))), actual: node.OwnedBytes);
        Assert.Equal(expected: node.OwnedBytes, actual: node.InstalledAccount.SteadyBytes);
    }

    private static ShaderPipelineRenderNode Node(FakePipelineGpu gpu, DetailsFactory factory) {
        var catalog = new RenderGraphPackageCatalog(packages: [new RenderGraphPackage(Id: Package, Inputs: [],
            Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
            Members: ShaderWorkCounters.Members, Summary: "Counts named work.")]);
        var recorders = new RenderGraphPackageRecorders();

        recorders.Register(factory: factory, package: Package);
        var graph = new RenderGraphDefinition(Schema: RenderGraphSchemas.Graph, Name: "details", Outputs: ["out"],
            Resources: [new ShaderPipelineResource(Name: "out", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative())],
            Packages: [new RenderGraphPackagePass(Name: "sky", Package: Package, Outputs: ["out"])]);
        var node = new ShaderPipelineRenderNode(name: "details", deviceContext: gpu, pipelines: new GpuPassPipelineCache(),
            hostsOnDirectX: false, width: 32, height: 32, packages: recorders);

        node.Swap(pipeline: new CompiledShaderPipeline(plan: new RenderGraphCompiler(packages: catalog).Compile(definition: graph).Pipeline,
            shaders: new Dictionary<string, CompiledShader>()));
        return node;
    }

    private sealed class DetailsFactory : IRenderGraphPackageFactory {
        public string[] Labels = [];
        public readonly nint[] Buffers = new nint[3];

        public int Slot;
        public uint FirstRow;

        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(owner: this);

        private sealed class Recorder(DetailsFactory owner) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public IReadOnlyList<string> WorkDetails(in FrameContext context) => owner.Labels;
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                owner.Slot = recording.Slot;
                owner.FirstRow = recording.WorkDetailRow;
                owner.Buffers[recording.Slot] = recording.WorkCounters!.Value.Buffer.BufferHandle;
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
