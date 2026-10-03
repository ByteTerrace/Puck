using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphHistoryLawTests {
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void AGraphicsReloadTargetsTheCarriedHistoryCursorAndItsOwnDepthSlot(int standingFrames) {
        var gpu = new FakePipelineGpu { Recording = true };
        var model = new Model();
        using var node = Node(gpu, model, publishHistory: true, colorWrite: true);

        node.ProduceUntilInstalled();
        var previous = model.Blends[^1].Output;
        var images = gpu.CreatedObjects.Count(predicate: item => (item.Kind == "R8G8B8A8Unorm image"));

        node.ProduceFrame(context: default);
        for (var frame = 0; (frame < standingFrames); frame++) { node.ProduceFrame(context: default); }
        Assert.Single(collection: model.Blends);
        node.Swap(pipeline: GraphicsHistory());
        node.ProduceBuildStart(gpu: gpu);
        gpu.RenderPasses.Clear(); gpu.DescriptorWrites.Clear(); gpu.Barriers.Clear();
        var published = node.ProduceFrame(context: default);
        var begun = Assert.Single(collection: gpu.RenderPasses);
        var color = Assert.Single(collection: begun.Colors);

        Assert.Equal(images, gpu.CreatedObjects.Count(predicate: item => (item.Kind == "R8G8B8A8Unorm image")));
        Assert.Contains(collection: gpu.DescriptorWrites, filter: write => (write.Handle == (previous + 1)));
        Assert.NotEqual(actual: color, expected: previous);
        Assert.Equal(published.ImageHandle, color);
        Assert.Contains(collection: gpu.Barriers, filter: barrier => ((barrier.Handle == color) && barrier.Barrier.DestinationAccess.HasFlag(flag: GpuAccess.ColorAttachmentWrite)));
        Assert.NotEqual(actual: begun.Depth, expected: 0);
        var depth = begun.Depth;

        for (var frame = 0; (frame < 4); frame++) {
            gpu.RenderPasses.Clear();
            published = node.ProduceFrame(context: default);
            begun = Assert.Single(collection: gpu.RenderPasses);
            Assert.Equal(published.ImageHandle, Assert.Single(collection: begun.Colors));
            Assert.NotEqual(actual: begun.Depth, expected: depth);
            depth = begun.Depth;
        }
        node.Reset();
        gpu.RenderPasses.Clear();
        published = node.ProduceFrame(context: default);
        begun = Assert.Single(collection: gpu.RenderPasses);
        Assert.Equal(published.ImageHandle, Assert.Single(collection: begun.Colors));
    }

    private static CompiledShaderPipeline GraphicsHistory() {
        var graph = new RenderGraphDefinition(name: "graphics-history", outputs: ["history"],
            resources: [
                new ShaderPipelineResource(Name: "history", Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative(), History: true, Initialization: ShaderPipelineInitialization.Zero),
                new ShaderPipelineResource(Name: "depth", Kind: ShaderPipelineResourceKind.Depth, Format: "D32Float", Dimensions: ShaderPipelineDimensions.Relative()),
            ],
            passes: [new ShaderPipelinePass(Name: "draw", Source: "draw.hlsl", EntryPoint: "ps", Kind: ShaderPipelineDocumentPassKind.Geometry,
                Inputs: [new ResourceReference(Name: "history", PreviousFrame: true)], Outputs: ["history", "depth"],
                Geometry: new ShaderPipelineGeometry(VertexEntryPoint: "vs", StrideBytes: 12,
                    Attributes: [new ShaderPipelineVertexAttribute(Location: 0, Format: "R32G32B32Float")],
                    Vertices: [-1f, -1f, 0.5f, 1f, -1f, 0.5f, -1f, 1f, 0.5f], Indices: [0, 1, 2]))]);
        var stages = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Vertex] = new byte[] { 1 }, [ShaderStage.Fragment] = new byte[] { 2 } };
        var compiled = new CompiledShader(name: "draw", sourcePath: "draw.hlsl", sourceHash: "graphics-history", spirv: stages, dxil: stages, diagnostics: []);

        return new CompiledShaderPipeline(plan: new ShaderPipelineCompiler().Compile(definition: graph), shaders: new Dictionary<string, CompiledShader> { ["draw"] = compiled });
    }
}
