using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a write whose row is selected live addresses one member of a zone table or a
/// declared family per firing. Its read and write sets cover every member it could select, and a selection that
/// resolves to no row is an absence, so the firing rewinds rather than dropping the value silently.</summary>
public sealed class LiveDestinationLawTests {
    private static StateSection Section(long index) => new(
        Families: [new StateFamily(
                Indices: [0, 2],
                Name: RulesFixture.Name(value: "Pile"),
                Size: 2
            )],
        Rows: [
            EvaluatorFixture.Slot(
                name: "score",
                value: 0L
            ),
            EvaluatorFixture.Slot(
                name: "index",
                value: index
            ),
            EvaluatorFixture.Slot(
                name: "Pile0",
                value: 0L
            ),
            EvaluatorFixture.Slot(
                name: "Pile2",
                value: 0L
            ),
        ]
    );

    private static Rule Rule() => new(
        Locals: [new RuleLocal(
                Expression: RulesFixture.Program(text: "index"),
                Kind: CellKind.Int,
                Name: RulesFixture.Name(value: "i")
            )],
        Effects: [
            EvaluatorFixture.Set(
                row: "score",
                value: 5m
            ),
            new ActionEffect.SetState(
                State: $"Pile[{RuleFacts.LocalPrefix}i]",
                Value: 1m
            ),
        ],
        Name: RulesFixture.Name(value: "write")
    );

    [Fact]
    public void ALiveDestinationsWriteSetListsEveryMemberItCouldSelect() {
        var section = Section(index: 0L);
        var context = EvaluatorFixture.Context(section: section);
        var compiled = RuleCompiler.Compile(
            context: context,
            rule: Rule()
        );
        var writes = new List<CellAccess>();

        compiled.CollectWrites(into: writes);

        var rows = writes.Select(selector: static access => access.RowOrdinal).ToHashSet();

        // Both members are candidates, so both are in the write set even though one firing touches one of them.
        Assert.Contains(expected: Ordinal(context: context, name: "Pile0"), collection: rows);
        Assert.Contains(expected: Ordinal(context: context, name: "Pile2"), collection: rows);

        var reads = new List<CellAccess>();

        compiled.CollectReads(into: reads);

        // The cell the index resolves through is a read, so a scheduler sees the dependency.
        Assert.Contains(expected: Ordinal(context: context, name: "index"), collection: reads.Select(selector: static access => access.RowOrdinal));
    }

    [Fact]
    public void ALiveDestinationThatSelectsNoRowRefusesAndRewindsTheFiring() {
        // Family index 1 is a gap: the family carries 0 and 2 only.
        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [Rule()],
            section: Section(index: 1L)
        );

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        var refusal = Assert.Single(collection: evaluator.Diagnostics());

        Assert.Equal(
            actual: refusal.Refusal,
            expected: RuleEffectRefusal.Arithmetic
        );

        // The earlier effect of the same firing is rewound: nothing half-lands.
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 0L
        );
    }

    private static int Ordinal(RuleCompileContext context, string name) {
        Assert.True(condition: context.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: name
        ));

        return handle.Ordinal;
    }
}
