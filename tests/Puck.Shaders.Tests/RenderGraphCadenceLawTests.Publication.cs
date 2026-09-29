namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphCadenceLawTests {
    private const string ImageWriter = "test.image-writer";
    private const string ImageReader = "test.image-reader";

    private static RenderGraphPackageCatalog ImageCatalog() => new(packages: [
        new RenderGraphPackage(Id: ImageWriter, Inputs: [], Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)], Members: [], Summary: "Writes an intermediate."),
        new RenderGraphPackage(Id: ImageReader, Inputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeRead)], Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)], Members: [], Summary: "Writes an ordinary public image.")
    ]);
    private static CompiledShaderPipeline ImagePipeline(string lifetime) {
        var intermediate = new ShaderPipelineResource(Name: "scratch", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative(),
            Retained: (lifetime == "retained"), Transient: (lifetime == "transient"));
        var definition = new RenderGraphDefinition(Schema: RenderGraphSchemas.Graph, Name: "selection", Outputs: ["out"],
            Resources: [intermediate, intermediate with { Name = "out", Retained = false, Transient = false }],
            Packages: [
                new RenderGraphPackagePass(Name: "write", Package: ImageWriter, Outputs: ["scratch"]),
                new RenderGraphPackagePass(Name: "copy", Package: ImageReader, Inputs: ["scratch"], Outputs: ["out"])
            ]);

        return new CompiledShaderPipeline(plan: new RenderGraphCompiler(ImageCatalog()).Compile(definition: definition).Pipeline, shaders: new Dictionary<string, CompiledShader>());
    }
    private static ShaderPipelineRenderNode ImageNode(FakePipelineGpu gpu, ImageFactory factory, string lifetime) {
        var packages = new RenderGraphPackageRecorders();

        packages.Register(factory: factory, package: ImageWriter); packages.Register(factory: factory, package: ImageReader);
        var node = new ShaderPipelineRenderNode(name: "selection", deviceContext: gpu, pipelines: new GpuPassPipelineCache(),
            hostsOnDirectX: false, width: 32, height: 32, packages: packages);

        node.Swap(pipeline: ImagePipeline(lifetime: lifetime));
        return node;
    }

    [InlineData("retained")]
    [InlineData("transient")]
    [Theory]
    public void SharedIntermediateAllocationsCannotEscapeThroughOutputSelection(string lifetime) {
        using var node = ImageNode(new FakePipelineGpu(), new ImageFactory(), lifetime);
        var error = Assert.Throws<ArgumentException>(testCode: () => node.SelectOutput(name: "scratch"));

        Assert.Contains("scratch", error.Message, StringComparison.Ordinal);
        Assert.Contains(lifetime, error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void OrdinaryPerSlotIntermediatesRemainSelectable() {
        var factory = new ImageFactory();
        using var node = ImageNode(new FakePipelineGpu(), factory, "ordinary");

        node.SelectOutput(name: "scratch");
        var surface = node.ProduceUntilInstalled();

        Assert.Equal(factory.Images["write"], surface.ImageHandle);
    }
    [Fact]
    public void ReloadCannotKeepASelectionWhoseNewStorageIsRetained() {
        var gpu = new FakePipelineGpu();
        var factory = new ImageFactory();
        using var node = ImageNode(factory: factory, gpu: gpu, lifetime: "ordinary");

        node.SelectOutput(name: "scratch");
        node.ProduceUntilInstalled();
        node.Swap(pipeline: ImagePipeline(lifetime: "retained"));
        node.ProduceBuildStart(gpu: gpu);
        var surface = node.ProduceFrame(context: default);

        Assert.Equal(factory.Images["copy"], surface.ImageHandle);
        Assert.NotEqual(factory.Images["write"], surface.ImageHandle);
    }

    private sealed class ImageFactory : IRenderGraphPackageFactory {
        public readonly Dictionary<string, nint> Images = [];

        public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) => null;
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(owner: this, pass: context.Pass);

        private sealed class Recorder(ImageFactory owner, string pass) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public ulong? Signature(in Puck.Hosting.FrameContext context) => 1;
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                owner.Images[pass] = recording.Outputs[0].Image.ImageHandle;
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}
