using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a saturating write stores the clamped value while the mutation still carries the
/// rule's own operand, and NEVER refuses on range — so it never triggers a transaction's <c>onFailure</c>; a
/// Refuse-policy write outside the row's envelope refuses by name and, inside a transaction, rolls back every
/// earlier step in the same branch and fires <c>onFailure</c> when one is authored, exactly as any other refused
/// step would. Uses <see cref="FrameHost"/>, the production <see cref="StateFrame"/>-backed rule host, so every
/// write decides through the row's own <see cref="StateRow.TryAdmitWrite"/>.</summary>
public sealed class TransactionEnvelopeLawTests {
    private static (FrameHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules, RuleLatch Latch) Arrange(IReadOnlyList<Rule> rules, StateRow[] rows) {
        var section = new Section(rows: rows);
        var catalog = StateCatalog.Compile(section: section);
        var context = new RuleCompileContext(
            section: section,
            catalog: catalog,
            tables: null,
            patterns: null,
            generators: null,
            simulationRateHz: 240,
            vocabulary: RuleVocabulary.Core
        );
        var host = new FrameHost(
            new FrameLayout(
                rows: rows,
                topology: static _ => null
            ),
            rows,
            catalog,
            CompiledPatterns.Empty,
            []
        );

        host.Frame.Load(source: new RowStore(rows: rows));

        return (host, host.Evaluator, RuleCompiler.CompileAll(
            context: context,
            rules: rules
        ), host.Latch);
    }
    private static Rule R(string name, params ActionEffect[] effects) => new(
        Name: CellName.Parse(candidate: name),
        Effects: effects,
        Gate: null
    );
    private static StateRow Slot(string name, long value, long? min = null, long? max = null, StateOverflow overflow = StateOverflow.Refuse) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Min: min,
        Max: max,
        Overflow: overflow,
        Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: value
            )]
    );

    [Fact]
    public void ASaturatingWriteInsideATransactionCommitsAndNeverFiresOnFailure() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "grow",
                    new ActionEffect.Transaction(
                        Effects: [
                            new ActionEffect.SetState(
                                State: "flag",
                                Value: 1m
                            ),
                            new ActionEffect.AddState(
                                State: "meter",
                                Value: 50m
                            ),
                        ],
                        OnFailure: [new ActionEffect.SetState(
                                State: "refunds",
                                Value: 1m
                            )]
                    )
                )],
            rows: [Slot(
                    name: "meter",
                    value: 90L,
                    min: 0L,
                    max: 100L,
                    overflow: StateOverflow.Saturate
                ), Slot(
                    name: "flag",
                    value: 0L
                ), Slot(
                    name: "refunds",
                    value: 0L
                )]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));

        Assert.Equal(
            expected: 100L,
            actual: Cell(
                host: host,
                name: "meter"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: Cell(
                host: host,
                name: "flag"
            )
        );
        // The transaction committed, so onFailure never fired.
        Assert.Equal(
            expected: 0L,
            actual: Cell(
                host: host,
                name: "refunds"
            )
        );
        Assert.Empty(collection: evaluator.Diagnostics());
    }
    [Fact]
    public void ARefusingWriteInsideATransactionRollsBackEveryStepAndFiresOnFailure() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "grow",
                    new ActionEffect.Transaction(
                        Effects: [
                            new ActionEffect.SetState(
                                State: "flag",
                                Value: 1m
                            ),
                            new ActionEffect.AddState(
                                State: "meter",
                                Value: 50m
                            ),
                        ],
                        OnFailure: [new ActionEffect.SetState(
                                State: "refunds",
                                Value: 1m
                            )]
                    )
                )],
            rows: [Slot(
                    name: "meter",
                    value: 90L,
                    min: 0L,
                    max: 100L,
                    overflow: StateOverflow.Refuse
                ), Slot(
                    name: "flag",
                    value: 0L
                ), Slot(
                    name: "refunds",
                    value: 0L
                )]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));

        // Neither transaction step landed — flag rolled back to its pre-transaction value.
        Assert.Equal(
            expected: 90L,
            actual: Cell(
                host: host,
                name: "meter"
            )
        );
        Assert.Equal(
            expected: 0L,
            actual: Cell(
                host: host,
                name: "flag"
            )
        );
        Assert.Equal(
            expected: 1L,
            actual: Cell(
                host: host,
                name: "refunds"
            )
        );

        var diagnostic = Assert.Single(collection: evaluator.Diagnostics());

        Assert.Equal<Enum>(
            RuleEffectRefusal.MutationRejected,
            diagnostic.Refusal
        );
    }
    // One-sided ranges behave identically inside a transaction: a floor with no ceiling saturates or refuses on
    // its declared side alone.
    [InlineData(StateOverflow.Saturate, 0L, 1L)]
    [InlineData(StateOverflow.Refuse, 5L, 0L)]
    [Theory]
    public void AOneSidedRangeInsideATransactionSaturatesOrRefusesOnItsDeclaredSideAlone(StateOverflow overflow, long expectedMeter, long expectedFlag) {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "drain",
                    new ActionEffect.Transaction(Effects: [
                        new ActionEffect.SetState(
                            State: "flag",
                            Value: 1m
                        ),
                        new ActionEffect.AddState(
                            State: "meter",
                            Value: -100m
                        ),
                    ])
                )],
            rows: [Slot(
                    name: "meter",
                    value: 5L,
                    min: 0L,
                    overflow: overflow
                ), Slot(
                    name: "flag",
                    value: 0L
                )]
        );

        Assert.Equal(
            expected: (expectedFlag == 1L),
            actual: evaluator.Evaluate(
                latch: latch,
                rules: rules,
                stepTicks: 1UL,
                tick: 1UL,
                engineTick: 1UL
            )
        );
        Assert.Equal(
            expected: expectedMeter,
            actual: Cell(
                host: host,
                name: "meter"
            )
        );
        Assert.Equal(
            expected: expectedFlag,
            actual: Cell(
                host: host,
                name: "flag"
            )
        );
    }
    // A saturating write at the top level (outside any transaction) also stores the clamped value and reports no
    // refusal — the frame write path and the transaction path decide identically.
    [Fact]
    public void ATopLevelSaturatingWriteStoresTheClampedValueWithNoRefusal() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "grow",
                    new ActionEffect.AddState(
                        State: "meter",
                        Value: 500m
                    )
                )],
            rows: [Slot(
                    name: "meter",
                    value: 90L,
                    min: 0L,
                    max: 100L,
                    overflow: StateOverflow.Saturate
                )]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: 100L,
            actual: Cell(
                host: host,
                name: "meter"
            )
        );
        Assert.Empty(collection: evaluator.Diagnostics());
    }

    private static long Cell(FrameHost host, string name) {
        Assert.True(condition: host.Frame.TryStored(
            row: StateRows.FindStateRow(
                rows: host.Frame.Rows,
                name: name
            )!,
            key: StateRow.SlotKey,
            value: out var value,
            text: out _
        ));

        return value;
    }

    private sealed class Section(IReadOnlyList<StateRow> rows) : IStateSection {
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<StateRow> Rows => rows;
    }
}
