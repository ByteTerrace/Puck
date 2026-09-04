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
        m_patternWord = (patterns.Count == 0) ? [] : new long[WordCeiling(definition)];
    }

    // The longest word any source in this document can produce: the widest row ceiling, capped at the word cap.
    private static int WordCeiling(WorldDefinition definition) {
        var ceiling = 1;

        foreach (var row in definition.State) {
            ceiling = Math.Max(ceiling, row.Capacity ?? row.CellCeiling);
        }

        return Math.Min(ceiling, WorldPatternCapacity.MaxWord);
    }

    // $match: — the word is read at this tick through compiled row handles (no name scan) and the same per-cell
    // read every other state read uses, so an advancing attribute cell reads its live value. The verdict is 1 or 0
    // and nothing else: every source fits the word buffer, and a board origin that names no cell reads the empty
    // word, which the pattern decides like any other.
    private long[] m_patternWord = [];

    private long ReadPatternFact(CompiledWorldOperand operand, ulong tick) {
        if (!m_patterns.TryGet(name: operand.Pattern!, pattern: out var pattern)) {
            throw new InvalidOperationException($"pattern operand '{operand.Pattern}' outlived the compiled rules");
        }

        if (!WorldStateReader.TryReadHandle(definition: m_definition, catalog: m_definition.StateCatalog, handle: operand.StateHandle, key: null, tick: tick, row: out var row, rawValue: out _, text: out _)) {
            throw new InvalidOperationException($"pattern operand over '{operand.Row}' outlived its compiled row handle");
        }

        Span<long> word = m_patternWord;
        var length = 0;

        if (operand.Board is { } query) {
            if (row.Board is null) {
                return 0L;
            }

            Span<long> values = stackalloc long[query.Topology.CellCount];
            WorldBoardQueries.Read(row, query.Topology, values);
            var key = ResolveOperandKey(key: operand.Key, keyFrom: operand.KeyFrom, tick: tick);

            if (key is not null && query.Topology.TryCell(key, out var origin)) {
                var cell = origin;

                for (var distance = 1; distance < query.Topology.CellCount; distance++) {
                    cell = query.Topology.Neighbour(cell, query.Direction);

                    if (cell < 0 || cell == origin) {
                        break;
                    }

                    word[length++] = values[cell];
                }
            }
        } else {
            var source = row;

            if (row.Zone is not null && !WorldStateReader.TryReadHandle(definition: m_definition, catalog: m_definition.StateCatalog, handle: operand.FilterHandle, key: null, tick: tick, row: out source, rawValue: out _, text: out _)) {
                throw new InvalidOperationException($"pattern attribute '{operand.FilterRow}' outlived its compiled row handle");
            }

            foreach (var cell in (row.Cells ?? [])) {
                WorldStateReader.ReadCell(row: source, key: cell.Key.Value, tick: tick, rawValue: out var raw, text: out _);
                word[length++] = raw ?? 0L;
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
