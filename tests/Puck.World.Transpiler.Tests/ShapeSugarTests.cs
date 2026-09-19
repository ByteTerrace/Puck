using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The `prototype`/`document` block grammar: `prototypes { prototype "id" { document { ... } } }`, whose
/// `document.shapes` reaches the same `shape Type "name" { }` sugar a root-level creation document uses.</summary>
public class ShapeSugarTests {
    private static void AssertSameJson(string expectedJson, JsonNode? actual) {
        var expected = JsonNode.Parse(expectedJson);

        Assert.True(
            condition: JsonNode.DeepEquals(
                node1: expected,
                node2: actual
            ),
            userMessage: $"expected:{Environment.NewLine}{expected?.ToJsonString()}{Environment.NewLine}actual:{Environment.NewLine}{actual?.ToJsonString()}"
        );
    }
    private static JsonObject FirstPrototype(string body) {
        var json = Lower(body: body);
        var prototypes = Assert.IsType<JsonArray>(@object: json["prototypes"]);

        return Assert.IsType<JsonObject>(@object: prototypes[0]);
    }
    private static JsonObject Lower(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(source)
        );
        return compilation.RequireJson();
    }

    [Fact]
    public void DecompiledPrototypeUsesShapeBlockSugarWithDefaultsElided() {
        const string Json = """
        {
            "schema": "puck.world.definition.v1",
            "prototypes": [
                {
                    "id": "pipelineProp",
                    "document": {
                        "schema": "puck.creation.v1",
                        "shapes": [
                            {
                                "id": 0,
                                "type": "Box",
                                "name": "block",
                                "position": [0, 0, 0],
                                "blend": "Union",
                                "smooth": 0,
                                "rotation": [0, 0, 0, 1],
                                "scale": [1, 1, 1]
                            }
                        ]
                    }
                }
            ]
        }
        """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "prototypes {"
        );
        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "prototype \"pipelineProp\" {"
        );
        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "document {"
        );
        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "shape Box \"block\" {"
        );
        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "blend:"
        );
        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "smooth:"
        );
        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "Union"
        );
    }
    // A `document.shapes` row with no `type` is a basis-merge patch onto a base document's shape (named by `id`
    // alone), not a whole shape the `shape Type "name" { }` grammar can carry — the whole array falls back to the
    // generic value path so the patch's own JSON shape survives untouched.
    [Fact]
    public void PartialMergeShapeRowFallsBackToGenericPrintingForTheWholeArray() {
        const string Json = """
        {
            "schema": "puck.world.definition.v1",
            "prototypes": [
                {
                    "id": "moth",
                    "document": {
                        "schema": "puck.creation.v1",
                        "shapes": [
                            { "id": 1, "slides": [{ "driver": "stage-crouch", "axis": [0, 1, 0], "amplitude": -0.15, "phase": 0, "wave": "linear" }] }
                        ]
                    }
                }
            ]
        }
        """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "prototype \"moth\" {"
        );
        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "shape "
        );
        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "slides ["
        );

        // Round-trips: the fallback text still compiles back to the same JSON it was decompiled from.
        var loweringDiagnostics = new DiagnosticBag();
        var lowered = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: loweringDiagnostics,
            source: puck
        );

        Assert.False(
            condition: loweringDiagnostics.HasErrors,
            userMessage: loweringDiagnostics.FormatReport(puck)
        );

        var original = JsonNode.Parse(Json)!["prototypes"];

        Assert.True(condition: JsonNode.DeepEquals(
            node1: original,
            node2: lowered.Json!["prototypes"]
        ));
    }
    [Fact]
    public void PrototypeRowLowersIdFromTheQuotedNameAndDocumentFromTheNestedBlock() {
        var proto = FirstPrototype(body: """
            prototypes {
                prototype "pipelineProp" {
                    document {
                        schema: "puck.creation.v1"
                        shape Box "block" {
                            position [1, 2, 3]
                        }
                    }
                }
            }
            """);

        AssertSameJson(
            actual: proto,
            expectedJson: """
            {
                "id": "pipelineProp",
                "document": {
                    "schema": "puck.creation.v1",
                    "shapes": [
                        {
                            "id": 0,
                            "type": "Box",
                            "name": "block",
                            "position": [1, 2, 3],
                            "blend": "Union",
                            "smooth": 0,
                            "rotation": [0, 0, 0, 1],
                            "scale": [1, 1, 1]
                        }
                    ]
                }
            }
            """
        );
    }
    [Fact]
    public void PrototypeShapeStringRotationPassesThroughUntouched() {
        var proto = FirstPrototype(body: """
            prototypes {
                prototype "pipelineProp" {
                    document {
                        shape Box "block" {
                            position [0, 0, 0]
                            rotation: "state.transforms.identity"
                        }
                    }
                }
            }
            """);

        var shape = proto["document"]!["shapes"]![0];

        AssertSameJson(
            actual: shape,
            expectedJson: """
            {
                "id": 0,
                "type": "Box",
                "name": "block",
                "position": [0, 0, 0],
                "rotation": "state.transforms.identity",
                "blend": "Union",
                "smooth": 0,
                "scale": [1, 1, 1]
            }
            """
        );
    }
    // `group` carries no default: it is a different, meaningful value from an ungrouped shape's absent key, so an
    // authored `group: 0` is never elided or refilled — it passes through exactly like `parent`.
    [Fact]
    public void PrototypeShapeWithParentAndGroupPassesThroughGenerically() {
        var proto = FirstPrototype(body: """
            prototypes {
                prototype "rig" {
                    document {
                        shape Cylinder "waist" {
                            parent: "chest"
                            group: 0
                            position [0, 0.9, 0]
                        }
                    }
                }
            }
            """);

        var shape = proto["document"]!["shapes"]![0];

        AssertSameJson(
            actual: shape,
            expectedJson: """
            {
                "id": 0,
                "type": "Cylinder",
                "name": "waist",
                "parent": "chest",
                "group": 0,
                "position": [0, 0.9, 0],
                "blend": "Union",
                "smooth": 0,
                "rotation": [0, 0, 0, 1],
                "scale": [1, 1, 1]
            }
            """
        );
    }
    [Fact]
    public void PrototypeSweepWithProfilePassesThroughGenerically() {
        var proto = FirstPrototype(body: """
            prototypes {
                prototype "trim" {
                    document {
                        shape Sweep "rail" {
                            profile { a [0, 0, 0], b [1, 1, 1] }
                            position [0, 0, 0]
                        }
                    }
                }
            }
            """);

        var shape = proto["document"]!["shapes"]![0];

        AssertSameJson(
            actual: shape,
            expectedJson: """
            {
                "id": 0,
                "type": "Sweep",
                "name": "rail",
                "profile": { "a": [0, 0, 0], "b": [1, 1, 1] },
                "position": [0, 0, 0],
                "blend": "Union",
                "smooth": 0,
                "rotation": [0, 0, 0, 1],
                "scale": [1, 1, 1]
            }
            """
        );
    }
    // A row with no `id` cannot round-trip through the `prototype "id" { }` grammar (there is nothing to quote), so
    // the whole `prototypes` array falls back to the generic value path rather than guessing one.
    [Fact]
    public void PrototypesArrayWithoutIdFallsBackToGenericPrinting() {
        const string Json = """
        {
            "schema": "puck.world.definition.v1",
            "prototypes": [
                { "document": { "schema": "puck.creation.v1", "shapes": [] } }
            ]
        }
        """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "prototype \""
        );
        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "prototypes ["
        );
    }
}
