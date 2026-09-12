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
        var fullPath = Path.Combine(ShippedWorlds.FindDirectory(), relativePath);
        Assert.True(File.Exists(fullPath), $"Shipped world file not found: {fullPath}");
        return WorldDecompiler.Decompile(File.ReadAllText(fullPath));
    }

    [Fact]
    public void ChessRulesCarryNoLiteralTypeDiscriminators() {
        var puck = DecompileWorld("games/chess.world.json");
        foreach (var probe in LiteralTypeKeyProbes) {
            Assert.DoesNotContain(probe, puck, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RootWorldRulesCarryNoLiteralTypeDiscriminators() {
        var puck = DecompileWorld("puck.world.json");
        foreach (var probe in LiteralTypeKeyProbes) {
            Assert.DoesNotContain(probe, puck, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ChessRulesUseWhenAndCallFormSugar() {
        var puck = DecompileWorld("games/chess.world.json");
        Assert.Contains("rule \"tabletop-settle-hold-advance\" {", puck, StringComparison.Ordinal);
        Assert.Contains("when $physics:quiescent == 1 and settleHold < 60", puck, StringComparison.Ordinal);
        // The one gate in this file whose compareValue.left begins with '(' (§1.2/A11: `ParseAtom` would
        // otherwise misread the leading paren as a nested-gate grouping) falls back to a plain `gate:` property,
        // still printed through the call-form escape hatch rather than a literal `$type` object.
        Assert.Contains("gate: all(predicates: [", puck, StringComparison.Ordinal);
        Assert.Contains("compareState(comparison: \"Equal\", state: \"settleHold\", value: 60)", puck, StringComparison.Ordinal);
    }

    [Fact]
    public void ChessPlacementsElideDefaultYawAndScale() {
        var puck = DecompileWorld("games/chess.world.json");
        Assert.Contains("placement \"tabletop\" {", puck, StringComparison.Ordinal);
        var start = puck.IndexOf("placement \"tabletop\" {", StringComparison.Ordinal);
        var end = puck.IndexOf("\n    }", start, StringComparison.Ordinal);
        var row = puck[start..end];
        Assert.DoesNotContain("yawDegrees", row, StringComparison.Ordinal);
        Assert.DoesNotContain("scale", row, StringComparison.Ordinal);
        Assert.Contains("prototype: \"tabletop\"", row, StringComparison.Ordinal);
        Assert.Contains("solid", row, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderMarksOneTimeImport() {
        var puck = DecompileWorld("games/chess.world.json");
        Assert.StartsWith("// Decompiled from a canonical Puck world document — a one-time import.", puck, StringComparison.Ordinal);
        Assert.Contains("'let'/'template' cannot be recovered", puck, StringComparison.Ordinal);
    }

    // The shipped corpus authors every `prototypes[].document.shapes` array as a plain object/array literal (no
    // wrapping block statement reaches that position in the grammar — see AGENTS handoff notes), so shape-block
    // sugar is exercised here directly against a standalone `shapes` section rather than through a shipped world.
    // The input is JSON TEXT, parsed the same way the CLI's own `decompile` verb reads a file, rather than a
    // C#-literal-constructed JsonObject — a JsonValue built directly from a C# `int` converts only back to that
    // exact CLR type (`TryGetValue<long>`/`<double>` both fail), unlike one parsed from JSON text.
    [Fact]
    public void ShapesBlockElidesDefaultsAndKeepsNonDefaults() {
        const string json = """
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

        var puck = WorldDecompiler.Decompile(json);

        Assert.Contains("shape Box {", puck, StringComparison.Ordinal);
        var firstStart = puck.IndexOf("shape Box {", StringComparison.Ordinal);
        var firstEnd = puck.IndexOf("\n}", firstStart, StringComparison.Ordinal);
        var firstShape = puck[firstStart..firstEnd];
        Assert.DoesNotContain("id:", firstShape, StringComparison.Ordinal);
        Assert.DoesNotContain("blend", firstShape, StringComparison.Ordinal);
        Assert.DoesNotContain("smooth", firstShape, StringComparison.Ordinal);
        Assert.DoesNotContain("rotation", firstShape, StringComparison.Ordinal);
        Assert.DoesNotContain("scale", firstShape, StringComparison.Ordinal);
        // `group` carries no default (an ungrouped shape omits the key entirely instead) — an authored 0 is a
        // different, meaningful state from absence, so it always prints rather than eliding.
        Assert.Contains("group: 0", firstShape, StringComparison.Ordinal);
        Assert.Contains("position [0, 0, 0]", firstShape, StringComparison.Ordinal);

        Assert.Contains("shape Sphere {", puck, StringComparison.Ordinal);
        var secondStart = puck.IndexOf("shape Sphere {", StringComparison.Ordinal);
        var secondShape = puck[secondStart..];
        Assert.Contains("id: 5", secondShape, StringComparison.Ordinal);
        Assert.Contains("blend: \"Subtract\"", secondShape, StringComparison.Ordinal);
        Assert.Contains("smooth: 0.25", secondShape, StringComparison.Ordinal);
        Assert.Contains("rotation: \"state.transforms.identity\"", secondShape, StringComparison.Ordinal);
        Assert.Contains("scale [2, 2, 2]", secondShape, StringComparison.Ordinal);
        Assert.Contains("group: 3", secondShape, StringComparison.Ordinal);
    }

    [Fact]
    public void AnyTypeObjectOutsideSeatRigPrintsAsCallForm() {
        var root = new JsonObject {
            ["schema"] = "puck.world.def.v1",
            ["cameras"] = new JsonArray(
                new JsonObject { ["$type"] = "worldPoint", ["x"] = 1, ["y"] = 2, ["z"] = 3 }
            ),
        };

        var puck = WorldDecompiler.Decompile(root);

        Assert.DoesNotContain("$type", puck, StringComparison.Ordinal);
        Assert.Contains("worldPoint(x: 1, y: 2, z: 3)", puck, StringComparison.Ordinal);
    }
}
