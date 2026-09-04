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
        if (query.Kind == WorldBoardQueryKind.Mask) {
            var mask = 0L;
            for (var ordinal = 0; ordinal < topology.CellCount && ordinal < WorldBoardMask.MaxCells; ordinal++) {
                if (values[ordinal] >= query.Value && values[ordinal] <= query.Upper) {
                    mask |= 1L << ordinal;
                }
            }
            return mask;
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

    /// <summary>Moves every set bit of a cell mask to its neighbour in the query's direction, dropping a bit whose
    /// cell has no neighbour that way.</summary>
    /// <param name="query">The compiled topology and direction.</param>
    /// <param name="mask">Bit c set for cell ordinal c.</param>
    /// <returns>The shifted mask.</returns>
    public static long ShiftMask(CompiledWorldBoardQuery query, long mask) {
        var topology = query.Topology;
        var bits = (ulong)mask;
        var shifted = 0UL;
        while (bits != 0UL) {
            var cell = System.Numerics.BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1UL;
            if (cell >= topology.CellCount) {
                continue;
            }
            var neighbour = topology.Neighbour(cell, query.Direction);
            if (neighbour >= 0 && neighbour < WorldBoardMask.MaxCells) {
                shifted |= 1UL << neighbour;
            }
        }
        return (long)shifted;
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

    // Dijkstra over a binary heap keyed (distance, cell ordinal): the same settle order as a linear scan (least
    // distance, lowest ordinal on ties), at O((V + E) log V) instead of O(V²) per query. Stale heap entries are
    // skipped on pop; the visit budget counts settled cells exactly as before.
    private static long PathCost(CompiledWorldBoardQuery query, ReadOnlySpan<long> costs, int source) {
        var topology = query.Topology;
        var count = topology.CellCount;
        var capacity = count * (topology.DirectionCount + 1);
        var distancePool = System.Buffers.ArrayPool<long>.Shared.Rent(count + capacity);
        var cellPool = System.Buffers.ArrayPool<int>.Shared.Rent(capacity);
        var settledPool = System.Buffers.ArrayPool<byte>.Shared.Rent(count);
        try {
            var distances = distancePool.AsSpan(0, count);
            var heapDistance = distancePool.AsSpan(count, capacity);
            var heapCell = cellPool.AsSpan(0, capacity);
            var settled = settledPool.AsSpan(0, count);
            distances.Fill(long.MaxValue);
            settled.Clear();
            distances[source] = 0;
            var size = 0;
            Push(heapDistance, heapCell, ref size, 0, source);
            for (var visited = 0; visited <= query.MaxVisits; visited++) {
                var best = -1;
                var distance = long.MaxValue;
                while (size > 0) {
                    Pop(heapDistance, heapCell, ref size, out var candidateDistance, out var candidate);
                    if (settled[candidate] != 0 || candidateDistance != distances[candidate]) {
                        continue;
                    }
                    best = candidate;
                    distance = candidateDistance;
                    break;
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
                    var relaxed = distance + costs[neighbour];
                    if (relaxed < distances[neighbour]) {
                        distances[neighbour] = relaxed;
                        Push(heapDistance, heapCell, ref size, relaxed, neighbour);
                    }
                }
            }
            return -2;
        } finally {
            System.Buffers.ArrayPool<long>.Shared.Return(distancePool);
            System.Buffers.ArrayPool<int>.Shared.Return(cellPool);
            System.Buffers.ArrayPool<byte>.Shared.Return(settledPool);
        }
    }

    private static bool Before(long distanceA, int cellA, long distanceB, int cellB) =>
        distanceA < distanceB || (distanceA == distanceB && cellA < cellB);

    private static void Push(Span<long> distance, Span<int> cell, ref int size, long value, int ordinal) {
        var index = size++;
        while (index > 0) {
            var parent = (index - 1) / 2;
            if (!Before(value, ordinal, distance[parent], cell[parent])) {
                break;
            }
            distance[index] = distance[parent];
            cell[index] = cell[parent];
            index = parent;
        }
        distance[index] = value;
        cell[index] = ordinal;
    }

    private static void Pop(Span<long> distance, Span<int> cell, ref int size, out long value, out int ordinal) {
        value = distance[0];
        ordinal = cell[0];
        size--;
        if (size == 0) {
            return;
        }
        var lastDistance = distance[size];
        var lastCell = cell[size];
        var index = 0;
        while (true) {
            var left = (2 * index) + 1;
            if (left >= size) {
                break;
            }
            var right = left + 1;
            var child = (right < size && Before(distance[right], cell[right], distance[left], cell[left])) ? right : left;
            if (!Before(distance[child], cell[child], lastDistance, lastCell)) {
                break;
            }
            distance[index] = distance[child];
            cell[index] = cell[child];
            index = child;
        }
        distance[index] = lastDistance;
        cell[index] = lastCell;
    }

}

public sealed partial class WorldServer {
    private long ReadBoardFact(CompiledWorldOperand operand, ulong tick) {
        var query = operand.Board!;
        if (query.Kind == WorldBoardQueryKind.CellOf) {
            var index = ResolveBodyRef(bodyRef: operand.BodyA!.Value, tick: tick);
            return Body(index: index) is { } body && query.Topology.TryCellOf(position: body.FixedPosition, cell: out var cell) ? cell : -1;
        }
        var row = WorldDefinitionRows.FindStateRow(m_definition.State, operand.Row!);
        if (row?.Board is null) {
            return -1;
        }
        if (query.Kind == WorldBoardQueryKind.Offset) {
            var originKey = ResolveOperandKey(operand.Key, operand.KeyFrom, tick);
            var origin = originKey is not null && query.Topology.TryCell(originKey, out var originCell) ? originCell : -1;
            return origin >= 0 && query.Topology.TryOffset(origin, query.Dx, query.Dz, out var offset) ? offset : -1;
        }
        var values = BoardScratch(query.Topology.CellCount);
        WorldBoardQueries.Read(row, query.Topology, values);
        var key = ResolveOperandKey(operand.Key, operand.KeyFrom, tick);
        var source = key is not null && query.Topology.TryCell(key, out var sourceCell) ? sourceCell : -1;
        return WorldBoardQueries.Evaluate(query, values, row.Board.Empty, source);
    }
}
