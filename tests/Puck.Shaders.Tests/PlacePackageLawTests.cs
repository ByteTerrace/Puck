using System.Buffers.Binary;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws of the <c>place</c> package (<see cref="PlacePackage"/>) on <see cref="FakePipelineGpu"/>: the deployed kernel
/// run as a graph's package pass dispatches once over the output's extent with its base and source written at the
/// bindings its interface gives them and nothing pushed; a host placement replaces the rect and sharpness in the pass
/// block the dispatch reads without rebinding the config; a source the host shows nowhere draws nothing and the node
/// publishes the base in the output's place; with no host placement the bound config's rect is what the dispatch
/// reads; a steady placed frame allocates nothing; and the kernel and its include read the interface the catalog
/// declares.
/// </summary>
public sealed class PlacePackageLawTests {
    private const uint Extent = 64;
    private const string Pass = "pane";

    private static readonly ShaderPipelineExternalImage Base = Image(handle: 0x7000);
    private static readonly ShaderPipelineExternalImage Source = Image(handle: 0x8000);
    // The layout the catalog declares for the package, which the plan lays every place pass out by.
    private static readonly ShaderPipelineParameterLayout Layout = ShaderPipelineParameterLayout.ForPackage(
        config: RenderGraphPackageCatalog.PlaceConfig,
        members: RenderGraphPackageCatalog.PlaceMembers,
        package: RenderGraphPackageCatalog.Place
    );

    private static ShaderPipelineExternalImage Image(nint handle) => new(
        Format: GpuPixelFormat.R8G8B8A8Unorm,
        Height: Extent,
        ImageHandle: handle,
        ImageViewHandle: (handle + 1),
        Layout: GpuImageLayout.ShaderReadOnly,
        Width: Extent
    );
    private static RenderGraphDefinition Graph(JsonElement? config) => new(
        Name: "placed",
        Outputs: ["output"],
        Packages: [new RenderGraphPackagePass(
            Config: config,
            Inputs: ["base", "source"],
            Name: Pass,
            Outputs: ["output"],
            Package: RenderGraphPackageCatalog.Place
        )],
        Resources: [
            External(name: "base"),
            External(name: "source"),
            new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Relative(),
                Format: "R8G8B8A8Unorm",
                Name: "output"
            ),
        ],
        Schema: RenderGraphSchemas.Graph
    );
    private static ShaderPipelineResource External(string name) => new(
        Dimensions: ShaderPipelineDimensions.Relative(),
        Format: "R8G8B8A8Unorm",
        Initialization: ShaderPipelineInitialization.External,
        Name: name
    );
    private static ShaderPipelineRenderNode Node(FakePipelineGpu gpu, IRenderGraphPlacements? placements, JsonElement? config = null) {
        var packages = new RenderGraphPackageRecorders();

        packages.Register(
            factory: new PlacePackage(placements: placements),
            package: RenderGraphPackageCatalog.Place
        );

        var plan = new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).Compile(definition: Graph(config: config));
        var node = new ShaderPipelineRenderNode(
            deviceContext: gpu,
            height: Extent,
            hostsOnDirectX: false,
            name: "placed",
            outputLayout: GpuImageLayout.ShaderReadOnly,
            packages: packages,
            width: Extent
        );

        node.Swap(pipeline: new CompiledShaderPipeline(
            plan: plan.Pipeline,
            shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
        ));
        node.BindImage(
            image: Base,
            name: "base"
        );
        node.BindImage(
            image: Source,
            name: "source"
        );

        return node;
    }
    private static void ProduceUntilPublished(ShaderPipelineRenderNode node) => Assert.True(
        condition: SpinWait.SpinUntil(
            condition: () => !node.ProduceFrame(context: default).IsEmpty,
            timeout: TimeSpan.FromSeconds(value: 30)
        ),
        userMessage: "The pass never published a frame."
    );
    // Where a member of the pass group binds.
    private static uint BindingOf(string member) => Layout.Layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass)).Resources.Single(predicate: resource => (resource.Member.Name == member)).Binding;
    // The rect and sharpness the last recorded dispatch read, from the pass block of the pass set it bound; nothing is
    // pushed.
    private static (float Left, float Top, float Width, float Height, float Sharpness) Read(FakePipelineGpu gpu) {
        Assert.Empty(collection: gpu.PushedConstants);

        var block = gpu.ConstantBlock(
            set: gpu.BoundSets.Last(predicate: static set => (set.Group == ((uint)ShaderInterfaceGroup.Pass))).Set,
            sizeBytes: ((int)Layout.SizeBytes)
        );
        var rect = ((int)Layout.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceRect));

        return (
            BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: rect)),
            BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: (rect + 4))),
            BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: (rect + 8))),
            BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: (rect + 12))),
            BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: ((int)Layout.BlockOffsetOf(member: RenderGraphPackageCatalog.PlaceSharpness))))
        );
    }

    [Fact]
    public void AHostPlacementReachesThePassBlockAndOneDispatchCoversTheOutput() {
        var gpu = new FakePipelineGpu();
        var placements = new Placements(shown: new RenderGraphPlacement(
            Height: 0.5f,
            Left: 0.5f,
            Sharpness: 1f,
            Shown: true,
            Top: 0.25f,
            Width: 0.25f
        ));

        using var node = Node(
            gpu: gpu,
            placements: placements
        );

        ProduceUntilPublished(node: node);
        gpu.Recording = true;
        _ = node.ProduceFrame(context: default);
        gpu.Recording = false;

        Assert.Equal(
            actual: Assert.Single(collection: gpu.Dispatches),
            expected: ((Extent / 8), (Extent / 8), 1u)
        );
        Assert.Equal(
            actual: Read(gpu: gpu),
            expected: (0.5f, 0.25f, 0.25f, 0.5f, 1f)
        );
        Assert.Contains(
            collection: gpu.DescriptorWrites,
            filter: static write => ((write.Binding == BindingOf(member: RenderGraphPackageCatalog.PlaceBase)) && (write.Handle == Base.ImageViewHandle))
        );
        Assert.Contains(
            collection: gpu.DescriptorWrites,
            filter: static write => ((write.Binding == BindingOf(member: RenderGraphPackageCatalog.PlaceSource)) && (write.Handle == Source.ImageViewHandle))
        );
        Assert.Equal(
            actual: placements.Asked,
            expected: [("placed", Pass)]
        );
    }
    [Fact]
    public void TheKernelReadsTheInterfaceItsPackageDeclares() {
        // The include is the generated declarations of the interface the catalog declares, byte for byte, so a document
        // pass compiling the kernel with ports named base, source and destination reads the same one.
        Assert.Equal(
            actual: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: $"src/Puck.Shaders/Assets/Shaders/Graph/{ShaderFrameInterface.IncludeFileName(interfaceName: Layout.Interface.Name)}")),
            expected: ShaderInterfaceHlsl.Generate(shaderInterface: Layout.Interface)
        );
        // The deployed kernel reads every block and binding where that interface places them.
        Assert.Null(@object: Layout.Layout.Mismatch(reflected: SpirvInterfaceReader.Read(module: File.ReadAllBytes(path: Path.Combine(paths: [
            AppContext.BaseDirectory,
            "Assets",
            "Shaders",
            "Graph",
            $"{PlacePackage.KernelStem}.spv",
        ])))));
    }
    [Fact]
    public void ASourceShownNowhereDrawsNothingAndTheBaseIsPublished() {
        var gpu = new FakePipelineGpu();

        using var node = Node(
            gpu: gpu,
            placements: new Placements(shown: default)
        );

        ProduceUntilPublished(node: node);
        gpu.Recording = true;

        var shown = node.ProduceFrame(context: default);

        gpu.Recording = false;

        Assert.Empty(collection: gpu.Dispatches);
        Assert.Equal(
            actual: shown.ImageHandle,
            expected: Base.ImageHandle
        );
    }
    [Fact]
    public void WithoutAHostPlacementTheDispatchReadsTheBoundConfig() {
        var gpu = new FakePipelineGpu();

        using var node = Node(
            config: JsonDocument.Parse(json: """{ "rect": [0.25, 0, 0.5, 1], "sharpness": 0.5 }""").RootElement.Clone(),
            gpu: gpu,
            placements: null
        );

        ProduceUntilPublished(node: node);
        gpu.Recording = true;
        _ = node.ProduceFrame(context: default);
        gpu.Recording = false;

        Assert.Equal(
            actual: Read(gpu: gpu),
            expected: (0.25f, 0f, 0.5f, 1f, 0.5f)
        );
    }
    [Fact]
    public void ASteadyPlacedFrameAllocatesNothing() {
        var gpu = new FakePipelineGpu();

        using var node = Node(
            gpu: gpu,
            placements: new Placements(shown: new RenderGraphPlacement(
                Height: 1f,
                Left: 0f,
                Sharpness: 0f,
                Shown: true,
                Top: 0f,
                Width: 0.5f
            ))
        );

        ProduceUntilPublished(node: node);

        for (var frame = 0; (frame < 8); frame++) {
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

    // A host that shows every pass's source at one placement, and records which passes asked.
    private sealed class Placements(RenderGraphPlacement shown) : IRenderGraphPlacements {
        public List<(string Instance, string Pass)> Asked { get; } = [];

        public bool TryGet(string instance, string pass, out RenderGraphPlacement placement) {
            if (Asked.Count < 1) {
                Asked.Add(item: (instance, pass));
            }

            placement = shown;

            return true;
        }
    }
}
