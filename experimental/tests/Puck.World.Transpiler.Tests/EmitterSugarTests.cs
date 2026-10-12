using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Emitter lowering for the `.puck` DSL sugar wave (§1-§5 of the sugar wave spec): gate classification,
/// effect-statement lowering, local/decision/option, shape/placement row elision, and the unit-dimension table.</summary>
public class EmitterSugarTests {
    private static JsonObject FirstRule(string body) {
        var (json, diagnostics) = WorldSources.Lower(body: body);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport(body)
        );
        var rules = Assert.IsType<JsonArray>(@object: json["rules"]);

        return Assert.IsType<JsonObject>(@object: rules[0]);
    }
    // ---- Shipped-world cross-checks --------------------------------------------------------------------------

    private static JsonObject LoadShippedRule(string relativeWorldPath, string ruleName) {
        var worldsDir = ShippedWorlds.FindDirectory();
        var fullPath = Path.Combine(
            path1: worldsDir,
            path2: relativeWorldPath
        );

        Assert.True(
            condition: File.Exists(path: fullPath),
            userMessage: $"Shipped world file not found: {fullPath}"
        );

        var root = JsonNode.Parse(File.ReadAllText(path: fullPath));
        var rules = Assert.IsType<JsonArray>(@object: root!["rules"]);

        foreach (var candidate in rules) {
            var obj = Assert.IsType<JsonObject>(@object: candidate);

            if (string.Equals(
                a: obj["name"]?.ToString(),
                b: ruleName,
                comparisonType: StringComparison.Ordinal
            )) {
                return obj;
            }
        }
        throw new InvalidOperationException(message: $"rule '{ruleName}' not found in {relativeWorldPath}");
    }

    [Fact]
    public void HoundWitnessClaimGateMatchesTheShippedRule() {
        var expected = LoadShippedRule(
            relativeWorldPath: "puck.world.json",
            ruleName: "witness-claim"
        );

        var rule = FirstRule(body: """
            rule "witness-claim" {
                when boneHolder[0] >= 0 and houndIdentity[$each] != boneHolder[0] and los(each, cell, boneHolder, 0) == 1 and distance(each, cell, boneHolder, 0) <= 9
                mode: Edge
                forEach: hound
                boneHolderTrust[$pair:each:cell:boneHolder:0] += (1 - boneHolderTrust[$pair:each:cell:boneHolder:0]) * 0.3
            }
            """);

        var mismatch = JsonMismatch.Find(
            actual: rule,
            expected: expected,
            path: "witness-claim"
        );

        Assert.Null(@object: mismatch);
    }
}
