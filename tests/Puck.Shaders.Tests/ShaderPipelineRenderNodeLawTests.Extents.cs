using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>
/// The node reads each pass's extent from its planned pass (<see cref="ShaderPipelinePlannedPass.ResolveExtent"/>),
/// including a package's independent buffer grid. Image exports bound the package ceiling by their output extent;
/// every compute pass dispatches over its planned extent before and after a resize.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
    [Theory]
    [InlineData(ShaderPipelineResourceKind.Buffer, false)]
    [InlineData(ShaderPipelineResourceKind.Image, false)]
    [InlineData(ShaderPipelineResourceKind.Buffer, true)]
    public void OnlyBufferExportsUseTheirPackageCeilingBeyondTheOutputGrid(ShaderPipelineResourceKind kind, bool mixed) {
        const string Package = "test.native-grid";
        var gpu = new FakePipelineGpu();
        var factory = new NativeGridPackage();
        var pipelines = new GpuPassPipelineCache();
        var packages = new RenderGraphPackageRecorders(regionCopy: new GpuRegionCopyPass(
            pipelines: pipelines, kernel: new byte[] { UploadModelGpu.RegionCopyBytecode }));
        packages.Register(factory: factory, package: Package);
        var port = kind == ShaderPipelineResourceKind.Buffer
            ? RenderGraphPackagePort.Buffer(RenderGraphPortAccess.ComputeWrite, null, null)
            : RenderGraphPackagePort.Image(RenderGraphPortAccess.ComputeWrite);
        var catalog = new RenderGraphPackageCatalog([new(Id: Package, Members: [], Inputs: [],
            Outputs: mixed ? [port, RenderGraphPackagePort.Image(RenderGraphPortAccess.ComputeWrite)] : [port],
            Summary: "Records at a package-native grid.")]);
        var output = kind == ShaderPipelineResourceKind.Buffer
            ? new ShaderPipelineResource(Name: "result", Kind: kind, SizeBytes: 256UL)
            : Image(name: "result", format: "R8G8B8A8Unorm", dimensions: ShaderPipelineDimensions.Relative());
        var compiled = new RenderGraphCompiler(catalog).Compile(new RenderGraphDefinition(
            Name: "native-grid", Schema: RenderGraphSchemas.Graph,
            Resources: mixed ? [output, Image(name: "image", format: "R8G8B8A8Unorm", dimensions: ShaderPipelineDimensions.Relative())] : [output],
            Outputs: mixed ? ["result", "image"] : ["result"],
            Packages: [new(Name: "record", Package: Package, Outputs: mixed ? ["result", "image"] : ["result"])]));
        using var node = new ShaderPipelineRenderNode(deviceContext: gpu, width: 1, height: 1,
            hostsOnDirectX: false, packages: packages, pipelines: pipelines, name: "native-grid");
        node.Swap(new CompiledShaderPipeline(compiled.Pipeline, new Dictionary<string, CompiledShader>()));
        TestLiveness.Until(step: () => {
            _ = Produce(node);
            return node.FrameCounter != 0 || node.LastSwapError is not null;
        }, reason: () => "The package ceiling was neither installed nor refused.");

        if (kind == ShaderPipelineResourceKind.Image || mixed) {
            Assert.Contains("outside its 1x1 ceiling", Assert.IsType<InvalidDataException>(node.LastSwapError).Message);
            Assert.Equal((0u, 0u), factory.Recorded);
        } else {
            Assert.Null(node.LastSwapError);
            Assert.Equal((1u, 1u), node.Extent);
            Assert.Equal((512u, 512u), factory.Built);
            Assert.Equal((512u, 512u), factory.Recorded);
        }
    }

    private sealed class NativeGridPackage : IRenderGraphPackageFactory, IShaderPipelineRenderExtent {
        public long Revision => 0;
        public double Grid => 1;
        public (uint Width, uint Height) Built { get; private set; }
        public (uint Width, uint Height) Recorded { get; private set; }
        public IShaderPipelineRenderExtent? RenderExtentOf(string instance) => this;
        public (uint Width, uint Height) CeilingAt(uint width, uint height) => (512, 512);
        public (uint Width, uint Height) FrameAt(uint width, uint height) => (512, 512);
        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
            Built = (context.Width, context.Height);
            return ValueTask.FromResult<IDisposable?>(null);
        }
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(this);
        private sealed class Recorder(NativeGridPackage owner) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                owner.Recorded = (recording.Width, recording.Height);
                return RenderGraphPackageOutcome.Drew;
            }
        }
    }

    // A relative-size output, a fixed-size output, a pass whose output declares no dimensions (its input's), and a pass
    // that touches only buffers (the frame's).
    private static CompiledShaderPipeline Extents() {
        var definition = new RenderGraphDefinition(
            name: "extents",
            outputs: ["fixed", "sum"],
            passes: [
                Pass(inputs: [], kind: ShaderPipelineDocumentPassKind.Compute, name: "fill", outputs: [new ResourceReference(Name: "half")]),
                Pass(inputs: [new ResourceReference(Name: "half")], kind: ShaderPipelineDocumentPassKind.Compute, name: "draw", outputs: [new ResourceReference(Name: "fixed")]),
                Pass(inputs: [new ResourceReference(Name: "fixed")], kind: ShaderPipelineDocumentPassKind.Compute, name: "count", outputs: [new ResourceReference(Name: "counts")]),
                Pass(inputs: [new ResourceReference(Name: "counts")], kind: ShaderPipelineDocumentPassKind.Compute, name: "total", outputs: [new ResourceReference(Name: "sum")]),
            ],
            resources: [
                Image(dimensions: ShaderPipelineDimensions.Relative(height: 0.5, width: 0.5), format: "R8G8B8A8Unorm", name: "half"),
                Image(dimensions: ShaderPipelineDimensions.Absolute(height: 8, width: 16), format: "R8G8B8A8Unorm", name: "fixed"),
                new ShaderPipelineResource(Kind: ShaderPipelineResourceKind.Buffer, Name: "counts", SizeBytes: 64UL),
                new ShaderPipelineResource(Kind: ShaderPipelineResourceKind.Buffer, Name: "sum", SizeBytes: 64UL),
            ]
        );
        var plan = new ShaderPipelineCompiler().Compile(definition: definition);

        return new CompiledShaderPipeline(
            plan: plan,
            shaders: plan.Passes.ToDictionary(
                elementSelector: static pass => Shader(
                    kind: pass.Declaration!.Kind,
                    name: pass.Name
                ),
                keySelector: static pass => pass.Name
            )
        );
    }
    // One recorded frame's dispatch group counts, in pass order.
    private static List<(uint X, uint Y, uint Z)> RecordedDispatches(FakePipelineGpu gpu, ShaderPipelineRenderNode node) {
        gpu.Dispatches.Clear();
        gpu.Recording = true;
        try {
            _ = Produce(node: node);
        } finally {
            gpu.Recording = false;
        }

        return [.. gpu.Dispatches];
    }
    // The group counts each pass dispatches over its planned extent at a frame.
    private static List<(uint X, uint Y, uint Z)> PlannedDispatches(ShaderPipelinePlan plan, uint width, uint height) => [.. plan.Passes.Select(selector: pass => {
        var (passWidth, passHeight) = pass.ResolveExtent(
            frameHeight: height,
            frameWidth: width
        );
        var declaration = pass.Declaration!;

        return (
            (((passWidth + declaration.GroupSizeX) - 1u) / declaration.GroupSizeX),
            (((passHeight + declaration.GroupSizeY) - 1u) / declaration.GroupSizeY),
            (((1u + declaration.GroupSizeZ) - 1u) / declaration.GroupSizeZ)
        );
    })];

    [Fact]
    public void EveryPassDispatchesOverItsPlannedExtentBeforeAndAfterAResize() {
        var gpu = new FakePipelineGpu();
        var pipeline = Extents();
        using var node = Node(gpu: gpu);

        node.Swap(pipeline: pipeline);
        _ = node.ProduceUntilInstalled();

        Assert.Equal(
            actual: RecordedDispatches(gpu: gpu, node: node),
            expected: PlannedDispatches(height: Extent, plan: pipeline.Plan, width: Extent)
        );

        const uint ResizedWidth = (Extent * 4);
        const uint ResizedHeight = (Extent * 2);

        node.Resize(
            height: ResizedHeight,
            width: ResizedWidth
        );
        TestLiveness.Until(
            reason: () => "The resized graph never installed.",
            step: () => {
                _ = Produce(node: node);

                return (node.Extent == (ResizedWidth, ResizedHeight));
            }
        );
        Assert.Equal(
            actual: RecordedDispatches(gpu: gpu, node: node),
            expected: PlannedDispatches(height: ResizedHeight, plan: pipeline.Plan, width: ResizedWidth)
        );
        Assert.NotEqual(
            actual: PlannedDispatches(height: ResizedHeight, plan: pipeline.Plan, width: ResizedWidth),
            expected: PlannedDispatches(height: Extent, plan: pipeline.Plan, width: Extent)
        );
    }
}
