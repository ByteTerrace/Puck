using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>A document pass's ports are members of its interface's pass group: where each binds follows from the pass,
/// never from the document, and the identifier a source reads it by is its resource's name in camel case or its
/// <c>as</c>. A collision, an <c>as</c> that is not an identifier, and a source that never names a port are each refused
/// by name.</summary>
public sealed class ShaderPipelinePortLawTests {
    private static RenderGraphDefinition Definition(ShaderPipelinePass pass, ShaderPipelineResource[]? extra = null, string[]? outputs = null) => new(
        "ports",
        [Image(
                "input",
                external: true
            ), Image("output"), .. (extra ?? [])],
        [pass],
        (outputs ?? ["output"])
    );
    private static ShaderPipelineResource Image(string name, bool external = false) => new(
        name,
        Format: "R8G8B8A8Unorm",
        Dimensions: ShaderPipelineDimensions.Relative(),
        Initialization: (external
        ? ShaderPipelineInitialization.External
        : ShaderPipelineInitialization.Undefined)
    );
    private static string InterfaceRefusal(RenderGraphDefinition definition) {
        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: definition));

        return Assert.Single(
            collection: error.Diagnostics,
            predicate: static diagnostic => (diagnostic.Code == "SHADERPIPE_INTERFACE")
        ).Message;
    }
    private static ShaderPipelinePass Pass(ResourceReference input, ResourceReference output) =>
        new(
            "pass",
            "pass.hlsl",
            "main",
            ShaderPipelineDocumentPassKind.Compute,
            [input],
            [output]
        );
    private static ShaderInterfaceGroupLayout PassGroup(ShaderPipelinePlannedPass pass) =>
        Assert.Single(
            collection: pass.Parameters.Layout.Groups,
            predicate: static group => (group.Group == ShaderInterfaceGroup.Pass)
        );

    [Fact]
    public void A_compute_pass_binds_its_block_then_its_inputs_then_its_outputs_in_the_pass_group() {
        var pass = Assert.Single(collection: new ShaderPipelineCompiler().Compile(definition: Definition(pass: Pass(
            input: "input",
            output: "output"
        ))).Passes);
        var group = PassGroup(pass: pass);

        Assert.False(condition: pass.Parameters.IsPushed);
        Assert.Equal(
            actual: group.Set,
            expected: 3u
        );
        Assert.Equal(
            actual: group.Resources.Select(selector: static resource => (resource.Member.Name, resource.Binding, resource.Kind)),
            expected: [
                ("input", 1u, GpuBindingKind.SampledImage),
                ("inputSampler", 2u, GpuBindingKind.Sampler),
                ("output", 3u, GpuBindingKind.StorageImage),
            ]
        );
        Assert.Equal(
            actual: group.BlockMembers[0].Name,
            expected: ShaderFrameInterface.Extent
        );
        Assert.Equal(
            actual: Assert.Single(
                collection: pass.Parameters.Layout.Groups,
                predicate: static group => (group.Group == ShaderInterfaceGroup.Frame)
            ).Set,
            expected: 0u
        );
    }
    [Fact]
    public void A_buffer_input_is_a_read_only_buffer_and_a_compute_buffer_output_a_read_write_one() {
        var pass = new ShaderPipelinePass(
            "pass",
            "pass.hlsl",
            "main",
            ShaderPipelineDocumentPassKind.Compute,
            ["table"],
            ["output", "counts"]
        );
        var planned = Assert.Single(collection: new ShaderPipelineCompiler().Compile(definition: Definition(
            extra: [
                new ShaderPipelineResource(
                    "table",
                    Initialization: ShaderPipelineInitialization.External,
                    Kind: ShaderPipelineResourceKind.Buffer,
                    SizeBytes: 16
                ),
                new ShaderPipelineResource(
                    "counts",
                    Kind: ShaderPipelineResourceKind.Buffer,
                    SizeBytes: 16
                ),
            ],
            outputs: ["output", "counts"],
            pass: pass
        )).Passes);

        Assert.Equal(
            actual: PassGroup(pass: planned).Resources.Select(selector: static resource => (resource.Member.Name, resource.Kind)),
            expected: [
                ("table", GpuBindingKind.ReadOnlyBuffer),
                ("output", GpuBindingKind.StorageImage),
                ("counts", GpuBindingKind.ReadWriteBuffer),
            ]
        );
    }
    [Fact]
    public void A_graphics_color_attachment_binds_nothing() {
        var pass = Pass(
            input: "input",
            output: "output"
        ) with { Kind = ShaderPipelineDocumentPassKind.Fullscreen };
        var planned = Assert.Single(collection: new ShaderPipelineCompiler().Compile(definition: Definition(pass: pass)).Passes);

        Assert.Equal(
            actual: PassGroup(pass: planned).Resources.Select(selector: static resource => resource.Member.Name),
            expected: ["input", "inputSampler"]
        );
    }
    [Fact]
    public void A_graphics_color_attachment_cannot_name_as() {
        var pass = Pass(
            input: "input",
            output: new(
                "output",
                As: "color"
            )
        ) with { Kind = ShaderPipelineDocumentPassKind.Fullscreen };
        var error = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => ShaderPipelineCompiler.Plan(definition: Definition(pass: pass)));

        Assert.Contains(
            collection: error.Diagnostics,
            filter: static diagnostic => (diagnostic.Code == "SHADERPIPE_GRAPHICS_ATTACHMENT_AS")
        );
    }
    [InlineData("palette-region", false, null, "paletteRegion")]
    [InlineData("a_b.c", false, null, "aBC")]
    [InlineData("history", true, null, "previousHistory")]
    [InlineData("history", true, "past", "past")]
    [InlineData("output", false, "image", "image")]
    [InlineData("_", true, null, null)]
    [InlineData("-.", false, null, null)]
    [InlineData("2d-map", false, null, null)]
    [Theory]
    public void A_port_reads_as_its_resource_name_in_camel_case_unless_it_names_as(string name, bool previousFrame, string? alias, string? expected) {
        var reference = new ResourceReference(
            As: alias,
            Name: name,
            PreviousFrame: previousFrame
        );

        if (expected is null) {
            Assert.Contains(
                actualString: Assert.Throws<InvalidDataException>(testCode: () => ShaderPipelinePassPorts.Identifier(reference: reference)).Message,
                expectedSubstring: "not an HLSL identifier"
            );
            return;
        }

        Assert.Equal(
            actual: ShaderPipelinePassPorts.Identifier(reference: reference),
            expected: expected
        );
    }
    [InlineData("_")]
    [InlineData("2d-map")]
    [Theory]
    public void A_resource_name_reading_as_no_identifier_is_refused_by_the_pass(string name) {
        var message = InterfaceRefusal(definition: Definition(
            extra: [Image(
                name,
                external: true
            )],
            pass: Pass(
                input: name,
                output: "output"
            )
        ));

        Assert.Contains(
            actualString: message,
            expectedSubstring: $"pass 'pass' port '{name}' reads as"
        );
    }
    [Fact]
    public void Two_ports_of_one_pass_reading_as_one_identifier_are_refused_by_name() {
        var message = InterfaceRefusal(definition: Definition(pass: Pass(
            input: "input",
            output: new(
                "output",
                As: "input"
            )
        )));

        Assert.Contains(
            actualString: message,
            expectedSubstring: "input 'input' and output 'output' both read as 'input'"
        );
        Assert.Contains(
            actualString: message,
            expectedSubstring: "\"as\""
        );
    }
    [InlineData("time")]
    [InlineData("extent")]
    [InlineData("gain")]
    [Theory]
    public void A_port_reading_as_a_frame_value_or_a_config_field_is_refused_by_name(string alias) {
        var message = InterfaceRefusal(definition: Definition(pass: (Pass(
            input: new(
                "input",
                As: alias
            ),
            output: "output"
        ) with {
            Config = new Dictionary<string, ShaderConfigField>(comparer: StringComparer.Ordinal) { ["gain"] = new(Type: ShaderValueType.Float) },
        })));

        Assert.Contains(
            actualString: message,
            expectedSubstring: $"input 'input' reads as '{alias}', which a frame value or config field of the pass already names"
        );
    }
    [InlineData("2d")]
    [InlineData("a-b")]
    [InlineData("")]
    [Theory]
    public void An_as_that_is_not_an_hlsl_identifier_is_refused(string alias) {
        var message = InterfaceRefusal(definition: Definition(pass: Pass(
            input: new(
                "input",
                As: alias
            ),
            output: "output"
        )));

        Assert.Contains(
            actualString: message,
            expectedSubstring: "not an HLSL identifier"
        );
    }
    [Fact]
    public void A_port_is_named_by_a_token_of_its_source_or_an_include_outside_comments() {
        var pass = Pass(
            input: "input",
            output: "output"
        );

        Assert.Null(@object: ShaderPipelinePassPorts.UnnamedPort(
            pass: pass,
            texts: ["#include \"body.hlsli\"\n", "float4 f() { return input.Load(0); }\nvoid g() { output[uint2(0, 0)] = 0; }\n"]
        ));
        Assert.Contains(
            actualString: ShaderPipelinePassPorts.UnnamedPort(
                pass: pass,
                texts: ["// input and output, in a comment only.\n/* output */ float4 h() { return inputs + outputs; }\n"]
            ),
            expectedSubstring: "input 'input' reads as 'input'"
        );
    }
    [Fact]
    public void A_source_that_never_names_a_port_is_refused_at_load_naming_the_pass_the_port_its_identifier_and_as() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-ports-");

        try {
            var graph = Path.Combine(
                path1: directory.FullName,
                path2: "renamed.graph.json"
            );

            File.WriteAllText(
                contents: """
                {
                  "$schema": "puck.render.graph.v1",
                  "name": "renamed",
                  "resources": [
                    { "name": "color", "kind": "Image", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Absolute", "width": 8, "height": 8 } }
                  ],
                  "passes": [
                    { "name": "fill", "source": "fill.hlsl", "entryPoint": "main", "kind": "Compute", "outputs": [ { "name": "color" } ] }
                  ],
                  "outputs": ["color"]
                }
                """,
                path: graph
            );
            File.WriteAllText(
                contents: "#include \"fill.interface.hlsli\"\n[numthreads(8, 8, 1)]\nvoid main(uint3 id : SV_DispatchThreadID) {\n    image[id.xy] = float4(1.0, 0.0, 0.0, 1.0);\n}\n",
                path: Path.Combine(
                    path1: directory.FullName,
                    path2: "fill.hlsl"
                )
            );

            var result = new ShaderPipelineLoader(compiler: new ShaderCompiler(cacheDirectory: directory.CreateSubdirectory(path: "cache").FullName)).Load(
                cancellationToken: TestContext.Current.CancellationToken,
                name: "renamed",
                path: graph
            );

            Assert.Equal(
                actual: result.Status,
                expected: ShaderPipelineLoadStatus.Failed
            );
            Assert.Equal(
                actual: result.Message,
                expected: "[SHADERPIPE_INTERFACE] Pass 'fill' output 'color' reads as 'color', which 'fill.hlsl' never names; give the port \"as\": \"<identifier>\" with the name the source reads it by."
            );
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void Null_document_collections_produce_a_planner_diagnostic() {
        var definition = new RenderGraphDefinition(
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
    public void History_still_requires_initialized_first_frame_contents() {
        var definition = new RenderGraphDefinition(
            "history",
            [Image("output") with { History = true }],
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
}
