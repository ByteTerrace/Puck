using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The `prototype`/`document` block grammar: `prototypes { prototype "id" { document { ... } } }`, whose
/// `document.shapes` reaches the same `shape Type "name" { }` sugar a root-level creation document uses.</summary>
public class ShapeSugarTests {
    private static JsonObject Lower(string body) {
        var source = $"schema: \"puck.world.def.v1\"\n\n{body}";
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source);
        Assert.False(parseResult.Diagnostics.HasErrors, parseResult.Diagnostics.FormatReport(source));

        var diagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(parseResult.Value!, diagnostics: diagnostics, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(source));
        return loweringResult.Value!;
    }

    private static JsonObject FirstPrototype(string body) {
        var json = Lower(body);
        var prototypes = Assert.IsType<JsonArray>(json["prototypes"]);
        return Assert.IsType<JsonObject>(prototypes[0]);
    }

    private static void AssertSameJson(string expectedJson, JsonNode? actual) {
        var expected = JsonNode.Parse(expectedJson);
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"expected:{Environment.NewLine}{expected?.ToJsonString()}{Environment.NewLine}actual:{Environment.NewLine}{actual?.ToJsonString()}"
        );
    }

    [Fact]
    public void PrototypeRowLowersIdFromTheQuotedNameAndDocumentFromTheNestedBlock() {
        var proto = FirstPrototype("""
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

        AssertSameJson("""
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
            """, proto);
    }

    [Fact]
    public void PrototypeShapeStringRotationPassesThroughUntouched() {
        var proto = FirstPrototype("""
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
        AssertSameJson("""
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
            """, shape);
    }

    // `group` carries no default: it is a different, meaningful value from an ungrouped shape's absent key, so an
    // authored `group: 0` is never elided or refilled — it passes through exactly like `parent`.
    [Fact]
    public void PrototypeShapeWithParentAndGroupPassesThroughGenerically() {
        var proto = FirstPrototype("""
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
        AssertSameJson("""
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
            """, shape);
    }

    [Fact]
    public void PrototypeSweepWithProfilePassesThroughGenerically() {
        var proto = FirstPrototype("""
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
        AssertSameJson("""
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
            """, shape);
    }

    [Fact]
    public void DecompiledPrototypeUsesShapeBlockSugarWithDefaultsElided() {
        const string json = """
        {
            "schema": "puck.world.def.v1",
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

        var puck = WorldDecompiler.Decompile(json);

        Assert.Contains("prototypes {", puck, StringComparison.Ordinal);
        Assert.Contains("prototype \"pipelineProp\" {", puck, StringComparison.Ordinal);
        Assert.Contains("document {", puck, StringComparison.Ordinal);
        Assert.Contains("shape Box \"block\" {", puck, StringComparison.Ordinal);
        Assert.DoesNotContain("blend:", puck, StringComparison.Ordinal);
        Assert.DoesNotContain("smooth:", puck, StringComparison.Ordinal);
        Assert.DoesNotContain("Union", puck, StringComparison.Ordinal);
    }

    // A row with no `id` cannot round-trip through the `prototype "id" { }` grammar (there is nothing to quote), so
    // the whole `prototypes` array falls back to the generic value path rather than guessing one.
    [Fact]
    public void PrototypesArrayWithoutIdFallsBackToGenericPrinting() {
        const string json = """
        {
            "schema": "puck.world.def.v1",
            "prototypes": [
                { "document": { "schema": "puck.creation.v1", "shapes": [] } }
            ]
        }
        """;

        var puck = WorldDecompiler.Decompile(json);

        Assert.DoesNotContain("prototype \"", puck, StringComparison.Ordinal);
        Assert.Contains("prototypes [", puck, StringComparison.Ordinal);
    }

    // A `document.shapes` row with no `type` is a basis-merge patch onto a base document's shape (named by `id`
    // alone), not a whole shape the `shape Type "name" { }` grammar can carry — the whole array falls back to the
    // generic value path so the patch's own JSON shape survives untouched.
    [Fact]
    public void PartialMergeShapeRowFallsBackToGenericPrintingForTheWholeArray() {
        const string json = """
        {
            "schema": "puck.world.def.v1",
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

        var puck = WorldDecompiler.Decompile(json);

        Assert.Contains("prototype \"moth\" {", puck, StringComparison.Ordinal);
        Assert.DoesNotContain("shape ", puck, StringComparison.Ordinal);
        Assert.Contains("slides [", puck, StringComparison.Ordinal);

        // Round-trips: the fallback text still compiles back to the same JSON it was decompiled from.
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(puck, diagnostics: diagnostics);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(puck));
        var loweringDiagnostics = new DiagnosticBag();
        var lowered = WorldDocumentEmitter.LowerWithDiagnostics(parseResult.Value!, diagnostics: loweringDiagnostics, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(loweringDiagnostics.HasErrors, loweringDiagnostics.FormatReport(puck));

        var original = JsonNode.Parse(json)!["prototypes"];
        Assert.True(JsonNode.DeepEquals(original, lowered.Value!["prototypes"]));
    }
}
