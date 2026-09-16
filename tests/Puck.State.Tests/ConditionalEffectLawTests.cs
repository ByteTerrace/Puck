using Xunit;

namespace Puck.State.Tests;

/// <summary>Guards transaction rollback, branch isolation, and trace/diagnostic reporting for
/// <see cref="ActionEffect.If"/>.</summary>
public sealed class ConditionalEffectLawTests {
    private sealed class DocumentHost : IRuleHost, IStateSection {
        private readonly Stack<(IReadOnlyList<StateRow> Rows, int Composed)> m_scopes = new();
        private readonly long[] m_patternWord = new long[PatternCapacity.MaxWord];

        private int m_composed;
        private StateStore? m_store;

        public List<RuleRuntimeDiagnostic> Narrated { get; } = [];
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

        public void EndPreflight() => (Rows, m_composed) = m_scopes.Pop();
        public EffectOutcome FireEffect(EffectFact effect, string ruleName, ulong tick, ulong stepTicks, bool preflight) => EffectOutcome.Applied;
        public void RefusalRecorded(in RuleRuntimeDiagnostic diagnostic) => Narrated.Add(item: diagnostic);
        public void ReportTableKeyMissing(string table, long key) => Evaluator.ReportTableKeyMissing(key: key, table: table);
        public CompiledTable Table(int ordinal) => throw new InvalidOperationException();

        public bool TryApply(StateMutation mutation, ulong tick, bool preflight, out string reason) {
            reason = string.Empty;
            switch (mutation) {
                case StateMutation.UpsertCell cell when RefusedRows.Contains(item: cell.Row):
                    reason = $"row '{cell.Row}' refuses every write";
                    return false;
                case StateMutation.UpsertCell cell: {
                    var row = StateRows.FindStateRow(rows: Rows, name: cell.Row)!;
                    var key = CellName.Parse(candidate: cell.Key);
                    var cells = new List<StateCell>(collection: (row.Cells ?? []));
                    var at = cells.FindIndex(match: c => (c.Key == key));
                    var current = ((at >= 0) ? cells[at].Value : 0L);

                    if (!row.TryAdmitWrite(
                        current: current,
                        operand: cell.Value,
                        write: cell.Write,
                        stored: out var admitted,
                        reason: out reason
                    )) {
                        return false;
                    }

                    var next = new StateCell(Key: key, Value: admitted);
                    if (at >= 0) { cells[at] = next; } else { cells.Add(item: next); }
                    Install(cells: cells, preflight: preflight, row: row);
                    return true;
                }
                default:
                    reason = $"{mutation.GetType().Name} is not supported";
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

    private static StateRow Slot(string name, long value = 0L) =>
        new(
            Name: CellName.Parse(candidate: name),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: value)]
        );

    private static (DocumentHost Host, RuleEvaluator Evaluator, CompiledRule[] Rules, RuleLatch Latch) Arrange(
        IReadOnlyList<Rule> rules,
        params StateRow[] rows
    ) {
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

        return (host, evaluator, RuleCompiler.CompileAll(context: context, rules: rules), new RuleLatch());
    }

    [Fact]
    public void IfEffect_WhenConditionTrue_ExecutesThenBranchOnly() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [
                new Rule(
                    Name: CellName.Parse(candidate: "branchRule"),
                    Gate: null,
                    Effects: [
                        new ActionEffect.If(
                            Condition: new ActionPredicate.CompareState(
                                State: "flag",
                                Comparison: ActionStateComparison.Equal,
                                Value: 1m
                            ),
                            Then: [new ActionEffect.SetState(State: "target", Value: 10m)],
                            Else: [new ActionEffect.SetState(State: "target", Value: 20m)]
                        ),
                    ]
                ),
            ],
            rows: [Slot(name: "flag", value: 1L), Slot(name: "target", value: 0L)]
        );

        Assert.True(evaluator.Evaluate(
            engineTick: 1UL,
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL
        ));

        Assert.Equal(10L, host.Cell("target"));
        Assert.Empty(host.Narrated);
    }

    [Fact]
    public void IfEffect_WhenConditionFalse_ExecutesElseBranchOnly() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [
                new Rule(
                    Name: CellName.Parse(candidate: "branchRule"),
                    Gate: null,
                    Effects: [
                        new ActionEffect.If(
                            Condition: new ActionPredicate.CompareState(
                                State: "flag",
                                Comparison: ActionStateComparison.Equal,
                                Value: 1m
                            ),
                            Then: [new ActionEffect.SetState(State: "target", Value: 10m)],
                            Else: [new ActionEffect.SetState(State: "target", Value: 20m)]
                        ),
                    ]
                ),
            ],
            rows: [Slot(name: "flag", value: 0L), Slot(name: "target", value: 0L)]
        );

        Assert.True(evaluator.Evaluate(
            engineTick: 1UL,
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL
        ));

        Assert.Equal(20L, host.Cell("target"));
        Assert.Empty(host.Narrated);
    }

    [Fact]
    public void IfEffect_InsideTransaction_RefusalRollsBackAllWritesInTransaction() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [
                new Rule(
                    Name: CellName.Parse(candidate: "txRule"),
                    Gate: null,
                    Effects: [
                        new ActionEffect.Transaction(
                            Effects: [
                                new ActionEffect.SetState(State: "beforeIf", Value: 1m),
                                new ActionEffect.If(
                                    Condition: new ActionPredicate.CompareState(
                                        State: "flag",
                                        Comparison: ActionStateComparison.Equal,
                                        Value: 1m
                                    ),
                                    Then: [
                                        new ActionEffect.SetState(State: "insideIf", Value: 2m),
                                        new ActionEffect.SetState(State: "lockedRow", Value: 3m),
                                    ]
                                ),
                            ],
                            OnFailure: [
                                new ActionEffect.SetState(State: "failedRollback", Value: 99m),
                            ]
                        ),
                    ]
                ),
            ],
            rows: [
                Slot(name: "flag", value: 1L),
                Slot(name: "beforeIf", value: 0L),
                Slot(name: "insideIf", value: 0L),
                Slot(name: "lockedRow", value: 0L),
                Slot(name: "failedRollback", value: 0L),
            ]
        );

        host.RefusedRows.Add("lockedRow");

        Assert.True(evaluator.Evaluate(
            engineTick: 1UL,
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL
        ));

        // Transaction failed at lockedRow -> beforeIf and insideIf must roll back completely
        Assert.Equal(0L, host.Cell("beforeIf"));
        Assert.Equal(0L, host.Cell("insideIf"));
        Assert.Equal(0L, host.Cell("lockedRow"));
        // onFailure ran
        Assert.Equal(99L, host.Cell("failedRollback"));
    }

    [Fact]
    public void IfEffect_ConditionSeesPrecedingWriteInSameFiring() {
        var (host, evaluator, rules, latch) = Arrange(
            rules: [
                new Rule(
                    Name: CellName.Parse(candidate: "sequential"),
                    Gate: null,
                    Effects: [
                        new ActionEffect.SetState(State: "flag", Value: 1m),
                        new ActionEffect.If(
                            Condition: new ActionPredicate.CompareState(
                                State: "flag",
                                Comparison: ActionStateComparison.Equal,
                                Value: 1m
                            ),
                            Then: [new ActionEffect.SetState(State: "result", Value: 42m)]
                        ),
                    ]
                ),
            ],
            rows: [Slot(name: "flag", value: 0L), Slot(name: "result", value: 0L)]
        );

        Assert.True(evaluator.Evaluate(
            engineTick: 1UL,
            latch: latch,
            rules: rules,
            stepTicks: 1UL,
            tick: 1UL
        ));

        Assert.Equal(1L, host.Cell("flag"));
        Assert.Equal(42L, host.Cell("result"));
    }
}
