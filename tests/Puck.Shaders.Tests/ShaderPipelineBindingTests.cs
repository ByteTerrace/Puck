namespace Puck.Shaders.Tests;

public sealed class ShaderPipelineBindingTests {
    private static ShaderPipelineDefinition Definition(ShaderPipelinePass pass) => new(
        "bindings",
        [Image(
                "input",
                external: true
            ), Image("output")],
        [pass],
        ["output"]
    );
    private static ShaderPipelineResource Image(string name, bool external = false) => new(
        name,
        Format: "R8G8B8A8Unorm",
        Dimensions: ShaderPipelineDimensions.Relative(),
        Initialization: (external
        ? ShaderPipelineInitialization.External
        : ShaderPipelineInitialization.Undefined)
    );
    private static ShaderPipelinePass Pass(ResourceReference input, ResourceReference output) =>
        new(
            "pass",
            "pass.hlsl",
            ShaderSourceLanguage.Hlsl,
            "main",
            ShaderPipelinePassKind.Compute,
            [input],
            [output]
        );

    [Fact]
    public void Explicit_input_slot_is_reserved_before_assigning_implicit_outputs() {
        var plan = new ShaderPipelineCompiler().Compile(definition: Definition(pass: Pass(
            input: new(
                "input",
                Binding: 0
            ),
            output: "output"
        )));

        Assert.Equal(
            1u,
            plan.Passes[0].Declaration.OutputReferences[0].Binding
        );
        Assert.Equal(
            0u,
            plan.Passes[0].Declaration.InputReferences[0].Binding
        );
    }
    [Fact]
    public void Fullscreen_attachment_cannot_silently_ignore_a_descriptor_binding() {
        var pass = Pass(
            input: "input",
            output: new(
                "output",
                Binding: 7
            )
        ) with { Kind = ShaderPipelinePassKind.Fullscreen };
        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => ShaderPipelineCompiler.Plan(definition: Definition(pass: pass)));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_FULLSCREEN_ATTACHMENT_BINDING")
        );
    }
    [Fact]
    public void Fullscreen_color_attachment_does_not_consume_a_descriptor_slot() {
        var authored = Pass(
            input: "input",
            output: "output"
        ) with { Kind = ShaderPipelinePassKind.Fullscreen };
        var pass = new ShaderPipelineCompiler().Compile(definition: Definition(pass: authored)).Passes[0].Declaration;

        Assert.Equal(
            0u,
            pass.InputReferences[0].Binding
        );
        Assert.Null(value: pass.OutputReferences[0].Binding);
    }
    [Fact]
    public void Implicit_compute_output_precedes_input_in_the_canonical_plan() {
        var authored = Pass(
            input: "input",
            output: "output"
        );
        var plan = new ShaderPipelineCompiler().Compile(definition: Definition(pass: authored));
        var pass = Assert.Single(collection: plan.Passes).Declaration;

        Assert.Equal(
            0u,
            Assert.Single(collection: pass.OutputReferences).Binding
        );
        Assert.Equal(
            1u,
            Assert.Single(collection: pass.InputReferences).Binding
        );
        Assert.Null(value: authored.InputReferences[0].Binding);
        Assert.Null(value: authored.OutputReferences[0].Binding);
    }
    [Fact]
    public void Input_output_descriptor_collision_is_refused_before_gpu_creation() {
        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: Definition(pass: Pass(
            input: new(
                "input",
                Binding: 3
            ),
            output: new(
                "output",
                Binding: 3
            )
        ))));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_DUPLICATE_BINDING")
        );
    }
    [Fact]
    public void Null_document_collections_produce_a_planner_diagnostic() {
        var definition = new ShaderPipelineDefinition(
            name: "null",
            outputs: [],
            passes: [],
            resources: null!
        );
        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_DOCUMENT_SHAPE")
        );
    }
    [Fact]
    public void Persistent_history_still_requires_initialized_first_frame_contents() {
        var definition = new ShaderPipelineDefinition(
            "history",
            [Image("output") with { Persistent = true, History = true }],
            [Pass(
                    input: new(
                        "output",
                        PreviousFrame: true
                    ),
                    output: "output"
                )],
            ["output"]
        );
        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_FEEDBACK_DECLARATION")
        );
    }
    [Fact]
    public void Shadertoy_output_binding_must_match_the_adapter() {
        var authored = Pass(
            input: "input",
            output: new(
                "output",
                Binding: 4
            )
        ) with { Language = ShaderSourceLanguage.ShadertoyGlsl };
        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: Definition(pass: authored)));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_SHADERTOY_BINDINGS")
        );
    }
    [Fact]
    public void Sparse_fullscreen_input_is_refused_before_native_pipeline_creation() {
        var pass = Pass(
            input: new(
                "input",
                Binding: 3
            ),
            output: "output"
        ) with { Kind = ShaderPipelinePassKind.Fullscreen };
        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => ShaderPipelineCompiler.Plan(definition: Definition(pass: pass)));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_FULLSCREEN_BINDING")
        );
    }
}
