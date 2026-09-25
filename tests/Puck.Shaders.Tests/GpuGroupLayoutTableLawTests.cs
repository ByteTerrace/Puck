using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>Holds the gate spike's interfaces to the neutral tables both backends' planners are tested against, so the
/// groups the bytecode readers confirmed are the groups the planners place.</summary>
public sealed class GpuGroupLayoutTableLawTests {
    private static string Describe(GpuPipelineLayoutDescription description) =>
        ($"push {description.PushesIndex}: " + string.Join(
            separator: " | ",
            values: description.Groups.Select(selector: static group => $"group {group.Ordinal} [{string.Join(separator: ", ", values: group.Bindings.Select(selector: static binding => $"{binding.Binding}:{binding.Kind}x{binding.Count}"))}]")
        ));

    [Fact]
    public void Film_grains_interface_lays_out_the_film_grain_table() {
        Assert.Equal(
            actual: Describe(description: ShaderInterfaceSpike.FilmGrain.Layout().PipelineLayout(pushesIndex: true)),
            expected: Describe(description: GpuGroupLayoutTables.FilmGrain(pushesIndex: true))
        );
    }
    [Fact]
    public void Pixelates_interface_lays_out_the_pixelate_table() {
        Assert.Equal(
            actual: Describe(description: ShaderInterfaceSpike.Pixelate.Layout().PipelineLayout(pushesIndex: false)),
            expected: Describe(description: GpuGroupLayoutTables.Pixelate(pushesIndex: false))
        );
    }
    [Fact]
    public void An_interface_that_pushes_a_block_has_no_pipeline_layout() {
        var exception = Assert.Throws<InvalidOperationException>(testCode: () => ShaderFrameInterface.For(config: null, name: "echo").Layout().PipelineLayout(pushesIndex: false));

        Assert.Contains(
            actualString: exception.Message,
            expectedSubstring: "pushes its Frame block"
        );
    }
}
