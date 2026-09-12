namespace Puck.Shaders.Tests;

public sealed class ShaderPipelineLivenessTests {
    private static ShaderPipelineResource Image(string name) => new(name, Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative());
    private static ShaderPipelinePass Pass(string name, IReadOnlyList<ResourceReference> inputs, IReadOnlyList<ResourceReference> outputs) =>
        new(name, name + ".hlsl", ShaderSourceLanguage.Hlsl, "main", ShaderPipelinePassKind.Compute, inputs, outputs);

    [Fact]
    public void Unreachable_work_and_its_resources_are_excluded_from_execution() {
        var definition = new ShaderPipelineDefinition("live", [Image("unused"), Image("intermediate"), Image("image")],
            [Pass("dead", [], ["unused"]), Pass("source", [], ["intermediate"]), Pass("display", ["intermediate"], ["image"])], ["image"]);
        var plan = new ShaderPipelineCompiler().Compile(definition);
        Assert.Equal(["source", "display"], plan.PassOrder);
        Assert.DoesNotContain(plan.Resources, resource => resource.Name == "unused");
        Assert.Equal(3, plan.Definition.Passes.Count);
        var intermediate = Assert.Single(plan.Resources, resource => resource.Name == "intermediate");
        Assert.Equal(0, intermediate.FirstUsePassIndex);
        Assert.Equal(1, intermediate.LastUsePassIndex);
        Assert.Equal(2, Assert.Single(plan.Resources, resource => resource.Name == "image").LastUsePassIndex);
    }

    [Fact]
    public void A_previous_frame_dependency_retains_its_writer_without_creating_a_same_frame_edge() {
        var definition = new ShaderPipelineDefinition("history",
            [Image("history") with { History = true, Persistent = true, Initialization = ShaderPipelineInitialization.Zero }, Image("image")],
            [Pass("display", [new("history", PreviousFrame: true)], ["image"]), Pass("simulate", [], ["history"])], ["image"]);
        var plan = new ShaderPipelineCompiler().Compile(definition);
        Assert.Equal(["display", "simulate"], plan.PassOrder);
        Assert.Empty(plan.Passes[0].Dependencies);
        var history = Assert.Single(plan.Resources, resource => resource.Name == "history");
        Assert.Equal(1, history.WriterPassIndex);
        Assert.Equal(0, history.FirstUsePassIndex);
        Assert.Equal(2, history.LastUsePassIndex);
    }

    [Fact]
    public void Publishing_an_intermediate_keeps_its_branch_live() {
        var definition = new ShaderPipelineDefinition("inspection", [Image("first"), Image("second")],
            [Pass("one", [], ["first"]), Pass("two", [], ["second"])], ["first", "second"]);
        var plan = new ShaderPipelineCompiler().Compile(definition);
        Assert.Equal(2, plan.Passes.Count);
        Assert.All(plan.Resources, resource => Assert.Equal(2, resource.LastUsePassIndex));
    }
}
