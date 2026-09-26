using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>Holds the gate spike's interfaces to the neutral tables both backends' planners are tested against, so the
/// groups the bytecode readers confirmed are the groups the planners place.</summary>
public sealed class GpuGroupLayoutTableLawTests {
    private static string Describe(GpuPipelineLayoutDescription description) =>
        ($"stages {description.Stages} push {description.PushesIndex}: " + string.Join(
            separator: " | ",
            values: description.Groups.Select(selector: static group => $"group {group.Ordinal} [{string.Join(separator: ", ", values: group.Bindings.Select(selector: static binding => $"{binding.Binding}:{binding.Kind}x{binding.Count}"))}]")
        ));

    [Fact]
    public void Film_grains_interface_lays_out_the_film_grain_table() {
        Assert.Equal(
            actual: Describe(description: ShaderInterfaceSpike.FilmGrain.Layout().PipelineLayout(
                stages: ShaderPipelineDocumentPassKind.Fullscreen.Stages()
            )),
            expected: Describe(description: GpuGroupLayoutTables.FilmGrain(pushesIndex: false))
        );
    }
    [Fact]
    public void An_interface_that_pushes_an_index_lays_out_a_table_pushing_one() {
        var pushing = new ShaderInterface(
            members: ShaderInterfaceSpike.FilmGrain.Members,
            name: ShaderInterfaceSpike.FilmGrain.Name,
            pushesIndex: true
        );

        Assert.Equal(
            actual: Describe(description: pushing.Layout().PipelineLayout(stages: ShaderPipelineDocumentPassKind.Fullscreen.Stages())),
            expected: Describe(description: GpuGroupLayoutTables.FilmGrain(pushesIndex: true))
        );
    }
    [Fact]
    public void Pixelates_interface_lays_out_the_pixelate_table() {
        Assert.Equal(
            actual: Describe(description: ShaderInterfaceSpike.Pixelate.Layout().PipelineLayout(
                stages: ShaderPipelineDocumentPassKind.Compute.Stages()
            )),
            expected: Describe(description: GpuGroupLayoutTables.Pixelate(pushesIndex: false))
        );
    }
    [Fact]
    public void A_pass_kind_names_the_stages_its_pipeline_layout_is_visible_to() {
        Assert.Equal(
            actual: (ShaderPipelineDocumentPassKind.Compute.Stages(), ShaderPipelineDocumentPassKind.Fullscreen.Stages(), ShaderPipelineDocumentPassKind.Geometry.Stages()),
            expected: (GpuShaderStage.Compute, GpuShaderStage.Vertex | GpuShaderStage.Fragment, GpuShaderStage.Vertex | GpuShaderStage.Fragment)
        );
    }
}
