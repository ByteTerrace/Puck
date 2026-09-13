using System.Text.Json;


namespace Puck.Shaders.Tests;

public sealed class ShaderPipelineTests {
    private static ShaderPipelineResource Image(
        string name,
        ShaderPipelineInitialization initialization = ShaderPipelineInitialization.Undefined,
        bool persistent = false,
        bool history = false
    ) => new(
        Name: name,
        Format: "R8G8B8A8Unorm",
        Dimensions: ShaderPipelineDimensions.Relative(),
        Persistent: persistent,
        History: history,
        Initialization: initialization
    );
    private static ShaderPipelinePass Pass(
        string name,
        IReadOnlyList<ResourceReference> inputs,
        IReadOnlyList<ResourceReference> outputs,
        ShaderPipelinePassKind kind = ShaderPipelinePassKind.Compute
    ) => new(
        name,
        $"{name}.hlsl",
        ShaderSourceLanguage.Hlsl,
        "main",
        kind,
        inputs,
        outputs
    );

    [Fact]
    public void Cycle_diagnostic_names_the_path() {
        var definition = new ShaderPipelineDefinition(
            name: "cycle",
            resources: [Image("a"), Image("b")],
            passes: [Pass(
                    "one",
                    ["b"],
                    ["a"]
                ), Pass(
                    "two",
                    ["a"],
                    ["b"]
                )],
            outputs: ["a"]
        );

        var exception = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));

        var diagnostic = Assert.Single(
            collection: exception.Diagnostics,
            predicate: diagnostic => (diagnostic.Code == "SHADERPIPE_CYCLE")
        );

        Assert.Contains(
            expectedSubstring: "one",
            actualString: diagnostic.Message
        );
        Assert.Contains(
            expectedSubstring: "two",
            actualString: diagnostic.Message
        );
        Assert.Contains(
            expectedSubstring: "->",
            actualString: diagnostic.Message
        );
    }
    [Fact]
    public void Direct_construction_rejects_invalid_initialization_dimension_and_format_values() {
        var invalidInitialization = new ShaderPipelineDefinition(
            "init",
            [Image("out") with { Initialization = ((ShaderPipelineInitialization)99) }],
            [Pass(
                    "draw",
                    [],
                    ["out"]
                )],
            ["out"]
        );
        var initError = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: invalidInitialization));

        Assert.Contains(
            collection: initError.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_INITIALIZATION")
        );

        var invalidDimensions = new ShaderPipelineDefinition(
            "dimensions",
            [Image("out") with { Dimensions = new ShaderPipelineDimensions(
                    Height: 1,
                    Mode: ((ShaderPipelineDimensionMode)99),
                    Width: 1
                ) }],
            [Pass(
                    "draw",
                    [],
                    ["out"]
                )],
            ["out"]
        );
        var dimensionsError = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: invalidDimensions));

        Assert.Contains(
            collection: dimensionsError.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_DIMENSION_MODE")
        );

        var invalidFormat = new ShaderPipelineDefinition(
            "format",
            [new ShaderPipelineResource(
                    "out",
                    Format: "UnknownFormat",
                    Dimensions: ShaderPipelineDimensions.Relative()
                )],
            [Pass(
                    "draw",
                    [],
                    ["out"]
                )],
            ["out"]
        );
        var formatError = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: invalidFormat));

        Assert.Contains(
            collection: formatError.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_RESOURCE_FORMAT")
        );
    }
    [Fact]
    public void Fullscreen_and_shadertoy_reject_runtime_unsupported_output_formats() {
        var fullscreen = new ShaderPipelineDefinition(
            "fullscreen-format",
            [new ShaderPipelineResource(
                    "out",
                    Format: "R16G16B16A16Float",
                    Dimensions: ShaderPipelineDimensions.Relative()
                )],
            [Pass(
                    inputs: [],
                    kind: ShaderPipelinePassKind.Fullscreen,
                    name: "draw",
                    outputs: ["out"]
                )],
            ["out"]
        );
        var fullscreenError = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: fullscreen));

        Assert.Contains(
            collection: fullscreenError.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_FULLSCREEN_FORMAT")
        );

        var shadertoy = new ShaderPipelineDefinition(
            "toy-format",
            [new ShaderPipelineResource(
                    "out",
                    Format: "B8G8R8A8Unorm",
                    Dimensions: ShaderPipelineDimensions.Relative()
                )],
            [Pass(
                    "draw",
                    [],
                    ["out"]
                ) with { Language = ShaderSourceLanguage.ShadertoyGlsl }],
            ["out"]
        );
        var shadertoyError = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: shadertoy));

        Assert.Contains(
            collection: shadertoyError.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_SHADERTOY_FORMAT")
        );
    }
    [Fact]
    public void Global_config_is_rejected_until_pipeline_scope_is_runtime_bound() {
        var definition = new ShaderPipelineDefinition(
            name: "global",
            resources: [Image("out")],
            passes: [Pass(
                    "draw",
                    [],
                    ["out"]
                )],
            outputs: ["out"]
        ) {
            Config = new Dictionary<string, ShaderConfigField> {
                ["gain"] = new(ShaderValueType.Float),
            },
        };

        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_GLOBAL_CONFIG_UNSUPPORTED")
        );
    }
    [Fact]
    public void Incompatible_resource_kind_fields_and_typed_buffers_are_rejected() {
        var definition = new ShaderPipelineDefinition(
            "fields",
            [
            new ShaderPipelineResource(
                    "image",
                    Format: "R8G8B8A8Unorm",
                    Dimensions: ShaderPipelineDimensions.Relative(),
                    SizeBytes: 4
                ),
            new ShaderPipelineResource(
                    "buffer",
                    ShaderPipelineResourceKind.Buffer,
                    Initialization: ShaderPipelineInitialization.External,
                    SizeBytes: 64,
                    ElementType: ShaderValueType.Float4,
                    StrideBytes: 16
                )],
            [Pass(
                    "draw",
                    [],
                    ["image"]
                )],
            ["image"]
        );
        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_RESOURCE_KIND_FIELDS")
        );
        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_UNSUPPORTED_TYPED_BUFFER")
        );
    }
    [Fact]
    public void Independent_passes_use_authored_order_as_the_tie_breaker() {
        var definition = new ShaderPipelineDefinition(
            name: "independent",
            resources: [Image("a"), Image("b")],
            passes: [Pass(
                    "z",
                    [],
                    ["a"]
                ), Pass(
                    "a",
                    [],
                    ["b"]
                )],
            outputs: ["a", "b"]
        );

        Assert.Equal(
            expected: ["z", "a"],
            actual: new ShaderPipelineCompiler().Compile(definition: definition).PassOrder
        );
    }
    [Fact]
    public void Initialized_only_graphs_are_rejected_without_live_passes() {
        var definition = new ShaderPipelineDefinition(
            "initialized",
            [Image(
                    "out",
                    ShaderPipelineInitialization.External
                )],
            [],
            ["out"]
        );
        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_NO_LIVE_PASSES")
        );
    }
    [Fact]
    public void Json_pipeline_documents_reject_unknown_properties_and_missing_schema() {
        var json = "{\"$schema\":\"puck.shader.pipeline.v1\",\"name\":\"empty\",\"resources\":[],\"passes\":[],\"outputs\":[],\"unexpected\":true}";

        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: json,
            jsonTypeInfo: ShaderPipelineJsonContext.Default.ShaderPipelineDefinition
        ));

        var definition = new ShaderPipelineDefinition(
            null!,
            "empty",
            [],
            [],
            []
        );
        var exception = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));

        Assert.Contains(
            collection: exception.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_SCHEMA")
        );
    }
    [Fact]
    public void One_off_source_inference_rejects_ambiguous_extensions() {
        var compute = ShaderPipelineDefinition.FromShaderSource(
            "compute",
            "effect.comp"
        );

        Assert.Equal(
            ShaderSourceLanguage.Glsl,
            compute.Passes[0].Language
        );
        Assert.Equal(
            ShaderPipelinePassKind.Compute,
            compute.Passes[0].Kind
        );
        var fragment = ShaderPipelineDefinition.FromShaderSource(
            "fragment",
            "effect.frag"
        );

        Assert.Equal(
            ShaderSourceLanguage.Glsl,
            fragment.Passes[0].Language
        );
        Assert.Equal(
            ShaderPipelinePassKind.Fullscreen,
            fragment.Passes[0].Kind
        );
        Assert.Throws<ArgumentException>(testCode: () => ShaderPipelineDefinition.FromShaderSource(
            "vertex",
            "effect.vert"
        ));
        Assert.Throws<ArgumentException>(testCode: () => ShaderPipelineDefinition.FromShaderSource(
            "unknown",
            "effect.shader"
        ));
    }
    [Fact]
    public void Parameter_layout_binds_defaults_after_frame_prefix() {
        var config = new Dictionary<string, ShaderConfigField> {
            ["amount"] = new ShaderConfigField(
            ShaderValueType.Float,
            Default: JsonDocument.Parse("0.25").RootElement.Clone()
        ),
            ["count"] = new ShaderConfigField(
            ShaderValueType.Uint,
            Default: JsonDocument.Parse("3").RootElement.Clone()
        ),
        };
        var pass = Pass(
            "configured",
            [],
            ["out"]
        ) with { Config = config };
        var layout = ShaderPipelineParameterLayout.Resolve(pass: pass);

        Assert.Equal(
            expected: ((uint)ShaderFrameConstants.SizeBytes),
            actual: layout.Slots[0].Offset
        );
        Assert.True(
            condition: layout.TryBind(
                config: null,
                reason: out var reason,
                values: out var values
            ),
            reason
        );
        Assert.Equal(
            expected: 0x3E800000u,
            actual: BitConverter.ToUInt32(value: values.Bytes.Span.Slice(
                ((int)layout.Slots[0].Offset),
                4
            ))
        );
        Assert.Equal(
            expected: 3u,
            actual: BitConverter.ToUInt32(value: values.Bytes.Span.Slice(
                ((int)layout.Slots[1].Offset),
                4
            ))
        );
    }
    [Fact]
    public void Parameter_layout_uses_ordinal_names_for_vector_packing() {
        var config = new Dictionary<string, ShaderConfigField> {
            ["zVector"] = new ShaderConfigField(ShaderValueType.Float2),
            ["aScalar"] = new ShaderConfigField(ShaderValueType.Float),
            ["mVector"] = new ShaderConfigField(ShaderValueType.Float3),
        };
        var layout = ShaderPipelineParameterLayout.Resolve(pass: Pass(
            "configured",
            [],
            ["out"]
        ) with { Config = config });

        Assert.Equal(
            expected: ["aScalar", "mVector", "zVector"],
            actual: layout.Slots.Select(selector: slot => slot.Name)
        );
        Assert.Equal(
            expected: ((uint)ShaderFrameConstants.SizeBytes),
            actual: layout.Slots[0].Offset
        );
        Assert.Equal(
            expected: (((uint)ShaderFrameConstants.SizeBytes) + 4),
            actual: layout.Slots[1].Offset
        );
        Assert.Equal(
            expected: (((uint)ShaderFrameConstants.SizeBytes) + 16),
            actual: layout.Slots[2].Offset
        );
    }
    [Fact]
    public void Passes_without_outputs_are_rejected() {
        var definition = new ShaderPipelineDefinition(
            "no-output",
            [Image(
                    "out",
                    ShaderPipelineInitialization.External
                )],
            [Pass(
                    "draw",
                    ["out"],
                    []
                )],
            ["out"]
        );
        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_PASS_OUTPUTS")
        );
    }
    [Fact]
    public void Plan_snapshots_nested_collections() {
        var inputs = new List<ResourceReference> { "input" };
        var outputs = new List<ResourceReference> { "out" };
        var resources = new List<ShaderPipelineResource> { Image(
            "input",
            ShaderPipelineInitialization.External
        ), Image("out") };
        var passes = new List<ShaderPipelinePass> { Pass(
            "draw",
            inputs,
            outputs
        ) };
        var definition = new ShaderPipelineDefinition(
            name: "snapshot",
            outputs: ["out"],
            passes: passes,
            resources: resources
        );
        var plan = new ShaderPipelineCompiler().Compile(definition: definition);

        inputs.Add(item: "out");
        outputs.Clear();
        resources.Clear();
        passes.Clear();

        Assert.Single(collection: plan.Passes[0].Declaration.InputReferences);
        Assert.Single(collection: plan.Passes[0].Declaration.OutputReferences);
        Assert.Equal(
            expected: 2,
            actual: plan.Resources.Count
        );
    }
    [Fact]
    public void Planner_orders_same_frame_dependencies_deterministically() {
        var definition = new ShaderPipelineDefinition(
            name: "chain",
            resources: [
                Image("final"),
                Image("middle"),
                Image(
                    "input",
                    ShaderPipelineInitialization.External
                ),
            ],
            passes: [
                Pass(
                    "second",
                    ["middle"],
                    ["final"]
                ),
                Pass(
                    "first",
                    ["input"],
                    ["middle"]
                ),
            ],
            outputs: ["final"]
        );

        var plan = new ShaderPipelineCompiler().Compile(definition: definition);

        Assert.Equal(
            expected: ["first", "second"],
            actual: plan.PassOrder
        );
        Assert.Equal(
            expected: 0,
            actual: plan.Passes[1].Dependencies.Single()
        );
        Assert.Equal(
            expected: 1,
            actual: plan.Resources.Single(predicate: resource => (resource.Name == "final")).WriterPassIndex
        );
    }
    [Fact]
    public void Planner_refuses_unimplemented_depth_and_fullscreen_mrt() {
        var depth = new ShaderPipelineDefinition(
            name: "depth",
            resources: [new ShaderPipelineResource(
                    "depth",
                    ShaderPipelineResourceKind.Depth,
                    Format: "D32Float",
                    Dimensions: ShaderPipelineDimensions.Relative(),
                    Initialization: ShaderPipelineInitialization.Zero
                )],
            passes: [Pass(
                    "depthPass",
                    [],
                    ["depth"]
                )],
            outputs: ["depth"]
        );
        var depthError = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: depth));

        Assert.Contains(
            collection: depthError.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_UNSUPPORTED_DEPTH")
        );

        var mrt = new ShaderPipelineDefinition(
            name: "mrt",
            resources: [Image("color"), Image("velocity")],
            passes: [Pass(
                    inputs: [],
                    kind: ShaderPipelinePassKind.Fullscreen,
                    name: "draw",
                    outputs: ["color", "velocity"]
                )],
            outputs: ["color", "velocity"]
        );
        var mrtError = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: mrt));

        Assert.Contains(
            collection: mrtError.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_UNSUPPORTED_MRT")
        );

        var fullscreenBuffer = new ShaderPipelineDefinition(
            name: "fullscreen-buffer",
            resources: [
                new ShaderPipelineResource(
                    "input",
                    ShaderPipelineResourceKind.Buffer,
                    Initialization: ShaderPipelineInitialization.External,
                    SizeBytes: 64
                ),
                Image("out")],
            passes: [Pass(
                    inputs: ["input"],
                    kind: ShaderPipelinePassKind.Fullscreen,
                    name: "draw",
                    outputs: ["out"]
                )],
            outputs: ["out"]
        );
        var bufferError = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: fullscreenBuffer));

        Assert.Contains(
            collection: bufferError.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_UNSUPPORTED_FULLSCREEN_BUFFER")
        );
    }
    [Fact]
    public void Planner_rejects_config_blocks_that_exceed_portable_push_constant_budget() {
        var definition = new ShaderPipelineDefinition(
            "large-config",
            [Image("out")],
            [Pass(
                    "draw",
                    [],
                    ["out"]
                ) with {
                Config = new Dictionary<string, ShaderConfigField> {
                    ["a"] = new(ShaderValueType.Float4),
                    ["b"] = new(ShaderValueType.Float4),
                },
            }],
            ["out"]
        );

        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_PUSH_CONSTANT_LIMIT")
        );
    }
    [Fact]
    public void Planner_rejects_nonportable_workgroup_dimensions_and_invocations() {
        var oversizedDimension = new ShaderPipelineDefinition(
            "group-dimension",
            [Image("out")],
            [Pass(
                    "draw",
                    [],
                    ["out"]
                ) with { GroupSizeX = 256 }],
            ["out"]
        );
        var dimensionError = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: oversizedDimension));

        Assert.Contains(
            collection: dimensionError.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_WORKGROUP_LIMIT")
        );

        var oversizedProduct = new ShaderPipelineDefinition(
            "group-product",
            [Image("out")],
            [Pass(
                    "draw",
                    [],
                    ["out"]
                ) with { GroupSizeX = 16, GroupSizeY = 16 }],
            ["out"]
        );
        var productError = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: oversizedProduct));

        Assert.Contains(
            collection: productError.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_WORKGROUP_INVOCATIONS")
        );
    }
    [Fact]
    public void Planner_reports_resource_limit_and_invalid_typed_buffer() {
        var definition = new ShaderPipelineDefinition(
            name: "limits",
            resources: [
                Image(
                    "a",
                    ShaderPipelineInitialization.Zero
                ),
                new ShaderPipelineResource(
                    "b",
                    ShaderPipelineResourceKind.Buffer,
                    Initialization: ShaderPipelineInitialization.External,
                    SizeBytes: 64,
                    ElementType: ShaderValueType.Float4,
                    StrideBytes: 4
                ),
            ],
            passes: [],
            outputs: []
        );

        var exception = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler(limits: new ShaderPipelineLimits(MaxResources: 1)).Compile(definition: definition));

        Assert.Contains(
            collection: exception.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_LIMIT_RESOURCES")
        );
        Assert.Contains(
            collection: exception.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_BUFFER_STRIDE")
        );
    }
    [Fact]
    public void Previous_frame_feedback_requires_persistent_history_and_initialization() {
        var missingDeclaration = new ShaderPipelineDefinition(
            name: "feedback",
            resources: [Image("state")],
            passes: [Pass(
                    "step",
                    [new ResourceReference(
                            "state",
                            PreviousFrame: true
                        )],
                    ["state"]
                )],
            outputs: ["state"]
        );

        var exception = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: missingDeclaration));

        Assert.Contains(
            collection: exception.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_FEEDBACK_DECLARATION")
        );

        var valid = new ShaderPipelineDefinition(
            name: "feedback",
            resources: [Image(
                    "state",
                    ShaderPipelineInitialization.Zero,
                    persistent: true,
                    history: true
                )],
            passes: [Pass(
                    "step",
                    [new ResourceReference(
                            "state",
                            PreviousFrame: true
                        )],
                    ["state"]
                )],
            outputs: ["state"]
        );

        Assert.Equal(
            expected: ["step"],
            actual: new ShaderPipelineCompiler().Compile(definition: valid).PassOrder
        );
    }
    [Fact]
    public void Relative_dimensions_resolve_subpixel_scales_and_clamp_to_one() {
        Assert.Equal(
            expected: (320u, 180u),
            actual: ShaderPipelineDimensions.Relative(
                height: 0.5,
                width: 0.5
            ).Resolve(
                frameHeight: 360,
                frameWidth: 640
            )
        );
        Assert.Equal(
            expected: (1u, 1u),
            actual: ShaderPipelineDimensions.Relative(
                height: 0.001,
                width: 0.001
            ).Resolve(
                frameHeight: 360,
                frameWidth: 640
            )
        );
        Assert.Equal(
            expected: (1920u, 1080u),
            actual: ShaderPipelineDimensions.Absolute(
                height: 1080,
                width: 1920
            ).Resolve(
                frameHeight: 360,
                frameWidth: 640
            )
        );
    }
    [Fact]
    public void Same_resource_current_and_previous_inputs_get_distinct_bindings() {
        var state = Image(
            "state",
            ShaderPipelineInitialization.Zero,
            persistent: true,
            history: true
        );
        var definition = new ShaderPipelineDefinition(
            "dual-read",
            [state, Image("out")],
            [
            Pass(
                    "display",
                    [new ResourceReference("state"), new ResourceReference(
                            "state",
                            PreviousFrame: true
                        )],
                    ["out"]
                ),
            Pass(
                    "simulate",
                    [],
                    ["state"]
                )],
            ["out"]
        );
        var display = new ShaderPipelineCompiler().Compile(definition: definition).Passes.Single(predicate: pass => (pass.Name == "display")).Declaration;

        Assert.Equal(
            2,
            display.InputReferences.Count
        );
        Assert.NotEqual(
            display.InputReferences[0].Binding,
            display.InputReferences[1].Binding
        );
    }
    [Fact]
    public void Typed_external_buffer_and_compute_outputs_are_supported() {
        var definition = new ShaderPipelineDefinition(
            name: "typed",
            resources: [
                new ShaderPipelineResource(
                    "input",
                    ShaderPipelineResourceKind.Buffer,
                    Initialization: ShaderPipelineInitialization.External,
                    SizeBytes: 64
                ),
                Image("color"),
                Image("velocity"),
            ],
            passes: [Pass(
                    "draw",
                    ["input"],
                    ["color", "velocity"]
                )],
            outputs: ["color", "velocity"]
        );

        var plan = new ShaderPipelineCompiler().Compile(definition: definition);

        Assert.Equal(
            expected: 2,
            actual: plan.Passes[0].Declaration.OutputReferences.Count
        );
        Assert.Equal(
            expected: 64ul,
            actual: plan.Resources.Single(predicate: resource => (resource.Name == "input")).Declaration.SizeBytes
        );
    }
    [Fact]
    public void Uninitialized_resources_and_multiple_writers_are_refused() {
        var definition = new ShaderPipelineDefinition(
            name: "invalid",
            resources: [Image("out"), Image("unused")],
            passes: [Pass(
                    "one",
                    [],
                    ["out"]
                ), Pass(
                    "two",
                    [],
                    ["out"]
                )],
            outputs: ["out"]
        );

        var exception = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));

        Assert.Contains(
            collection: exception.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_SINGLE_WRITER")
        );
        Assert.Contains(
            collection: exception.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_UNINITIALIZED_RESOURCE")
        );
    }
}
