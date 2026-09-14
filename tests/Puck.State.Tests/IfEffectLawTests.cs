using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <c>ActionEffect.If</c> — a false condition is not a failure and runs
/// <c>else</c> (or nothing); a condition that cannot evaluate runs neither branch and is a counted
/// <see cref="RuleEffectRefusal.Arithmetic"/> refusal; both branches read the frame at the effect's own position, so
/// an earlier same-firing write (numeric, text, or a removal, on any row) is visible to the condition and to a later
/// effect exactly as it would be outside an <c>if</c>; an <c>if</c> may sit inside a <c>transaction</c>, and a
/// <c>transaction</c> may sit inside an <c>if</c> that is not itself inside one; a branch is not a transaction, so an
/// earlier effect in the same branch keeps its write even when a later one in the same branch refuses; and a
/// transaction's own non-submitting effects replay the branch <c>if</c> chose during preflight rather than
/// re-reading the condition, so a later step's committed write can never make the branch fire twice or not at all.</summary>
public sealed class IfEffectLawTests {
    // An effect this library owns nothing about (SubmitsMutation false): the host is asked to fire it, and records
    // whether the call was the transaction's own preflight dry run or its post-commit real replay.
    private sealed class NarrateEffect(string describe) : EffectFact(describe) {
        public override long Cost(RuleCompileContext context) => 1;
        public override bool SubmitsMutation => false;
    }

    // A document-shaped host: numeric writes decide through the row's own TryAdmitWrite (so a bounded row refuses
    // or saturates exactly as the real engine does), text writes replace the cell's text, and a row named in
    // RefusedRows refuses every write by name — the same "locked row" convention RuleEvaluatorLawTests uses, plus
    // support for RemoveCell and text cells that HeadlessHost there does not need.
    private sealed class DocumentHost : IRuleHost, IStateSection {
        private readonly Stack<(IReadOnlyList<StateRow> Rows, int Composed)> m_scopes = new();
        private readonly long[] m_patternWord = new long[PatternCapacity.MaxWord];

        private int m_composed;
        private StateStore? m_store;

        public List<RuleRuntimeDiagnostic> Narrated { get; } = [];
        public List<(bool Preflight, string Describe)> NonSubmittingCalls { get; } = [];
        public HashSet<string> RefusedRows { get; } = [];
        public RuleEvaluator Evaluator { get; set; } = null!;

        public DocumentHost(params StateRow[] rows) {
            Rows = rows;
            Catalog = StateCatalog.Compile(section: this);
        }

        IReadOnlyList<IStateSlot>? IStateSection.IdentitySlots => null;
        IReadOnlyList<LatticeTopology>? IStateSection.Lattices => null;
        IReadOnlyList<IStateSlot>? IStateSection.ParticipantSlots => null;

        public string? BoundEachKey => Evaluator.BoundEachKey;
        public string? BoundPreviousKey { get; set; }
        public string? BoundTokenKey { get; set; }
        public StateCatalog Catalog { get; }
        public int Installs { get; private set; }
        public Span<long> PatternWord => m_patternWord;
        public CompiledPatterns Patterns { get; set; } = CompiledPatterns.Empty;
        public IReadOnlyList<StateRow> Rows { get; private set; }
        public StateStore Store => (m_store ??= new RowStore(rows: () => Rows));
        public bool TableKeyMissing { get; set; }
        public ulong Tick => Evaluator.Tick;
        public ulong EngineTick => Evaluator.EngineTick;

        private void Install(StateRow row, List<StateCell> cells, bool preflight) {
            var rows = new List<StateRow>(collection: Rows);

            rows[rows.IndexOf(item: row)] = row with { Cells = cells };
            Rows = rows;
            if (preflight) { m_composed++; } else { Installs++; }
        }

        public void BeginPreflight() => m_scopes.Push(item: (Rows, m_composed));
        public long BindingValue(int ordinal) => Evaluator.BindingValue(ordinal: ordinal);
        public Span<long> BoardScratch(int cells) => new long[cells];
        public int BoundIndex(BoundKey key) => Evaluator.BoundIndex(key: key);
        public long Cell(string row, string? key = null) {
            Assert.True(
                condition: StateReader.TryRead(
                    rows: Rows,
                    rowName: row,
                    key: key,
                    tick: Evaluator.Tick,
                    engineTick: Evaluator.EngineTick,
                    row: out _,
                    rawValue: out var raw,
                    text: out _
                ),
                userMessage: row
            );
            return (raw ?? 0L);
        }
        public string? Text(string row, string? key = null) {
            Assert.True(
                condition: StateReader.TryRead(
                    rows: Rows,
                    rowName: row,
                    key: key,
                    tick: Evaluator.Tick,
                    engineTick: Evaluator.EngineTick,
                    row: out _,
                    rawValue: out _,
                    text: out var text
                ),
                userMessage: row
            );
            return text;
        }
        public void EndPreflight() => (Rows, m_composed) = m_scopes.Pop();
        public EffectOutcome FireEffect(EffectFact effect, string ruleName, ulong tick, ulong stepTicks, bool preflight) {
            NonSubmittingCalls.Add(item: (preflight, effect.Describe));

            return EffectOutcome.Applied;
        }
        public void RefusalRecorded(in RuleRuntimeDiagnostic diagnostic) => Narrated.Add(item: diagnostic);
        public void ReportTableKeyMissing(string table, long key) => Evaluator.ReportTableKeyMissing(
            key: key,
            table: table
        );
        public CompiledTable Table(int ordinal) => throw new InvalidOperationException();
        public bool TryApply(StateMutation mutation, ulong tick, bool preflight, out string reason) {
            reason = string.Empty;

            switch (mutation) {
                case StateMutation.UpsertCell cell when RefusedRows.Contains(item: cell.Row):
                    reason = $"row '{cell.Row}' refuses every write";

                    return false;
                case StateMutation.UpsertCell cell: {
                        var row = StateRows.FindStateRow(
                            rows: Rows,
                            name: cell.Row
                        )!;
                        var key = CellName.Parse(candidate: cell.Key);
                        var cells = new List<StateCell>(collection: (row.Cells ?? []));
                        var at = cells.FindIndex(match: c => (c.Key == key));

                        if (row.Kind == CellKind.Text) {
                            var textCell = new StateCell(
                                Key: key,
                                Text: cell.Text
                            );

                            if (at >= 0) { cells[at] = textCell; } else { cells.Add(item: textCell); }
                            Install(
                                cells: cells,
                                preflight: preflight,
                                row: row
                            );

                            return true;
                        }

                        var current = ((at >= 0)
                            ? cells[at].Value
                            : 0L
                        );

                        if (!row.TryAdmitWrite(
                            current: current,
                            operand: cell.Value,
                            write: cell.Write,
                            stored: out var admitted,
                            reason: out reason
                        )) {
                            return false;
                        }

                        var next = new StateCell(
                            Key: key,
                            Value: admitted
                        );

                        if (at >= 0) { cells[at] = next; } else { cells.Add(item: next); }
                        Install(
                            cells: cells,
                            preflight: preflight,
                            row: row
                        );

                        return true;
                    }
                case StateMutation.RemoveCell cell: {
                        var row = StateRows.FindStateRow(
                            rows: Rows,
                            name: cell.Row
                        )!;
                        var key = CellName.Parse(candidate: cell.Key);
                        var cells = new List<StateCell>(collection: (row.Cells ?? []));

                        if (cells.RemoveAll(match: c => (c.Key == key)) == 0) {
                            reason = $"'{cell.Row}' holds no cell '{cell.Key}'";

                            return false;
                        }
                        Install(
                            cells: cells,
                            preflight: preflight,
                            row: row
                        );

                        return true;
                    }
                default:
                    reason = $"{mutation.GetType().Name} is not a headless mutation";

                    return false;
            }
        }
        public bool TryCommitPreflight(ulong tick, out string reason) {
            reason = string.Empty;
            var (_, before) = m_scopes.Pop();
            var installed = (m_composed > before);

            m_composed = before;
            if (installed) { Installs++; }

            return installed;
        }
        public bool TryEvaluateOwn(CompiledRule rule, RuleLatch latch, ulong tick, ulong stepTicks, out bool applied) {
            applied = false;

            return false;
        }
    }

    private static (DocumentHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules, RuleLatch Latch) Arrange(IReadOnlyList<Rule> rules, StateRow[] rows) {
        var host = new DocumentHost(rows);
        var evaluator = new RuleEvaluator(host: host);

        host.Evaluator = evaluator;
        var context = new RuleCompileContext(
            section: host,
            catalog: host.Catalog,
            tables: null,
            patterns: null,
            generators: null,
            simulationRateHz: 240,
            vocabulary: RuleVocabulary.Core
        );

        return (host, evaluator, RuleCompiler.CompileAll(
            context: context,
            rules: rules
        ), new RuleLatch());
    }
    private static ValueExpression Expr(string text) {
        Assert.True(
            condition: ExpressionSpelling.TryParse(
                error: out var error,
                text: text,
                tokens: out var tokens
            ),
            userMessage: error
        );

        return new ValueExpression(Tokens: tokens);
    }
    private static Rule R(string name, ActionPredicate? gate, params ActionEffect[] effects) => new(
        Name: CellName.Parse(candidate: name),
        Effects: effects,
        Gate: gate
    );
    private static StateRow Slot(string name, long value = 0L, CellKind kind = CellKind.Int, long? min = null, long? max = null) => new(
        Name: CellName.Parse(candidate: name),
        Kind: kind,
        Min: min,
        Max: max,
        Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: value
            )]
    );
    private static StateRow TextSlot(string name, string? text = null) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Text,
        Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Text: text
            )]
    );

    [Fact]
    public void AFailedConditionRunsNeitherBranchAndIsACountedArithmeticRefusalNotASilentClose() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "cond",
                    gate: null,
                    effects: new ActionEffect.If(
                        Condition: new ActionPredicate.CompareValue(
                            Left: Expr(text: "primeAt(999)"),
                            Comparison: ActionStateComparison.Greater,
                            Right: Expr(text: "0"),
                            Kind: CellKind.Int
                        ),
                        Then: [new ActionEffect.SetState(
                                State: "hit",
                                Value: 1m
                            )],
                        Else: [new ActionEffect.SetState(
                                State: "miss",
                                Value: 1m
                            )]
                    )
                )],
            rows: [Slot(name: "hit"), Slot(name: "miss")]
        );

        Assert.False(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: 0L,
            actual: host.Cell(row: "hit")
        );
        Assert.Equal(
            expected: 0L,
            actual: host.Cell(row: "miss")
        );

        var diagnostic = Assert.Single(collection: evaluator.Diagnostics());

        Assert.Equal<Enum>(
            RuleEffectRefusal.Arithmetic,
            diagnostic.Refusal
        );
    }
    [Fact]
    public void AFalseConditionRunsTheElseBranchAndIsNotAFailure() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "cond",
                    gate: null,
                    effects: new ActionEffect.If(
                        Condition: new ActionPredicate.CompareState(
                            State: "flag",
                            Comparison: ActionStateComparison.Equal,
                            Value: 1m
                        ),
                        Then: [new ActionEffect.SetState(
                                State: "hit",
                                Value: 1m
                            )],
                        Else: [new ActionEffect.SetState(
                                State: "miss",
                                Value: 1m
                            )]
                    )
                )],
            rows: [Slot(name: "flag"), Slot(name: "hit"), Slot(name: "miss")]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: 0L,
            actual: host.Cell(row: "hit")
        );
        Assert.Equal(
            expected: 1L,
            actual: host.Cell(row: "miss")
        );
        Assert.Empty(collection: evaluator.Diagnostics());
    }
    [Fact]
    public void AFalseConditionWithNoElseRunsNothingAndIsNotAFailure() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "cond",
                    gate: null,
                    effects: new ActionEffect.If(
                        Condition: new ActionPredicate.CompareState(
                            State: "flag",
                            Comparison: ActionStateComparison.Equal,
                            Value: 1m
                        ),
                        Then: [new ActionEffect.SetState(
                                State: "hit",
                                Value: 1m
                            )]
                    )
                )],
            rows: [Slot(name: "flag"), Slot(name: "hit")]
        );

        Assert.False(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: 0L,
            actual: host.Cell(row: "hit")
        );
        Assert.Equal(
            expected: 0,
            actual: host.Installs
        );
        Assert.Empty(collection: evaluator.Diagnostics());
    }
    [Fact]
    public void ATrueConditionFiresTheThenBranchAlone() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "cond",
                    gate: null,
                    effects: new ActionEffect.If(
                        Condition: new ActionPredicate.CompareState(
                            State: "flag",
                            Comparison: ActionStateComparison.Equal,
                            Value: 1m
                        ),
                        Then: [new ActionEffect.SetState(
                                State: "hit",
                                Value: 1m
                            )],
                        Else: [new ActionEffect.SetState(
                                State: "miss",
                                Value: 1m
                            )]
                    )
                )],
            rows: [Slot(
                    name: "flag",
                    value: 1L
                ), Slot(name: "hit"), Slot(name: "miss")]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: 1L,
            actual: host.Cell(row: "hit")
        );
        Assert.Equal(
            expected: 0L,
            actual: host.Cell(row: "miss")
        );
    }
    // The condition reads the same view a later effect's own operand reads: an earlier top-level effect's write
    // (in this same firing, on a different row) is visible to the condition that follows it.
    [Fact]
    public void AnEarlierEffectsWriteOnAnotherRowIsVisibleToALaterIfsCondition() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "cascade",
                    gate: null,
                    effects: [
                    new ActionEffect.SetState(
                        State: "gold",
                        Value: 5m
                    ),
                    new ActionEffect.If(
                        Condition: new ActionPredicate.CompareState(
                            State: "gold",
                            Comparison: ActionStateComparison.Equal,
                            Value: 5m
                        ),
                        Then: [new ActionEffect.SetState(
                                State: "unlocked",
                                Value: 1m
                            )]
                    ),
                ]
                )],
            rows: [Slot(name: "gold"), Slot(name: "unlocked")]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: 5L,
            actual: host.Cell(row: "gold")
        );
        Assert.Equal(
            expected: 1L,
            actual: host.Cell(row: "unlocked")
        );
    }
    // Cross-row visibility through a text write and a removal, both ahead of the branch that reads them.
    [Fact]
    public void AnEarlierTextWriteAndAnEarlierRemovalAreBothVisibleInsideTheChosenBranch() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "cascade",
                    gate: null,
                    effects: [
                    new ActionEffect.SetState(
                        State: "label",
                        Text: "hello"
                    ),
                    new ActionEffect.RemoveStateCell(
                        State: "marker",
                        Key: "x"
                    ),
                    new ActionEffect.If(
                        Condition: new ActionPredicate.CompareState(
                            State: "gate",
                            Comparison: ActionStateComparison.Equal,
                            Value: 1m
                        ),
                        Then: [
                            new ActionEffect.SetState(
                                State: "mirror",
                                FromState: "label"
                            ),
                            new ActionEffect.SetState(
                                State: "stillHasMarker",
                                FromState: "marker",
                                FromKey: "x"
                            ),
                        ]
                    ),
                ]
                )],
            rows: [
                Slot(
                    name: "gate",
                    value: 1L
                ),
                TextSlot(name: "label"),
                TextSlot(name: "mirror"),
                new StateRow(
                    Name: CellName.Parse(candidate: "marker"),
                    Kind: CellKind.Int,
                    Capacity: 4,
                    Cells: [new StateCell(
                            Key: CellName.Parse(candidate: "x"),
                            Value: 7L
                        )]
                ),
                Slot(
                    name: "stillHasMarker",
                    value: -1L,
                    min: -1L,
                    max: 100L
                ),
            ]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: "hello",
            actual: host.Text(row: "mirror")
        );
        // The marker cell was already removed before the branch ran — a literal-key read of a missing cell reads
        // as zero (the same convention an addState mint reads a not-yet-created key against), not the cell's old
        // value of 7 from before the removal.
        Assert.Equal(
            expected: 0L,
            actual: host.Cell(row: "stillHasMarker")
        );
    }
    // An if is legal inside a transaction; its condition reads the transaction's own in-flight preflighted writes.
    [Fact]
    public void AnIfInsideATransactionReadsTheTransactionsOwnInFlightWrite() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "deal",
                    gate: null,
                    effects: new ActionEffect.Transaction(Effects: [
                        new ActionEffect.AddState(
                            State: "gold",
                            Value: 5m
                        ),
                        new ActionEffect.If(
                            Condition: new ActionPredicate.CompareState(
                                State: "gold",
                                Comparison: ActionStateComparison.GreaterOrEqual,
                                Value: 5m
                            ),
                            Then: [new ActionEffect.SetState(
                                    State: "unlocked",
                                    Value: 1m
                                )]
                        ),
                    ])
                )],
            rows: [Slot(name: "gold"), Slot(name: "unlocked")]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: 5L,
            actual: host.Cell(row: "gold")
        );
        Assert.Equal(
            expected: 1L,
            actual: host.Cell(row: "unlocked")
        );
        Assert.Equal(
            expected: 1,
            actual: host.Installs
        );
    }
    // A transaction is legal inside an if that is not itself inside one, and commits its whole branch atomically.
    [Fact]
    public void ATransactionInsideAnIfCommitsAtomically() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "grant",
                    gate: null,
                    effects: new ActionEffect.If(
                        Condition: new ActionPredicate.CompareState(
                            State: "eligible",
                            Comparison: ActionStateComparison.Equal,
                            Value: 1m
                        ),
                        Then: [new ActionEffect.Transaction(Effects: [
                                new ActionEffect.SetState(
                                    State: "a",
                                    Value: 1m
                                ),
                                new ActionEffect.SetState(
                                    State: "b",
                                    Value: 1m
                                ),
                            ])]
                    )
                )],
            rows: [Slot(
                    name: "eligible",
                    value: 1L
                ), Slot(name: "a"), Slot(name: "b")]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: 1L,
            actual: host.Cell(row: "a")
        );
        Assert.Equal(
            expected: 1L,
            actual: host.Cell(row: "b")
        );
    }
    // A refused transaction inside an if's branch rolls back its own steps and fires onFailure, exactly as it
    // would at top level.
    [Fact]
    public void ARefusedTransactionInsideAnIfsBranchRollsBackAndFiresOnFailure() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "grant",
                    gate: null,
                    effects: new ActionEffect.If(
                        Condition: new ActionPredicate.CompareState(
                            State: "eligible",
                            Comparison: ActionStateComparison.Equal,
                            Value: 1m
                        ),
                        Then: [new ActionEffect.Transaction(
                                Effects: [
                                    new ActionEffect.SetState(
                                        State: "a",
                                        Value: 1m
                                    ),
                                    new ActionEffect.SetState(
                                        State: "locked",
                                        Value: 1m
                                    ),
                                ],
                                OnFailure: [new ActionEffect.SetState(
                                        State: "refunds",
                                        Value: 1m
                                    )]
                            )]
                    )
                )],
            rows: [Slot(
                    name: "eligible",
                    value: 1L
                ), Slot(name: "a"), Slot(name: "locked"), Slot(name: "refunds")]
        );

        host.RefusedRows.Add(item: "locked");

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: 0L,
            actual: host.Cell(row: "a")
        );
        Assert.Equal(
            expected: 0L,
            actual: host.Cell(row: "locked")
        );
        Assert.Equal(
            expected: 1L,
            actual: host.Cell(row: "refunds")
        );
    }
    // A branch is not a transaction: an earlier effect's write in the same branch survives a later sibling's
    // refusal, and the refusal is reported by name exactly as a top-level refusal would be.
    [Fact]
    public void AnEarlierEffectInTheSameBranchKeepsItsWriteWhenALaterSiblingRefuses() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "grant",
                    gate: null,
                    effects: new ActionEffect.If(
                        Condition: new ActionPredicate.CompareState(
                            State: "eligible",
                            Comparison: ActionStateComparison.Equal,
                            Value: 1m
                        ),
                        Then: [
                            new ActionEffect.SetState(
                                State: "a",
                                Value: 1m
                            ),
                            new ActionEffect.SetState(
                                State: "locked",
                                Value: 1m
                            ),
                        ]
                    )
                )],
            rows: [Slot(
                    name: "eligible",
                    value: 1L
                ), Slot(name: "a"), Slot(name: "locked")]
        );

        host.RefusedRows.Add(item: "locked");

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: 1L,
            actual: host.Cell(row: "a")
        );
        Assert.Equal(
            expected: 0L,
            actual: host.Cell(row: "locked")
        );

        var diagnostic = Assert.Single(collection: evaluator.Diagnostics());

        Assert.Equal<Enum>(
            RuleEffectRefusal.MutationRejected,
            diagnostic.Refusal
        );
    }
    // A rule with no if effect at all fires exactly as before — the if effect changes nothing about a rule that
    // never authors one.
    [Fact]
    public void ARuleWithNoIfEffectFiresUnchanged() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "plain",
                    gate: null,
                    effects: new ActionEffect.AddState(
                        State: "counter",
                        Value: 1m
                    )
                )],
            rows: [Slot(name: "counter")]
        );

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: 1L,
            actual: host.Cell(row: "counter")
        );
        Assert.Equal(
            expected: 1,
            actual: host.Installs
        );
        Assert.Empty(collection: evaluator.Diagnostics());
    }
    // The centerpiece: a transaction whose LATER step writes the very row an EARLIER if's condition read. Replaying
    // the branch if chose during preflight — never re-reading the condition — is the only way the non-submitting
    // effect of the CHOSEN branch fires exactly once, since the transaction's own committed write flips what the
    // condition would read if it were naively re-evaluated after commit.
    [Fact]
    public void ATransactionWhoseLaterStepFlipsTheIfsConditionStillFiresTheChosenBranchsNonSubmittingEffectExactlyOnce() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [R(
                    name: "flip",
                    gate: null,
                    effects: new ActionEffect.Transaction(Effects: [
                        new ActionEffect.If(
                            Condition: new ActionPredicate.CompareState(
                                State: "flag",
                                Comparison: ActionStateComparison.Equal,
                                Value: 0m
                            ),
                            Then: [new ActionEffect.SetState(
                                    State: "hit",
                                    Value: 1m
                                )],
                            Else: [new ActionEffect.SetState(
                                    State: "miss",
                                    Value: 1m
                                )]
                        ),
                        new ActionEffect.SetState(
                            State: "flag",
                            Value: 1m
                        ),
                    ])
                )],
            rows: [Slot(name: "flag"), Slot(name: "hit"), Slot(name: "miss")]
        );
        // Splice a non-submitting probe effect into the Then branch by compiling once, then wrapping the compiled
        // program with a NarrateEffect the RuleEvaluator dispatches to the host — the compiler owns no vocabulary
        // for an emitted narration, so this is added at the compiled-effect layer rather than through ActionEffect.
        var ifEffect = ((TransactionEffect)rules[0].Effects[0]).Effects[0];
        var wrappedThen = new IfEffect(
            condition: ((IfEffect)ifEffect).Condition,
            describe: ifEffect.Describe,
            elseEffects: ((IfEffect)ifEffect).Else,
            then: [.. ((IfEffect)ifEffect).Then, new NarrateEffect(describe: "chosen-then")]
        );

        ((TransactionEffect)rules[0].Effects[0]).Effects[0] = wrappedThen;

        Assert.True(condition: evaluator.Evaluate(
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL,
            engineTick: 1UL
        ));
        Assert.Equal(
            expected: 1L,
            actual: host.Cell(row: "flag")
        );
        Assert.Equal(
            expected: 1L,
            actual: host.Cell(row: "hit")
        );
        Assert.Equal(
            expected: 0L,
            actual: host.Cell(row: "miss")
        );
        // Exactly one REAL (non-preflight) fire of the chosen branch's non-submitting effect — the replayed
        // decision, not a naive re-check that would read flag=1 (post-commit) and choose neither branch.
        Assert.Single(collection: host.NonSubmittingCalls, predicate: call => (!call.Preflight && (call.Describe == "chosen-then")));
    }
}
