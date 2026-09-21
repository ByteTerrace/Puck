using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>A reserved channel spelled with no arguments — a bare <c>$phase</c>, <c>$local</c>, and so on, with no
/// colon at all — is refused by a <see cref="RuleException"/> naming what the channel expects, the same as a
/// channel spelled with the wrong number of arguments. No channel handler reads past the end of an empty argument
/// list.</summary>
public sealed class ChannelCallArgumentCountLawTests {
    [Theory]
    [InlineData("$phase")]
    [InlineData("$board")]
    [InlineData("$match")]
    [InlineData("$history")]
    [InlineData("$local")]
    [InlineData("$table")]
    [InlineData("$reduce")]
    [InlineData("$symmetry")]
    [InlineData("$search")]
    public void BareChannelIsRefused(string channel) {
        var rule = RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ActionStateComparison.Equal,
                State: channel,
                Value: 0m
            ),
            name: "bare"
        );

        Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(
            context: RulesFixture.Context(),
            rule: rule
        ));
    }
}
