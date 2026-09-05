namespace Puck.State;

/// <summary>Deterministic discrete queries over immutable topology and caller-owned value spans.</summary>
public static class BoardQueries {
    /// <summary>Reads a board into scratch storage. Missing cells have the authored empty value.</summary>
    /// <param name="row">The validated board row.</param>
    /// <param name="topology">The compiled addressing.</param>
    /// <param name="values">Scratch storage with at least CellCount entries.</param>
    public static void Read(StateRow row, CompiledTopology topology, Span<long> values) {
        values[..topology.CellCount].Fill(((StateDomain.CellsOf)row.EffectiveDomain).Empty);
        var cells = row.Cells;
        for (var cellIndex = 0; cellIndex < (cells?.Count ?? 0); cellIndex++) {
            var cell = cells![cellIndex];
            if (topology.TryCell(cell.Key.Value, out var index)) {
                values[index] = cell.Value;
            }
        }
    }

    /// <summary>Evaluates one preflighted query. A ray search never revisits its own wrapped origin.</summary>
    /// <param name="query">The compiled query.</param>
    /// <param name="values">One value per cell.</param>
    /// <param name="empty">The unoccupied value for a ray.</param>
    /// <param name="source">The source cell for neighbour, ray and path queries.</param>
    /// <param name="dynamicTarget">The live-resolved destination ordinal for a <see cref="BoardPathCostQuery"/>
    /// whose <see cref="BoardPathCostQuery.TargetFrom"/> is set; ignored otherwise, and irrelevant for every other
    /// query kind. The caller resolves it (a tick-dependent state read) before this otherwise tick-agnostic
    /// evaluation runs.</param>
    /// <returns>The result in the query's documented integer domain.</returns>
    public static long Evaluate(BoardQuery query, ReadOnlySpan<long> values, long empty, int source, int dynamicTarget = 0) {
        var topology = query.Topology;
        if (query is BoardCanonicalQuery) {
            return CanonicalFingerprint(topology, values);
        }
        if (query is BoardMaskQuery mask) {
            var result = 0L;
            for (var ordinal = 0; ordinal < topology.CellCount && ordinal < BoardMask.MaxCells; ordinal++) {
                if (values[ordinal] >= mask.Lower && values[ordinal] <= mask.Upper) {
                    result |= 1L << ordinal;
                }
            }
            return result;
        }
        if ((uint)source >= topology.CellCount) {
            return -1;
        }
        if (query is BoardNeighbourQuery neighbour) {
            return topology.Neighbour(source, neighbour.Direction);
        }
        if (query is BoardPathCostQuery pathCost) {
            return PathCost(pathCost, values, source, (pathCost.TargetFrom is null) ? pathCost.Target : dynamicTarget);
        }
        if (query is BoardAttacksQuery attacks) {
            var directions = attacks.Directions;
            for (var directionIndex = 0; directionIndex < directions.Length; directionIndex++) {
                var direction = directions[directionIndex];
                var rayCell = source;
                for (var distance = 1; distance < topology.CellCount; distance++) {
                    rayCell = topology.Neighbour(rayCell, direction);
                    if (rayCell < 0 || rayCell == source) {
                        break;
                    }
                    if (values[rayCell] != empty) {
                        if (values[rayCell] >= attacks.Lower && values[rayCell] <= attacks.Upper) {
                            return 1;
                        }
                        break;
                    }
                }
            }
            return 0;
        }
        throw new InvalidOperationException($"unhandled board query kind {query.Kind}");
    }

    /// <summary>Reads the ray from the origin (exclusive) in one direction, stopping at the edge or on return to the
    /// origin; an origin that names no cell is the empty word.</summary>
    /// <param name="topology">The compiled addressing.</param>
    /// <param name="values">One value per cell.</param>
    /// <param name="origin">The origin cell, or -1 for none.</param>
    /// <param name="direction">The direction ordinal.</param>
    /// <param name="word">The word buffer, at least CellCount long.</param>
    /// <returns>The ray's length.</returns>
    public static int ReadRay(CompiledTopology topology, ReadOnlySpan<long> values, int origin, int direction, Span<long> word) {
        var length = 0;

        if (origin < 0) {
            return 0;
        }

        var cell = origin;

        for (var distance = 1; distance < topology.CellCount; distance++) {
            cell = topology.Neighbour(cell, direction);

            if (cell < 0 || cell == origin) {
                break;
            }

            word[length++] = values[cell];
        }

        return length;
    }

    // The least FNV-1a fingerprint of the board's values over every element: the same number for every board in
    // one symmetry orbit, so a ring of fingerprints answers repetition up to symmetry.
    private static long CanonicalFingerprint(CompiledTopology topology, ReadOnlySpan<long> values) {
        var least = ulong.MaxValue;
        for (var element = 0; element < topology.ElementCount; element++) {
            // The image board holds value[c] at image(c). The fold is a commutative sum of per-pair mixes, so it
            // depends only on the set of (image ordinal, value) pairs and not on the order cells are walked.
            var hash = 0UL;
            for (var cell = 0; cell < topology.CellCount; cell++) {
                var pair = (((ulong)topology.Image(element, cell)) * 0x9E3779B97F4A7C15UL) ^ ((ulong)values[cell]);
                pair *= 0xBF58476D1CE4E5B9UL;
                pair ^= pair >> 31;
                pair *= 0x94D049BB133111EBUL;
                pair ^= pair >> 29;
                hash += pair;
            }
            least = Math.Min(least, hash);
        }
        return (long)least;
    }

    /// <summary>Carries every set bit of a cell mask through a point-group element.</summary>
    /// <param name="topology">The compiled topology.</param>
    /// <param name="element">The element ordinal.</param>
    /// <param name="mask">Bit c set for cell ordinal c.</param>
    /// <returns>The image mask.</returns>
    public static long ImageOfMask(CompiledTopology topology, int element, long mask) {
        var bits = (ulong)mask;
        var image = 0UL;
        while (bits != 0UL) {
            var cell = System.Numerics.BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1UL;
            if (cell < topology.CellCount) {
                var carried = topology.Image(element, cell);
                if (carried < BoardMask.MaxCells) {
                    image |= 1UL << carried;
                }
            }
        }
        return (long)image;
    }

    /// <summary>Moves every set bit of a cell mask to its neighbour in the query's direction, dropping a bit whose
    /// cell has no neighbour that way.</summary>
    /// <param name="query">The compiled topology and direction.</param>
    /// <param name="mask">Bit c set for cell ordinal c.</param>
    /// <returns>The shifted mask.</returns>
    public static long ShiftMask(BoardNeighbourQuery query, long mask) {
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
            if (neighbour >= 0 && neighbour < BoardMask.MaxCells) {
                shifted |= 1UL << neighbour;
            }
        }
        return (long)shifted;
    }

    // Dijkstra over a binary heap keyed (distance, cell ordinal): the same settle order as a linear scan (least
    // distance, lowest ordinal on ties), at O((V + E) log V) instead of O(V²) per query. Stale heap entries are
    // skipped on pop; the visit budget counts settled cells.
    private static long PathCost(BoardPathCostQuery query, ReadOnlySpan<long> costs, int source, int target) {
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
                if (best == target) {
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
