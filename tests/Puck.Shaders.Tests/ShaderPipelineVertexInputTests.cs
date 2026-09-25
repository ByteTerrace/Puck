using System.Text.Json;

namespace Puck.Shaders.Tests;

/// <summary>A fullscreen pass's <c>vertex</c> member: the document spelling, the planner's refusal on a compute pass, and
/// the render node binding the shared fullscreen-triangle vertex buffer exactly for a <c>Position</c> pass.</summary>
public sealed class ShaderPipelineVertexInputTests {
    private static ShaderPipelineDefinition Copy(ShaderPipelineVertexInput? vertex, ShaderPipelinePassKind secondKind = ShaderPipelinePassKind.Fullscreen) => new(
        name: "copy",
        outputs: ["image"],
        passes: [
            new ShaderPipelinePass(
                EntryPoint: "main",
                Kind: ShaderPipelinePassKind.Compute,
                Name: "fill",
                Outputs: [new ResourceReference(
                    Binding: 0,
                    Name: "gray"
                )],
                Source: "fill.hlsl"
            ),
            new ShaderPipelinePass(
                EntryPoint: "main",
                Inputs: [new ResourceReference(
                    Binding: ((secondKind == ShaderPipelinePassKind.Fullscreen)
                        ? 0u
                        : 1u),
                    Name: "gray"
                )],
                Kind: secondKind,
                Name: "copy",
                Outputs: [((secondKind == ShaderPipelinePassKind.Fullscreen)
                    ? new ResourceReference(Name: "image")
                    : new ResourceReference(
                        Binding: 0,
                        Name: "image"
                    ))],
                Source: "copy.hlsl",
                Vertex: vertex
            ),
        ],
        resources: [
            new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Absolute(
                    height: 8,
                    width: 8
                ),
                Format: "R8G8B8A8Unorm",
                Name: "gray"
            ),
            new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Absolute(
                    height: 8,
                    width: 8
                ),
                Format: "R8G8B8A8Unorm",
                Name: "image"
            ),
        ]
    );
    private static int VertexBuffersCreated(ShaderPipelineVertexInput? vertex) {
        ReadOnlyMemory<byte> bytecode = new byte[] { 0x03, 0x02, 0x23, 0x07 };
        var plan = new ShaderPipelineCompiler().Compile(definition: Copy(vertex: vertex));
        var shaders = new Dictionary<string, CompiledShader> {
            ["fill"] = new(
                diagnostics: [],
                dxil: new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Compute] = bytecode },
                name: "fill",
                sourceHash: "fill",
                sourcePath: "fill.hlsl",
                spirv: new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Compute] = bytecode }
            ),
            ["copy"] = new(
                diagnostics: [],
                dxil: new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Vertex] = bytecode, [ShaderStage.Fragment] = bytecode },
                name: "copy",
                sourceHash: "copy",
                sourcePath: "copy.hlsl",
                spirv: new Dictionary<ShaderStage, ReadOnlyMemory<byte>> { [ShaderStage.Vertex] = bytecode, [ShaderStage.Fragment] = bytecode }
            ),
        };
        var gpu = new FakePipelineGpu();

        using (var node = new ShaderPipelineRenderNode(
            deviceContext: gpu,
            height: 8,
            hostsOnDirectX: false,
            name: "copy",
            width: 8
        )) {
            node.Swap(pipeline: new CompiledShaderPipeline(
                plan: plan,
                shaders: shaders
            ));
            _ = node.ProduceUntilInstalled();
            Assert.True(condition: node.IsReady);
        }

        return gpu.CreatedObjects.Count(predicate: static created => (created.Kind == "Vertex buffer"));
    }

    [Fact]
    public void APositionPassReadsTheSharedVertexBufferAndAVertexIdPassBindsNone() {
        Assert.Equal(
            expected: (Position: 1, VertexId: 0, Omitted: 0),
            actual: (
                Position: VertexBuffersCreated(vertex: ShaderPipelineVertexInput.Position),
                VertexId: VertexBuffersCreated(vertex: ShaderPipelineVertexInput.VertexId),
                Omitted: VertexBuffersCreated(vertex: null)
            )
        );
    }
    [Fact]
    public void AComputePassDeclaringVertexIsRefusedByName() {
        var refusal = Assert.Throws<ShaderPipelineCompilationException>(testCode: () => new ShaderPipelineCompiler().Compile(definition: Copy(
            secondKind: ShaderPipelinePassKind.Compute,
            vertex: ShaderPipelineVertexInput.Position
        )));

        Assert.Contains(
            collection: refusal.Diagnostics,
            filter: static diagnostic => ((diagnostic.Code == "SHADERPIPE_VERTEX_INPUT") && (diagnostic.Name == "copy"))
        );
    }
    [Fact]
    public void TheDocumentSpellsTheVertexInputByNameAndRefusesAnUnknownOne() {
        const string Pass = """{ "name": "copy", "source": "copy.hlsl", "entryPoint": "main", "kind": "Fullscreen", "vertex": "{0}" }""";
        var parsed = JsonSerializer.Deserialize(
            json: Pass.Replace(
                newValue: "Position",
                oldValue: "{0}"
            ),
            jsonTypeInfo: ShaderPipelineJsonContext.Default.ShaderPipelinePass
        );

        Assert.Equal(
            expected: ShaderPipelineVertexInput.Position,
            actual: parsed!.Vertex
        );
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: Pass.Replace(
                newValue: "Triangle",
                oldValue: "{0}"
            ),
            jsonTypeInfo: ShaderPipelineJsonContext.Default.ShaderPipelinePass
        ));
    }
}
