using System.Globalization;
using System.Text.Json.Serialization;

namespace Puck.World.Browser.Engine;

/// <summary>One captured rule's evaluations, grouped by name — the shape <c>Judge</c> exports as <c>rules[]</c>.</summary>
/// <param name="Name">The rule's name.</param>
/// <param name="Mode">The rule's trigger mode (<c>Continuous</c>/<c>Edge</c>), by name.</param>
/// <param name="Evaluations">Each captured evaluation, in evaluation order.</param>
public readonly record struct BrowserRuleTrace(string Name, string Mode, IReadOnlyList<BrowserEvaluation> Evaluations);
/// <summary>One captured rule evaluation — a flattened read of <see cref="RuleTraceEvaluation"/>.</summary>
public readonly record struct BrowserEvaluation(
    [property: JsonConverter(typeof(UInt64AsStringJsonConverter))] ulong Tick,
    string? EachKey,
    IReadOnlyList<string> Bindings,
    IReadOnlyList<string> Zones,
    IReadOnlyList<string> Conjuncts,
    bool GateOpen,
    bool EdgeHeld,
    IReadOnlyList<string> Effects
);
/// <summary>One cell the frame changed between the before- and after-image of a <c>Judge</c> call.</summary>
/// <param name="Row">The row name.</param>
/// <param name="Key">The cell key (see <see cref="BrowserSession"/>'s remarks on <c>Ring</c>/<c>Zone</c> keys).</param>
/// <param name="Old">The value before the tick.</param>
/// <param name="New">The value after the tick.</param>
public readonly record struct BrowserWrite(
    string Row,
    string Key,
    [property: JsonConverter(typeof(LongAsStringJsonConverter))] long Old,
    [property: JsonConverter(typeof(LongAsStringJsonConverter))] long New
);
/// <summary>One runtime refusal category the evaluator has observed since the session's own construction.</summary>
public readonly record struct BrowserRefusal(
    string Category,
    [property: JsonConverter(typeof(UInt64AsStringJsonConverter))] ulong Count,
    [property: JsonConverter(typeof(UInt64AsStringJsonConverter))] ulong LastTick,
    string Rule,
    string Effect,
    string Detail
);
/// <summary>One <c>Judge</c> call's whole trace: every armed rule's captured evaluations, the frame's own before/after
/// diff, and the evaluator's cumulative refusal ledger.</summary>
public readonly record struct BrowserJudgeResult(IReadOnlyList<BrowserRuleTrace> Rules, IReadOnlyList<BrowserWrite> Writes, IReadOnlyList<BrowserRefusal> Refusals);
/// <summary>One <c>ReadRow</c> result: whether the cell exists in the frame, and its value (numeric rows) or text
/// (a text row's own cell, read straight through the row rather than the frame — see <see cref="StateFrame"/>'s
/// own remarks on an unframed row).</summary>
public readonly record struct BrowserCellValue(bool Found, [property: JsonConverter(typeof(LongAsStringJsonConverter))] long Value, string? Text);
/// <summary>One row's whole current content — the shape <c>Rows</c> exports. Only the row's AUTHORED cells are
/// listed (its <see cref="StateRow.Cells"/>); a dense board's un-authored cells (still readable individually
/// through <c>ReadRow</c> or in bulk through <c>Cells</c>/<c>BoardMask</c>) are not enumerated here, since a large
/// lattice's full cell list would dwarf the rest of the read-back for no board a studio actually authors sparsely.</summary>
public readonly record struct BrowserRowSnapshot(string Name, string Kind, bool Keyed, IReadOnlyList<BrowserRowCell> Cells);
/// <summary>One authored cell of a <see cref="BrowserRowSnapshot"/>.</summary>
public readonly record struct BrowserRowCell(string Key, [property: JsonConverter(typeof(LongAsStringJsonConverter))] long Value, string? Text);

/// <summary>The most evaluations one <c>Judge</c> call captures across every rule combined — a forEach-heavy document
/// could otherwise produce an unbounded trace in one tick.</summary>
public static class BrowserJudgeLimits {
    /// <summary>Gets the evaluation capture ceiling.</summary>
    public const int MaxTraceEvaluations = 4096;
}

/// <summary>One compiled-and-installed world document: a <see cref="FrameHost"/> over its state section plus the
/// compiled rules <see cref="WorldRuleCompilation"/> produced at validation, held live behind an opaque handle in
/// <see cref="BrowserSessionRegistry"/> across the JS boundary's otherwise-stateless calls. Pure C# — no
/// <c>[JSExport]</c>/marshalling concern lives here; <c>Puck.World.Browser.Exports.BrowserExports</c> is the
/// only caller.</summary>
public sealed class BrowserSession {
    private readonly FrameHost m_host;
    private CompiledWorldRule[] m_rules;
    private WorldRuleCompileContext m_expressionContext;

    internal BrowserSession(WorldDefinition definition, WorldRuleCompilation compilation) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: compilation);

        Definition = definition;
        m_rules = compilation.Rules;
        m_expressionContext = WorldRuleCompiler.Context(definition: definition);

        var patternErrors = new List<string>();

        if (!CompiledPatterns.TryCompileAll(rows: definition.Patterns, patterns: out var patterns, errors: patternErrors)) {
            // Unreachable for a definition that already passed WorldDefinitionValidator.TryValidateLocally —
            // ValidatePatterns runs the same compiler over the same rows before validation admits the document.
            throw new InvalidOperationException(message: $"pattern compilation failed for an already-validated document: {string.Join(separator: "; ", values: patternErrors)}");
        }

        var layout = new FrameLayout(rows: definition.State, topology: name => WorldTopologyCompilation.Find(definition: definition, name: name));

        m_host = new FrameHost(layout: layout, rows: definition.State, catalog: definition.StateCatalog, patterns: patterns!, tables: compilation.Tables);

        LoadRows(rows: definition.State);
    }

    /// <summary>Gets the currently installed document.</summary>
    public WorldDefinition Definition { get; private set; }

    // A raw row list carries only authored cells; a derived board's own row materializes here on every
    // load/rebind, exactly as RuleFrameFixture.Evaluate does for a hypothetical position.
    private void LoadRows(IReadOnlyList<StateRow> rows) {
        var materialized = rows.Select(selector: row => ((row.Inverse is { } inverse) && (row.EffectiveDomain is StateDomain.CellsOf board))
            ? (row with { Cells = DerivedBoards.Compose(rows, inverse, WorldTopologyCompilation.Find(definition: Definition, name: board.Topology)!) })
            : row).ToArray();

        m_host.Rebind(rows: materialized);
        m_host.Frame.Load(source: new RowStore(rows: materialized));
    }

    /// <summary>Rebinds the session to an edited document whose state rows lay out identically to the installed
    /// one (see <see cref="FrameLayout.Fits"/>) — a structural edit (a renamed or resized row, a new topology)
    /// refuses rather than silently discarding the frame's current values; the caller recompiles a fresh session
    /// instead.</summary>
    /// <param name="definition">The candidate replacement, already parsed and validated by the caller.</param>
    /// <param name="reason">Why the rebind refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the rows fit the installed layout.</returns>
    public bool TryRebind(WorldDefinition definition, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!m_host.Frame.Layout.Fits(rows: definition.State)) {
            reason = "the candidate's state rows do not lay out identically to the installed document (a row was added, removed, renamed, or changed kind/domain) — recompile a fresh session instead.";

            return false;
        }

        Definition = definition;
        m_rules = WorldRuleCompiler.CompileAll(definition: definition);
        m_expressionContext = WorldRuleCompiler.Context(definition: definition);
        LoadRows(rows: definition.State);
        reason = string.Empty;

        return true;
    }

    /// <summary>Judges one tick over every rule, capturing every rule's own evaluations and diffing the frame's
    /// values before and after.</summary>
    /// <param name="tick">The tick the reads answer as of.</param>
    /// <returns>The whole trace.</returns>
    public BrowserJudgeResult Judge(ulong tick) {
        var before = m_host.Frame.Values.ToArray();

        m_host.Evaluator.ArmTraceAll(maxEvaluations: BrowserJudgeLimits.MaxTraceEvaluations);
        m_host.Judge(rules: m_rules, tick: tick);

        var rules = GroupTrace(captured: m_host.Evaluator.TraceCaptured);
        var writes = DiffWrites(before: before, after: m_host.Frame.Values);
        var refusals = m_host.Evaluator.Diagnostics().Select(selector: static diagnostic => new BrowserRefusal(
            Category: diagnostic.Refusal.ToString(),
            Count: diagnostic.Count,
            LastTick: diagnostic.LastTick,
            Rule: diagnostic.Rule,
            Effect: diagnostic.Effect,
            Detail: diagnostic.Detail
        )).ToArray();

        m_host.Evaluator.DisarmTrace();

        return new BrowserJudgeResult(Rules: rules, Writes: writes, Refusals: refusals);
    }

    private BrowserRuleTrace[] GroupTrace(IReadOnlyList<RuleTraceEvaluation> captured) {
        var order = new List<string>();
        var byRule = new Dictionary<string, List<BrowserEvaluation>>(comparer: StringComparer.Ordinal);

        foreach (var entry in captured) {
            if (!byRule.TryGetValue(key: entry.Rule, value: out var list)) {
                list = [];
                byRule[entry.Rule] = list;
                order.Add(item: entry.Rule);
            }

            list.Add(item: new BrowserEvaluation(
                Bindings: entry.Bindings,
                Conjuncts: entry.Conjuncts,
                EachKey: entry.EachKey,
                EdgeHeld: entry.EdgeHeld,
                Effects: entry.Effects,
                GateOpen: entry.GateOpen,
                Tick: entry.Tick,
                Zones: entry.Zones
            ));
        }

        var result = new BrowserRuleTrace[order.Count];

        for (var index = 0; (index < order.Count); index++) {
            var name = order[index];
            var mode = m_rules.FirstOrDefault(predicate: rule => string.Equals(a: rule.Name, b: name, comparisonType: StringComparison.Ordinal))?.Mode.ToString() ?? "";

            result[index] = new BrowserRuleTrace(Name: name, Mode: mode, Evaluations: byRule[name]);
        }

        return result;
    }
    private BrowserWrite[] DiffWrites(long[] before, ReadOnlySpan<long> after) {
        var layout = m_host.Frame.Layout;
        var rows = Definition.State;
        var writes = new List<BrowserWrite>();

        for (var index = 0; (index < after.Length); index++) {
            if (before[index] == after[index]) {
                continue;
            }

            var rowOrdinal = layout.RowOfIndex(index: index);
            var rowLayout = layout[rowOrdinal];
            var row = rows[rowOrdinal];
            var key = ResolveKey(layout: rowLayout, row: row, within: (index - rowLayout.Offset));

            writes.Add(item: new BrowserWrite(Row: row.Name.Value, Key: key, Old: before[index], New: after[index]));
        }

        return [.. writes];
    }
    // Slot and Keyed/Board resolve to the row's own authored key spelling; Ring and Zone have no single authored
    // key per cell (a ring slot is a rotating position, a zone cell is a live pile position) — reported as their
    // raw within-row index instead, a documented simplification rather than a silent misresolution.
    private static string ResolveKey(FrameRowLayout layout, StateRow row, int within) => layout.Kind switch {
        FrameRowKind.Slot => StateRow.SlotKey.Value,
        FrameRowKind.Keyed => ((row.Cells is { } cells) && (within < cells.Count)) ? cells[within].Key.Value : within.ToString(provider: CultureInfo.InvariantCulture),
        FrameRowKind.Board => layout.Topology!.Key(cell: within),
        FrameRowKind.Ring => ((within == (layout.Length - 1)) ? "$cursor" : within.ToString(provider: CultureInfo.InvariantCulture)),
        _ => within.ToString(provider: CultureInfo.InvariantCulture),
    };

    /// <summary>Reads one cell through the frame — the same read a rule's own state token resolves.</summary>
    /// <param name="row">The row name.</param>
    /// <param name="key">The cell key.</param>
    public BrowserCellValue ReadRow(string row, string key) {
        if ((m_host.Frame.Find(name: row) is not { } source) || !CellName.TryParse(candidate: key, name: out var cellKey, reason: out _)) {
            return new BrowserCellValue(Found: false, Value: 0L, Text: null);
        }

        var found = m_host.Frame.TryStored(row: source, key: cellKey, value: out var value, text: out var text);

        return new BrowserCellValue(Found: found, Value: value, Text: text);
    }
    /// <summary>Writes one cell through the frame, on the same terms a rule's own effect would.</summary>
    /// <param name="row">The row name.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="value">The operand.</param>
    /// <param name="add">Whether to add to the stored value rather than replace it.</param>
    /// <param name="reason">Why the write refused, or empty.</param>
    /// <returns><see langword="true"/> when the write applied.</returns>
    public bool TryWriteRow(string row, string key, long value, bool add, out string reason) {
        if (m_host.Frame.Find(name: row) is not { } source) {
            reason = $"row '{row}' is not in the installed document.";

            return false;
        }
        if (!CellName.TryParse(candidate: key, name: out var cellKey, reason: out reason)) {
            return false;
        }

        return m_host.Frame.TryWrite(row: source, key: cellKey, value: value, write: (add ? StateWriteKind.Add : StateWriteKind.Set), reason: out reason);
    }
    /// <summary>Evaluates one postfix or infix expression against the current frame, on the same terms a rule's own
    /// expression token would.</summary>
    /// <param name="expression">The infix or <c>{"tokens":[...]}</c> postfix spelling.</param>
    /// <param name="kind">The kind the expression must produce.</param>
    /// <param name="tick">The tick a time-sensitive token (a timer read) answers as of.</param>
    /// <param name="value">The computed value on success.</param>
    /// <param name="error">Why evaluation refused, or empty.</param>
    /// <returns><see langword="true"/> when the expression parsed, compiled, and evaluated.</returns>
    public bool TryEvaluate(string expression, CellKind kind, ulong tick, out long value, out string error) {
        value = 0L;

        ValueExpression parsed;

        try {
            parsed = ValueExpression.Parse(text: expression);
        } catch (FormatException exception) {
            error = exception.Message;

            return false;
        }

        CompiledExpressionToken[] compiled;

        try {
            compiled = RuleCompiler.CompileExpression(expression: parsed, kind: kind, ruleName: "$evaluate", verb: "evaluate", context: m_expressionContext);
        } catch (RuleException exception) {
            error = exception.Message;

            return false;
        }

        if (!m_host.Evaluator.TryEvaluateExpression(program: compiled, kind: kind, tick: tick, value: out value)) {
            error = "the expression could not be evaluated against the current frame (a dynamic-table key miss, or a read the frame refuses).";

            return false;
        }

        error = string.Empty;

        return true;
    }
    /// <summary>Reads a board row's occupancy as one 64-bit mask — bit <c>i</c> set when cell <c>i</c> is not at the
    /// board's declared empty value. Only meaningful for a topology of at most
    /// <see cref="Puck.State.BoardMask.MaxCells"/> cells; a larger board reports <see langword="false"/>.</summary>
    /// <param name="row">The board row's name.</param>
    /// <param name="mask">The occupancy mask on success.</param>
    /// <returns><see langword="true"/> when <paramref name="row"/> is a board row within the mask ceiling.</returns>
    public bool TryBoardMask(string row, out ulong mask) {
        mask = 0UL;

        if (!m_host.Frame.Layout.TryOrdinal(name: row, ordinal: out var ordinal)) {
            return false;
        }

        var layout = m_host.Frame.Layout[ordinal];

        if ((layout.Kind != FrameRowKind.Board) || (layout.Length > Puck.State.BoardMask.MaxCells)) {
            return false;
        }

        var values = m_host.Frame.Values.Slice(start: layout.Offset, length: layout.Length);

        for (var cell = 0; (cell < values.Length); cell++) {
            if (values[cell] != layout.Empty) {
                mask |= (1UL << cell);
            }
        }

        return true;
    }
    /// <summary>Gets the frame's deterministic content hash (see <see cref="StateFrameHash"/>) — the determinism
    /// canary's shared comparator between a native and a wasm run.</summary>
    public ulong StateHash() => StateFrameHash.Compute(frame: m_host.Frame);
    /// <summary>Reads every row's authored cells through the frame — the whole-state read-back <c>Rows</c> exports.</summary>
    public IReadOnlyList<BrowserRowSnapshot> Rows() {
        var rows = Definition.State;
        var result = new BrowserRowSnapshot[rows.Count];

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var cells = new List<BrowserRowCell>(capacity: (row.Cells?.Count ?? 0));

            foreach (var cell in (row.Cells ?? [])) {
                var read = ReadRow(row: row.Name.Value, key: cell.Key.Value);

                cells.Add(item: new BrowserRowCell(Key: cell.Key.Value, Value: read.Value, Text: read.Text));
            }

            result[index] = new BrowserRowSnapshot(Name: row.Name.Value, Kind: row.Kind.ToString(), Keyed: row.IsKeyed, Cells: cells);
        }

        return result;
    }
}
