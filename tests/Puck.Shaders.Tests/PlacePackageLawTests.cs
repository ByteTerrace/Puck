using System.Buffers.Binary;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws of the <c>place</c> package (<see cref="PlacePackage"/>) on <see cref="FakePipelineGpu"/>: the deployed kernel
/// run as a graph's package pass dispatches once over the output's extent with its base and source written at their
/// bindings; a host placement replaces the pushed rect and sharpness without rebinding the config; a source the host
/// shows nowhere draws nothing and the node publishes the base in the output's place; with no host placement the bound
/// config's rect is what is pushed; and a steady placed frame allocates nothing.
/// </summary>
public sealed class PlacePackageLawTests {
    private const uint Extent = 64;
    private const string Pass = "pane";

    private static readonly ShaderPipelineExternalImage Base = Image(handle: 0x7000);
    private static readonly ShaderPipelineExternalImage Source = Image(handle: 0x8000);

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
    // The rect and sharpness the last recorded frame pushed, read at the frame block's config offsets.
    private static (float Left, float Top, float Width, float Height, float Sharpness) Pushed(FakePipelineGpu gpu) {
        var block = gpu.PushedConstants[^1].Data;

        return (
            BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: 96)),
            BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: 100)),
            BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: 104)),
            BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: 108)),
            BinaryPrimitives.ReadSingleLittleEndian(source: block.AsSpan(start: 112))
        );
    }

    [Fact]
    public void AHostPlacementIsPushedAndOneDispatchCoversTheOutput() {
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
            actual: Pushed(gpu: gpu),
            expected: (0.5f, 0.25f, 0.25f, 0.5f, 1f)
        );
        Assert.Contains(
            collection: gpu.DescriptorWrites,
            filter: static write => ((write.Binding == PlacePackage.BaseBinding) && (write.Handle == Base.ImageViewHandle))
        );
        Assert.Contains(
            collection: gpu.DescriptorWrites,
            filter: static write => ((write.Binding == PlacePackage.SourceBinding) && (write.Handle == Source.ImageViewHandle))
        );
        Assert.Equal(
            actual: placements.Asked,
            expected: [("placed", Pass)]
        );
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
    public void WithoutAHostPlacementTheBoundConfigIsPushed() {
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
            actual: Pushed(gpu: gpu),
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
