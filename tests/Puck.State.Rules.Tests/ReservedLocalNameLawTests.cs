using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: the rule compiler mints the binding a computed key needs as a local named
/// <c>$key&lt;n&gt;</c> on the same list an author's locals sit on, so an authored local under the reserved prefix is
/// refused by name and cannot shadow one; an authored local without it compiles.</summary>
public sealed class ReservedLocalNameLawTests {
    private static Rule WithLocal(string name) => RulesFixture.Rule(name: "probe") with {
        Locals = [new RuleLocal(
            Expression: RulesFixture.Program(text: "1"),
            Name: RulesFixture.Name(value: name)
        )],
    };

    [InlineData("$key0")]
    [InlineData("$anything")]
    [Theory]
    public void AnAuthoredLocalUnderTheReservedPrefixIsRefused(string name) {
        var exception = Assert.Throws<RuleException>(testCode: () => RulesFixture.Compile(rule: WithLocal(name: name)));

        Assert.Equal(
            actual: exception.Refusal,
            expected: RuleRefusal.NameReserved
        );
        Assert.Contains(
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "reserved '$' prefix"
        );
    }
    // The control: the same local without the prefix compiles, so the refusal is the prefix's alone.
    [Fact]
    public void AnAuthoredLocalWithoutThePrefixCompiles() => Assert.NotNull(@object: RulesFixture.Compile(rule: WithLocal(name: "key0")));
}
