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
            if (!m_patterns.TryGet(
                name: patternName,
                pattern: out var pattern
            )) {
                return $"[world.match: '{patternName}' names no pattern]";
            }
            if (WorldDefinitionRows.FindStateRow(
                rows: Host.Definition.State,
                name: rowName
            ) is not { } row) {
                return $"[world.match: '{rowName}' names no state row]";
            }

            var arena = Arena;

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
                        definition: Host.Definition,
                        pattern: pattern.Source,
                        tokenDomain: zone.Row.Value,
                        ruleName: "world.match",
                        tokens: out expression,
                        reason: out var valueReason
                    )) {
                        return $"[world.match: {valueReason}]";
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

                var length = PatternOperand.ReadWord(
                    attributeOrdinal: attributeOrdinal,
                    kind: pattern.Source.Kind,
                    reader: this,
                    rowOrdinal: rowOrdinal,
                    start: start,
                    tokenExpression: expression,
                    word: word
                );

                lines.Add(item: Narrate(
                    pattern: pattern,
                    values: word.AsSpan(
                        length: length,
                        start: 0
                    )
                ));
            }

            return $"[world.match: {patternName} over {rowName} | {string.Join(
                separator: " | ",
                values: lines
            )}]";
        }
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
