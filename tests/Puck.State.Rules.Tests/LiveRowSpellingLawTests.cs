using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>A bracketed row position selects a row live only when its head names a declared family or the zone table.
/// Every other bracketed spelling leaves the live-row path as an ordinary row position, so the compiler answers with a
/// counted refusal instead of an exception the document validator does not catch.</summary>
public sealed class LiveRowSpellingLawTests {
    public static TheoryData<string> MalformedRowPositions() => new(
        "a.b[0]",
        "$bogus[0]",
        "1bad[0]",
        "[0]",
        "score !![0]",
        "piles[0]",
        "é[0]"
    );
    [MemberData(memberName: nameof(MalformedRowPositions))]
    [Theory]
    public void ABracketedRowPositionThatNamesNoFamilyRefusesByName(string spelling) {
        var failure = Assert.Throws<RuleException>(testCode: () => RulesFixture.Compile(rule: RulesFixture.Rule(
            gate: new ActionPredicate.CompareState(
                Comparison: ExpressionOp.Greater,
                State: spelling,
                Value: 0m
            ),
            name: "bracketed"
        )));

        _ = Assert.IsType<RuleRefusal>(@object: failure.Refusal);
    }
}
