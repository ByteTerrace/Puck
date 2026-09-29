using System.Text.Json;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>Retained intermediates preserve their last queued write across flight slots. Explicit disjoint forwarding
/// preserves predecessor identities; ordinary forwarding invalidates them. All barriers still come from the plan.</summary>
public sealed partial class RenderGraphCadenceLawTests {
    private const string Writer = "test.retained-writer";
    private const string Shade = "test.retained-shade";
    private const string Composite = "test.retained-composite";

    private static readonly IReadOnlyDictionary<string, ShaderConfigField> Config = new Dictionary<string, ShaderConfigField> {
        ["gain"] = new(Default: JsonDocument.Parse("1").RootElement, Type: ShaderValueType.Float),
    };

    private static RenderGraphPackageCatalog Catalog(bool input = false) => new(packages: [
        new RenderGraphPackage(Id: Writer, Config: Config, Inputs: (input ? [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeRead, strideBytes: null, count: null)] : []), Outputs: [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeWrite, strideBytes: null, count: null)], Members: [], Summary: "Writes the first field."),
        new RenderGraphPackage(Id: Shade, Inputs: [], Outputs: [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeWrite, strideBytes: null, count: null)], Members: [], Summary: "Writes the second field."),
        new RenderGraphPackage(Id: Composite, Inputs: [RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeRead, strideBytes: null, count: null)], Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)], Members: [], Summary: "Reads both retained fields.")
    ]);
    private static RenderGraphDefinition Graph(bool preserve = true) => new(
        Schema: RenderGraphSchemas.Graph, Name: "retained", Outputs: ["out"],
        Resources: [
            new ShaderPipelineResource(Name: "a", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 16, Retained: true),
            new ShaderPipelineResource(Name: "b", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 16, From: "a", PreservesPredecessor: preserve),
            new ShaderPipelineResource(Name: "out", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative())
        ],
        Packages: [
            new RenderGraphPackagePass(Name: "write", Package: Writer, Outputs: ["a"]),
            new RenderGraphPackagePass(Name: "shade", Package: Shade, Outputs: ["b"]),
            new RenderGraphPackagePass(Name: "composite", Package: Composite, Inputs: ["b"], Outputs: ["out"])
        ]);
    private static ShaderPipelineRenderNode Node(FakePipelineGpu gpu, Model model, RenderGraphDefinition? graph = null, ShaderPipelineResource? input = null, bool previous = false) {
        var packages = new RenderGraphPackageRecorders(regionCopy: new GpuRegionCopyPass(pipelines: new GpuPassPipelineCache(), kernel: new byte[] { UploadModelGpu.RegionCopyBytecode }));

        foreach (var id in new[] { Writer, Shade, Composite }) { packages.Register(factory: model, package: id); }
        var node = new ShaderPipelineRenderNode(deviceContext: gpu, width: 32, height: 32, hostsOnDirectX: false,
            name: "retained", packages: packages, outputLayout: GpuImageLayout.ShaderReadOnly, pipelines: new GpuPassPipelineCache());

        graph ??= Graph();
        if (input is not null) {
            graph = graph with {
                Resources = [.. graph.Resources, input],
                Packages = graph.Packages!.Select(selector: pass => ((pass.Name == "write") ? pass with { Inputs = [new ResourceReference(input.Name, PreviousFrame: previous)] } : pass)).ToArray(),
            };
        }
        node.Swap(pipeline: new CompiledShaderPipeline(plan: new RenderGraphCompiler(Catalog(input: (input is not null))).Compile(definition: graph).Pipeline,
            shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)));
        return node;
    }

    [Fact]
    public void AStandingIntermediateKeepsItsLastWriteBeyondEveryFlightSlot() {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model);

        node.ProduceUntilInstalled();
        for (var frame = 0; (frame < 10); frame++) { node.ProduceFrame(context: default); }
        Assert.Equal(actual: model.Writes, expected: 1);
        Assert.Equal(actual: model.Shades, expected: 1);
        Assert.Equal(11, model.Samples.Count);
        Assert.All(model.Samples, sample => Assert.Equal(actual: sample, expected: (10, 20)));
        Assert.Single(collection: model.Contents);
        Assert.Single(collection: gpu.CreatedObjects, predicate: item => ((item.Kind == "buffer") && (item.Bytes == 16)));
        var work = new GpuWorkSample();

        Assert.True(condition: node.TryReadCompleted(sample: work));
        Assert.Equal([GpuPassState.Standing, GpuPassState.Standing, GpuPassState.Executed], Enumerable.Range(count: 3, start: 0).Select(selector: work.GetPassState));
        Assert.False(condition: work.TryGetPassCount(column: 0, pass: 0, value: out _));
    }
    [Fact]
    public void UpstreamAndDisjointChangesInvalidateExactlyTheirDependentVersions() {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model);

        node.ProduceUntilInstalled();
        model.ShadeSignature = 2; model.ShadeValue = 40;
        node.ProduceFrame(context: default);
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (1, 2));
        Assert.Equal((10, 40), model.Samples[^1]);
        model.WriterSignature = 2; model.WriterValue = 30;
        node.ProduceFrame(context: default);
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (2, 3));
        Assert.Equal((30, 40), model.Samples[^1]);
        node.ProduceFrame(context: default);
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (2, 3));
    }
    [Fact]
    public void AnOrdinaryForwardingWriteCannotLeaveItsPredecessorReusable() {
        var model = new Model();
        using var node = Node(new FakePipelineGpu(), model, Graph(preserve: false));

        node.ProduceUntilInstalled();
        for (var frame = 0; (frame < 6); frame++) { node.ProduceFrame(context: default); }
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (7, 7));
    }
    [Fact]
    public void AForcedSignatureAndAnInactivePassAreDistinctFromStanding() {
        var model = new Model();
        using var node = Node(new FakePipelineGpu(), model);

        node.ProduceUntilInstalled();
        model.WriterSignature = null;
        node.ProduceFrame(context: default); node.ProduceFrame(context: default);
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (3, 3));
        model.WriterSignature = 1;
        node.ProduceFrame(context: default);
        Assert.Equal(actual: model.Writes, expected: 4);
        model.InactiveShade = true;
        node.ProduceFrame(context: default); node.ProduceFrame(context: default);
        var sample = new GpuWorkSample();

        Assert.True(condition: node.TryReadCompleted(sample: sample));
        Assert.Equal(GpuPassState.Standing, sample.GetPassState(pass: 0));
        Assert.Equal(GpuPassState.Skipped, sample.GetPassState(pass: 1));
        model.InactiveShade = false;
        node.ProduceFrame(context: default);
        Assert.Equal(actual: model.Shades, expected: 5);
    }
    [InlineData("reset")]
    [InlineData("resize")]
    [InlineData("loss")]
    [Theory]
    public void LifecycleChangesCannotReuseEarlierContents(string change) {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model);

        node.ProduceUntilInstalled(); node.ProduceFrame(context: default);
        switch (change) {
            case "reset": node.Reset(); node.ProduceFrame(context: default); break;
            case "resize": node.Resize(height: 64, width: 64); node.ProduceBuildStart(gpu: gpu); node.ProduceFrame(context: default); break;
            case "loss": node.OnDeviceLost(); node.ProduceUntilInstalled(); break;
        }
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (2, 2));
    }
    [InlineData("zero")]
    [InlineData("history")]
    [InlineData("external")]
    [Theory]
    public void InputsWithoutARetainedContentVersionAlwaysExecute(string kind) {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        var input = new ShaderPipelineResource(Name: "source", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 16,
            History: (kind == "history"), Initialization: ((kind == "external") ? ShaderPipelineInitialization.External : ShaderPipelineInitialization.Zero));
        using var external = gpu.CreateDeviceLocal(16, GpuBufferUsage.Storage, new GpuObjectName("test", "external"));
        using var node = Node(gpu, model, input: input, previous: (kind == "history"));

        if (kind == "external") { node.BindBuffer(buffer: external, name: "source"); }
        node.ProduceUntilInstalled();
        for (var frame = 0; (frame < 5); frame++) { node.ProduceFrame(context: default); }
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (6, 6));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void NodeOwnedParameterEditsInvalidateEvenAnUnchangedPackageSignature(bool rebind) {
        var model = new Model();
        using var node = Node(new FakePipelineGpu(), model);

        node.ProduceUntilInstalled(); node.ProduceFrame(context: default);
        if (rebind) {
            using var config = JsonDocument.Parse("{\"gain\":2}");

            Assert.True(condition: node.TrySetConfig("write", config.RootElement, out _));
        } else { Assert.True(condition: node.TryWriteParameter(field: "gain", passName: "write", value: 2)); }
        node.ProduceFrame(context: default);
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (2, 2));
        Assert.True(condition: node.TryWriteParameter(field: "gain", passName: "write", value: 2));
        node.ProduceFrame(context: default);
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (2, 2));
    }
    [Fact]
    public void AFailedFrameCannotPublishAContentIdentityThatNeverSubmitted() {
        var model = new Model { RefuseComposite = true };
        using var node = Node(new FakePipelineGpu(), model);

        Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceUntilInstalled());
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (1, 1));
        model.RefuseComposite = false;
        node.ProduceFrame(context: default);
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (2, 2));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void FailedRecordingRetriesOnlyUnsubmittedRetainedInitialization(bool previouslySubmitted) {
        var gpu = new FakePipelineGpu { Recording = true };
        var model = new Model();
        var input = new ShaderPipelineResource(Name: "source", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 16,
            Initialization: ShaderPipelineInitialization.Zero, Retained: true);
        using var node = Node(gpu, model, input: input);

        if (previouslySubmitted) { node.ProduceUntilInstalled(); }
        model.RefuseComposite = true;
        Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceUntilInstalled());
        gpu.Events.Clear();
        model.RefuseComposite = false;
        node.ProduceFrame(context: default);
        Assert.Equal((previouslySubmitted ? 0 : 1), gpu.Events.Count(predicate: item => item.StartsWith(comparisonType: StringComparison.Ordinal, value: "clear buffer ")));
        var writes = model.Writes;

        node.ProduceFrame(context: default);
        Assert.Equal(actual: model.Writes, expected: writes);
        Assert.Equal((10, 20), model.Samples[^1]);
    }
    [Fact]
    public void ZeroInitializedRetainedInputsKeepTheirInitializationIdentityUntilReset() {
        var model = new Model();
        var input = new ShaderPipelineResource(Name: "source", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 16,
            Initialization: ShaderPipelineInitialization.Zero, Retained: true);
        using var node = Node(new FakePipelineGpu(), model, input: input);

        node.ProduceUntilInstalled();
        for (var frame = 0; (frame < 5); frame++) { node.ProduceFrame(context: default); }
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (1, 1));
        node.Reset(); node.ProduceFrame(context: default);
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (2, 2));
    }
    [Fact]
    public void SteadyStandingFramesAllocateNeitherGpuObjectsNorManagedStorage() {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model);

        node.ProduceUntilInstalled();
        for (var frame = 0; (frame < 8); frame++) { node.ProduceFrame(context: default); }
        var objects = gpu.CreationCount;
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var frame = 0; (frame < 32); frame++) { node.ProduceFrame(context: default); }
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.Equal(actual: allocated, expected: 0);
        Assert.Equal(objects, gpu.CreationCount);
        Assert.Equal(actual: (model.Writes, model.Shades), expected: (1, 1));
    }
    [Fact]
    public void AStandingWriterLeavesTheActualLastAccessForTheNextWriter() {
        var gpu = new FakePipelineGpu { Recording = true };
        var model = new Model();
        using var node = Node(gpu, model);

        node.ProduceUntilInstalled();
        var handle = Assert.Single(collection: model.Contents).Key;

        gpu.Barriers.Clear();
        model.ShadeSignature = 2;
        node.ProduceFrame(context: default);
        var write = Assert.Single(collection: gpu.Barriers, predicate: item => ((item.Handle == handle) && item.Barrier.DestinationAccess.HasFlag(flag: GpuAccess.ShaderWrite)));

        Assert.Equal(GpuAccess.ShaderRead, write.Barrier.SourceAccess);
        Assert.Equal(GpuAccess.ShaderRead | GpuAccess.ShaderWrite, write.Barrier.DestinationAccess);
        Assert.Equal(GpuStage.ComputeShader, write.Barrier.SourceStage);
    }
    [InlineData("transient")]
    [InlineData("history")]
    [InlineData("external")]
    [InlineData("published")]
    [InlineData("forwarded")]
    [InlineData("unretained-preservation")]
    [InlineData("root-preservation")]
    [Theory]
    public void InvalidRetainedContractsAreRefusedByName(string kind) {
        var graph = Graph();
        var resources = graph.Resources.ToArray();

        switch (kind) {
            case "transient": resources[0] = resources[0] with { Transient = true }; break;
            case "history": resources[1] = resources[1] with { History = true }; break;
            case "external": resources[0] = resources[0] with { Initialization = ShaderPipelineInitialization.Host }; graph = graph with { Packages = graph.Packages!.Where(predicate: pass => (pass.Name != "write")).ToArray() }; break;
            case "published": graph = graph with { Outputs = ["out", "b"] }; break;
            case "forwarded": resources[1] = resources[1] with { Retained = true }; break;
            case "unretained-preservation": resources[0] = resources[0] with { Retained = false }; break;
            case "root-preservation": resources[0] = resources[0] with { PreservesPredecessor = true }; break;
        }
        if (kind == "external") {
            resources = [resources[0], resources[2]];
            graph = graph with { Packages = [graph.Packages![^1] with { Inputs = ["a"] }] };
        }
        graph = graph with { Resources = resources };
        var failure = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new RenderGraphCompiler(Catalog()).Compile(definition: graph));

        Assert.Contains(collection: failure.Diagnostics, filter: diagnostic => (diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "'a'") || diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "'b'")));
        Assert.Contains(collection: failure.Diagnostics, filter: diagnostic => (diagnostic.Code == "SHADERPIPE_RETAINED"));
    }

    private sealed class Model : IRenderGraphPackageFactory {
        public ulong? WriterSignature = 1;
        public ulong? ShadeSignature = 1;
        public int WriterValue = 10;
        public int ShadeValue = 20;

        public int Writes;
        public int Shades;
        public bool InactiveShade;
        public bool RefuseComposite;
        public bool StageRegion;

        public readonly Dictionary<nint, (int First, int Second)> Contents = new(capacity: 4);
        public readonly List<(int First, int Second)> Samples = new(capacity: 128);

        public IReadOnlyList<RenderGraphPackageRegion> Regions(RenderGraphPackageRecorderContext context) =>
            ((StageRegion && (context.Package == Writer)) ? [new RenderGraphPackageRegion(ByteCount: 16, Name: "source")] : []);
        public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => null;
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(owner: this, package: context.Package);

        private sealed class Recorder(Model owner, string package) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public bool Skips(in FrameContext context) => ((package == Shade) && owner.InactiveShade);
            public ulong? Signature(in FrameContext context) => ((package == Writer) ? owner.WriterSignature : owner.ShadeSignature);
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                if (package == Composite) {
                    if (owner.RefuseComposite) { throw new InvalidOperationException(message: "Injected composite refusal."); }
                    if (owner.InactiveShade) { owner.Samples.Add(item: default); } else { owner.Samples.Add(item: owner.Contents[recording.Inputs[0].Buffer!.BufferHandle]); }
                } else {
                    var handle = recording.Outputs[0].Buffer!.BufferHandle;

                    if (package == Writer) { owner.Writes++; owner.Contents[handle] = (owner.WriterValue, 0); } else { owner.Shades++; owner.Contents[handle] = (owner.Contents[handle].First, owner.ShadeValue); }
                }
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
