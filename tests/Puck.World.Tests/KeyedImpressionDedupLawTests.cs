using Xunit;


namespace Puck.World.Tests;

/// <summary>Proves the social-memory replacement primitive — an impression is an ordinary keyed row, and evidence
/// deduplication is an authored gate over two existing pieces of vocabulary: a <see cref="ExpressionProgram"/>
/// packing (origin, sequence) into one Int64 via <c>shiftLeft</c>/<c>bitOr</c>, and
/// <see cref="ActionPredicate.CompareValue"/> comparing that live pair against a row's own remembered marker. A
/// Level-mode rule re-evaluates its gate every tick it holds, so without the freshness check a standing claim would
/// re-blend the belief every tick rather than once per event — the <c>trust</c>/<c>trustUngated</c> rows below
/// isolate exactly that difference, fed by the same origin/sequence pair, one gated and one not.</summary>
public sealed class KeyedImpressionDedupLawTests {
    // (1 - trust) * 0.5 — the same bounded blend-toward-one the garden's re-authored belief rows use.
    private static ExpressionProgram Blend(string trustRow) => new(Instructions: [
        Instruction.Constant(value: 1), Instruction.Operand(key: "0",
            name: trustRow
        ), Instruction.Of(operation: ExpressionOp.Subtract),
        Instruction.Constant(value: 0.5m), Instruction.Of(operation: ExpressionOp.Multiply),
    ]);
    private static WorldDefinition BuildDocument() {
        var rows = new List<WorldStateRow> {
            Slot(name: "origin"), Slot(
            name: "seq",
            nonNegative: true
        ),
            new(
            Name: CellName.Parse(candidate: "trust"),
            Kind: CellKind.Fixed,
            Capacity: 4,
            Min: 0L,
            Max: 65_536L,
            Cells: [new StateCell(
                    Key: CellName.Parse(candidate: "0"),
                    Value: CellValue.Fixed(rawBits: 0)
                )]
        ),
            new(
            Name: CellName.Parse(candidate: "mark"),
            Kind: CellKind.Int,
            Capacity: 4,
            Min: 0L,
            Cells: [new StateCell(
                    Key: CellName.Parse(candidate: "0"),
                    Value: CellValue.Int(value: 0)
                )]
        ),
            new(
            Name: CellName.Parse(candidate: "trustUngated"),
            Kind: CellKind.Fixed,
            Capacity: 4,
            Min: 0L,
            Max: 65_536L,
            Cells: [new StateCell(
                    Key: CellName.Parse(candidate: "0"),
                    Value: CellValue.Fixed(rawBits: 0)
                )]
        ),
        };

        return Fixtures.BuildDocument().WithWorldState(rows: rows) with {
            Rules = [
                new WorldRule(
                Name: CellName.Parse(candidate: "gated-belief"),
                Mode: ActionTriggerMode.Level,
                Gate: new ActionPredicate.CompareValue(
                    Left: new ExpressionProgram(Instructions: [Instruction.Operand(key: "0",
                            name: "mark"
                        )]),
                    Comparison: ExpressionOp.NotEqual,
                    Right: Packed(),
                    Kind: CellKind.Int
                ),
                Effects: [
                        new ActionEffect.AddState(
                        State: "trust",
                        Key: "0",
                        Expression: Blend(trustRow: "trust")
                    ),
                        new ActionEffect.SetState(
                        State: "mark",
                        Key: "0",
                        Expression: Packed()
                    ),
                    ]
            ),
                // The control: the same blend, the same Level cadence, no freshness gate at all — proving the
                // dedup gate above is load-bearing rather than a no-op the effects would already refuse.
                new WorldRule(
                Name: CellName.Parse(candidate: "ungated-belief"),
                Mode: ActionTriggerMode.Level,
                Gate: new ActionPredicate.CompareState(
                    State: "origin",
                    Comparison: ExpressionOp.GreaterOrEqual,
                    Value: 0
                ),
                Effects: [new ActionEffect.AddState(
                        State: "trustUngated",
                        Key: "0",
                        Expression: Blend(trustRow: "trustUngated")
                    )]
            ),
            ],
        };
    }
    private static ExpressionProgram Packed() => new(Instructions: [
        Instruction.Operand(name: "origin"), Instruction.Constant(value: 32), Instruction.Of(operation: ExpressionOp.ShiftLeft),
        Instruction.Operand(name: "seq"), Instruction.Of(operation: ExpressionOp.BitOr),
    ]);
    private static long Read(WorldDefinition definition, string row) {
        var found = WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: row
        )!;

        return (StateRows.FindCell(
            cells: found.Cells,
            key: CellName.Parse(candidate: "0")
        )?.Value.AsFixed ?? 0L);
    }
    private static WorldStateRow Slot(string name, long initial = 0, bool nonNegative = false) => StateFixtures.IntSlot(
        min: (nonNegative ? 0L : null),
        name: name,
        value: initial
    );
    private static void Write(WorldFixture fixture, string row, long value) => fixture.WriteSlot(row: row, value: value);

    [Fact]
    public void StandingClaim_BlendsOnceThenHoldsWhileTheControlKeepsMovingEveryTick() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument());

        Write(
            fixture: fixture,
            row: "origin",
            value: 5
        );
        Write(
            fixture: fixture,
            row: "seq",
            value: 1
        );

        fixture.Step();
        Assert.Equal(
            expected: 32_768L,
            actual: Read(
                definition: fixture.Server.Definition,
                row: "trust"
            )
        ); // 0.5

        for (var index = 0; (index < 4); index++) {
            fixture.Step();
        }

        // The gated row admitted the one event once and stayed there; the ungated control kept re-blending every
        // tick with no new evidence at all, proving the gate — not mere idempotence of the effect — holds it still.
        Assert.Equal(
            expected: 32_768L,
            actual: Read(
                definition: fixture.Server.Definition,
                row: "trust"
            )
        ); // still 0.5
        Assert.Equal(
            expected: 63_488L,
            actual: Read(
                definition: fixture.Server.Definition,
                row: "trustUngated"
            )
        ); // 0.96875

        // A genuinely new event (a fresh sequence from the same origin) is admitted once more.
        Write(
            fixture: fixture,
            row: "seq",
            value: 2
        );
        fixture.Step();
        Assert.Equal(
            expected: 49_152L,
            actual: Read(
                definition: fixture.Server.Definition,
                row: "trust"
            )
        ); // 0.75
    }
}
