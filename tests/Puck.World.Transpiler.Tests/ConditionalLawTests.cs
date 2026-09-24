using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A conditional is <c>condition ? whenTrue : whenFalse</c> in a document value as in a rule, and a value
/// takes the rule language's two prefix operators, <c>-</c> and <c>~</c>, and no third.</summary>
public sealed class ConditionalLawTests {
    [InlineData("1 > 0 ? 10 : 20", 10L)]
    [InlineData("0 ? 10 : 20", 20L)]
    [InlineData("0 ? 1 : 0 ? 2 : 3", 3L)]
    [InlineData("1 ? 0 ? 5 : 6 : 7", 6L)]
    [InlineData("(1 ? 2 : 3) + 4", 6L)]
    [InlineData("1 + 1 == 2 ? 1 << 3 : 0", 8L)]
    [InlineData("0 ?? 1 ? 4 : 5", 5L)]
    [Theory]
    public void AValueConditionalFoldsTheArmItsConditionSelects(string text, long expected) {
        var lowered = WorldSources.LowerClean(body: $"value: {text}\n");

        Assert.Equal(
            expected: expected,
            actual: lowered["value"]?.GetValue<long>()
        );
    }
    // Only the selected arm is evaluated, so an arm the condition rules out may be one that has no value.
    [Fact]
    public void TheArmAConditionRulesOutIsNeverEvaluated() {
        var lowered = WorldSources.LowerClean(body: """
            let items = [1, 2]

            value: length(items) > 5 ? items[5] : "short"
            """);

        Assert.Equal(
            expected: "short",
            actual: lowered["value"]?.GetValue<string>()
        );
    }
    [Fact]
    public void AnArmMayHoldAnyValue() {
        var lowered = WorldSources.LowerClean(body: """
            let flipped = 1

            value: flipped ? [1, 0, 0, 0] : [0, 1, 0, 0]
            """);

        Assert.Equal(
            expected: "[1,0,0,0]",
            actual: lowered["value"]?.ToJsonString()
        );
    }
    [InlineData("value: a ? b : c")]
    [InlineData("value: a ? b : c ? d : e")]
    [InlineData("value: (a ? b : c) ? d : e")]
    [InlineData("value: a ? (b ? c : d) : e")]
    [InlineData("value: (a ? b : c) + 1")]
    [InlineData("value: -(a ? b : c)")]
    [InlineData("value: map(xs, x => x > 0 ? x : -x)")]
    [Theory]
    public void TheFormatterPrintsAConditionalAsItReadsBack(string line) {
        var source = $"schema: \"puck.world.definition.v1\"\n{line}\n";
        var formatted = PuckFormat.Format(source: source);

        Assert.Equal(
            actual: formatted,
            expected: source
        );
    }
    [Fact]
    public void ADerivedConditionalIsTheRuleLanguagesConditional() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            state {
                world {
                    table board {
                        cell0 = 5
                    }
                }
                derive open = board.cell0 > 0 ? 1 : 0
            }

            rule "claim" when open == 1 {
                board.cell0 = 0
            }
            """);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport("")
        );
        Assert.Equal(
            expected: "board[cell0] > 0 ? 1 : 0",
            actual: WorldExpressionJson.Text(node: json["rules"]![0]!["gate"]!["left"])
        );
    }
    [Fact]
    public void AValueTakesNoUnaryPlus() {
        var (_, diagnostics) = WorldSources.Lower(body: """
            let spread = 2

            value: +spread
            """);

        Assert.Contains(
            collection: diagnostics,
            filter: static diagnostic => diagnostic.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "'+' is not a prefix operator"
            )
        );
    }
}
