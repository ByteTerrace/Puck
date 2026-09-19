using System.Globalization;
using System.Text.Json.Serialization;
using CompiledExpressionToken = Puck.State.Rules.CompiledExpressionToken;
using CompiledRule = Puck.State.Rules.CompiledRule;
using CompiledRuleGroup = Puck.State.Rules.CompiledRuleGroup;
using RuleCompiler = Puck.State.Rules.RuleCompiler;
using RuleEvaluation = Puck.State.Rules.RuleEvaluation;
using RuleGroupDeclaration = Puck.State.Rules.RuleGroupDeclaration;
using RuleGroupState = Puck.State.Rules.RuleGroupState;
using RuleTraceEvaluation = Puck.State.Rules.RuleTraceEvaluation;

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
    IReadOnlyList<string> Locals,
    IReadOnlyList<string> Zones,
    IReadOnlyList<string> Conjuncts,
    bool GateOpen,
    bool EdgeHeld,
    IReadOnlyList<string> Effects
);
/// <summary>One cell the arena changed between the before- and after-image of a <c>Judge</c> call.</summary>
/// <param name="Row">The row name.</param>
/// <param name="Key">The cell key.</param>
/// <param name="Old">The value before the tick, in the cell's own raw encoding.</param>
/// <param name="New">The value after the tick, in the cell's own raw encoding.</param>
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
/// <summary>One <c>Judge</c> call's whole trace: every armed rule's captured evaluations, the arena's own
/// before/after diff, the evaluator's cumulative refusal ledger, and every hostless world-operand read this tick's
/// own rules made (see <see cref="BrowserRuleReader"/>).</summary>
public readonly record struct BrowserJudgeResult(IReadOnlyList<BrowserRuleTrace> Rules, IReadOnlyList<BrowserWrite> Writes, IReadOnlyList<BrowserRefusal> Refusals, IReadOnlyList<BrowserHostFact> HostFacts);
/// <summary>One <c>ReadRow</c> result as the wire's own tag-and-payload pair: whether the arena holds the cell, the
/// <see cref="CellKind"/> case it carries, and that case's wire spelling.</summary>
/// <remarks>Nothing here names a sibling member of the cell record, so a reader never has to know the row's kind out
/// of band to know which member is live — the payload says what it is.</remarks>
/// <param name="Found">Whether the arena holds the addressed cell.</param>
/// <param name="Kind">The carried case by name, or <see langword="null"/> when nothing is carried.</param>
/// <param name="Value">The carried case's wire spelling — see <see cref="Spell"/> — or <see langword="null"/> when
/// nothing is carried.</param>
public readonly record struct BrowserCellValue(bool Found, string? Kind, string? Value) {
    /// <summary>Gets the answer for a cell no row of this session addresses.</summary>
    public static BrowserCellValue Absent => new(
        Found: false,
        Kind: null,
        Value: null
    );

    /// <summary>Carries one read onto the wire under the kind its row declares.</summary>
    /// <param name="found">Whether the arena holds the cell.</param>
    /// <param name="kind">The row's declared kind.</param>
    /// <param name="value">The kind's own wire spelling, or <see langword="null"/>.</param>
    /// <returns>The payload.</returns>
    public static BrowserCellValue Carrying(bool found, CellKind kind, string? value) => new(
        Found: found,
        Kind: kind.ToString(),
        Value: value
    );
    /// <summary>Spells one carrier as its case's wire payload: a 64-bit number as a decimal string (the raw
    /// <c>FixedQ4816</c> bits for <see cref="CellKind.Fixed"/>, which is the same raw channel <c>WriteRow</c>
    /// takes), <c>true</c>/<c>false</c> for <see cref="CellKind.Bool"/>, the text itself for
    /// <see cref="CellKind.Text"/>, and the base64url components for <see cref="CellKind.Vector"/>.</summary>
    /// <param name="value">The carrier.</param>
    /// <returns>The spelling, or <see langword="null"/> when the carrier holds no case (or holds a vector no
    /// <see cref="StateVector"/> admits).</returns>
    public static string? Spell(in CellValue value) => (!value.HasValue
        ? null
        : value.Kind switch {
            CellKind.Bool => (value.AsBool
            ? "true"
            : "false"),
            CellKind.Fixed => value.AsFixed.ToString(provider: CultureInfo.InvariantCulture),
            CellKind.Int => value.AsInt.ToString(provider: CultureInfo.InvariantCulture),
            CellKind.Text => value.AsText,
            CellKind.Vector => SpellVector(components: value.AsVector.Span),
            _ => throw new InvalidOperationException(message: $"Unknown cell kind '{value.Kind}'."),
        }
    );

    private static string? SpellVector(ReadOnlySpan<sbyte> components) => (StateVector.TryCreate(
        components: components,
        error: out _,
        vector: out var vector
    )
        ? vector!.ToBase64Url()
        : null
    );
}
/// <summary>One row's whole current content — the shape <c>Rows</c> exports. Only the row's AUTHORED cells are
/// listed (its <see cref="StateRow.Cells"/>); a dense board's un-authored cells (still readable individually
/// through <c>ReadRow</c> or in bulk through <c>Cells</c>/<c>BoardMask</c>) are not enumerated here, since a large
/// lattice's full cell list would dwarf the rest of the read-back for no board a studio actually authors sparsely.</summary>
public readonly record struct BrowserRowSnapshot(string Name, string Kind, bool Keyed, IReadOnlyList<BrowserRowCell> Cells);
/// <summary>One authored cell of a <see cref="BrowserRowSnapshot"/>: its key and the wire spelling of the one case
/// the snapshot's own <see cref="BrowserRowSnapshot.Kind"/> declares (see <see cref="BrowserCellValue.Spell"/>),
/// <see langword="null"/> where the arena holds no such cell.</summary>
public readonly record struct BrowserRowCell(string Key, string? Value);
/// <summary>The most evaluations one <c>Judge</c> call captures across every rule combined — a forEach-heavy document
/// could otherwise produce an unbounded trace in one tick.</summary>
public static class BrowserJudgeLimits {
    /// <summary>Gets the evaluation capture ceiling.</summary>
    public const int MaxTraceEvaluations = 4096;
}
/// <summary>One compiled-and-installed world document: a <see cref="StateArena"/> over its state section, the rules
/// <see cref="WorldFactsCompiler"/> compiles against that arena's catalog, and the
/// <see cref="BrowserRuleReader"/> host they evaluate through — held live behind an opaque handle in
/// <see cref="BrowserSessionRegistry"/> across the JS boundary's otherwise-stateless calls. Pure C# — no
/// <c>[JSExport]</c>/marshalling concern lives here; <c>Puck.World.Browser.Exports.BrowserExports</c> is the
/// only caller.</summary>
public sealed class BrowserSession {
    /// <summary>The category a rule refused admission reads back under in a judged tick's refusal ledger.</summary>
    public const string AdmissionRefusalCategory = "RuleNotAdmitted";

    private readonly StateArena m_arena;
    private readonly RuleGroupState m_groupState = new();
    private readonly BrowserRuleReader m_host;

    private BrowserRefusal[] m_admissionRefusals;
    private WorldFactsCompileContext m_context;
    private CompiledRuleGroup[] m_groups;
    private CompiledRule[] m_rules;
    // The members of m_rules no group claims: a claimed rule runs only under its group's pass or cursor, so
    // walking m_rules in the direct pass as well would fire every member twice per judged tick.
    private CompiledRule[] m_ungrouped;

    internal BrowserSession(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        Definition = definition;

        if (!StateArena.TryCreate(
            arena: out var arena,
            catalog: definition.StateCatalog,
            options: WorldSlotLanes.Options(definition: definition),
            reason: out var reason,
            section: definition.StateRaw,
            time: ArenaTime.Origin
        )) {
            // Unreachable for a definition that already passed WorldDefinitionValidator.TryValidateLocally, which
            // admits the same values the arena's own load door does.
            throw new InvalidOperationException(message: $"the arena refused an already-validated document: {reason}");
        }

        m_arena = arena;
        m_host = new BrowserRuleReader(
            arena: arena,
            dynamics: definition.Dynamics,
            generators: definition.Generators,
            instanceIdentity: WorldDefinitionLoader.BootInstanceName,
            ticksPerSecond: definition.SimulationRateHz
        );
        m_context = WorldFactsCompiler.Context(definition: definition);
        m_groups = [];
        m_rules = [];
        m_ungrouped = [];
        m_admissionRefusals = [];
        Install(definition: definition);
    }

    /// <summary>Gets the currently installed document.</summary>
    public WorldDefinition Definition { get; private set; }

    // A cell the arena does not hold reads as the default carrier, which holds no case at all: it answers 0 and no
    // text rather than throwing on Kind.
    private static long Raw(in CellValue value) => (!value.HasValue
        ? 0L
        : value.Kind switch {
            CellKind.Bool => (value.AsBool
            ? 1L
            : 0L),
            CellKind.Fixed => value.AsFixed,
            CellKind.Int => value.AsInt,
            _ => 0L,
        }
    );
    // A cell's identity across the before- and after-image of one judged tick: its row's catalog ordinal over its
    // key's intern ordinal. Both are stable for the life of a catalog, so a cell that moved position within its row
    // is still the same cell here.
    private static long Identity(int rowOrdinal, CellKey key) => (((long)rowOrdinal) << 32) | ((uint)key.Ordinal);
    // Rules the host does not serve are not evaluated: RuleNeeds.Admit is asked once per document load, and its
    // refusal rides every judged tick's refusal ledger so an author sees which rules this engine cannot run.
    private void Install(WorldDefinition definition) {
        var admitted = new List<CompiledRule>();
        var refusals = new List<BrowserRefusal>();

        foreach (var rule in WorldFactsCompiler.CompileAll(definition: definition)) {
            if (RuleNeeds.Admit(
                needs: rule.Needs,
                reader: m_host,
                refusal: out var refusal
            )) {
                admitted.Add(item: rule);
            } else {
                refusals.Add(item: new BrowserRefusal(
                    Category: AdmissionRefusalCategory,
                    Count: 1UL,
                    Detail: refusal,
                    Effect: string.Empty,
                    LastTick: 0UL,
                    Rule: rule.Name
                ));
            }
        }

        m_rules = [.. admitted];
        // Groups compile against the admitted array, not the compiled one, so a group's Members index what this
        // session evaluates. A group naming a rule this host refused therefore refuses compilation whole rather
        // than running a partial cascade; the member's own admission refusal already says why.
        var refused = false;

        try {
            m_groups = RuleCompiler.CompileGroups(
                context: m_context,
                groups: definition.RuleGroups,
                rules: m_rules
            );
        } catch (RuleException exception) {
            m_groups = [];
            refused = true;
            refusals.Add(item: new BrowserRefusal(
                Category: AdmissionRefusalCategory,
                Count: 1UL,
                Detail: exception.Message,
                Effect: string.Empty,
                LastTick: 0UL,
                Rule: string.Empty
            ));
        }

        // A group's own needs are its trigger's, and the gate that decides whether the group runs is read through
        // this host exactly as a rule's gate is, so it is admitted here beside them.
        var admittedGroups = new List<CompiledRuleGroup>(capacity: m_groups.Length);

        foreach (var group in m_groups) {
            if (RuleNeeds.Admit(
                needs: group.Needs,
                reader: m_host,
                refusal: out var refusal
            )) {
                admittedGroups.Add(item: group);
            } else {
                refused = true;
                refusals.Add(item: new BrowserRefusal(
                    Category: AdmissionRefusalCategory,
                    Count: 1UL,
                    Detail: refusal,
                    Effect: string.Empty,
                    LastTick: 0UL,
                    Rule: group.Name
                ));
            }
        }

        m_groups = [.. admittedGroups];
        m_admissionRefusals = [.. refusals];
        // A refused group is refused whole, its members included: they are claimed by the group, so the direct pass
        // drops them by authored name rather than running what the group would have run. KEEP IN SYNC with the
        // server, where the same refusal is a validation line and no part of the document installs.
        m_ungrouped = (refused
            ? Unclaimed(
                groups: definition.RuleGroups,
                rules: m_rules
            )
            : RuleCompiler.Ungrouped(
                groups: m_groups,
                rules: m_rules
            )
        );
        m_groupState.Prune(compiled: m_groups);
    }
    // The compiled rules no AUTHORED group claims, resolved by name rather than by member index, because the groups
    // that would carry the indices are exactly what failed to compile.
    private static CompiledRule[] Unclaimed(CompiledRule[] rules, IReadOnlyList<RuleGroupDeclaration>? groups) {
        if (groups is not { Count: > 0 }) {
            return rules;
        }

        var claimed = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var group in groups) {
            foreach (var step in (group?.Steps ?? [])) {
                _ = claimed.Add(item: (step?.Rule.Value ?? string.Empty));
            }
        }

        var unclaimed = new List<CompiledRule>(capacity: rules.Length);

        foreach (var rule in rules) {
            if (!claimed.Contains(item: rule.Name)) {
                unclaimed.Add(item: rule);
            }
        }

        return [.. unclaimed];
    }
    private BrowserRuleTrace[] GroupTrace(IReadOnlyList<RuleTraceEvaluation> captured) {
        var order = new List<string>();
        var byRule = new Dictionary<string, List<BrowserEvaluation>>(comparer: StringComparer.Ordinal);

        foreach (var entry in captured) {
            if (!byRule.TryGetValue(
                key: entry.Rule,
                value: out var list
            )) {
                list = [];
                byRule[entry.Rule] = list;
                order.Add(item: entry.Rule);
            }

            list.Add(item: new BrowserEvaluation(
                Locals: entry.Locals,
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
            var mode = (m_rules.FirstOrDefault(predicate: rule => string.Equals(
                a: rule.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            ))?.Mode.ToString() ?? "");

            result[index] = new BrowserRuleTrace(
                Name: name,
                Mode: mode,
                Evaluations: byRule[name]
            );
        }

        return result;
    }
    private BrowserWrite[] DiffWrites(Dictionary<long, CellValue> before) {
        var catalog = m_arena.Catalog;
        var layout = m_arena.Layout;
        var writes = new List<BrowserWrite>();

        for (var ordinal = 0; (ordinal < layout.RowCount); ordinal++) {
            if (layout[ordinal].HostOwned) {
                continue;
            }

            var count = m_arena.CellCount(rowOrdinal: ordinal);

            for (var position = 0; (position < count); position++) {
                if (
                    !m_arena.TryKeyAt(
                    key: out var key,
                    position: position,
                    rowOrdinal: ordinal
                ) ||
                    !m_arena.TryReadAt(
                    position: position,
                    rowOrdinal: ordinal,
                    value: out var after
                )
                ) {
                    continue;
                }

                var stored = (before.TryGetValue(
                    key: Identity(
                        key: key,
                        rowOrdinal: ordinal
                    ),
                    value: out var old
                )
                    ? old
                    : default
                );

                if (stored.Equals(other: after)) {
                    continue;
                }

                writes.Add(item: new BrowserWrite(
                    Key: catalog.Keys[key: key].Value,
                    New: Raw(value: in after),
                    Old: Raw(value: in stored),
                    Row: catalog.Descriptors[ordinal].Name
                ));
            }
        }

        return [.. writes];
    }
    private Dictionary<long, CellValue> Snapshot() {
        var layout = m_arena.Layout;
        var snapshot = new Dictionary<long, CellValue>();

        for (var ordinal = 0; (ordinal < layout.RowCount); ordinal++) {
            if (layout[ordinal].HostOwned) {
                continue;
            }

            var count = m_arena.CellCount(rowOrdinal: ordinal);

            for (var position = 0; (position < count); position++) {
                if (
                    m_arena.TryKeyAt(
                    key: out var key,
                    position: position,
                    rowOrdinal: ordinal
                ) &&
                    m_arena.TryReadAt(
                    position: position,
                    rowOrdinal: ordinal,
                    value: out var value
                )
                ) {
                    snapshot[Identity(
                        key: key,
                        rowOrdinal: ordinal
                    )] = value;
                }
            }
        }

        return snapshot;
    }
    // A row name resolves against the installed catalog's document lane; a key interns into the same catalog the
    // compiled rules address through, so a read and a rule's own read reach the same cell.
    private bool TryAddress(string row, string key, out int rowOrdinal, out CellKey cellKey) {
        cellKey = default;
        rowOrdinal = -1;

        if (!m_arena.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: row
        )) {
            return false;
        }

        rowOrdinal = handle.Ordinal;

        return (CellName.TryParse(
            candidate: key,
            name: out var parsed,
            reason: out _
        ) && m_arena.Catalog.Keys.TryIntern(
            key: out cellKey,
            name: parsed,
            reason: out _
        ));
    }
    // A design-time session never changes its simulation rate between ticks, so its engine-tick coordinate is the
    // tick count times the document rate's step width; a resident world (rateHz 0) never advances.
    private ulong EngineTickAt(ulong tick) => ((Definition.SimulationRateHz > 0)
        ? checked((tick * (Puck.Maths.FixedTickConversion.TicksPerSecond / ((ulong)Definition.SimulationRateHz))))
        : 0UL
    );

    /// <summary>Judges one tick over every admitted rule, capturing each rule's own evaluations and diffing the
    /// arena before and after.</summary>
    /// <param name="tick">The tick the reads answer as of.</param>
    /// <returns>The whole trace.</returns>
    public BrowserJudgeResult Judge(ulong tick) {
        var before = Snapshot();

        m_host.Advance(
            engineTick: EngineTickAt(tick: tick),
            tick: tick
        );
        m_host.BeginJudge();
        m_host.Evaluator.ArmTraceAll(maxEvaluations: BrowserJudgeLimits.MaxTraceEvaluations);

        foreach (var rule in m_ungrouped) {
            m_host.RuleName = rule.Name;
            m_host.Evaluator.EvaluateRule(
                latch: m_host.Latch,
                rule: rule,
                stepTicks: 1UL
            );
        }

        m_host.RuleName = string.Empty;
        m_host.Evaluator.EvaluateGroups(
            groups: m_groups,
            latch: m_host.Latch,
            rules: m_rules,
            state: m_groupState,
            stepTicks: 1UL
        );

        var rules = GroupTrace(captured: m_host.Evaluator.TraceCaptured);
        var writes = DiffWrites(before: before);
        var refusals = m_admissionRefusals.Concat(second: m_host.Evaluator.Diagnostics().Select(selector: static diagnostic => new BrowserRefusal(
            Category: diagnostic.Refusal.ToString(),
            Count: diagnostic.Count,
            LastTick: diagnostic.LastTick,
            Rule: diagnostic.Rule,
            Effect: diagnostic.Effect,
            Detail: diagnostic.Detail
        ))).ToArray();
        var hostFacts = m_host.HostFacts.ToArray();

        m_host.Evaluator.DisarmTrace();

        return new BrowserJudgeResult(
            HostFacts: hostFacts,
            Refusals: refusals,
            Rules: rules,
            Writes: writes
        );
    }
    /// <summary>Reads one cell through the arena — the same read a rule's own state token resolves.</summary>
    /// <param name="row">The row name.</param>
    /// <param name="key">The cell key.</param>
    /// <returns>The cell's value, or a not-found result.</returns>
    public BrowserCellValue ReadRow(string row, string key) {
        if (!TryAddress(
            cellKey: out var cellKey,
            key: key,
            row: row,
            rowOrdinal: out var ordinal
        )) {
            return BrowserCellValue.Absent;
        }

        var kind = m_arena.Layout[ordinal].Kind;

        // A vector rides its own arena column rather than the cell word, so it is read as components and carried
        // back through the same spelling every other case takes.
        if (kind == CellKind.Vector) {
            var vectorFound = m_arena.TryReadVector(
                components: out var components,
                key: cellKey,
                rowOrdinal: ordinal
            );

            return BrowserCellValue.Carrying(
                found: vectorFound,
                kind: kind,
                value: ((vectorFound && !components.IsEmpty)
                    ? BrowserCellValue.Spell(value: CellValue.Vector(components: components.ToArray()))
                    : null
                )
            );
        }

        var found = m_arena.TryRead(
            key: cellKey,
            rowOrdinal: ordinal,
            value: out var value
        );

        return BrowserCellValue.Carrying(
            found: found,
            kind: kind,
            value: BrowserCellValue.Spell(value: in value)
        );
    }
    /// <summary>Reads every row's authored cells through the arena — the whole-state read-back <c>Rows</c> exports.</summary>
    /// <returns>One snapshot per row, in document order.</returns>
    public IReadOnlyList<BrowserRowSnapshot> Rows() {
        var rows = Definition.State;
        var result = new BrowserRowSnapshot[rows.Count];

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var cells = new List<BrowserRowCell>(capacity: (row.Cells?.Count ?? 0));

            foreach (var cell in (row.Cells ?? [])) {
                var read = ReadRow(
                    row: row.Name.Value,
                    key: cell.Key.Value
                );

                cells.Add(item: new BrowserRowCell(
                    Key: cell.Key.Value,
                    Value: read.Value
                ));
            }

            result[index] = new BrowserRowSnapshot(
                Name: row.Name.Value,
                Kind: row.Kind.ToString(),
                Keyed: row.IsKeyed,
                Cells: cells
            );
        }

        return result;
    }
    /// <summary>Gets the arena's deterministic content hash — the determinism canary's shared comparator between a
    /// native and a wasm run.</summary>
    /// <returns>The hash.</returns>
    public ulong StateHash() => m_arena.ComputeHash();
    /// <summary>Reads a board row's occupancy as one 64-bit mask — bit <c>i</c> set when cell <c>i</c> is not at the
    /// board's declared empty value. Only meaningful for a topology of at most
    /// <see cref="Puck.State.BoardMask.MaxCells"/> cells; a larger board reports <see langword="false"/>.</summary>
    /// <param name="row">The board row's name.</param>
    /// <param name="mask">The occupancy mask on success.</param>
    /// <returns><see langword="true"/> when <paramref name="row"/> is a board row within the mask ceiling.</returns>
    public bool TryBoardMask(string row, out ulong mask) {
        mask = 0UL;

        if (!m_arena.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: row
        )) {
            return false;
        }

        var layout = m_arena.Layout[handle.Ordinal];

        if (
            (layout.Topology is not { } topology) ||
            (topology.CellCount > Puck.State.BoardMask.MaxCells)
        ) {
            return false;
        }

        Span<long> values = stackalloc long[topology.CellCount];

        if (!m_arena.TryReadBoard(
            rowOrdinal: handle.Ordinal,
            values: values
        )) {
            return false;
        }

        for (var cell = 0; (cell < values.Length); cell++) {
            if (values[cell] != layout.Empty) {
                mask |= (1UL << cell);
            }
        }

        return true;
    }
    /// <summary>Evaluates one postfix or infix expression against the arena, on the same terms a rule's own
    /// expression token would.</summary>
    /// <param name="expression">The infix or <c>{"tokens":[...]}</c> postfix spelling.</param>
    /// <param name="kind">The kind the expression must produce.</param>
    /// <param name="tick">The tick a time-sensitive token (a timer read) answers as of.</param>
    /// <param name="value">The computed value on success.</param>
    /// <param name="error">Why evaluation refused, or empty.</param>
    /// <returns><see langword="true"/> when the expression parsed, compiled, and evaluated.</returns>
    public bool TryEvaluate(string expression, CellKind kind, ulong tick, out long value, out string error) {
        value = 0L;

        ExpressionProgram parsed;

        try {
            parsed = ExpressionProgram.Parse(text: expression);
        } catch (FormatException exception) {
            error = exception.Message;

            return false;
        }

        CompiledExpressionToken[] compiled;

        try {
            compiled = RuleCompiler.CompileExpression(
                context: m_context,
                expression: parsed,
                kind: kind,
                ruleName: "$evaluate",
                verb: "evaluate"
            );
        } catch (RuleException exception) {
            error = exception.Message;

            return false;
        }

        m_host.Advance(
            engineTick: EngineTickAt(tick: tick),
            tick: tick
        );

        if (!Puck.State.Rules.RuleExpressions.TryEvaluate(
            fault: out var fault,
            kind: kind,
            program: compiled,
            reader: m_host,
            value: out value
        )) {
            error = $"the expression could not be evaluated against the current state: {RuleEvaluation.DescribeFault(fault: fault)}.";

            return false;
        }

        error = string.Empty;

        return true;
    }
    /// <summary>Rebinds the session to an edited document whose state section has the installed catalog's shape — a
    /// structural edit (a renamed or resized row, a new topology) refuses rather than silently discarding the
    /// arena's current values; the caller recompiles a fresh session instead.</summary>
    /// <param name="definition">The candidate replacement, already parsed and validated by the caller.</param>
    /// <param name="reason">Why the rebind refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the candidate's section has the installed shape.</returns>
    public bool TryRebind(WorldDefinition definition, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!m_arena.Catalog.MatchesShape(section: definition.StateRaw)) {
            reason = "the candidate's state rows do not lay out identically to the installed document (a row was added, removed, renamed, or changed kind/domain) — recompile a fresh session instead.";

            return false;
        }
        // At the session's own time, not the origin: a rebind is a fresh load of the same arena, so a cell born
        // under a value-over-time trait settles where the session stands, exactly as the server's own resync
        // settles one at its Time.
        if (!m_arena.TryLoad(
            reason: out var loadReason,
            rows: definition.State,
            time: m_host.Time
        )) {
            reason = loadReason;

            return false;
        }

        Definition = definition;
        m_context = WorldFactsCompiler.Context(definition: definition);
        Install(definition: definition);
        reason = string.Empty;

        return true;
    }
    /// <summary>Writes one cell through the arena, on the same terms a rule's own effect would.</summary>
    /// <param name="row">The row name.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="value">The operand.</param>
    /// <param name="add">Whether to add to the stored value rather than replace it.</param>
    /// <param name="reason">Why the write refused, or empty.</param>
    /// <returns><see langword="true"/> when the write applied.</returns>
    public bool TryWriteRow(string row, string key, long value, bool add, out string reason) {
        if (!TryAddress(
            cellKey: out var cellKey,
            key: key,
            row: row,
            rowOrdinal: out var ordinal
        )) {
            reason = $"row '{row}' and key '{key}' do not address a cell of the installed document.";

            return false;
        }

        var mark = m_arena.BeginScope();

        _ = m_host.Apply(
            mutation: Mutation.Written(
                key: cellKey,
                operand: value,
                rowOrdinal: ordinal,
                write: (add
                ? StateWriteKind.Add
                : StateWriteKind.Set)
            ),
            refusal: out var refusal
        );

        if (refusal.IsRefused) {
            m_arena.Rewind(mark: mark);
            reason = refusal.Reason;

            return false;
        }

        m_arena.Commit(mark: mark);
        reason = string.Empty;

        return true;
    }
    /// <summary>Writes one vector cell through the arena.</summary>
    /// <param name="row">The row name.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="components">The unit vector components.</param>
    /// <param name="reason">Why the write refused, or empty.</param>
    /// <returns><see langword="true"/> when the write applied.</returns>
    public bool TryWriteVectorRow(string row, string key, ReadOnlySpan<sbyte> components, out string reason) {
        if (!TryAddress(
            cellKey: out var cellKey,
            key: key,
            row: row,
            rowOrdinal: out var ordinal
        )) {
            reason = $"row '{row}' and key '{key}' do not address a cell of the installed document.";

            return false;
        }

        var mark = m_arena.BeginScope();

        if (!m_arena.TryWriteVector(
            components: components,
            key: cellKey,
            reason: out reason,
            rowOrdinal: ordinal
        )) {
            m_arena.Rewind(mark: mark);

            return false;
        }

        m_arena.Commit(mark: mark);

        return true;
    }
}
