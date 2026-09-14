namespace Puck.State;

/// <summary>A rule host over a <see cref="StateFrame"/>: the document's own rules evaluate against a hypothetical
/// board, every state effect writes the frame, and nothing reaches an installed section. A preflight scope is the
/// frame's own undo journal, restored on <see cref="IRuleHost.EndPreflight"/>; an effect arm the library does not
/// own is skipped, never fired; a rule kind only a document host understands is refused back to the library's own
/// evaluation. The host owns its evaluator and latch, so a judge run starts every edge closed.</summary>
public sealed class FrameHost : IRuleHost {
    private readonly IReadOnlyList<CompiledTable> m_tables;

    private int m_depth;
    private int m_writes;

    private readonly long[] m_patternWord = new long[PatternCapacity.MaxWord];
    private readonly List<int> m_scopeMarks = [];
    private readonly List<int> m_scopeWrites = [];
    private long[] m_boardScratch = new long[BoardMask.MaxCells];

    /// <summary>Gets the latch <see cref="Judge"/> clears before each run.</summary>
    public RuleLatch Latch { get; } = new();

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
        Frame = new StateFrame(
            layout: layout,
            rows: rows
        );
        Catalog = catalog;
        Patterns = patterns;
        m_tables = tables;
        Evaluator = new RuleEvaluator(host: this);
    }

    /// <inheritdoc/>
    public string? BoundEachKey => Evaluator.BoundEachKey;
    /// <inheritdoc/>
    public int BoundEachPosition => Evaluator.BoundEachPosition;
    /// <inheritdoc/>
    public StateHandle BoundEachRowHandle => Evaluator.BoundEachRowHandle;
    /// <inheritdoc/>
    public string? BoundPreviousKey { get; set; }
    /// <inheritdoc/>
    public string? BoundTokenKey { get; set; }
    /// <inheritdoc/>
    public StateCatalog Catalog { get; }
    /// <summary>Gets the evaluator over this host.</summary>
    public RuleEvaluator Evaluator { get; }
    /// <summary>Gets the frame every read and write goes to.</summary>
    public StateFrame Frame { get; }
    /// <inheritdoc/>
    public Span<long> PatternWord => m_patternWord;
    /// <inheritdoc/>
    public CompiledPatterns Patterns { get; }
    /// <summary>Gets how many refusal categories the evaluator has reported since construction.</summary>
    public int Refusals { get; private set; }
    /// <inheritdoc/>
    public StateStore Store => Frame;
    /// <inheritdoc/>
    public bool TableKeyMissing { get; set; }
    /// <inheritdoc/>
    public ulong Tick => Evaluator.Tick;
    /// <inheritdoc/>
    public ulong EngineTick => Evaluator.EngineTick;
    /// <summary>Gets how many writes the frame accepted since construction.</summary>
    public int Writes => m_writes;

    private static bool Refuse(string message, out string reason) {
        reason = message;

        return false;
    }
    private bool TryUpsert(StateMutation.UpsertCell cell, out string reason) {
        var handle = cell.Handle;

        if (handle == default) {
            _ = Catalog.TryResolve(
                lane: StateLane.Document,
                name: cell.Row,
                handle: out handle
            );
        }

        if (
            !StateReader.TryResolveRowHandle(
            rows: Frame.Rows,
            catalog: Catalog,
            handle: handle,
            rowOrdinal: out var ordinal,
            row: out var resolved
        ) ||
            !string.Equals(
            a: cell.Row,
            b: resolved.Name,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return Refuse(
                message: $"row '{cell.Row}' is not in the frame",
                reason: out reason
            );
        }

        CellName key;

        if (cell.CellKey != default) {
            if (!string.Equals(
                a: cell.Key,
                b: cell.CellKey.Value,
                comparisonType: StringComparison.Ordinal
            )) {
                return Refuse(
                    message: "the parsed cell key does not match the mutation key",
                    reason: out reason
                );
            }
            key = cell.CellKey;
            reason = string.Empty;
        } else if (!CellName.TryParse(
            candidate: cell.Key,
            name: out key,
            reason: out reason
        )) {
            return false;
        }

        if (cell.Vector is { } vec) {
            return Frame.TryWriteVector(
                rowOrdinal: ordinal,
                key: key,
                components: vec.Components,
                reason: out reason
            );
        }

        return Frame.TryWrite(
            rowOrdinal: ordinal,
            key: key,
            value: cell.Value,
            write: cell.Write,
            reason: out reason
        );
    }

    /// <inheritdoc/>
    public void BeginPreflight() {
        if (m_scopeMarks.Count == m_depth) {
            m_scopeMarks.Add(item: 0);
            m_scopeWrites.Add(item: 0);
        }

        m_scopeMarks[m_depth] = Frame.BeginJournalScope();
        m_scopeWrites[m_depth] = m_writes;
        m_depth++;
    }
    /// <inheritdoc/>
    public long BindingValue(int ordinal) => Evaluator.BindingValue(ordinal: ordinal);
    /// <inheritdoc/>
    public Span<long> BoardScratch(int cells) {
        if (m_boardScratch.Length < cells) {
            m_boardScratch = new long[cells];
        }

        return m_boardScratch.AsSpan(
            length: cells,
            start: 0
        );
    }
    /// <inheritdoc/>
    public int BoundIndex(BoundKey key) => Evaluator.BoundIndex(key: key);
    /// <inheritdoc/>
    public void EndPreflight() {
        m_depth--;
        Frame.RewindJournalScope(mark: m_scopeMarks[m_depth]);
        m_writes = m_scopeWrites[m_depth];
    }
    /// <inheritdoc/>
    public EffectOutcome FireEffect(EffectFact effect, string ruleName, ulong tick, ulong stepTicks, bool preflight) => EffectOutcome.Skipped;
    /// <summary>Evaluates rules over the frame in array order with every edge closed, as one tick.</summary>
    /// <param name="rules">The rules, already restricted to what a frame can evaluate.</param>
    /// <param name="tick">The tick the reads answer as of.</param>
    /// <param name="engineTick">The engine tick a <see cref="StateAdvance"/> read answers as of — a frame judge has
    /// no live simulation rate of its own, so a caller with no independent engine clock passes <paramref name="tick"/>
    /// itself.</param>
    /// <returns><see langword="true"/> when any effect wrote the frame.</returns>
    public bool Judge(CompiledRule[] rules, ulong tick, ulong engineTick) {
        Latch.Clear();

        return Evaluator.Evaluate(
            rules: rules,
            latch: Latch,
            tick: tick,
            engineTick: engineTick,
            stepTicks: 1UL
        );
    }
    /// <summary>Rebinds the frame to rows the layout fits.</summary>
    /// <param name="rows">The rows.</param>
    public void Rebind(IReadOnlyList<StateRow> rows) => Frame.Rebind(rows: rows);
    /// <inheritdoc/>
    public void RefusalRecorded(in RuleRuntimeDiagnostic diagnostic) => Refusals++;
    /// <inheritdoc/>
    public void ReportTableKeyMissing(string table, long key) => Evaluator.ReportTableKeyMissing(
        key: key,
        table: table
    );
    /// <inheritdoc/>
    public CompiledTable Table(int ordinal) => m_tables[ordinal];
    /// <inheritdoc/>
    public bool TryApply(StateMutation mutation, ulong tick, bool preflight, out string reason) {
        var applied = mutation switch {
            StateMutation.UpsertCell cell => TryUpsert(
            cell: cell,
            reason: out reason
        ),
            StateMutation.Apply { Transform: StateTransform.BoardCombine combine } => Frame.TryBoardCombine(
            combine: combine,
            reason: out reason
        ),
            StateMutation.Apply { Transform: StateTransform.WriteSet writeSet } => Frame.TryWriteSet(
            reason: out reason,
            writeSet: writeSet
        ),
            StateMutation.Apply { Transform: StateTransform.Push push } apply => Frame.TryPush(
            push: push,
            catalog: Catalog,
            handle: apply.Handle,
            reason: out reason
        ),
            StateMutation.Apply { Transform: StateTransform.ClearEnclosed enclosed } => Frame.TryClearEnclosed(
            enclosed: enclosed,
            reason: out reason
        ),
            StateMutation.Apply { Transform: StateTransform.Transfer transfer } => Frame.TryTransfer(
            reason: out reason,
            transfer: transfer
        ),
            StateMutation.Apply apply => Refuse(
            message: $"a frame does not apply a {apply.Transform.GetType().Name} transform",
            reason: out reason
        ),
            StateMutation.ApplyVector applyVector => TryApplyVector(
            applyVector: applyVector,
            reason: out reason
        ),
            StateMutation.RemoveCell => Refuse(
            message: "a frame never removes a cell",
            reason: out reason
        ),
            StateMutation.Generate => Refuse(
            message: "a frame never draws",
            reason: out reason
        ),
            _ => Refuse(
            message: $"state mutation '{mutation.GetType().Name}' has no frame mapping",
            reason: out reason
        ),
        };

        if (applied) {
            m_writes++;
        }

        return applied;
    }
    private bool TryApplyVector(StateMutation.ApplyVector applyVector, out string reason) {
        var transform = applyVector.Transform;
        return transform switch {
            ResolvedVectorTransform.Copy or ResolvedVectorTransform.Mix or ResolvedVectorTransform.Mean =>
                Frame.TryApplyVector(transform: transform, reason: out reason),
            _ => Refuse(
                message: $"a frame does not apply a {transform.GetType().Name} vector transform",
                reason: out reason
            ),
        };
    }
    /// <inheritdoc/>
    public bool TryCommitPreflight(ulong tick, out string reason) {
        m_depth--;
        Frame.CommitJournalScope();
        reason = string.Empty;

        return (m_writes != m_scopeWrites[m_depth]);
    }
    /// <inheritdoc/>
    public bool TryEvaluateOwn(CompiledRule rule, RuleLatch latch, ulong tick, ulong stepTicks, out bool applied) {
        applied = false;

        return false;
    }
    /// <inheritdoc/>
    public bool TryRowVersion(StateHandle row, out ulong version) {
        version = 0UL;

        if (
            !StateReader.TryResolveRowHandle(
            rows: Frame.Rows,
            catalog: Catalog,
            handle: row,
            rowOrdinal: out var ordinal,
            row: out _
        ) ||
            (Frame.Layout[ordinal].Kind == FrameRowKind.Unframed)
        ) {
            return false;
        }

        version = Frame.RowVersion(rowOrdinal: ordinal);

        return true;
    }
}
