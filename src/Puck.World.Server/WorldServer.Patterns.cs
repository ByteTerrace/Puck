using System.Globalization;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private CompiledWorldPatterns m_patterns = CompiledWorldPatterns.Empty;

    // Trusted second compile: the validator already refused any document whose patterns do not compile.
    private void ReconcilePatterns(WorldDefinition definition) {
        var errors = new List<string>();

        if (!CompiledWorldPatterns.TryCompileAll(definition: definition, patterns: out var patterns, errors: errors)) {
            throw new InvalidOperationException($"patterns failed to compile after validation: {string.Join("; ", errors)}");
        }

        m_patterns = patterns;
    }

    // $match: — the word is read at this tick through the same (row, key) seam every other state read uses, so an
    // advancing attribute cell reads its live value. -1 is "undecided": a source longer than the word cap, an
    // origin that names no cell, or a row the document no longer declares.
    private long ReadPatternFact(CompiledWorldOperand operand, ulong tick) {
        if (!m_patterns.TryGet(name: operand.Pattern!, pattern: out var pattern) ||
            WorldDefinitionRows.FindStateRow(rows: m_definition.State, name: operand.Row!) is not { } row) {
            return -1L;
        }

        Span<long> word = stackalloc long[WorldPatternCapacity.MaxWord];
        var length = 0;

        if (operand.Board is { } query) {
            if (row.Board is null) {
                return -1L;
            }

            Span<long> values = stackalloc long[query.Topology.CellCount];
            WorldBoardQueries.Read(row, query.Topology, values);
            var key = ResolveOperandKey(key: operand.Key, keyFrom: operand.KeyFrom, tick: tick);

            if (key is null || !query.Topology.TryCell(key, out var origin)) {
                return -1L;
            }

            var cell = origin;

            for (var distance = 1; distance < query.Topology.CellCount; distance++) {
                cell = query.Topology.Neighbour(cell, query.Direction);

                if (cell < 0 || cell == origin) {
                    break;
                }
                if (length == word.Length) {
                    return -1L;
                }

                word[length++] = values[cell];
            }
        } else {
            var sourceRow = ((row.Zone is not null) ? operand.FilterRow! : row.Name.Value);

            foreach (var cell in (row.Cells ?? [])) {
                if (length == word.Length) {
                    return -1L;
                }

                word[length++] = (WorldStateReader.TryRead(
                    definition: m_definition,
                    rowName: sourceRow,
                    key: cell.Key.Value,
                    tick: tick,
                    row: out _,
                    rawValue: out var raw,
                    text: out _
                ) ? (raw ?? 0L) : 0L);
            }
        }

        return pattern.Match(values: word[..length]);
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

            return $"patterns {m_patterns.Count} compiled, {states} state(s), word <= {WorldPatternCapacity.MaxWord} token(s) per read";
        }
    }
}
