using System.Text.Json;
using Puck.Abstractions.Gpu;
using Puck.Hosting;


namespace Puck.Shaders.Tests;

public sealed class ShaderPipelineTests {
    // Where a pass's first config field can start in its pass block: after the extent, a uint2 at 0.
    private const uint ExtentEnd = 8;
    // The resources a pass of these tests writes.
    private static readonly IReadOnlyDictionary<string, ShaderPipelineResource> OutResources = new Dictionary<string, ShaderPipelineResource>(comparer: StringComparer.Ordinal) { ["out"] = Image(name: "out") };

    private static ShaderPipelineResource Image(
        string name,
        ShaderPipelineInitialization initialization = ShaderPipelineInitialization.Undefined,
        bool history = false
    ) => new(
        Name: name,
        Format: "R8G8B8A8Unorm",
        Dimensions: ShaderPipelineDimensions.Relative(),
        History: history,
        Initialization: initialization
    );
    private static ShaderPipelinePass Pass(
        string name,
        IReadOnlyList<ResourceReference> inputs,
        IReadOnlyList<ResourceReference> outputs,
        ShaderPipelineDocumentPassKind kind = ShaderPipelineDocumentPassKind.Compute
    ) => new(
        name,
        $"{name}.hlsl",
        "main",
        kind,
        inputs,
        outputs
    );

    [Fact]
    public void Cycle_diagnostic_names_the_path() {
        var definition = new RenderGraphDefinition(
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
        var invalidInitialization = new RenderGraphDefinition(
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

        var invalidDimensions = new RenderGraphDefinition(
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

        var invalidFormat = new RenderGraphDefinition(
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
    public void Fullscreen_draws_into_its_declared_color_format_and_an_image_never_declares_a_depth_format() {
        RenderGraphDefinition Fullscreen(string format) => new(
            "fullscreen-format",
            [new ShaderPipelineResource(
                    "out",
                    Format: format,
                    Dimensions: ShaderPipelineDimensions.Relative()
                )],
            [Pass(
                    inputs: [],
                    kind: ShaderPipelineDocumentPassKind.Fullscreen,
                    name: "draw",
                    outputs: ["out"]
                )],
            ["out"]
        );

        Assert.Equal(
            expected: GpuAttachmentLoad.Clear,
            actual: new ShaderPipelineCompiler().Compile(definition: Fullscreen(format: "R16G16B16A16Float")).Passes[0].Attachments.Single().Load
        );

        var depthFormat = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: Fullscreen(format: "D32Float")));

        Assert.Contains(
            collection: depthFormat.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_RESOURCE_FORMAT")
        );
    }
    [Fact]
    public void A_graph_document_declares_config_only_on_its_passes() {
        var json = "{\"$schema\":\"puck.render.graph.v1\",\"name\":\"global\",\"resources\":[],\"passes\":[],\"outputs\":[],\"config\":{\"gain\":{\"type\":\"Float\"}}}";

        Assert.Throws<JsonException>(testCode: () => RenderGraphDefinition.Parse(json: json));
    }
    [Fact]
    public void An_image_declaring_a_buffer_size_is_rejected() {
        var definition = new RenderGraphDefinition(
            "fields",
            [
            new ShaderPipelineResource(
                    "image",
                    Format: "R8G8B8A8Unorm",
                    Dimensions: ShaderPipelineDimensions.Relative(),
                    SizeBytes: 4
                ),
            ],
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
    }
    [InlineData("elementType", "\"Float4\"")]
    [InlineData("elementFormat", "\"R32Uint\"")]
    [Theory]
    public void A_buffer_carrying_a_typed_layout_member_fails_as_an_unknown_member(string member, string value) {
        var json = $$"""{ "name": "buffer", "kind": "Buffer", "sizeBytes": 64, "{{member}}": {{value}} }""";
        var refusal = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: json,
            jsonTypeInfo: RenderGraphJsonContext.Default.ShaderPipelineResource
        ));

        Assert.Contains(
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: member,
            actualString: refusal.Message
        );
        Assert.NotNull(@object: JsonSerializer.Deserialize(
            json: """{ "name": "buffer", "kind": "Buffer", "sizeBytes": 64 }""",
            jsonTypeInfo: RenderGraphJsonContext.Default.ShaderPipelineResource
        ));
    }
    [Fact]
    public void Independent_passes_use_authored_order_as_the_tie_breaker() {
        var definition = new RenderGraphDefinition(
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
        var definition = new RenderGraphDefinition(
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
    public void Json_graph_documents_reject_unknown_properties_and_missing_schema() {
        var json = "{\"$schema\":\"puck.render.graph.v1\",\"name\":\"empty\",\"resources\":[],\"passes\":[],\"outputs\":[],\"unexpected\":true}";

        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: json,
            jsonTypeInfo: RenderGraphJsonContext.Default.RenderGraphDefinition
        ));

        var definition = new RenderGraphDefinition(
            null!,
            "empty",
            [],
            [],
            []
        );
        var exception = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => RenderGraphCompiler.ShaderPasses.Compile(definition: definition));

        Assert.Contains(
            collection: exception.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "RENDERGRAPH_SCHEMA")
        );
    }
    [Fact]
    public void The_planner_refuses_package_passes_a_graph_compiler_has_not_checked() {
        var definition = new RenderGraphDefinition(
            name: "packaged",
            outputs: ["out"],
            packages: [new RenderGraphPackagePass(
                Name: "world",
                Outputs: ["out"],
                Package: RenderGraphPackageCatalog.SdfWorld
            )],
            passes: [],
            resources: [Image("out")]
        );
        var planner = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));
        var pipelineHost = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => RenderGraphCompiler.ShaderPasses.Compile(definition: definition));

        Assert.Equal(
            expected: ["SHADERPIPE_PACKAGE_PASS"],
            actual: planner.Diagnostics.Select(selector: static diagnostic => diagnostic.Code)
        );
        Assert.Equal(
            expected: ["RENDERGRAPH_PACKAGE_UNKNOWN"],
            actual: pipelineHost.Diagnostics.Select(selector: static diagnostic => diagnostic.Code)
        );
        var planned = new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).Compile(definition: definition).Pipeline.Passes.Single();

        Assert.Equal(
            expected: ShaderPipelinePassKind.Package,
            actual: planned.Kind
        );
        // The planned pass carries its step, not a declaration: the package, its ports and its extent.
        Assert.Null(@object: planned.Declaration);
        Assert.Equal(
            expected: (RenderGraphPackageCatalog.SdfWorld, "out", 0, ((uint)64), ((uint)32)),
            actual: (planned.Package!.Package, planned.Package.Outputs.Single().Name, planned.Package.Inputs.Count, planned.Package.ResolveExtent(frameHeight: 32, frameWidth: 64).Width, planned.Package.ResolveExtent(frameHeight: 32, frameWidth: 64).Height)
        );
    }
    [Fact]
    public void A_one_off_source_is_an_hlsl_compute_pass_unless_its_kind_is_given() {
        var compute = RenderGraphDefinition.FromShaderSource(
            "compute",
            "effect.hlsl"
        );

        Assert.Equal(
            ShaderPipelineDocumentPassKind.Compute,
            compute.ShaderPasses[0].Kind
        );
        Assert.Equal(
            "main",
            compute.ShaderPasses[0].EntryPoint
        );
        Assert.Equal(
            ShaderPipelineDocumentPassKind.Fullscreen,
            RenderGraphDefinition.FromShaderSource(
                kind: ShaderPipelineDocumentPassKind.Fullscreen,
                name: "fragment",
                sourcePath: "effect.frag.hlsl"
            ).ShaderPasses[0].Kind
        );
        Assert.Throws<ArgumentException>(testCode: () => RenderGraphDefinition.FromShaderSource(
            "glsl",
            "effect.glsl"
        ));
        Assert.Throws<ArgumentException>(testCode: () => RenderGraphDefinition.FromShaderSource(
            "unknown",
            "effect.shader"
        ));
    }
    [Fact]
    public void Parameter_layout_binds_defaults_after_the_pass_extent() {
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
        var layout = ShaderPipelineParameterLayout.Resolve(
            pass: pass,
            resources: OutResources
        );

        Assert.Equal(
            expected: ExtentEnd,
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
        var layout = ShaderPipelineParameterLayout.Resolve(
            pass: (Pass(
                "configured",
                [],
                ["out"]
            ) with { Config = config }),
            resources: OutResources
        );

        Assert.Equal(
            expected: ["aScalar", "mVector", "zVector"],
            actual: layout.Slots.Select(selector: slot => slot.Name)
        );
        // A float3 starts a 16-byte row and a float2 an 8-byte boundary, whatever the extent before them.
        Assert.Equal(
            expected: ExtentEnd,
            actual: layout.Slots[0].Offset
        );
        Assert.Equal(
            expected: 16u,
            actual: layout.Slots[1].Offset
        );
        Assert.Equal(
            expected: 32u,
            actual: layout.Slots[2].Offset
        );
    }
    [Fact]
    public void Passes_without_outputs_are_rejected() {
        var definition = new RenderGraphDefinition(
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
        var definition = new RenderGraphDefinition(
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

        Assert.Single(collection: plan.Passes[0].Declaration!.InputReferences);
        Assert.Single(collection: plan.Passes[0].Declaration!.OutputReferences);
        Assert.Equal(
            expected: 2,
            actual: plan.Resources.Count
        );
    }
    [Fact]
    public void Planner_orders_same_frame_dependencies_deterministically() {
        var definition = new RenderGraphDefinition(
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
    public void Planner_refuses_misused_depth_graphics_mrt_and_graphics_buffers() {
        var depth = new RenderGraphDefinition(
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

        Assert.Equal(
            expected: ["SHADERPIPE_DEPTH_INITIALIZATION", "SHADERPIPE_DEPTH_PUBLIC", "SHADERPIPE_DEPTH_WRITER"],
            actual: depthError.Diagnostics.Select(selector: static diagnostic => diagnostic.Code).Order(comparer: StringComparer.Ordinal)
        );

        var mrt = new RenderGraphDefinition(
            name: "mrt",
            resources: [Image("color"), Image("velocity")],
            passes: [Pass(
                    inputs: [],
                    kind: ShaderPipelineDocumentPassKind.Fullscreen,
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

        var fullscreenBuffer = new RenderGraphDefinition(
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
                    kind: ShaderPipelineDocumentPassKind.Fullscreen,
                    name: "draw",
                    outputs: ["out"]
                )],
            outputs: ["out"]
        );
        var bufferError = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: fullscreenBuffer));

        Assert.Contains(
            collection: bufferError.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_UNSUPPORTED_GRAPHICS_BUFFER")
        );
    }
    [Fact]
    public void Planner_rejects_a_pushed_block_that_exceeds_the_portable_push_constant_budget() {
        var pass = Pass(
            "draw",
            [],
            ["out"]
        ) with {
            Config = new Dictionary<string, ShaderConfigField> {
                ["a"] = new(ShaderValueType.Float4),
                ["b"] = new(ShaderValueType.Float4),
                ["c"] = new(ShaderValueType.Float),
            },
        };
        var definition = new RenderGraphDefinition(
            "large-config",
            [Image("out")],
            [pass],
            ["out"]
        );

        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => ShaderPipelineCompiler.PlanShaderSet(
            definition: definition,
            pushed: new Dictionary<string, ShaderPipelineParameterLayout>(comparer: StringComparer.Ordinal) {
                ["draw"] = ShaderPipelineParameterLayout.Pushed(
                    config: pass.Config,
                    interfaceName: "draw"
                ),
            }
        ));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_PUSH_CONSTANT_LIMIT")
        );
        // The same config fits a document pass's pass block, which it binds as a constant buffer.
        Assert.Single(collection: new ShaderPipelineCompiler().Compile(definition: definition).Passes);
    }
    [Fact]
    public void Planner_rejects_a_pass_block_that_exceeds_the_portable_uniform_range() {
        var definition = new RenderGraphDefinition(
            "huge-config",
            [Image("out")],
            [Pass(
                    "draw",
                    [],
                    ["out"]
                ) with {
                Config = Enumerable.Range(
                    count: 1024,
                    start: 0
                ).ToDictionary(
                    elementSelector: static _ => new ShaderConfigField(ShaderValueType.Float4),
                    keySelector: static index => $"field{index}"
                ),
            }],
            ["out"]
        );

        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: diagnostic => (diagnostic.Code == "SHADERPIPE_PASS_BLOCK_LIMIT")
        );
    }
    [Fact]
    public void Planner_rejects_nonportable_workgroup_dimensions_and_invocations() {
        var oversizedDimension = new RenderGraphDefinition(
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

        var oversizedProduct = new RenderGraphDefinition(
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
    public void Planner_reports_the_resource_limit() {
        var definition = new RenderGraphDefinition(
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
                    SizeBytes: 64
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
    }
    [Fact]
    public void Previous_frame_feedback_requires_history_and_initialization() {
        var missingDeclaration = new RenderGraphDefinition(
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

        var valid = new RenderGraphDefinition(
            name: "feedback",
            resources: [Image(
                    "state",
                    ShaderPipelineInitialization.Zero,
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
            history: true
        );
        var definition = new RenderGraphDefinition(
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
        var display = new ShaderPipelineCompiler().Compile(definition: definition).Passes.Single(predicate: pass => (pass.Name == "display")).Declaration!;

        Assert.Equal(
            2,
            display.InputReferences.Count
        );
        Assert.Equal(
            actual: display.InputReferences.Select(selector: ShaderPipelinePassPorts.Identifier),
            expected: ["state", "previousState"]
        );
    }
    [Fact]
    public void Typed_external_buffer_and_compute_outputs_are_supported() {
        var definition = new RenderGraphDefinition(
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
            actual: plan.Passes[0].Declaration!.OutputReferences.Count
        );
        Assert.Equal(
            expected: 64ul,
            actual: plan.Resources.Single(predicate: resource => (resource.Name == "input")).Declaration.SizeBytes
        );
    }
    [Fact]
    public void Uninitialized_resources_and_multiple_writers_are_refused() {
        var definition = new RenderGraphDefinition(
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
