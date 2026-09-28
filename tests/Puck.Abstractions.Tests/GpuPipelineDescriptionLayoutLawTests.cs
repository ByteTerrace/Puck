using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for a pipeline description created from a <see cref="GpuPipelineLayoutDescription"/>: a compute description's
/// layout is a compute pipeline's and a graphics description's a graphics pipeline's, and a compute description that
/// states its bindings twice, a layout beside the bindings it replaces, is refused by name before any backend plans it.
/// </summary>
public sealed class GpuPipelineDescriptionLayoutLawTests {
    private static GpuPipelineLayoutDescription Layout(GpuShaderStage stages) => new(
        groups: [new GpuGroupLayoutDescription(
            bindings: [new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ConstantBuffer)],
            ordinal: 3
        )],
        pushesIndex: true,
        stages: stages
    );
    private static GpuGraphicsPipelineDescription Graphics(GpuPipelineLayoutDescription layout) => new(
        Layout: layout,
        Name: "graphics",
        VertexInput: new GpuVertexInputLayout(Attributes: [], StrideBytes: 0)
    );

    [Fact]
    public void A_layout_of_the_pipelines_own_stages_is_the_one_it_binds() {
        var compute = Layout(stages: GpuShaderStage.Compute);
        var graphics = Layout(stages: GpuShaderStage.Vertex | GpuShaderStage.Fragment);

        Assert.Same(
            actual: new GpuComputePipelineDescription(Bindings: [], Layout: compute, Name: "compute", PushConstantBinding: null).RequireLayout(),
            expected: compute
        );
        Assert.Same(
            actual: Graphics(layout: graphics).RequireLayout(),
            expected: graphics
        );
    }
    [Fact]
    public void A_layout_of_the_other_pipeline_kinds_stages_is_refused_by_name() {
        Assert.Contains(
            actualString: Assert.Throws<ArgumentException>(testCode: () => new GpuComputePipelineDescription(Bindings: [], Layout: Layout(stages: GpuShaderStage.Fragment), Name: "compute", PushConstantBinding: null).RequireLayout()).Message,
            expectedSubstring: "Compute pipeline 'compute' has a layout for the stages 'Fragment', not compute."
        );
        Assert.Contains(
            actualString: Assert.Throws<ArgumentException>(testCode: () => Graphics(layout: Layout(stages: GpuShaderStage.Compute)).RequireLayout()).Message,
            expectedSubstring: "Graphics pipeline 'graphics' has a layout for the compute stage."
        );
    }
    [Fact]
    public void A_layout_beside_the_bindings_it_replaces_is_refused_by_name() {
        var compute = Layout(stages: GpuShaderStage.Compute);
        var push = new GpuPushConstantBinding(data: new byte[4], offset: 0, stageFlags: GpuShaderStage.Compute);

        foreach (var description in ((GpuComputePipelineDescription[])[
            new(Bindings: [new GpuComputeBinding(Binding: 0, Kind: GpuBindingKind.StorageImage)], Layout: compute, Name: "compute", PushConstantBinding: null),
            new(Bindings: [], Layout: compute, Name: "compute", PushConstantBinding: push),
        ])) {
            Assert.StartsWith(
                actualString: Assert.Throws<ArgumentException>(testCode: () => description.RequireLayout()).Message,
                expectedStartString: "Compute pipeline 'compute' states its bindings twice"
            );
        }
    }
}
