namespace Puck.World.Server;

/// <summary>Deterministic discrete queries over immutable topology and caller-owned value spans.</summary>
public static class WorldBoardQueries {
    /// <summary>Reads a board into scratch storage. Missing cells have the authored empty value.</summary>
    /// <param name="row">The validated board row.</param>
    /// <param name="topology">The compiled addressing.</param>
    /// <param name="values">Scratch storage with at least CellCount entries.</param>
    public static void Read(WorldStateRow row, CompiledWorldTopology topology, Span<long> values) {
        values[..topology.CellCount].Fill(row.Board!.Empty);
        var cells = row.Cells;
        for (var cellIndex = 0; cellIndex < (cells?.Count ?? 0); cellIndex++) {
            var cell = cells![cellIndex];
            if (topology.TryCell(cell.Key.Value, out var index)) {
                values[index] = cell.Value;
            }
        }
    }

    /// <summary>Evaluates one preflighted query. Searches never revisit the origin of a wrapped ray or line.</summary>
    /// <param name="query">The compiled query.</param>
    /// <param name="values">One value per cell.</param>
    /// <param name="empty">The unoccupied value for rays.</param>
    /// <param name="source">The source cell for neighbour, ray and path queries.</param>
    /// <returns>The result in the query's documented integer domain.</returns>
    public static long Evaluate(CompiledWorldBoardQuery query, ReadOnlySpan<long> values, long empty, int source) {
        var topology = query.Topology;
        if (query.Kind == WorldBoardQueryKind.Line) {
            return HasLine(query, values) ? 1 : 0;
        }
        if ((uint)source >= topology.CellCount) {
            return -1;
        }
        if (query.Kind == WorldBoardQueryKind.Neighbour) {
            return topology.Neighbour(source, query.Direction);
        }
        if (query.Kind == WorldBoardQueryKind.PathCost) {
            return PathCost(query, values, source);
        }
        var cell = source;
        for (var distance = 1; distance < topology.CellCount; distance++) {
            cell = topology.Neighbour(cell, query.Direction);
            if (cell < 0 || cell == source) {
                break;
            }
            if (values[cell] != empty) {
                return query.Kind == WorldBoardQueryKind.RayCell ? cell : distance;
            }
        }
        return -1;
    }

    private static bool HasLine(CompiledWorldBoardQuery query, ReadOnlySpan<long> values) {
        var topology = query.Topology;
        for (var start = 0; start < topology.CellCount; start++) {
            if (values[start] != query.Value) {
                continue;
            }
            for (var direction = 0; direction < topology.DirectionCount; direction++) {
                var cell = start;
                var matched = 1;
                while (matched < query.Length) {
                    cell = topology.Neighbour(cell, direction);
                    if (cell < 0 || cell == start || values[cell] != query.Value) {
                        break;
                    }
                    matched++;
                }
                if (matched != query.Length) {
                    continue;
                }
                if (!query.Exact) {
                    return true;
                }
                var next = topology.Neighbour(cell, direction);
                if (next == start) {
                    return true;
                }
                var previous = topology.Neighbour(start, (direction + topology.DirectionCount / 2) % topology.DirectionCount);
                if ((next < 0 || values[next] != query.Value) && (previous < 0 || values[previous] != query.Value)) {
                    return true;
                }
            }
        }
        return false;
    }

    private static long PathCost(CompiledWorldBoardQuery query, ReadOnlySpan<long> costs, int source) {
        var topology = query.Topology;
        Span<long> distances = stackalloc long[topology.CellCount];
        Span<byte> settled = stackalloc byte[topology.CellCount];
        distances.Fill(long.MaxValue);
        settled.Clear();
        distances[source] = 0;
        for (var visited = 0; visited <= query.MaxVisits; visited++) {
            var best = -1;
            var distance = long.MaxValue;
            // Ascending ordinal is the stable tie-break. Zero-cost edges are admitted; negative cells block entry.
            for (var cell = 0; cell < topology.CellCount; cell++) {
                if (settled[cell] == 0 && distances[cell] < distance) {
                    best = cell;
                    distance = distances[cell];
                }
            }
            if (best < 0 || distance > query.MaxCost) {
                return -1;
            }
            if (visited == query.MaxVisits) {
                return -2;
            }
            if (best == query.Target) {
                return distance;
            }
            settled[best] = 1;
            for (var direction = 0; direction < topology.DirectionCount; direction++) {
                var neighbour = topology.Neighbour(best, direction);
                if (neighbour < 0 || settled[neighbour] != 0 || costs[neighbour] < 0 || costs[neighbour] > query.MaxCost - distance) {
                    continue;
                }
                distances[neighbour] = Math.Min(distances[neighbour], distance + costs[neighbour]);
            }
        }
        return -2;
    }
}

public sealed partial class WorldServer {
    private long ReadBoardFact(CompiledWorldOperand operand, ulong tick) {
        var query = operand.Board!;
        var row = WorldDefinitionRows.FindStateRow(m_definition.State, operand.Row!);
        if (row?.Board is null) {
            return -1;
        }
        Span<long> values = stackalloc long[query.Topology.CellCount];
        WorldBoardQueries.Read(row, query.Topology, values);
        var key = ResolveOperandKey(operand.Key, operand.KeyFrom, tick);
        var source = key is not null && query.Topology.TryCell(key, out var cell) ? cell : -1;
        return WorldBoardQueries.Evaluate(query, values, row.Board.Empty, source);
    }
}
