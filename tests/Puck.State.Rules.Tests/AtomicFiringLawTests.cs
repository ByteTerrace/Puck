using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: one rule firing is one arena journal scope. Every reversible effect lands in it,
/// the scope commits when every effect succeeds and rewinds on the first refusal with one counted refusal naming
/// the refusing effect, a <c>transaction</c> is a nested savepoint, and an irreversible arm is queued during the
/// scope, validated before the commit, and fired only after it.</summary>
public sealed class AtomicFiringLawTests {
    private static (ArenaEffectHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules, RuleLatch Latch) Arrange(params Rule[] rules) {
        var (host, evaluator, compiled, latch, _) = EvaluatorFixture.Arrange(rules: rules);

        return (host, evaluator, compiled, latch);
    }

    [Fact]
    public void ARefusedSecondEffectLeavesTheFirstCellUnchangedAndRecordsOneRefusal() {
        var (host, evaluator, rules, latch) = Arrange(rules: new Rule(
            Name: RulesFixture.Name(value: "pay"),
            Effects: [
                EvaluatorFixture.Set(
                    row: "score",
                    value: 7m
                ),
                EvaluatorFixture.Set(
                    row: "locked",
                    value: 1m
                ),
            ]
        ));

        // A rewound firing moved nothing, and the evaluation says so.
        Assert.False(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        ));

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 0L
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "locked"
            ),
            expected: 0L
        );

        var refusals = evaluator.Diagnostics();

        Assert.Single(collection: refusals);
        Assert.Equal(
            actual: refusals[0].Count,
            expected: 1UL
        );
        Assert.Contains(
            actualString: refusals[0].Effect,
            expectedSubstring: "locked"
        );
    }
    [Fact]
    public void AFiringWhoseUndoRecordPassesItsCeilingRewindsWithOneNamedRefusal() {
        var (host, evaluator, rules, latch) = Arrange(rules: new Rule(
            Name: RulesFixture.Name(value: "flood"),
            Effects: [
                EvaluatorFixture.Set(
                    row: "score",
                    value: 7m
                ),
                EvaluatorFixture.Set(
                    row: "other",
                    value: 9m
                ),
            ]
        ));
        var arena = host.Arena;
        var score = EvaluatorFixture.Ordinal(
            host: host,
            row: "score"
        );

        Assert.True(condition: arena.Catalog.Keys.TryResolve(
            key: out var slot,
            name: StateRow.SlotKey
        ));

        // An enclosing scope that already holds all the record the ceiling admits, so the firing's first write is
        // the one that crosses it.
        var outer = arena.BeginScope();

        while ((arena.Journal.Bytes + ArenaJournal.EntryBytes) <= ArenaCapacity.MaxJournalBytes) {
            Assert.True(condition: arena.TryWrite(
                key: slot,
                operand: 1L,
                reason: out _,
                rowOrdinal: score,
                write: StateWriteKind.Set
            ));
        }

        Assert.False(condition: arena.Journal.OverCeiling);
        Assert.False(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        ));

        // The firing rewound to what the enclosing scope had written, and its second effect never ran.
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 1L
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "other"
            ),
            expected: 0L
        );
        Assert.False(condition: arena.Journal.OverCeiling);

        var refusal = Assert.Single(collection: evaluator.Diagnostics());

        Assert.Equal(
            actual: refusal.Refusal,
            expected: RuleEffectRefusal.JournalCeiling
        );
        Assert.Equal(
            actual: refusal.Rule,
            expected: "flood"
        );
        Assert.Contains(
            actualString: refusal.Detail,
            expectedSubstring: "byte ceiling"
        );

        arena.Rewind(mark: outer);

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 0L
        );
    }
    [Fact]
    public void ASavepointRewindsItsOwnWritesWhileSiblingsAndOnFailureLand() {
        var (host, evaluator, rules, latch) = Arrange(rules: new Rule(
            Name: RulesFixture.Name(value: "deal"),
            Effects: [
                EvaluatorFixture.Set(
                    row: "score",
                    value: 1m
                ),
                new ActionEffect.Transaction(
                    Effects: [
                        EvaluatorFixture.Set(
                            row: "other",
                            value: 5m
                        ),
                        EvaluatorFixture.Set(
                            row: "locked",
                            value: 1m
                        ),
                    ],
                    OnFailure: [EvaluatorFixture.Set(
                            row: "flag",
                            value: 1m
                        )]
                ),
                EvaluatorFixture.Set(
                    row: "third",
                    value: 9m
                ),
            ]
        ));

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 1L
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "other"
            ),
            expected: 0L
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "flag"
            ),
            expected: 1L
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "third"
            ),
            expected: 9L
        );
    }
    [Fact]
    public void ARefusingOnFailureRewindsTheWholeFiring() {
        var (host, evaluator, rules, latch) = Arrange(rules: new Rule(
            Name: RulesFixture.Name(value: "deal"),
            Effects: [
                EvaluatorFixture.Set(
                    row: "score",
                    value: 1m
                ),
                new ActionEffect.Transaction(
                    Effects: [EvaluatorFixture.Set(
                            row: "locked",
                            value: 1m
                        )],
                    OnFailure: [EvaluatorFixture.Set(
                            row: "locked",
                            value: 2m
                        )]
                ),
                EvaluatorFixture.Set(
                    row: "other",
                    value: 3m
                ),
            ]
        ));

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 0L
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "other"
            ),
            expected: 0L
        );
    }
    [Fact]
    public void ASavepointWithNoFailureBranchPropagatesItsRefusal() {
        var (host, evaluator, rules, latch) = Arrange(rules: new Rule(
            Name: RulesFixture.Name(value: "deal"),
            Effects: [
                EvaluatorFixture.Set(
                    row: "score",
                    value: 1m
                ),
                new ActionEffect.Transaction(Effects: [EvaluatorFixture.Set(
                        row: "locked",
                        value: 1m
                    )]),
            ]
        ));

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 0L
        );
    }
    [Fact]
    public void AnIfBranchFiresInsideTheFiringsOwnScope() {
        var (host, evaluator, rules, latch) = Arrange(rules: new Rule(
            Name: RulesFixture.Name(value: "branch"),
            Effects: [
                EvaluatorFixture.Set(
                    row: "score",
                    value: 4m
                ),
                new ActionEffect.If(
                    Condition: EvaluatorFixture.Compare(
                        comparison: ActionStateComparison.Equal,
                        row: "score",
                        value: 4m
                    ),
                    Then: [EvaluatorFixture.Set(
                            row: "other",
                            value: 1m
                        )],
                    Else: [EvaluatorFixture.Set(
                            row: "flag",
                            value: 1m
                        )]
                ),
            ]
        ));

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        // The condition reads what the firing already wrote into its open scope, so the 'then' branch runs.
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "other"
            ),
            expected: 1L
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "flag"
            ),
            expected: 0L
        );
    }
    [Fact]
    public void AnIrreversibleArmNeverFiresFromARewoundScope() {
        var arms = new ArmLedger();

        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Name: RulesFixture.Name(value: "save"),
                Effects: [
                    new ArmLedger.Stamp(),
                    EvaluatorFixture.Set(
                        row: "locked",
                        value: 1m
                    ),
                ]
            )],
            vocabulary: arms.Vocabulary()
        );

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Empty(collection: arms.Fired);
        Assert.Equal(
            actual: arms.Preflights,
            expected: 0
        );
    }
    [Fact]
    public void AnIrreversibleArmIsValidatedBeforeTheCommitAndFiredAfterIt() {
        var arms = new ArmLedger();

        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Name: RulesFixture.Name(value: "save"),
                Effects: [
                    EvaluatorFixture.Set(
                        row: "score",
                        value: 3m
                    ),
                    new ArmLedger.Stamp(),
                ]
            )],
            vocabulary: arms.Vocabulary()
        );

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: arms.Preflights,
            expected: 1
        );
        Assert.Equal<long>(
            actual: arms.Fired,
            // The arm sees the committed value, which is what the pre-commit check was run against.
            expected: [3L]
        );
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 3L
        );
    }
    [Fact]
    public void AnIrreversibleArmRefusingBeforeTheCommitRewindsTheFiring() {
        var arms = new ArmLedger { RefusePreflight = true };

        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Name: RulesFixture.Name(value: "save"),
                Effects: [
                    EvaluatorFixture.Set(
                        row: "score",
                        value: 3m
                    ),
                    new ArmLedger.Stamp(),
                ]
            )],
            vocabulary: arms.Vocabulary()
        );

        Assert.False(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        ));

        Assert.Empty(collection: arms.Fired);
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 0L
        );
        Assert.Single(collection: evaluator.Diagnostics());
    }
    // Whatever throws out of a firing leaves with every scope the firing opened rewound, the savepoint's before the
    // firing's, and with nothing queued: the arena is untouched, and the next firing opens and commits as usual.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AThrowOutOfAFiringLeavesNoScopeOpenAndNothingWritten(bool insideSavepoint) {
        var arms = new ArmLedger { Throw = true };
        ActionEffect[] body = [
            EvaluatorFixture.Set(
                row: "score",
                value: 3m
            ),
            new ArmLedger.Stamp(),
            new ArmLedger.Trip(),
        ];

        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Name: RulesFixture.Name(value: "save"),
                Effects: (insideSavepoint
                    ? [
                        EvaluatorFixture.Set(
                            row: "flag",
                            value: 1m
                        ),
                        new ActionEffect.Transaction(
                            Effects: body,
                            OnFailure: []
                        ),
                    ]
                    : body
                )
            )],
            vocabulary: arms.Vocabulary()
        );
        var before = host.Arena.ComputeHash();

        _ = Assert.Throws<InvalidOperationException>(testCode: () => evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        ));
        Assert.Equal(
            actual: host.Arena.ComputeHash(),
            expected: before
        );

        // No scope is left open: a fresh one opens at the mark the first would have, and closes.
        var mark = host.Arena.BeginScope();

        Assert.Equal(
            actual: mark,
            expected: 0
        );

        host.Arena.Rewind(mark: mark);

        // The queue was discarded with the scope, so the arm the thrown firing queued never fires later.
        arms.Throw = false;

        Assert.True(condition: evaluator.Evaluate(
            latch: new RuleLatch(),
            rules: rules,
            stepTicks: 1UL
        ));
        Assert.Equal(
            actual: arms.Fired,
            expected: [3L]
        );
    }
    [Fact]
    public void AnIrreversibleArmRefusingAfterTheCommitLeavesTheArenaCommittedAndRecordsANamedRefusal() {
        var arms = new ArmLedger { RefuseFire = true };

        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Name: RulesFixture.Name(value: "save"),
                Effects: [
                    EvaluatorFixture.Set(
                        row: "score",
                        value: 3m
                    ),
                    new ArmLedger.Stamp(),
                ]
            )],
            vocabulary: arms.Vocabulary()
        );

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "score"
            ),
            expected: 3L
        );

        var refusals = evaluator.Diagnostics();

        Assert.Single(collection: refusals);
        Assert.Equal(
            actual: refusals[0].Refusal,
            expected: RuleEffectRefusal.IrreversibleArmFailed
        );
    }
    [Fact]
    public void AnIrreversibleArmInsideAFailedSavepointIsDiscarded() {
        var arms = new ArmLedger();

        var (host, evaluator, rules, latch, _) = EvaluatorFixture.Arrange(
            rules: [new Rule(
                Name: RulesFixture.Name(value: "save"),
                Effects: [new ActionEffect.Transaction(
                        Effects: [
                            new ArmLedger.Stamp(),
                            EvaluatorFixture.Set(
                                row: "locked",
                                value: 1m
                            ),
                        ],
                        OnFailure: [EvaluatorFixture.Set(
                                row: "flag",
                                value: 1m
                            )]
                    )]
            )],
            vocabulary: arms.Vocabulary()
        );

        _ = evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL
        );

        Assert.Empty(collection: arms.Fired);
        Assert.Equal(
            actual: EvaluatorFixture.Cell(
                host: host,
                row: "flag"
            ),
            expected: 1L
        );
    }
}
/// <summary>A registered arm that cannot be rewound, recording every pre-commit check and every firing so a law can
/// assert which ran.</summary>
public sealed class ArmLedger {
    /// <summary>An authored arm whose firing leaves the arena.</summary>
    public sealed record Stamp : ActionEffect;
    /// <summary>An authored reversible arm that throws while <see cref="Throw"/> is set.</summary>
    public sealed record Trip : ActionEffect;

    /// <summary>Gets the committed <c>score</c> each firing observed, in firing order.</summary>
    public List<long> Fired { get; } = [];
    /// <summary>Gets how many pre-commit checks ran.</summary>
    public int Preflights { get; private set; }
    /// <summary>Gets or sets whether the arm refuses when it fires after the commit.</summary>
    public bool RefuseFire { get; set; }
    /// <summary>Gets or sets whether the arm refuses its pre-commit check.</summary>
    public bool RefusePreflight { get; set; }
    /// <summary>Gets or sets whether <see cref="Trip"/> throws when it fires.</summary>
    public bool Throw { get; set; }

    /// <summary>Builds a vocabulary registering the arm.</summary>
    /// <returns>The vocabulary.</returns>
    public RuleVocabulary Vocabulary() => new(
        effects: [new StampArm(ledger: this), new TripArm(ledger: this)],
        keys: [],
        operands: [],
        predicates: []
    );

    private sealed class TripArm : EffectFamily {
        private readonly ArmLedger m_ledger;

        public TripArm(ArmLedger ledger) => m_ledger = ledger;

        public override string Discriminator => "trip";
        public override Type EffectType => typeof(Trip);

        public override IRuleEffect Compile(ActionEffect effect, string ruleName, RuleCompileContext context) => new TripEffect(ledger: m_ledger);
    }
    private sealed class TripEffect : RuleEffect {
        private readonly ArmLedger m_ledger;

        public TripEffect(ArmLedger ledger) : base(describe: "trip") => m_ledger = ledger;

        public override bool SubmitsMutation => false;

        public override RuleWork Cost(IRuleCostContext context) => 1L;
        public override bool TryFire(IEffectHost host, in EffectFiring firing, out EffectRefusal refusal) {
            refusal = EffectRefusal.None;

            return (m_ledger.Throw
                ? throw new InvalidOperationException(message: "the arm tripped")
                : true
            );
        }
    }
    private sealed class StampArm : EffectFamily {
        private readonly ArmLedger m_ledger;

        public StampArm(ArmLedger ledger) => m_ledger = ledger;

        public override string Discriminator => "stamp";
        public override Type EffectType => typeof(Stamp);

        public override IRuleEffect Compile(ActionEffect effect, string ruleName, RuleCompileContext context) => new StampEffect(
            ledger: m_ledger,
            rowOrdinal: RuleCompiler.ResolveRowOrdinal(
                context: context,
                name: "score"
            )
        );
    }
    private sealed class StampEffect : RuleEffect {
        private readonly ArmLedger m_ledger;
        private readonly int m_rowOrdinal;

        public StampEffect(ArmLedger ledger, int rowOrdinal) : base(describe: "stamp") {
            m_ledger = ledger;
            m_rowOrdinal = rowOrdinal;
        }

        public override EffectNeeds Needs => EffectNeeds.Irreversible;
        public override bool SubmitsMutation => false;

        public override RuleWork Cost(IRuleCostContext context) => 1L;
        public override bool TryFire(IEffectHost host, in EffectFiring firing, out EffectRefusal refusal) {
            ArgumentNullException.ThrowIfNull(argument: host);

            if (firing.Preflight) {
                m_ledger.Preflights++;
                refusal = (m_ledger.RefusePreflight
                    ? EffectRefusal.Of(
                        code: RuleEffectRefusal.MutationRejected,
                        reason: "the stamp cannot be admitted"
                    )
                    : EffectRefusal.None
                );

                return !m_ledger.RefusePreflight;
            }

            if (m_ledger.RefuseFire) {
                refusal = EffectRefusal.Of(
                    code: RuleEffectRefusal.IrreversibleArmFailed,
                    reason: "the stamp could not be written"
                );

                return false;
            }

            _ = host.Arena.TryRead(
                key: host.Catalog.Keys.Intern(name: StateRow.SlotKey),
                rowOrdinal: m_rowOrdinal,
                value: out var value
            );
            m_ledger.Fired.Add(item: value.AsInt);
            refusal = EffectRefusal.None;

            return true;
        }
    }
}
