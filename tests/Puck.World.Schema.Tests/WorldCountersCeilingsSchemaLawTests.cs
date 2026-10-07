using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The generated ceilings schema and the runtime reader share the compact document shape.</summary>
public sealed class WorldCountersCeilingsSchemaLawTests {
    private static readonly Lazy<Json.Schema.JsonSchema> Schema = new(valueFactory: () => SchemaVerdicts.Build(schema: WorldSchema.ExportCountersCeilings()));

    [Fact]
    public void EveryShippedCeilingsDocumentMatchesItsGeneratedSchema() {
        var files = Directory.GetFiles(RepositoryPaths.Resolve(relativePath: "tests/Puck.Counters"), "*.ceilings.json", SearchOption.AllDirectories);

        Assert.NotEmpty(collection: files);
        foreach (var file in files) {
            var document = JsonNode.Parse(File.ReadAllText(path: file))!;

            Assert.True(condition: (document["backends"]![0]!["ceilings"] is JsonObject), userMessage: $"{file}: ceilings must be grouped by node");
            Assert.True(condition: Schema.Value.Admits(instance: document), userMessage: $"{file}: {Schema.Value.Explain(instance: document)}");
        }
    }
    [Fact]
    public void ClassOverridesAndImplicitZerosRoundTripThroughTheRuntimeReader() {
        var original = Sample();
        var text = JsonSerializer.Serialize(original, WorldJsonContext.Default.WorldCountersCeilings);

        Assert.DoesNotContain(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "\"class\"");
        Assert.Contains(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "\"classes\"");
        var read = JsonSerializer.Deserialize(json: text, jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings)!;

        Assert.Equal(original.Backends[0].Ceilings, read.Backends[0].Ceilings);
        Assert.Equal(original.Backends[0].Devices[0].Ceilings, read.Backends[0].Devices[0].Ceilings);
        var document = JsonNode.Parse(text)!;

        Assert.True(condition: Schema.Value.Admits(instance: document), userMessage: Schema.Value.Explain(instance: document));
    }
    [Fact]
    public void AStoredZeroAndTheRowArraySpellingAreRefused() {
        var document = JsonNode.Parse("""
            {"schema":"puck.counters.ceilings.v1","workload":"world.json","script":"script.txt","width":8,"height":8,
             "layouts":[{"kinds":["gpu.dispatches"]}],
             "backends":[{"backend":"vulkan","ceilings":{"node":{"pass":{"shared":[0,0],"values":{"gpu.dispatches":3}}}},"devices":[]}]}
            """)!;

        Assert.True(condition: Schema.Value.Admits(instance: document), userMessage: Schema.Value.Explain(instance: document));
        Assert.Equal(3, JsonSerializer.Deserialize(json: document.ToJsonString(), jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings)!.Backends[0].Ceilings[0].Ceiling);
        document["backends"]![0]!["ceilings"]!["node"]!["pass"]!["values"]![GpuWork.Dispatches.Name] = 0;
        Assert.False(condition: Schema.Value.Admits(instance: document));
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(json: document.ToJsonString(), jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings));

        document = JsonNode.Parse(JsonSerializer.Serialize(Sample(), WorldJsonContext.Default.WorldCountersCeilings))!;
        document["backends"]![0]!["devices"]![0]!["ceilings"] = JsonNode.Parse("""
            {"node":{"pass":{"values":{"gpu.dispatches":4}}}}
            """);
        var refusal = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(json: document.ToJsonString(), jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings));

        Assert.Contains("cannot replace a shared budget", refusal.Message, StringComparison.Ordinal);
        document["backends"]![0]!["ceilings"] = new JsonArray();
        Assert.False(condition: Schema.Value.Admits(instance: document));
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(json: document.ToJsonString(), jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings));
    }

    private static WorldCountersCeilings Sample() => new("world.json", "script.txt", 8, 8,
        [new("vulkan", [new(new GpuDeviceIdentity("vulkan", "GPU", 1, 2, 0, "driver", "api"),
            [new("node", "upload", GpuWork.Dispatches.Name, WorkClass.PerBackendDeterministic, 4)])],
            [new("node", "pass", GpuWork.Dispatches.Name, WorkClass.Deterministic, 3),
             new("node", "pass", GpuWork.TexelsWritten.Name, WorkClass.PerBackendDeterministic, 0)])]);
}
