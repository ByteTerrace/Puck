using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Dot access: <c>row.key</c> compiles to the identical rule facts a bracket <c>row[key]</c> read produces,
/// while an operand position that stores its expression verbatim keeps the authored dotted text rather than
/// rewriting it to bracket form.</summary>
public class DotAccessEquivalenceTests {
    private static JsonObject FirstRule(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(source)
        );
        var rules = Assert.IsType<JsonArray>(@object: compilation.RequireJson()["rules"]);

        return Assert.IsType<JsonObject>(@object: rules[0]);
    }

    [Fact]
    public void DottedGateProducesTheSameCompareStateFactsAsABracketGate() {
        var dotted = FirstRule(body: """
            rule "r" {
                when vitals.mana >= 10
                flag = 1
            }
            """);
        var bracketed = FirstRule(body: """
            rule "r" {
                when vitals[mana] >= 10
                flag = 1
            }
            """);

        var mismatch = JsonMismatch.Find(
            actual: bracketed["gate"],
            expected: dotted["gate"],
            path: "gate"
        );

        Assert.Null(@object: mismatch);

        var gate = Assert.IsType<JsonObject>(@object: dotted["gate"]);

        Assert.Equal(
            "compareState",
            gate["$type"]?.ToString()
        );
        Assert.Equal(
            "vitals",
            gate["state"]?.ToString()
        );
        Assert.Equal(
            "mana",
            gate["key"]?.ToString()
        );
    }
    [Fact]
    public void DottedAssignmentTargetLowersLikeABracketTarget() {
        var dotted = FirstRule(body: """
            rule "r" {
                vitals.mana = 5
            }
            """);
        var bracketed = FirstRule(body: """
            rule "r" {
                vitals[mana] = 5
            }
            """);

        var mismatch = JsonMismatch.Find(
            actual: bracketed["effects"],
            expected: dotted["effects"],
            path: "effects"
        );

        Assert.Null(@object: mismatch);

        var effect = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: dotted["effects"])[0]);

        Assert.Equal(
            "setState",
            effect["$type"]?.ToString()
        );
        Assert.Equal(
            "vitals",
            effect["state"]?.ToString()
        );
        Assert.Equal(
            "mana",
            effect["key"]?.ToString()
        );
    }
    [Fact]
    public void ADottedEffectOperandLowersToTheSameProgramAsItsBracketForm() {
        var rule = FirstRule(body: """
            rule "r" {
                hp += vitals.mana
            }
            """);
        var effect = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: rule["effects"])[0]);

        Assert.Equal(
            "addState",
            effect["$type"]?.ToString()
        );
        Assert.Equal(
            "vitals[mana]",
            WorldExpressionJson.Text(node: effect["expression"])
        );
    }
    [Fact]
    public void ABracketEffectOperandLowersToTheProgramItSpells() {
        var rule = FirstRule(body: """
            rule "r" {
                hp += vitals[mana]
            }
            """);
        var effect = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: rule["effects"])[0]);

        Assert.Equal(
            "vitals[mana]",
            WorldExpressionJson.Text(node: effect["expression"])
        );
    }
    [Fact]
    public void ADottedLocalExpressionLowersToTheProgramItSpells() {
        var rule = FirstRule(body: """
            rule "r" {
                local dx : Int = vitals.mana - 10
                flag = dx
            }
            """);
        var locals = Assert.IsType<JsonArray>(@object: rule["locals"]);
        var local = Assert.IsType<JsonObject>(@object: locals[0]);

        Assert.Equal(
            "vitals[mana] - 10",
            WorldExpressionJson.Text(node: local["expression"])
        );
    }
}
