using System.Globalization;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private CompiledPatterns m_patterns = CompiledPatterns.Empty;

    // Trusted second compile: the validator already refused any document whose patterns do not compile.
    private void ReconcilePatterns(WorldDefinition definition) {
        var errors = new List<string>();

        if (!CompiledPatterns.TryCompileAll(rows: definition.Patterns, patterns: out var patterns, errors: errors)) {
            throw new InvalidOperationException($"patterns failed to compile after validation: {string.Join("; ", errors)}");
        }

        m_patterns = patterns;
        m_patternWord = (patterns.Count == 0) ? [] : new long[WordCeiling(definition)];
    }

    // The longest word any source in this document can produce: the widest row ceiling, capped at the word cap.
    private static int WordCeiling(WorldDefinition definition) {
        var ceiling = 1;

        foreach (var row in definition.State) {
            ceiling = Math.Max(ceiling, row.Capacity ?? row.CellCeiling);
        }

        return Math.Min(ceiling, PatternCapacity.MaxWord);
    }

    // $match: — the word is read at this tick through compiled row handles (no name scan) and the same per-cell
    // read every other state read uses, so an advancing attribute cell reads its live value. Acceptance is 1 or 0
    // and nothing else: every source fits the word buffer, and a board origin that names no cell reads the empty
    // word, which the pattern decides like any other.
    private long[] m_patternWord = [];
    // The token a pattern value expression is evaluating for; set only for the duration of one word read.
    private string? m_patternTokenKey;
    // A zone's cells in pile order read through its attribute row, a history ring oldest push first, or a keyed
    // row's own cells in cell order.
    private static int ReadWord(WorldStateRow row, WorldStateRow source, ulong tick, Span<long> word) {
        var length = 0;

        if (row.EffectiveDomain is StateDomain.Ring history) {
            var count = (int)Math.Min(row.HistoryCursor, history.Capacity);

            for (var age = count - 1; age >= 0; age--) {
                word[length++] = ReadHistorySlot(row, history, age, tick);
            }

            return length;
        }

        foreach (var cell in (row.Cells ?? [])) {
            StateReader.ReadCell(row: source, key: cell.Key.Value, tick: tick, rawValue: out var raw, text: out _);
            word[length++] = raw ?? 0L;
        }

        return length;
    }

    // A zone's tokens in pile order, each read through the pattern's value expression with $token bound to it; an
    // expression that fails on a token reads that letter as zero.
    private int ReadTupleWord(WorldStateRow row, CompiledExpressionToken[] expression, CellKind kind, ulong tick, Span<long> word) {
        var length = 0;

        try {
            foreach (var cell in (row.Cells ?? [])) {
                m_patternTokenKey = cell.Key.Value;
                word[length++] = TryEvaluateExpression(program: expression, kind: kind, tick: tick, value: out var raw) ? raw : 0L;
            }
        } finally {
            m_patternTokenKey = null;
        }

        return length;
    }

    // The slot pushed `age` pushes ago is (cursor - 1 - age) mod capacity, and the ring's cells ARE its slots in
    // order (the validator's invariant), so the value is one index away; a slot never written reads the empty value.
    private static long ReadHistorySlot(WorldStateRow row, StateDomain.Ring history, long age, ulong tick) {
        if (age >= Math.Min(row.HistoryCursor, history.Capacity)) {
            return history.Empty;
        }

        var slot = (int)((row.HistoryCursor - 1L - age) % history.Capacity);
        var cells = row.Cells;

        // Ring slots carry no time trait (the validator's rule), so the stored raw IS the live value.
        return (cells is null || slot >= cells.Count) ? history.Empty : cells[slot].Value;
    }

    // pushState: the value is resolved the way a write's is, then lands as a Push transform so the ring's cursor and
    // slot move in one journaled mutation.
    private bool FirePushState(PushStateEffect effect, string ruleName, ulong tick, bool preflight) {
        var push = effect;
        if (WorldDefinitionRows.FindStateRow(rows: m_definition.State, name: push.Row) is not { } row || row.EffectiveDomain is not StateDomain.Ring) {
            return false;
        }

        long raw;

        if (push.Expression is { } expression) {
            if (!TryEvaluateExpression(program: expression, kind: row.Kind, tick: tick, value: out raw)) {
                if (preflight) {
                    m_ruleStatePreflightRejected = true;
                }
                ReportRuleEffectRefusal(refusal: WorldRuleEffectRefusal.Arithmetic, ruleName: ruleName, effect: effect, tick: tick, detail: "the pushed expression overflowed, divided by zero, or shifted out of range");
                return false;
            }
        } else if (push.From is { } from) {
            var fact = ReadWorldFact(operand: from, tick: tick);

            if (fact.IsForever) {
                return false;
            }

            raw = fact.ToRaw(kind: row.Kind);
        } else {
            raw = push.RawValue;
        }

        return ApplyWorldRuleMutation(effect: effect, ruleName: ruleName, mutation: new WorldMutation.TransformState(WorldPrincipal.World, new StateTransform.Push(row.Name.Value, raw)), tick: tick, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0, preMetered: false, preflight: preflight);
    }

    /// <summary>Walks one word through a pattern at the console and narrates every step: the raw values, the letter
    /// each reads as, the state after it, and the verdicts.</summary>
    /// <param name="patternName">The pattern.</param>
    /// <param name="rowName">The source row.</param>
    /// <param name="attribute">For a zone source, the attribute row; ignored otherwise.</param>
    /// <param name="key">For a board source, the origin cell.</param>
    /// <param name="direction">For a board source, a direction name or <c>any</c>.</param>
    /// <returns>A deterministic, headless-safe read-back, or a refusal by name.</returns>
    public string DescribeMatch(string patternName, string rowName, string? attribute, string? key, string? direction) {
        lock (m_authorityGate) {
            if (!m_patterns.TryGet(name: patternName, pattern: out var pattern)) {
                return $"[world.match: '{patternName}' names no pattern]";
            }
            if (WorldDefinitionRows.FindStateRow(rows: m_definition.State, name: rowName) is not { } row) {
                return $"[world.match: '{rowName}' names no state row]";
            }

            var tick = m_lastCompletedTick;
            var word = new long[PatternCapacity.MaxWord];
            var lines = new List<string>();

            if (row.EffectiveDomain is StateDomain.CellsOf board) {
                if (WorldTopologyCompilation.Find(m_definition, board.Topology) is not { } topology) {
                    return $"[world.match: '{rowName}' names no compiled topology]";
                }
                if (key is null || !topology.TryCell(key, out var origin)) {
                    return $"[world.match: a board source needs an origin cell of '{board.Topology}']";
                }
                if (direction is null) {
                    return "[world.match: a board source needs a direction or any]";
                }

                var values = new long[topology.CellCount];
                BoardQueries.Read(row, topology, values);
                var first = (direction == "any") ? 0 : topology.Direction(direction);
                var last = (direction == "any") ? (topology.DirectionCount - 1) : first;

                if (first < 0) {
                    return $"[world.match: '{direction}' is not a direction of '{board.Topology}']";
                }
                for (var walked = first; walked <= last; walked++) {
                    var length = BoardQueries.ReadRay(topology, values, origin, walked, word);
                    lines.Add($"direction {walked}: {Narrate(pattern, word.AsSpan(0, length))}");
                }
            } else {
                var source = row;
                int length;

                if (row.EffectiveDomain is StateDomain.KeysOf { Ordered: true } zone && pattern.Source.Value is not null) {
                    if (!WorldRuleCompiler.TryCompilePatternValue(definition: m_definition, pattern: pattern.Source, tokenDomain: zone.Row.Value, ruleName: "world.match", tokens: out var expression, reason: out var valueReason)) {
                        return $"[world.match: {valueReason}]";
                    }
                    length = ReadTupleWord(row, expression!, pattern.Source.Kind, tick, word);
                } else {
                    if (row.EffectiveDomain is StateDomain.KeysOf { Ordered: true }) {
                        if (attribute is null || WorldDefinitionRows.FindStateRow(rows: m_definition.State, name: attribute) is not { } attributeRow) {
                            return "[world.match: a zone source needs its attribute row]";
                        }
                        source = attributeRow;
                    }
                    length = ReadWord(row, source, tick, word);
                }

                lines.Add(Narrate(pattern, word.AsSpan(0, length)));
            }

            return $"[world.match: {patternName} over {rowName} | {string.Join(" | ", lines)}]";
        }
    }

    private static string Narrate(CompiledPattern pattern, ReadOnlySpan<long> values) {
        var steps = new List<string>();
        var state = 0;
        var longest = (pattern.Accepts(0) ? 0 : -1);

        for (var index = 0; index < values.Length; index++) {
            var letter = pattern.LetterOf(values[index]);
            state = pattern.Step(state, letter);

            if (pattern.Accepts(state)) {
                longest = index + 1;
            }

            steps.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{values[index]}→{pattern.DescribeLetter(letter)}→s{state}{(pattern.Accepts(state) ? "✓" : "")}"));
        }

        return $"word[{values.Length}] {((steps.Count == 0) ? "(empty)" : string.Join(' ', steps))} accept={(pattern.Accepts(state) ? 1 : 0)} prefix={longest}";
    }

    /// <summary>Echoes every compiled pattern: kind, refined letter count, machine states against the budget, and attribute.</summary>
    /// <returns>A deterministic, headless-safe console read-back.</returns>
    public string DescribePatterns() {
        lock (m_authorityGate) {
            var rows = new List<string>();

            foreach (var pattern in m_patterns.All) {
                rows.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{pattern.Source.Name} kind={pattern.Source.Kind} letters={pattern.LetterCount} states={pattern.StateCount}/{pattern.Source.MaxStates} attribute={pattern.Source.Attribute ?? "none"}"
                ));
            }

            return $"[world.patterns: {((rows.Count == 0) ? "none" : string.Join("; ", rows))}]";
        }
    }

    /// <summary>The pattern portion of the <c>world.budget</c> cost sheet: table size, total machine states, and the
    /// longest word one evaluation may walk.</summary>
    public string DescribePatternBudget() {
        lock (m_authorityGate) {
            var states = 0;

            foreach (var pattern in m_patterns.All) {
                states += pattern.StateCount;
            }

            return $"patterns {m_patterns.Count} compiled, {states} state(s), word <= {PatternCapacity.MaxWord} token(s) per read";
        }
    }

    /// <summary>Echoes a topology's point group: every element by name, and with a cell, that cell's image under each.</summary>
    /// <param name="topologyName">A discrete topology of <c>state.lattices</c>.</param>
    /// <param name="cellKey">A cell key, or null for the element list alone.</param>
    /// <returns>A deterministic, headless-safe read-back, or a refusal by name.</returns>
    public string DescribeSymmetry(string topologyName, string? cellKey) {
        lock (m_authorityGate) {
            if (WorldTopologyCompilation.Find(m_definition, topologyName) is not { } topology) {
                return $"[world.topology: '{topologyName}' names no discrete topology]";
            }

            var parts = new List<string>();
            var cell = -1;

            if (cellKey is not null && !topology.TryCell(cellKey, out cell)) {
                return $"[world.topology: '{cellKey}' is not a cell of '{topologyName}']";
            }
            for (var element = 0; element < topology.ElementCount; element++) {
                parts.Add((cell < 0) ? topology.ElementName(element) : $"{topology.ElementName(element)}→{topology.Key(topology.Image(element, cell))}");
            }

            var aliases = string.Join(',', topology.ElementAliases().Select(pair => $"{pair.Alias}={pair.Canonical}"));

            return $"[world.topology: {topologyName} kind={topology.Kind} elements={topology.ElementCount} aliases={((aliases.Length == 0) ? "none" : aliases)} | {string.Join(' ', parts)}]";
        }
    }
}
