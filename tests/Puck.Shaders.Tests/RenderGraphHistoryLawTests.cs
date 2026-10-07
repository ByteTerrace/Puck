using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>Local history follows successful writes while unrelated passes continue submitting. Previous history
/// is an optional input, not a refresh demand; current readers and publication use the last written contents.</summary>
public sealed partial class RenderGraphHistoryLawTests {
    private const string Lit = "test.history-lit";
    private const string Sky = "test.history-sky";

    private readonly Xunit.ITestOutputHelper m_output;

    public RenderGraphHistoryLawTests(Xunit.ITestOutputHelper output) => m_output = output;

    private static CompiledShaderPipeline Pipeline(bool image, bool publishHistory = false, bool colorWrite = false) {
        var read = (image ? RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeRead)
            : RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeRead, strideBytes: null, count: null));
        var write = (image ? RenderGraphPackagePort.Image(access: (colorWrite ? RenderGraphPortAccess.ColorAttachmentWrite : RenderGraphPortAccess.ComputeWrite))
            : RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeWrite, strideBytes: null, count: null));
        var catalog = new RenderGraphPackageCatalog(packages: [
            new RenderGraphPackage(Id: Lit, Inputs: [read], Outputs: [write], Members: [], Summary: "Blends a new lit value with its preceding successful value."),
            new RenderGraphPackage(Id: Sky, Inputs: [read], Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)], Members: [], Summary: "Composites an independently changing sky.")
        ]);
        var history = (image
            ? new ShaderPipelineResource(Name: "history", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative(), History: true, Initialization: ShaderPipelineInitialization.Zero)
            : new ShaderPipelineResource(Name: "history", Kind: ShaderPipelineResourceKind.Buffer, SizeBytes: 16, History: true, Initialization: ShaderPipelineInitialization.Zero));
        var graph = new RenderGraphDefinition(Schema: RenderGraphSchemas.Graph, Name: "history", Outputs: (publishHistory ? ["history", "out"] : ["out"]),
            Resources: [history, new ShaderPipelineResource(Name: "out", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative())],
            Packages: [
                new RenderGraphPackagePass(Name: "lit", Package: Lit, Inputs: [new ResourceReference(Name: "history", PreviousFrame: true)], Outputs: ["history"]),
                new RenderGraphPackagePass(Name: "sky", Package: Sky, Inputs: ["history"], Outputs: ["out"])
            ]);

        return new CompiledShaderPipeline(plan: new RenderGraphCompiler(catalog).Compile(definition: graph).Pipeline,
            shaders: new Dictionary<string, CompiledShader>());
    }
    private static ShaderPipelineRenderNode Node(FakePipelineGpu gpu, Model model, bool image = true, bool publishHistory = false, bool colorWrite = false) {
        var packages = new RenderGraphPackageRecorders();

        model.Gpu = gpu;

        packages.Register(factory: model, package: Lit); packages.Register(factory: model, package: Sky);
        var node = new ShaderPipelineRenderNode(deviceContext: gpu, width: 32, height: 32, hostsOnDirectX: false,
            name: "history", packages: packages, outputLayout: GpuImageLayout.ShaderReadOnly, pipelines: new GpuPassPipelineCache());

        node.Swap(pipeline: Pipeline(colorWrite: colorWrite, image: image, publishHistory: publishHistory));
        return node;
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void SkyOnlySubmissionsKeepTheLastLitHistoryAndTheNextBlendReadsIt(bool image) {
        var model = new Model();
        using var node = Node(new FakePipelineGpu(), model, image);

        node.ProduceUntilInstalled();
        var first = Assert.Single(collection: model.Blends);

        for (var frame = 0; (frame < 13); frame++) { node.ProduceFrame(context: default); }
        Assert.Single(collection: model.Blends);
        Assert.Equal(14, model.Samples.Count);
        Assert.All(collection: model.Samples, action: sample => Assert.Equal(actual: sample, expected: (first.Output, 1)));
        model.Signature = 2; model.Increment = 2;
        node.ProduceFrame(context: default);
        Assert.Equal((first.Output, 1), (model.Blends[^1].Input, model.Blends[^1].Previous));
        Assert.NotEqual(first.Output, model.Blends[^1].Output);
        Assert.Equal(3, model.Samples[^1].Value);
    }
    [Fact]
    public void AStandingHistoryOutputAndItsExportKeepTheLastWrittenImage() {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model, publishHistory: true);
        var export = new FakeOutputExport(gpu, 32, 32);

        node.Export = export;
        var first = node.ProduceUntilInstalled();

        for (var frame = 0; (frame < 7); frame++) {
            var surface = node.ProduceFrame(context: default);

            Assert.Equal(first.ImageHandle, surface.ImageHandle);
            Assert.Equal(first.ImageHandle, gpu.CopiedImages[^1].Source);
        }
        Assert.Single(collection: model.Blends);
    }
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void FailedRecordingOrSubmissionDoesNotAdvanceHistory(bool submittedBefore, bool submitFailure) {
        var gpu = new FakePipelineGpu { Recording = true };
        var model = new Model();
        using var node = Node(gpu, model);

        if (submittedBefore) { node.ProduceUntilInstalled(); }
        var frames = node.FrameCounter;

        model.Signature = 2; model.Increment = 10;
        model.RefuseComposite = !submitFailure; gpu.RefuseNextSubmission = submitFailure;
        Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceUntilInstalled());
        Assert.Equal(frames, node.FrameCounter);
        var failed = model.Blends[^1];

        model.RefuseComposite = false;
        var attempts = model.Blends.Count;

        node.ProduceFrame(context: default);
        Assert.Equal((attempts + 1), model.Blends.Count);
        var restored = model.Blends[^1];

        Assert.Equal(actual: (restored.Input, restored.Output, restored.Previous), expected: (failed.Input, failed.Output, failed.Previous));
        Assert.Equal((submittedBefore ? 11 : 10), model.Samples[^1].Value);
        var writes = model.Blends.Count;

        node.ProduceFrame(context: default);
        Assert.Equal(writes, model.Blends.Count);
    }
    [Fact]
    public void LossAfterARefusedSubmissionDiscardsTheUnsubmittedHistoryAndMetadata() {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model);

        node.ProduceUntilInstalled();
        model.Signature = 2; model.Increment = 10; gpu.RefuseNextSubmission = true;
        Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceFrame(context: default));
        node.OnDeviceLost();
        Assert.Equal(0UL, node.CadenceCpuBytes);
        node.ProduceUntilInstalled();
        Assert.Equal(0, model.Blends[^1].Previous);
        Assert.Equal(10, model.Samples[^1].Value);
        var writes = model.Blends.Count;

        node.ProduceFrame(context: default);
        Assert.Equal(writes, model.Blends.Count);
    }
    [Fact]
    public void APostSubmitExportFailureKeepsTheCommittedCursorAndSubmissionSlot() {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model);
        var export = new FakeOutputExport(gpu, 32, 32);

        node.Export = export;
        node.ProduceUntilInstalled();
        var exported = Assert.Single(collection: export.Created);
        var frames = node.FrameCounter;

        model.Signature = 2; exported.RefuseCompletion = true;
        Assert.Throws<InvalidOperationException>(testCode: () => node.ProduceFrame(context: default));
        Assert.Equal((frames + 1), node.FrameCounter);
        var submitted = model.Blends[^1];

        model.Signature = 3; exported.RefuseCompletion = false;
        node.ProduceFrame(context: default);
        Assert.Equal((submitted.Output, 2), (model.Blends[^1].Input, model.Blends[^1].Previous));
        Assert.NotEqual(submitted.Output, model.Blends[^1].Output);
        Assert.Equal(3, model.Samples[^1].Value);
    }
    [Fact]
    public void ACompatibleReloadKeepsTheWrittenCursorAcrossStandingSubmissions() {
        var gpu = new FakePipelineGpu();
        var model = new Model();
        using var node = Node(gpu, model);

        node.ProduceUntilInstalled();
        var first = model.Blends[^1];

        for (var frame = 0; (frame < 7); frame++) { node.ProduceFrame(context: default); }
        node.Swap(pipeline: Pipeline(image: true));
        node.ProduceBuildStart(gpu: gpu);
        node.ProduceFrame(context: default);
        Assert.Equal((first.Output, 1), (model.Blends[^1].Input, model.Blends[^1].Previous));
        Assert.Equal(2, model.Samples[^1].Value);
    }
    [Fact]
    public void HistoryCadenceAddsNoGpuObjectsDescriptorsCopiesOrExtraDispatches() {
        var gpu = new FakePipelineGpu { Recording = true };
        var model = new Model();
        using var node = Node(gpu, model, image: false);

        node.ProduceUntilInstalled();
        Assert.Equal(3, gpu.CreatedObjects.Count(predicate: item => ((item.Kind == "buffer") && (item.Bytes == 16))));
        var objects = gpu.CreationCount;
        var pools = gpu.DescriptorPools.Count;
        var metadata = node.CadenceCpuBytes;

        Assert.True(condition: (metadata > 0));
        m_output.WriteLine(message: $"History cadence metadata payload: {metadata} bytes; history buffer instances: 3; unchanged standing GPU objects: {objects}.");
        gpu.Dispatches.Clear(); gpu.CopiedImages.Clear(); gpu.DescriptorWrites.Clear();
        for (var frame = 0; (frame < 12); frame++) { node.ProduceFrame(context: default); }
        Assert.Equal(12, gpu.Dispatches.Count);
        Assert.Equal(objects, gpu.CreationCount);
        Assert.Equal(pools, gpu.DescriptorPools.Count);
        Assert.Equal(metadata, node.CadenceCpuBytes);
        Assert.Empty(collection: gpu.CopiedImages);
        Assert.Empty(collection: gpu.DescriptorWrites);
    }

    private sealed class Model : IRenderGraphPackageFactory {
        public ulong Signature = 1;
        public int Increment = 1;
        public bool RefuseComposite;
        public bool SkipLit;
        public bool LoseComposite;
        public FakePipelineGpu? Gpu;
        public int CommittedWrites;

        public readonly Dictionary<nint, int> Contents = [];
        public readonly List<(nint Input, nint Output, int Previous)> Blends = [];
        public readonly List<(nint Handle, int Value)> Samples = [];
        public readonly List<nint> Clears = [];

        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(owner: this, package: context.Package);

        private sealed class Recorder(Model owner, string package) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public void Submitted() { if (package == Lit) { owner.CommittedWrites++; } }
            public bool Skips(in FrameContext context) => ((package == Lit) && owner.SkipLit);
            public ulong? Signature(in FrameContext context, RenderGraphExternalReads? reads) => ((package == Lit) ? owner.Signature : null);
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                foreach (var cleared in owner.Gpu!.ClearedImages) { owner.Contents.Remove(key: cleared); owner.Clears.Add(item: cleared); }
                owner.Gpu.ClearedImages.Clear();
                var input = recording.Inputs[0];
                var handle = (input.Buffer?.BufferHandle ?? input.Image.ImageHandle);
                var value = owner.Contents.GetValueOrDefault(key: handle);

                if (package == Lit) {
                    var output = recording.Outputs[0];
                    var written = (output.Buffer?.BufferHandle ?? output.Image.ImageHandle);

                    owner.Blends.Add(item: (handle, written, value));
                    owner.Contents[written] = (value + owner.Increment);
                } else {
                    if (owner.LoseComposite) { throw new DeviceLostException(message: "Injected loss during history recording."); }
                    if (owner.RefuseComposite) { throw new InvalidOperationException(message: "Injected sky recording refusal."); }
                    owner.Samples.Add(item: (handle, value));
                }
                recording.Recorder.Dispatch(commandBufferHandle: recording.CommandBuffer, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
