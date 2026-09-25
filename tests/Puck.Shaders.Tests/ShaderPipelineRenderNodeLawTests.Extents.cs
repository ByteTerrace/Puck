using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// The node reads each pass's extent from its planned pass (<see cref="ShaderPipelinePlannedPass.ResolveExtent"/>) and
/// has no rule of its own: every compute pass dispatches over the planned extent at the node's size, and again at the
/// new size after a resize.
/// </summary>
public sealed partial class ShaderPipelineRenderNodeLawTests {
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
        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => {
                    _ = Produce(node: node);

                    return (node.Extent == (ResizedWidth, ResizedHeight));
                },
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The resized graph never installed."
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
