namespace Puck.Shaders.Tests;

public sealed class ShaderPipelineBindingTests {
    private static ShaderPipelineResource Image(string name, bool external = false) => new(name,
        Format: "R8G8B8A8Unorm", Dimensions: ShaderPipelineDimensions.Relative(),
        Initialization: external ? ShaderPipelineInitialization.External : ShaderPipelineInitialization.Undefined);
    private static ShaderPipelineDefinition Definition(ShaderPipelinePass pass) => new("bindings",
        [Image("input", external: true), Image("output")], [pass], ["output"]);
    private static ShaderPipelinePass Pass(ResourceReference input, ResourceReference output) =>
        new("pass", "pass.hlsl", ShaderSourceLanguage.Hlsl, "main", ShaderPipelinePassKind.Compute, [input], [output]);

    [Fact]
    public void Implicit_compute_output_precedes_input_in_the_canonical_plan() {
        var authored = Pass("input", "output");
        var plan = new ShaderPipelineCompiler().Compile(Definition(authored));
        var pass = Assert.Single(plan.Passes).Declaration;
        Assert.Equal(0u, Assert.Single(pass.OutputReferences).Binding);
        Assert.Equal(1u, Assert.Single(pass.InputReferences).Binding);
        Assert.Null(authored.InputReferences[0].Binding);
        Assert.Null(authored.OutputReferences[0].Binding);
    }

    [Fact]
    public void Explicit_input_slot_is_reserved_before_assigning_implicit_outputs() {
        var plan = new ShaderPipelineCompiler().Compile(Definition(Pass(new("input", Binding: 0), "output")));
        Assert.Equal(1u, plan.Passes[0].Declaration.OutputReferences[0].Binding);
        Assert.Equal(0u, plan.Passes[0].Declaration.InputReferences[0].Binding);
    }

    [Fact]
    public void Input_output_descriptor_collision_is_refused_before_gpu_creation() {
        var error = Assert.Throws<ShaderPipelineCompilationException>(() => new ShaderPipelineCompiler().Compile(
            Definition(Pass(new("input", Binding: 3), new("output", Binding: 3)))));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "SHADERPIPE_DUPLICATE_BINDING");
    }

    [Fact]
    public void Fullscreen_color_attachment_does_not_consume_a_descriptor_slot() {
        var authored = Pass("input", "output") with { Kind = ShaderPipelinePassKind.Fullscreen };
        var pass = new ShaderPipelineCompiler().Compile(Definition(authored)).Passes[0].Declaration;
        Assert.Equal(0u, pass.InputReferences[0].Binding);
        Assert.Null(pass.OutputReferences[0].Binding);
    }

    [Fact]
    public void Shadertoy_output_binding_must_match_the_adapter() {
        var authored = Pass("input", new("output", Binding: 4)) with { Language = ShaderSourceLanguage.ShadertoyGlsl };
        var error = Assert.Throws<ShaderPipelineCompilationException>(() => new ShaderPipelineCompiler().Compile(Definition(authored)));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "SHADERPIPE_SHADERTOY_BINDINGS");
    }

    [Fact]
    public void Persistent_history_still_requires_initialized_first_frame_contents() {
        var definition = new ShaderPipelineDefinition("history",
            [Image("output") with { Persistent = true, History = true }],
            [Pass(new("output", PreviousFrame: true), "output")], ["output"]);
        var error = Assert.Throws<ShaderPipelineCompilationException>(() => new ShaderPipelineCompiler().Compile(definition));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "SHADERPIPE_FEEDBACK_DECLARATION");
    }

    [Fact]
    public void Null_document_collections_produce_a_planner_diagnostic() {
        var definition = new ShaderPipelineDefinition("null", null!, [], []);
        var error = Assert.Throws<ShaderPipelineCompilationException>(() => new ShaderPipelineCompiler().Compile(definition));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "SHADERPIPE_DOCUMENT_SHAPE");
    }
    [Fact]
    public void Sparse_fullscreen_input_is_refused_before_native_pipeline_creation() {
        var pass = Pass(new("input", Binding: 3), "output") with { Kind = ShaderPipelinePassKind.Fullscreen };
        var error = Assert.Throws<ShaderPipelineCompilationException>(() => ShaderPipelineCompiler.Plan(Definition(pass)));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "SHADERPIPE_FULLSCREEN_BINDING");
    }

    [Fact]
    public void Fullscreen_attachment_cannot_silently_ignore_a_descriptor_binding() {
        var pass = Pass("input", new("output", Binding: 7)) with { Kind = ShaderPipelinePassKind.Fullscreen };
        var error = Assert.Throws<ShaderPipelineCompilationException>(() => ShaderPipelineCompiler.Plan(Definition(pass)));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "SHADERPIPE_FULLSCREEN_ATTACHMENT_BINDING");
    }
}
