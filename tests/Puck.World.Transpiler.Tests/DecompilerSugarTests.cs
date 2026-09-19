using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Decompiler inverse for the `.puck` DSL sugar wave (§1-§4, §7 of the sugar wave spec): the rules-block
/// gate/effect/decision inverse, the generalized call-form escape hatch, and shape/placement default elision.</summary>
public class DecompilerSugarTests {
    private static readonly string[] LiteralTypeKeyProbes = [
        "\"$type\": \"compareState\"", "\"$type\":\"compareState\"",
        "\"$type\": \"setState\"", "\"$type\":\"setState\"",
        "\"$type\": \"all\"", "\"$type\":\"all\"",
        "\"$type\": \"any\"", "\"$type\":\"any\"",
        "\"$type\": \"not\"", "\"$type\":\"not\"",
        "\"$type\": \"addState\"", "\"$type\":\"addState\"",
        "\"$type\": \"countdownState\"", "\"$type\":\"countdownState\"",
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
    [Fact]
    public void ChessPlacementsElideDefaultYawAndScale() {
        var puck = DecompileWorld(relativePath: "games/chess.world.json");

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "placement \"tabletop\" {"
        );
        var start = puck.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: "placement \"tabletop\" {"
        );
        var end = puck.IndexOf(
            comparisonType: StringComparison.Ordinal,
            startIndex: start,
            value: "\n    }"
        );
        var row = puck[start..end];

        Assert.DoesNotContain(
            actualString: row,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "yawDegrees"
        );
        Assert.DoesNotContain(
            actualString: row,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "scale"
        );
        Assert.Contains(
            actualString: row,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "prototype: \"tabletop\""
        );
        Assert.Contains(
            actualString: row,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "solid"
        );
    }
    [Fact]
    public void ChessRulesCarryNoLiteralTypeDiscriminators() {
        var puck = DecompileWorld(relativePath: "games/chess.world.json");

        foreach (var probe in LiteralTypeKeyProbes) {
            Assert.DoesNotContain(
                actualString: puck,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: probe
            );
        }
    }
    [Fact]
    public void ChessRulesUseWhenAndCallFormSugar() {
        var puck = DecompileWorld(relativePath: "games/chess.world.json");

        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "rule \"tabletop-settle-hold-advance\" {"
        );
        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "when $physics:quiescent == 1 and settleHold < 60"
        );
        // A gate whose compareValue.left is a parenthesized expression spells as a `when` like any other: the
        // comparator standing after the balanced span is what says the span is an operand rather than a nested
        // gate, so no gate in this file falls back to the plain `gate:` property.
        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "and ((absolute(move[mover]) == 1 &"
        );
        Assert.DoesNotContain(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "gate: all(predicates: ["
        );
    }
    [Fact]
    public void HeaderNamesTheSourceAsCanonicalAndWarnsAgainstReDecompiling() {
        var puck = DecompileWorld(relativePath: "games/chess.world.json");

        Assert.StartsWith(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedStartString: "// Bootstrapped from a Puck world document. The '.puck' source is canonical: edit it and"
        );
        Assert.Contains(
            actualString: puck,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "would OVERWRITE this file"
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
