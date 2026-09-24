using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary><c>if</c>/<c>else if</c>/<c>else</c> lowering to <c>ActionEffect.If</c> and its decompiler
/// inverse.</summary>
public class ConditionalEffectSyntaxTests {
    private static JsonObject FirstRuleEffect(JsonObject json) {
        var rules = Assert.IsType<JsonArray>(@object: json["rules"]);
        var rule = Assert.IsType<JsonObject>(@object: rules[0]);

        return Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: rule["effects"])[0]);
    }

    [Fact]
    public void IfElseLowersToTheIfEffectWithConditionThenAndElse() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            rule "r" {
                if hp <= 0 {
                    alive = 0
                } else {
                    alive = 1
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );

        var effect = FirstRuleEffect(json: json);

        Assert.Equal(
            "if",
            effect["$type"]?.ToString()
        );
        var condition = Assert.IsType<JsonObject>(@object: effect["condition"]);

        Assert.Equal(
            "compareState",
            condition["$type"]?.ToString()
        );
        Assert.Equal(
            "hp",
            condition["state"]?.ToString()
        );
        Assert.Equal(
            "LessOrEqual",
            condition["comparison"]?.ToString()
        );

        var then = Assert.IsType<JsonArray>(@object: effect["then"]);
        var thenEffect = Assert.IsType<JsonObject>(@object: then[0]);

        Assert.Equal(
            "setState",
            thenEffect["$type"]?.ToString()
        );
        Assert.Equal(
            "alive",
            thenEffect["state"]?.ToString()
        );
        Assert.Equal(
            0,
            LoweredNumbers.AsDouble(node: thenEffect["value"])
        );

        var elseArr = Assert.IsType<JsonArray>(@object: effect["else"]);
        var elseEffect = Assert.IsType<JsonObject>(@object: elseArr[0]);

        Assert.Equal(
            1,
            LoweredNumbers.AsDouble(node: elseEffect["value"])
        );
    }
    [Fact]
    public void IfWithNoElseOmitsTheElseKeyEntirely() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            rule "r" {
                if hp <= 0 {
                    alive = 0
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );

        var effect = FirstRuleEffect(json: json);

        Assert.False(condition: effect.ContainsKey(propertyName: "else"));
    }
    [Fact]
    public void ElseIfChainsNestAsAnIfEffectInsideTheElseArray() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            rule "r" {
                if a == 1 {
                    x = 1
                } else if a == 2 {
                    x = 2
                } else {
                    x = 3
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );

        var effect = FirstRuleEffect(json: json);
        var elseArr = Assert.IsType<JsonArray>(@object: effect["else"]);
        var nestedIf = Assert.IsType<JsonObject>(@object: elseArr[0]);

        Assert.Equal(
            "if",
            nestedIf["$type"]?.ToString()
        );
        var nestedCondition = Assert.IsType<JsonObject>(@object: nestedIf["condition"]);

        Assert.Equal(
            2,
            LoweredNumbers.AsDouble(node: nestedCondition["value"])
        );
        var nestedElse = Assert.IsType<JsonArray>(@object: nestedIf["else"]);
        var nestedElseEffect = Assert.IsType<JsonObject>(@object: nestedElse[0]);

        Assert.Equal(
            3,
            LoweredNumbers.AsDouble(node: nestedElseEffect["value"])
        );
    }
    [Fact]
    public void IfUsesTheSamePredicateLoweringAsWhen() {
        var whenGate = WorldSources.Lower(body: """
            rule "r" {
                when a == 1
                flag = 1
            }
            """).Json;
        var ifCondition = WorldSources.Lower(body: """
            rule "r" {
                if a == 1 {
                    flag = 1
                }
            }
            """).Json;

        var rules1 = Assert.IsType<JsonArray>(@object: whenGate["rules"]);
        var gate = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonObject>(@object: rules1[0])["gate"]);
        var effect = FirstRuleEffect(json: ifCondition);
        var condition = Assert.IsType<JsonObject>(@object: effect["condition"]);

        var mismatch = JsonMismatch.Find(
            actual: condition,
            expected: gate,
            path: "condition"
        );

        Assert.Null(@object: mismatch);
    }
    [Fact]
    public void IfInsideATransactionCompiles() {
        var (_, diagnostics) = WorldSources.Lower(body: """
            rule "r" {
                transaction {
                    if a == 1 {
                        x = 1
                    } else {
                        x = 2
                    }
                }
            }
            """);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
    }
    [Fact]
    public void RepeatInsideAnIfBranchStillRefusesAsPuck037() {
        var (_, diagnostics) = WorldSources.Lower(body: """
            rule "r" {
                if a == 1 {
                    repeat 4 as k {
                        x = k
                    }
                }
            }
            """);

        Assert.Contains(
            collection: diagnostics,
            filter: d => (d.Code == "PUCK037")
        );
    }
    [Fact]
    public void IfElseLoweringDecompilesAndRecompilesToTheSameJson() {
        var (original, diagnostics) = WorldSources.Lower(body: """
            rule "r" {
                if a == 1 {
                    x = 1
                } else if a == 2 {
                    x = 2
                } else {
                    x = 3
                }
            }
            """);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );

        var decompiled = WorldDecompiler.Decompile(root: original);

        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "if a == 1 {"
        );
        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "else if a == 2 {"
        );

        var recompileDiagnostics = new DiagnosticBag();
        var recompiled = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: recompileDiagnostics,
            source: decompiled
        ).Json;

        Assert.False(
            condition: recompileDiagnostics.HasErrors,
            userMessage: recompileDiagnostics.FormatReport(decompiled)
        );

        var mismatch = JsonMismatch.Find(
            actual: recompiled,
            expected: original,
            path: "$"
        );

        Assert.Null(@object: mismatch);
    }
}
