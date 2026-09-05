namespace Puck.State;

/// <summary>A rule host over a <see cref="StateFrame"/>: the document's own rules evaluate against a hypothetical
/// board, every state effect writes the frame, and nothing reaches an installed section. A preflight scope is a
/// frame copy restored on <see cref="IRuleHost.EndPreflight"/>; an effect arm the library does not own is skipped,
/// never fired; a rule kind only a document host understands is refused back to the library's own evaluation. The
/// host owns its evaluator and latch, so a judge run starts every edge closed.</summary>
public sealed class FrameHost : IRuleHost {
    private readonly IReadOnlyList<CompiledTable> m_tables;
    private readonly long[] m_patternWord = new long[PatternCapacity.MaxWord];
    private readonly List<StateFrame> m_scopes = [];
    private readonly List<int> m_scopeWrites = [];
    private long[] m_boardScratch = new long[BoardMask.MaxCells];
    private int m_depth;
    private int m_writes;

    /// <summary>Initializes a host over a fresh frame.</summary>
    /// <param name="layout">The frame layout.</param>
    /// <param name="rows">The rows the layout was computed from.</param>
    /// <param name="catalog">The catalog the rules were compiled against.</param>
    /// <param name="patterns">The compiled patterns the rules name.</param>
    /// <param name="tables">The compiled tables, by ordinal.</param>
    public FrameHost(FrameLayout layout, IReadOnlyList<StateRow> rows, StateCatalog catalog, CompiledPatterns patterns, IReadOnlyList<CompiledTable> tables) {
        ArgumentNullException.ThrowIfNull(argument: catalog);
        ArgumentNullException.ThrowIfNull(argument: patterns);
        ArgumentNullException.ThrowIfNull(argument: tables);
        Frame = new StateFrame(layout: layout, rows: rows);
        Catalog = catalog;
        Patterns = patterns;
        m_tables = tables;
        Evaluator = new RuleEvaluator(host: this);
    }

    /// <summary>Gets the frame every read and write goes to.</summary>
    public StateFrame Frame { get; }
    /// <summary>Gets the evaluator over this host.</summary>
    public RuleEvaluator Evaluator { get; }
    /// <summary>Gets the latch <see cref="Judge"/> clears before each run.</summary>
    public RuleLatch Latch { get; } = new();
    /// <summary>Gets how many refusal categories the evaluator has reported since construction.</summary>
    public int Refusals { get; private set; }
    /// <summary>Gets how many writes the frame accepted since construction.</summary>
    public int Writes => m_writes;

    /// <inheritdoc/>
    public ulong Tick => Evaluator.Tick;
    /// <inheritdoc/>
    public StateStore Store => Frame;
    /// <inheritdoc/>
    public StateCatalog Catalog { get; }
    /// <inheritdoc/>
    public CompiledPatterns Patterns { get; }
    /// <inheritdoc/>
    public string? BoundEachKey => Evaluator.BoundEachKey;
    /// <inheritdoc/>
    public string? BoundTokenKey { get; set; }
    /// <inheritdoc/>
    public string? BoundPreviousKey { get; set; }
    /// <inheritdoc/>
    public bool TableKeyMissing { get; set; }
    /// <inheritdoc/>
    public Span<long> PatternWord => m_patternWord;

    /// <inheritdoc/>
    public int BoundIndex(BoundKey key) => Evaluator.BoundIndex(key: key);
    /// <inheritdoc/>
    public long BindingValue(int ordinal) => Evaluator.BindingValue(ordinal: ordinal);
    /// <inheritdoc/>
    public CompiledTable Table(int ordinal) => m_tables[ordinal];
    /// <inheritdoc/>
    public void ReportTableKeyMissing(string table, long key) => Evaluator.ReportTableKeyMissing(table: table, key: key);
    /// <inheritdoc/>
    public Span<long> BoardScratch(int cells) {
        if (m_boardScratch.Length < cells) {
            m_boardScratch = new long[cells];
        }

        return m_boardScratch.AsSpan(start: 0, length: cells);
    }

    /// <summary>Evaluates rules over the frame in array order with every edge closed, as one tick.</summary>
    /// <param name="rules">The rules, already restricted to what a frame can evaluate.</param>
    /// <param name="tick">The tick the reads answer as of.</param>
    /// <returns><see langword="true"/> when any effect wrote the frame.</returns>
    public bool Judge(CompiledRule[] rules, ulong tick) {
        Latch.Clear();

        return Evaluator.Evaluate(rules: rules, latch: Latch, tick: tick, stepTicks: 1UL);
    }

    /// <inheritdoc/>
    public bool TryApply(StateMutation mutation, ulong tick, bool preflight, out string reason) {
        var applied = mutation switch {
            StateMutation.UpsertCell cell => TryUpsert(cell: cell, reason: out reason),
            StateMutation.Apply { Transform: StateTransform.BoardCombine combine } => Frame.TryBoardCombine(combine: combine, reason: out reason),
            StateMutation.Apply { Transform: StateTransform.Push push } => ((Frame.Find(name: push.Row) is { } ring) ? Frame.TryPush(row: ring, value: push.Value, reason: out reason) : Refuse(message: $"row '{push.Row}' is not in the frame", reason: out reason)),
            StateMutation.Apply { Transform: StateTransform.ClearEnclosed enclosed } => Frame.TryClearEnclosed(enclosed: enclosed, reason: out reason),
            StateMutation.Apply apply => Refuse(message: $"a frame does not apply a {apply.Transform.GetType().Name} transform", reason: out reason),
            StateMutation.RemoveCell => Refuse(message: "a frame never removes a cell", reason: out reason),
            StateMutation.Generate => Refuse(message: "a frame never draws", reason: out reason),
            _ => Refuse(message: $"state mutation '{mutation.GetType().Name}' has no frame mapping", reason: out reason),
        };

        if (applied) {
            m_writes++;
        }

        return applied;
    }
    /// <inheritdoc/>
    public void BeginPreflight() {
        if (m_scopes.Count == m_depth) {
            m_scopes.Add(item: new StateFrame(layout: Frame.Layout, rows: Frame.Rows));
            m_scopeWrites.Add(item: 0);
        }

        m_scopes[m_depth].CopyFrom(other: Frame);
        m_scopeWrites[m_depth] = m_writes;
        m_depth++;
    }
    /// <inheritdoc/>
    public void EndPreflight() {
        m_depth--;
        Frame.CopyFrom(other: m_scopes[m_depth]);
        m_writes = m_scopeWrites[m_depth];
    }
    /// <inheritdoc/>
    public bool TryCommitPreflight(ulong tick, out string reason) {
        m_depth--;
        reason = string.Empty;

        return (m_writes != m_scopeWrites[m_depth]);
    }
    /// <inheritdoc/>
    public EffectOutcome FireEffect(EffectFact effect, string ruleName, ulong tick, ulong stepTicks, bool preflight) => EffectOutcome.Skipped;
    /// <inheritdoc/>
    public bool TryEvaluateOwn(CompiledRule rule, RuleLatch latch, ulong tick, ulong stepTicks, out bool applied) {
        applied = false;

        return false;
    }
    /// <inheritdoc/>
    public void RefusalRecorded(in RuleRuntimeDiagnostic diagnostic) => Refusals++;

    private bool TryUpsert(StateMutation.UpsertCell cell, out string reason) {
        if (Frame.Find(name: cell.Row) is not { } row) {
            return Refuse(message: $"row '{cell.Row}' is not in the frame", reason: out reason);
        }
        if (!CellName.TryParse(candidate: cell.Key, name: out var key, reason: out reason)) {
            return false;
        }

        return Frame.TryWrite(row: row, key: key, value: cell.Value, write: cell.Write, reason: out reason);
    }

    private static bool Refuse(string message, out string reason) {
        reason = message;

        return false;
    }
}
