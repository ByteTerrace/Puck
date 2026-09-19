using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a <c>push</c> transform holds one value, so a row whose kind resolves its write to
/// something other than a single-value write is refused by name rather than cast into one.</summary>
public sealed class PushRowKindLawTests {
    private static StateSection Section() => new(
        Rows: [
            new StateRow(
                Name: RulesFixture.Name(value: "trail"),
                Kind: CellKind.Vector,
                Domain: new StateDomain.Ring(
                    Capacity: 3,
                    Empty: -1L
                ),
                Space: "wide16"
            ),
            new StateRow(
                Name: RulesFixture.Name(value: "here"),
                Kind: CellKind.Vector,
                Space: "wide16"
            ),
        ],
        Spaces: [new StateSpace(
                Name: RulesFixture.Name(value: "wide16"),
                Model: "model",
                Revision: "r1",
                Dimensions: 16
            )]
    );

    [Fact]
    public void APushIntoAVectorRowIsRefusedRatherThanCastIntoAValueWrite() {
        var section = Section();
        var context = new RuleCompileContext(
            catalog: StateCatalog.Compile(section: section),
            generators: null,
            patterns: null,
            section: section,
            simulationRateHz: 30,
            tables: null,
            vocabulary: RuleVocabulary.Core
        );
        var failure = Assert.Throws<RuleException>(testCode: () => RuleCompiler.Compile(
            context: context,
            rule: new Rule(
                Name: RulesFixture.Name(value: "push"),
                Effects: [new ActionEffect.TransformState(Transform: new StateTransform.Push(
                            FromState: "here",
                            Row: "trail"
                        ))]
            )
        ));

        Assert.Equal(
            actual: failure.Refusal,
            expected: RuleRefusal.EffectSourceKindMismatch
        );
    }
}
