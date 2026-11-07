using System.Globalization;

namespace Puck.World.Server;

public sealed partial class WorldRuleHost {
    private CompiledPatterns m_patterns = CompiledPatterns.Empty;

    // Trusted second compile: the validator already refused any document whose patterns do not compile.
    internal void ReconcilePatterns(WorldDefinition definition) {
        var errors = new List<string>();

        if (!CompiledPatterns.TryCompileAll(
            rows: definition.Patterns,
            patterns: out var patterns,
            errors: errors
        )) {
            throw new InvalidOperationException(message: $"patterns failed to compile after validation: {string.Join(
                separator: "; ",
                values: errors
            )}");
        }

        m_patterns = patterns;
    }
    /// <summary>Walks one word through a pattern at the console and narrates every step: the raw values, the letter
    /// each reads as, the state after it, and the verdicts. The word is read off the arena at this host's
    /// <see cref="Time"/> through the same reads a <c>$match</c> operand takes
    /// (<see cref="PatternOperand.ReadWord"/> and <see cref="ArenaBoards.TryReadRay"/>), so it narrates what a rule
    /// matches.</summary>
    /// <param name="patternName">The pattern.</param>
    /// <param name="rowName">The source row.</param>
    /// <param name="attribute">For a zone source, the attribute row; ignored otherwise.</param>
    /// <param name="key">For a board source, the origin cell; for a word source, the token the word starts at, or
    /// <see langword="null"/> for the first.</param>
    /// <param name="direction">For a board source, a direction name or <c>any</c>.</param>
    /// <returns>A deterministic, headless-safe read-back, or a refusal by name.</returns>
    internal string DescribeMatch(string patternName, string rowName, string? attribute, string? key, string? direction) {
        lock (Host.AuthorityGate) {
            return DescribeMatchCore(Host.Definition, this, m_patterns, patternName, rowName, attribute, key, direction);
        }
    }

    /// <summary>Walks the same pattern machinery over only the reader's disclosed state, including tuple dependencies.</summary>
    /// <param name="view">The caller's disclosed document and completed tick pair.</param>
    /// <param name="patternName">The pattern.</param>
    /// <param name="rowName">The source row.</param>
    /// <param name="attribute">The attribute row of a keyed word.</param>
    /// <param name="key">The board origin or first token.</param>
    /// <param name="direction">The board direction, or <c>any</c>.</param>
    /// <returns>The same narration as the authority path, computed only from disclosed values.</returns>
    public static string DescribeDisclosedMatch(WorldStateReadView view, string patternName, string rowName, string? attribute, string? key, string? direction) {
        var definition = view.Definition;

        if (!CompiledPatterns.TryCompileAll(definition.Patterns, out var patterns, new List<string>())) {
            return "[world.match: disclosed patterns do not compile]";
        }
        var reader = new ArenaEffectHost(WorldStateReader.Seed(definition: definition), ticksPerSecond: definition.SimulationRateHz, dynamics: definition.Dynamics);

        reader.Advance(view.CompletedTick, view.CompletedEngineTick);
        return DescribeMatchCore(attribute: attribute, definition: definition, direction: direction, key: key, patternName: patternName, patterns: patterns, reader: reader, rowName: rowName);
    }

    private static string DescribeMatchCore(WorldDefinition definition, IStateReader reader, CompiledPatterns patterns, string patternName, string rowName, string? attribute, string? key, string? direction) {
        if (!patterns.TryGet(
            name: patternName,
            pattern: out var pattern
        )) {
            return $"[world.match: '{patternName}' names no pattern]";
        }
        if (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: rowName
        ) is not { } row) {
            return $"[world.match: '{rowName}' names no state row]";
        }

        var arena = reader.Arena;

        if (!arena.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: rowName
        )) {
            return $"[world.match: '{rowName}' names no arena row]";
        }

        var rowOrdinal = handle.Ordinal;
        var word = new long[PatternCapacity.MaxWord];
        var lines = new List<string>();

        if (row.EffectiveDomain is StateDomain.CellsOf board) {
            if (arena.Layout[rowOrdinal].Topology is not { } topology) {
                return $"[world.match: '{rowName}' names no compiled topology]";
            }
            if (
                (key is null) ||
                !topology.TryCell(
                cell: out var origin,
                key: key
            )
            ) {
                return $"[world.match: a board source needs an origin cell of '{board.Topology}']";
            }
            if (direction is null) {
                return "[world.match: a board source needs a direction or any]";
            }

            var values = new long[topology.CellCount];
            var first = ((direction == "any")
                ? 0
                : topology.Direction(token: direction)
            );
            var last = ((direction == "any")
                ? (topology.DirectionCount - 1)
                : first
            );

            if (first < 0) {
                return $"[world.match: '{direction}' is not a direction of '{board.Topology}']";
            }
            for (var walked = first; (walked <= last); walked++) {
                if (!ArenaBoards.TryReadRay(
                    arena: arena,
                    direction: walked,
                    origin: origin,
                    ray: out var ray,
                    reason: out var rayReason,
                    rowOrdinal: rowOrdinal,
                    values: values,
                    word: word
                )) {
                    return $"[world.match: {rayReason}]";
                }

                lines.Add(item: $"direction {walked}: {Narrate(
                    pattern: pattern,
                    values: ray
                )}");
            }
        } else {
            CompiledExpressionToken[]? expression = null;
            var attributeOrdinal = -1;

            if (
                (row.EffectiveDomain is StateDomain.KeysOf { Ordered: true } zone) &&
                (pattern.Source.Value is not null)
            ) {
                if (!WorldFactsCompiler.TryCompilePatternValue(
                    definition: definition,
                    pattern: pattern.Source,
                    tokenDomain: zone.Row.Value,
                    ruleName: "world.match",
                    tokens: out expression,
                    reason: out var valueReason
                )) {
                    return $"[world.match: {valueReason}]";
                }
                var needs = new RuleNeedsBuilder();

                RuleDataflow.CollectExpressionSchedulingFacts(into: needs, tokens: expression);
                if (!RuleNeeds.Admit(needs: needs.Build(), reader: reader, refusal: out var refusal)) {
                    return $"[world.match: {refusal}]";
                }
            } else if (row.EffectiveDomain is StateDomain.KeysOf { Ordered: true }) {
                if (
                    (attribute is null) ||
                    !arena.Catalog.TryResolve(
                    handle: out var attributeHandle,
                    lane: StateLane.Document,
                    name: attribute
                )
                ) {
                    return "[world.match: a zone source needs its attribute row]";
                }

                attributeOrdinal = attributeHandle.Ordinal;
            }

            var start = 0;

            if (key is not null) {
                start = (arena.Keys.TryResolve(
                    key: out var startKey,
                    name: key
                )
                    ? PatternOperand.StartPosition(
                        arena: arena,
                        key: startKey,
                        rowOrdinal: rowOrdinal
                    )
                    : int.MaxValue
                );
            }

            // The console's read decides what a token's fault means here: the word spells nothing, and says why.
            var scratch = reader.Scratch;
            var scope = scratch.EnterFaultScope();
            Outcome spelled;

            try {
                spelled = PatternOperand.ReadWord(
                    attributeOrdinal: attributeOrdinal,
                    kind: pattern.Source.Kind,
                    reader: reader,
                    rowOrdinal: rowOrdinal,
                    start: start,
                    tokenExpression: expression,
                    word: word
                );
            } finally {
                _ = scratch.LeaveFaultScope(scope: scope);
            }
            if (!spelled.TryFinite(raw: out var length)) {
                return $"[world.match: {patternName} over {rowName} | the word spells nothing, since a token's value expression did not answer: {RuleEvaluation.DescribeOutcome(outcome: spelled)}]";
            }

            lines.Add(item: Narrate(
                pattern: pattern,
                values: word.AsSpan(
                    length: ((int)length),
                    start: 0
                )
            ));
        }

        return $"[world.match: {patternName} over {rowName} | {string.Join(
            separator: " | ",
            values: lines
        )}]";
    }
    private static string Narrate(CompiledPattern pattern, ReadOnlySpan<long> values) {
        var steps = new List<string>();
        var state = 0;
        var longest = (pattern.Accepts(state: 0)
            ? 0
            : -1
        );

        for (var index = 0; (index < values.Length); index++) {
            var letter = pattern.LetterOf(value: values[index]);

            state = pattern.Step(
                letter: letter,
                state: state
            );

            if (pattern.Accepts(state: state)) {
                longest = (index + 1);
            }

            steps.Add(item: string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{values[index]}→{pattern.DescribeLetter(letter: letter)}→s{state}{(pattern.Accepts(state: state)
                ? "✓"
                : "")}"
            ));
        }

        return $"word[{values.Length}] {((steps.Count == 0)
            ? "(empty)"
            : string.Join(
                separator: ' ',
                values: steps
            ))} accept={(pattern.Accepts(state: state)
            ? 1
            : 0)} prefix={longest}";
    }

    /// <summary>Echoes every compiled pattern: kind, refined letter count, machine states against the budget, and attribute.</summary>
    /// <returns>A deterministic, headless-safe console read-back.</returns>
    internal string DescribePatterns() {
        lock (Host.AuthorityGate) {
            var rows = new List<string>();

            foreach (var pattern in m_patterns.All) {
                rows.Add(item: string.Create(
                    CultureInfo.InvariantCulture,
                    $"{pattern.Source.Name} kind={pattern.Source.Kind} letters={pattern.LetterCount} states={pattern.StateCount}/{pattern.Source.MaxStates} attribute={(pattern.Source.Attribute ?? "none")}"
                ));
            }

            return $"[world.patterns: {((rows.Count == 0)
                ? "none"
                : string.Join(
                    separator: "; ",
                    values: rows
                ))}]";
        }
    }
    /// <summary>The pattern portion of the <c>world.budget</c> cost sheet: table size, total machine states, and the
    /// longest word one evaluation may walk.</summary>
    internal string DescribePatternBudget() {
        lock (Host.AuthorityGate) {
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
    internal string DescribeSymmetry(string topologyName, string? cellKey) {
        lock (Host.AuthorityGate) {
            if (WorldTopologyCompilation.Find(
                definition: Host.Definition,
                name: topologyName
            ) is not { } topology) {
                return $"[world.topology: '{topologyName}' names no discrete topology]";
            }

            var parts = new List<string>();
            var cell = -1;

            if (
                (cellKey is not null) &&
                !topology.TryCell(
                cell: out cell,
                key: cellKey
            )
            ) {
                return $"[world.topology: '{cellKey}' is not a cell of '{topologyName}']";
            }
            for (var element = 0; (element < topology.ElementCount); element++) {
                parts.Add(item: ((cell < 0)
                    ? topology.ElementName(element: element)
                    : $"{topology.ElementName(element: element)}→{topology.Key(cell: topology.Image(
                        cell: cell,
                        element: element
                    ))}"));
            }

            var aliases = string.Join(
                separator: ',',
                values: topology.ElementAliases().Select(selector: pair => $"{pair.Alias}={pair.Canonical}")
            );

            return $"[world.topology: {topologyName} kind={topology.Kind} elements={topology.ElementCount} aliases={((aliases.Length == 0)
                ? "none"
                : aliases)} | {string.Join(
                separator: ' ',
                values: parts
            )}]";
        }
    }
}
