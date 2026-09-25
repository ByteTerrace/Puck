using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws of the <c>post.&lt;id&gt;</c> package (<see cref="PostProcessPackage"/>) on <see cref="FakePipelineGpu"/>: the
/// shipped film-grain set run as a package pass of a graph records what <see cref="FullscreenPassNode"/> records for the
/// same set over the same input (the render pass and graphics pipeline it is created for, the vertex buffer and the
/// draw, the input written at the set's binding, and the frame block pushed byte for byte, bound config and live changes
/// included); its pipeline is built off the frame thread and released with its graph on replacement, device loss and
/// disposal; a config that does not bind is refused by the graph compiler by name; and a steady frame allocates
/// nothing.
/// </summary>
public sealed class PostProcessPackageLawTests {
    private const uint Extent = 64;
    private const string Pass = "grain";

    private static readonly ShaderPipelineExternalImage Input = new(
        Format: GpuPixelFormat.R8G8B8A8Unorm,
        Height: Extent,
        ImageHandle: 0x7000,
        ImageViewHandle: 0x7001,
        Layout: GpuImageLayout.ShaderReadOnly,
        Width: Extent
    );

    private static string ShadersDirectory => Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "Assets",
        path3: "Shaders"
    );

    private static ShaderSetManifest FilmGrain() => ShaderSetManifest.Load(manifestPath: Path.Combine(
        path1: ShadersDirectory,
        path2: "Sdf",
        path3: "sdf-film-grain.puck.shader.json"
    ));
    private static RenderGraphPackageCatalog Catalog() => RenderGraphPackageCatalog.WithPostProcess(postProcess: ShaderSetCatalog.Scan(rootDirectory: ShadersDirectory));
    private static RenderGraphDefinition Graph(JsonElement? config, string package = "post.sdf-film-grain") => new(
        Name: "post",
        Outputs: ["output"],
        Packages: [new RenderGraphPackagePass(
            Config: config,
            Inputs: ["input"],
            Name: Pass,
            Outputs: ["output"],
            Package: package
        )],
        Resources: [
            new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Relative(),
                Format: "R8G8B8A8Unorm",
                Initialization: ShaderPipelineInitialization.External,
                Name: "input"
            ),
            new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Relative(),
                Format: "R8G8B8A8Unorm",
                Name: "output"
            ),
        ],
        Schema: RenderGraphSchemas.Graph
    );
    private static JsonElement Json(string text) => JsonDocument.Parse(json: text).RootElement.Clone();
    // A node running the film-grain set as the graph's one package pass over the bound input.
    private static ShaderPipelineRenderNode PackageNode(FakePipelineGpu gpu, JsonElement? config) {
        var manifest = FilmGrain();
        var package = new PostProcessPackage(manifest: manifest);
        var packages = new RenderGraphPackageRecorders();

        packages.Register(
            factory: package,
            package: package.Id
        );

        var plan = new RenderGraphCompiler(packages: Catalog()).Compile(definition: Graph(config: config));
        var node = new ShaderPipelineRenderNode(
            deviceContext: gpu,
            height: Extent,
            hostsOnDirectX: false,
            name: "post",
            outputLayout: GpuImageLayout.ShaderReadOnly,
            packages: packages,
            width: Extent
        );

        node.Swap(pipeline: new CompiledShaderPipeline(
            plan: plan.Pipeline,
            shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
        ));
        node.BindImage(
            image: Input,
            name: "input"
        );

        return node;
    }
    // The same set run by the node it replaces, over an inner node publishing the same input.
    private static FullscreenPassNode AdapterNode(FakePipelineGpu gpu, JsonElement? config) {
        var manifest = FilmGrain();

        return new FullscreenPassNode(
            config: manifest.BindConfig(config: config),
            deviceContext: gpu,
            height: Extent,
            hostsOnDirectX: false,
            inner: new InputNode(),
            manifest: manifest,
            width: Extent
        );
    }
    private static void ProduceUntilPublished(IRenderNode node) => Assert.True(
        condition: SpinWait.SpinUntil(
            condition: () => !node.ProduceFrame(context: default).IsEmpty,
            timeout: TimeSpan.FromSeconds(value: 30)
        ),
        userMessage: "The pass never published a frame."
    );
    // What a law compares of one pass's recording: the render pass and pipeline it was created for, the graphics
    // commands, the input written at a binding, and every frame block pushed.
    private static Recorded Record(FakePipelineGpu gpu, IRenderNode node, Action<int>? before = null) {
        ProduceUntilPublished(node: node);
        gpu.Recording = true;

        for (var frame = 0; (frame < 4); frame++) {
            before?.Invoke(obj: frame);
            _ = node.ProduceFrame(context: default);
        }

        gpu.Recording = false;

        var (pass, description) = Assert.Single(collection: gpu.GraphicsPipelines);

        return new Recorded(
            Commands: [.. gpu.GraphicsCommands.Select(selector: static command => $"{command.Command} {command.SizeBytes} {command.Count}")],
            Pipeline: $"{description.Name} {description.TextureSamplerCount} {description.EnableStorageBuffer} {description.DepthCompare} {description.PushConstantBinding?.Size} {description.PushConstantBinding?.StageFlags} {description.VertexInput.StrideBytes} {string.Join(separator: ",", values: description.VertexInput.Attributes)}",
            Pushes: [.. gpu.PushedConstants.Select(selector: static push => $"{push.BindPoint} {push.Stages} {Convert.ToHexString(inArray: push.Data)}")],
            RenderPass: $"{string.Join(separator: ",", values: pass.Colors)} {pass.Depth}",
            RenderPasses: gpu.RenderPasses.Count,
            Writes: [.. gpu.DescriptorWrites.Select(selector: static write => $"{write.Binding} {write.Handle}")]
        );
    }
    private static void AssertSameRecording(Recorded adapter, Recorded package) {
        Assert.Equal(
            expected: adapter.RenderPass,
            actual: package.RenderPass
        );
        Assert.Equal(
            expected: adapter.Pipeline,
            actual: package.Pipeline
        );
        Assert.Equal(
            expected: adapter.Commands,
            actual: package.Commands
        );
        Assert.Equal(
            expected: adapter.Writes,
            actual: package.Writes
        );
        Assert.Equal(
            expected: adapter.Pushes,
            actual: package.Pushes
        );
        Assert.Equal(
            expected: adapter.RenderPasses,
            actual: package.RenderPasses
        );
        Assert.Equal(
            expected: 4,
            actual: package.RenderPasses
        );
    }

    [InlineData(null)]
    [InlineData("""{"intensity":0.3,"seed":7}""")]
    [Theory]
    public void APostPassRecordsWhatTheFullscreenPassNodeRecordsForTheSameSetAndConfig(string? config) {
        var bound = ((config is null)
            ? ((JsonElement?)null)
            : Json(text: config));
        var adapterGpu = new FakePipelineGpu();
        var packageGpu = new FakePipelineGpu();
        using var adapter = AdapterNode(
            config: bound,
            gpu: adapterGpu
        );
        using var package = PackageNode(
            config: bound,
            gpu: packageGpu
        );

        AssertSameRecording(
            adapter: Record(
                gpu: adapterGpu,
                node: adapter
            ),
            package: Record(
                gpu: packageGpu,
                node: package
            )
        );
    }
    [Fact]
    public void ALiveConfigChangeReachesThePushedFrameBlockAsTheFullscreenPassNodeCarriesIt() {
        var adapterGpu = new FakePipelineGpu();
        var packageGpu = new FakePipelineGpu();
        using var adapter = AdapterNode(
            config: null,
            gpu: adapterGpu
        );
        using var package = PackageNode(
            config: null,
            gpu: packageGpu
        );

        AssertSameRecording(
            adapter: Record(
                before: frame => Assert.True(condition: ((frame != 2) || adapter.TrySetConfig(
                    field: "intensity",
                    value: 0.75f
                ))),
                gpu: adapterGpu,
                node: adapter
            ),
            package: Record(
                before: frame => Assert.True(condition: ((frame != 2) || package.TrySetConfig(
                    config: Json(text: """{"intensity":0.75}"""),
                    passName: Pass,
                    reason: out _
                ))),
                gpu: packageGpu,
                node: package
            )
        );
    }
    [Fact]
    public void AConfigThatDoesNotBindIsRefusedByTheGraphCompilerNamingThePass() {
        var compiler = new RenderGraphCompiler(packages: Catalog());
        var outOfRange = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => compiler.Compile(definition: Graph(config: Json(text: """{"intensity":5}"""))));
        var unconfigured = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => compiler.Compile(definition: Graph(
            config: Json(text: "{}"),
            package: RenderGraphPackageCatalog.Overlay
        )));

        foreach (var refusal in new[] { outOfRange, unconfigured }) {
            var diagnostic = Assert.Single(collection: refusal.Diagnostics);

            Assert.Equal(
                expected: ("RENDERGRAPH_PACKAGE_CONFIG", Pass),
                actual: (diagnostic.Code, diagnostic.Name)
            );
        }
    }
    [Fact]
    public void ThePipelineIsBuiltOffTheFrameThreadAndReleasedWithItsGraphOnReplacementDeviceLossAndDisposal() {
        var gpu = new FakePipelineGpu();
        var node = PackageNode(
            config: null,
            gpu: gpu
        );

        ProduceUntilPublished(node: node);

        var first = Assert.Single(
            collection: gpu.CreatedObjects,
            predicate: static created => (created.Kind == "graphics pipeline")
        );

        Assert.NotEqual(
            expected: Environment.CurrentManagedThreadId,
            actual: first.ThreadId
        );
        Assert.Equal(
            expected: 0,
            actual: first.DisposeCount
        );

        node.Resize(
            height: (Extent / 2),
            width: (Extent / 2)
        );
        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => {
                    _ = node.ProduceFrame(context: default);

                    return (node.Extent == ((Extent / 2), (Extent / 2)));
                },
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The resize never installed."
        );
        _ = node.ProduceFrame(context: default);
        _ = node.ProduceFrame(context: default);
        Assert.Equal(
            expected: 1,
            actual: first.DisposeCount
        );

        var second = gpu.CreatedObjects.Last(predicate: static created => (created.Kind == "graphics pipeline"));

        node.OnDeviceLost();
        Assert.Equal(
            expected: 1,
            actual: second.DisposeCount
        );
        node.BindImage(
            image: Input,
            name: "input"
        );
        ProduceUntilPublished(node: node);
        node.Dispose();
        Assert.All(
            action: static created => Assert.True(
                condition: (created.DisposeCount == 1),
                userMessage: created.ToString()
            ),
            collection: gpu.CreatedObjects.Where(predicate: static created => (created.Kind is "graphics pipeline" or "Vertex module" or "Fragment module" or "render pass" or "framebuffer" or "sampler" or "Vertex buffer")).ToArray()
        );
    }
    [Fact]
    public void ASteadyPostPassFrameAllocatesNothing() {
        var gpu = new FakePipelineGpu();
        using var node = PackageNode(
            config: null,
            gpu: gpu
        );

        ProduceUntilPublished(node: node);

        for (var frame = 0; (frame < 4); frame++) {
            _ = node.ProduceFrame(context: default);
        }

        Assert.Equal(
            actual: AllocationWindow.Least(window: () => {
                for (var frame = 0; (frame < 64); frame++) {
                    _ = node.ProduceFrame(context: default);
                }
            }),
            expected: 0L
        );
    }

    private sealed record Recorded(string RenderPass, string Pipeline, IReadOnlyList<string> Commands, IReadOnlyList<string> Writes, IReadOnlyList<string> Pushes, int RenderPasses);
    // An inner node publishing the law's input image every frame.
    private sealed class InputNode : IRenderNode {
        public NodeDescriptor Descriptor { get; } = new(
            Name: "input",
            SurfaceId: SurfaceId.New()
        );

        public void Dispose() { }
        public Surface ProduceFrame(in FrameContext context) => Surface.SameDeviceImage(
            format: SurfaceFormat.R8G8B8A8Unorm,
            height: Extent,
            imageHandle: Input.ImageHandle,
            imageViewHandle: Input.ImageViewHandle,
            width: Extent
        );
    }
}
