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
}
