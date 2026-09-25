namespace Puck.Shaders.Tests;

public sealed class ShaderPipelineLivenessTests {
    private static ShaderPipelineResource Image(string name) => new(
        name,
        Format: "R8G8B8A8Unorm",
        Dimensions: ShaderPipelineDimensions.Relative()
    );
    private static ShaderPipelinePass Pass(string name, IReadOnlyList<ResourceReference> inputs, IReadOnlyList<ResourceReference> outputs) =>
        new(
            name,
            (name + ".hlsl"),
            "main",
            ShaderPipelineDocumentPassKind.Compute,
            inputs,
            outputs
        );

    [Fact]
    public void A_previous_frame_dependency_retains_its_writer_without_creating_a_same_frame_edge() {
        var definition = new ShaderPipelineDefinition(
            "history",
            [Image(name: "history") with { History = true, Initialization = ShaderPipelineInitialization.Zero }, Image(name: "image")],
            [Pass(
                    "display",
                    [new(
                            "history",
                            PreviousFrame: true
                        )],
                    ["image"]
                ), Pass(
                    inputs: [],
                    name: "simulate",
                    outputs: ["history"]
                )],
            ["image"]
        );
        var plan = new ShaderPipelineCompiler().Compile(definition: definition);

        Assert.Equal(
            ["display", "simulate"],
            plan.PassOrder
        );
        Assert.Empty(collection: plan.Passes[0].Dependencies);
        var history = Assert.Single(
            collection: plan.Resources,
            predicate: resource => (resource.Name == "history")
        );

        Assert.Equal(
            1,
            history.WriterPassIndex
        );
        Assert.Equal(
            0,
            history.FirstUsePassIndex
        );
        Assert.Equal(
            2,
            history.LastUsePassIndex
        );
    }
    [Fact]
    public void Publishing_an_intermediate_keeps_its_branch_live() {
        var definition = new ShaderPipelineDefinition(
            "inspection",
            [Image(name: "first"), Image(name: "second")],
            [Pass(
                    inputs: [],
                    name: "one",
                    outputs: ["first"]
                ), Pass(
                    inputs: [],
                    name: "two",
                    outputs: ["second"]
                )],
            ["first", "second"]
        );
        var plan = new ShaderPipelineCompiler().Compile(definition: definition);

        Assert.Equal(
            2,
            plan.Passes.Count
        );
        Assert.All(
            plan.Resources,
            resource => Assert.Equal(
                2,
                resource.LastUsePassIndex
            )
        );
    }
    [Fact]
    public void Unreachable_work_and_its_resources_are_excluded_from_execution() {
        var definition = new ShaderPipelineDefinition(
            "live",
            [Image(name: "unused"), Image(name: "intermediate"), Image(name: "image")],
            [Pass(
                    inputs: [],
                    name: "dead",
                    outputs: ["unused"]
                ), Pass(
                    inputs: [],
                    name: "source",
                    outputs: ["intermediate"]
                ), Pass(
                    inputs: ["intermediate"],
                    name: "display",
                    outputs: ["image"]
                )],
            ["image"]
        );
        var plan = new ShaderPipelineCompiler().Compile(definition: definition);

        Assert.Equal(
            ["source", "display"],
            plan.PassOrder
        );
        Assert.DoesNotContain(
            collection: plan.Resources,
            filter: resource => (resource.Name == "unused")
        );
        Assert.Equal(
            3,
            plan.Definition.Passes.Count
        );
        var intermediate = Assert.Single(
            collection: plan.Resources,
            predicate: resource => (resource.Name == "intermediate")
        );

        Assert.Equal(
            0,
            intermediate.FirstUsePassIndex
        );
        Assert.Equal(
            1,
            intermediate.LastUsePassIndex
        );
        Assert.Equal(
            2,
            Assert.Single(
                collection: plan.Resources,
                predicate: resource => (resource.Name == "image")
            ).LastUsePassIndex
        );
    }
}
