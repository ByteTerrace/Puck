using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Decompiler inverse for the `.puck` DSL sugar wave (§1-§4, §7 of the sugar wave spec): the rules-block
/// gate/effect/decision inverse, the generalized call-form escape hatch, and shape/placement default elision.</summary>
public class DecompilerSugarTests {
    private static JsonObject Recompile(string source) {
        var compilation = WorldCompiler.Compile(source: source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(source));
        return Assert.IsType<JsonObject>(@object: compilation.Json);
    }

    private static readonly string[] LiteralTypeKeyProbes = [
        "\"$type\": \"compareState\"", "\"$type\":\"compareState\"",
        "\"$type\": \"setState\"", "\"$type\":\"setState\"",
        "\"$type\": \"all\"", "\"$type\":\"all\"",
        "\"$type\": \"any\"", "\"$type\":\"any\"",
        "\"$type\": \"not\"", "\"$type\":\"not\"",
        "\"$type\": \"addState\"", "\"$type\":\"addState\"",
        "\"$type\": \"transaction\"", "\"$type\":\"transaction\"",
    ];

    private static string DecompileWorld(string relativePath) {
        var fullPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );

        Assert.True(
            condition: File.Exists(path: fullPath),
            userMessage: $"Shipped world file not found: {fullPath}"
        );
        return WorldDecompiler.Decompile(jsonText: File.ReadAllText(path: fullPath));
    }

    [Fact]
    public void RecordsPoolsAndPoolEffectsDecompileToAuthoringSugar() {
        var root = JsonNode.Parse("""
        {
          "schema": "puck.world.definition.v1",
          "state": {
            "records": [{"name":"Player","fields":[{"name":"score","kind":"Int","default":{"kind":"Int","value":0},"min":0,"max":10}]}],
            "pools": [{"name":"players","record":"Player","capacity":1,"initial":[{"slot":0,"values":[{"field":"score","value":{"kind":"Int","value":3}}]}]}]
          },
          "rules": [{"name":"award","effects":[{"$type":"claim","pool":"players","binding":"player","effects":[{"$type":"setState","state":{"binding":"player","field":"score"},"value":1},{"$type":"release","binding":"player"}]}]}]
        }
        """);
        var puck = WorldDecompiler.Decompile(root: Assert.IsType<JsonObject>(@object: root));

        Assert.Contains(actualString: puck, expectedSubstring: "record Player");
        Assert.Contains(actualString: puck, expectedSubstring: "pool players of Player capacity(1)");
        Assert.Contains(actualString: puck, expectedSubstring: "claim players as player");
        Assert.Contains(actualString: puck, expectedSubstring: "release player");
    }
    [Fact]
    public void RecordTraitsAndRulePoolIterationRoundTripWithoutLoss() {
        var root = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
        {
          "schema":"puck.world.definition.v1",
          "state":{
            "spaces":[{"name":"semantic","model":"m","revision":"r","dimensions":8}],
            "records":[{"name":"Unit","fields":[
              {"name":"weight","kind":"Fixed","min":65536,"max":131072,"overflow":"Saturate","advance":{"perSecondNumerator":-1,"perSecondDenominator":1},"default":{"kind":"Fixed","value":65536}},
              {"name":"embedding","kind":"Vector","space":"semantic","default":{"kind":"Vector","value":"AQIDBAUGBwg"}}
            ]}],
            "pools":[{"name":"units","record":"Unit","capacity":1,"initial":[]}]
          },
          "rules":[{"name":"visit","poolForEach":{"pool":"units","binding":"unit"},"effects":[{"$type":"release","binding":"unit"}]}]
        }
        """));

        var source = WorldDecompiler.Decompile(root);

        Assert.Contains(actualString: source, expectedSubstring: "rule \"visit\" for each unit in units");
        Assert.Contains(actualString: source, expectedSubstring: "overflow: Saturate");
        Assert.Contains(actualString: source, expectedSubstring: "advance(perSecond: -1)");
        Assert.Contains(actualString: source, expectedSubstring: "space(\"semantic\")");
        Assert.True(condition: JsonNode.DeepEquals(node1: root, node2: Recompile(source: source)));
    }
    [Fact]
    public void RuntimeSnapshotsAndPairSeedsRoundTripWithoutLosingDeadGenerations() {
        var root = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
        {
          "schema": "puck.world.definition.v1",
          "state": {
            "records": [{"name":"Marker","fields":[{"name":"value","kind":"Int","default":{"kind":"Int","value":0}}]}],
            "pools": [{"name":"actors","record":"Marker","capacity":3,"snapshot":{"generations":[4,9,2],"live":[{"slot":1,"values":[{"field":"value","value":{"kind":"Int","value":7}}]}]}}],
            "pairPools": [{"name":"links","record":"Marker","leftPool":"actors","rightPool":"actors","maxLive":2,"directed":false,"allowSelf":true,"initial":[{"slot":0,"values":[{"field":"value","value":{"kind":"Int","value":5}}]}],"snapshot":{"generations":[8,3],"live":[{"slot":0,"values":[{"field":"value","value":{"kind":"Int","value":6}}]}]}}]
          }
        }
        """));

        var recompiled = Recompile(source: WorldDecompiler.Decompile(root));

        Assert.True(condition: JsonNode.DeepEquals(node1: root["state"]?["pools"], node2: recompiled["state"]?["pools"]));
        Assert.True(condition: JsonNode.DeepEquals(node1: root["state"]?["pairPools"], node2: recompiled["state"]?["pairPools"]));
    }
    [Fact]
    public void AnyTypeObjectOutsideSeatRigPrintsAsCallForm() {
        var root = new JsonObject {
            ["schema"] = "puck.world.definition.v1",
            ["cameras"] = new JsonArray(new JsonObject { ["$type"] = "worldPoint", ["x"] = 1, ["y"] = 2, ["z"] = 3 }),
        };

        var puck = WorldDecompiler.Decompile(root: root);

        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "$type"
        );
        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "worldPoint(x: 1, y: 2, z: 3)"
        );
    }
    // A views.post row prints as its named `post` block, its config as a nested block, and recompiles to the same rows
    // in the same order.
    [Fact]
    public void PostPassesPrintAsNamedBlocksAndRoundTrip() {
        var root = Assert.IsType<JsonObject>(@object: JsonNode.Parse(json: """
            {
              "schema": "puck.world.definition.v1",
              "views": {
                "post": [
                  { "name": "grain", "package": "sdf.film-grain", "config": { "intensity": 0.5, "size": 2 } },
                  { "name": "plain", "package": "sdf.film-grain" }
                ]
              }
            }
            """));

        var puck = WorldDecompiler.Decompile(root: root);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "post \"grain\" {"
        );
        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "post \"plain\" {"
        );
        Assert.True(
            condition: JsonNode.DeepEquals(node1: root["views"]?["post"], node2: Recompile(source: puck)["views"]?["post"]),
            userMessage: puck
        );
    }
    [Fact]
    public void RootWorldRulesCarryNoLiteralTypeDiscriminators() {
        var puck = DecompileWorld(relativePath: "puck.world.json");

        foreach (var probe in LiteralTypeKeyProbes) {
            Assert.DoesNotContain(
                actualString: puck,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: probe
            );
        }
    }
    // The shipped corpus authors every `prototypes[].document.shapes` array as a plain object/array literal (no
    // wrapping block statement reaches that position in the grammar — see AGENTS handoff notes), so shape-block
    // sugar is exercised here directly against a standalone `shapes` section rather than through a shipped world.
    // The input is JSON TEXT, parsed the same way the CLI's own `decompile` verb reads a file, rather than a
    // C#-literal-constructed JsonObject — a JsonValue built directly from a C# `int` converts only back to that
    // exact CLR type (`TryGetValue<long>`/`<double>` both fail), unlike one parsed from JSON text.
    [Fact]
    public void ShapesBlockElidesDefaultsAndKeepsNonDefaults() {
        const string Json = """
        {
            "schema": "puck.creation.v1",
            "shapes": [
                {
                    "id": 0,
                    "type": "Box",
                    "blend": "Union",
                    "smooth": 0,
                    "rotation": [0, 0, 0, 1],
                    "scale": [1, 1, 1],
                    "group": 0,
                    "position": [0, 0, 0]
                },
                {
                    "id": 5,
                    "type": "Sphere",
                    "blend": "Subtract",
                    "smooth": 0.25,
                    "rotation": "state.transforms.identity",
                    "scale": [2, 2, 2],
                    "group": 3,
                    "position": [1, 2, 3]
                }
            ]
        }
        """;

        var puck = WorldDecompiler.Decompile(jsonText: Json);

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "shape Box {"
        );
        var firstStart = puck.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: "shape Box {"
        );
        var firstEnd = puck.IndexOf(
            comparisonType: StringComparison.Ordinal,
            startIndex: firstStart,
            value: "\n}"
        );
        var firstShape = puck[firstStart..firstEnd];

        Assert.DoesNotContain(
            actualString: firstShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "id:"
        );
        Assert.DoesNotContain(
            actualString: firstShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "blend"
        );
        Assert.DoesNotContain(
            actualString: firstShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "smooth"
        );
        Assert.DoesNotContain(
            actualString: firstShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "rotation"
        );
        Assert.DoesNotContain(
            actualString: firstShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "scale"
        );
        // `group` carries no default (an ungrouped shape omits the key entirely instead) — an authored 0 is a
        // different, meaningful state from absence, so it always prints rather than eliding.
        Assert.Contains(
            actualString: firstShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "group: 0"
        );
        Assert.Contains(
            actualString: firstShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "position [0, 0, 0]"
        );

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "shape Sphere {"
        );
        var secondStart = puck.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: "shape Sphere {"
        );
        var secondShape = puck[secondStart..];

        Assert.Contains(
            actualString: secondShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "id: 5"
        );
        Assert.Contains(
            actualString: secondShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "blend: \"Subtract\""
        );
        Assert.Contains(
            actualString: secondShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "smooth: 0.25"
        );
        Assert.Contains(
            actualString: secondShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "rotation: \"state.transforms.identity\""
        );
        Assert.Contains(
            actualString: secondShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "scale [2, 2, 2]"
        );
        Assert.Contains(
            actualString: secondShape,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "group: 3"
        );
    }
}
