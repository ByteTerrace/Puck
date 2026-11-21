using Puck.Abstractions.Collections;
using Puck.Maths;

namespace Puck.State;

/// <summary>Deterministic discrete queries over immutable topology and caller-owned value spans.</summary>
public static class BoardQueries {
    // The least fingerprint of the board's values over every element: the same number for every board in one symmetry
    // orbit, so a ring of fingerprints answers repetition up to symmetry. Element 0 is the identity, so this is never
    // more than the board's own fingerprint.
    private static long CanonicalFingerprint(CompiledTopology topology, ReadOnlySpan<long> values) {
        var least = ulong.MaxValue;

        for (var element = 0; (element < topology.ElementCount); element++) {
            // The image board holds value[c] at image(c).
            var digest = MultisetDigest.Empty;

            for (var cell = 0; (cell < topology.CellCount); cell++) {
                digest = digest.Add(element: Pair(
                    ordinal: topology.Image(
                        cell: cell,
                        element: element
                    ),
                    value: values[cell]
                ));
            }
            least = Math.Min(
                val1: least,
                val2: digest.Value
            );
        }
        return ((long)least);
    }
    // The board as it stands, through the identity: the canonical fold's identity term.
    private static long Fingerprint(CompiledTopology topology, ReadOnlySpan<long> values) {
        var digest = MultisetDigest.Empty;

        for (var cell = 0; (cell < topology.CellCount); cell++) {
            digest = digest.Add(element: Pair(
                ordinal: cell,
                value: values[cell]
            ));
        }
        return ((long)digest.Value);
    }
    // One (ordinal, value) pair as one element word. A fingerprint is the multiset digest of its cells' pairs, so it
    // depends only on the set of pairs and not on the order cells are walked.
    private static ulong Pair(int ordinal, long value) =>
        ((((ulong)ordinal) * SplitMix64.GoldenGamma) ^ ((ulong)value));
    private static long Component(BoardComponentQuery query, ReadOnlySpan<long> values, int source, ArenaScratch scratch) {
        var enclosed = query.Kind == BoardQueryKind.EnclosedAt;
        var placement = enclosed || query.Kind == BoardQueryKind.BoundaryAt;
        var boundary = (query.BoundaryLower, query.BoundaryUpper);
        if (placement && !InRange(values[source], boundary)) {
            return -1;
        }

        var count = query.Topology.CellCount;
        using var marks = scratch.Rent<int>(count);
        using var stack = scratch.Rent<int>(count);
        var flood = new BoardFlood(query.Topology, values, (query.Lower, query.Upper), boundary, marks.Span, stack.Span, query.MaxVisits);

        if (enclosed) {
            return flood.EnclosedBeside(source, default);
        }
        if (!placement && !flood.CanStart(source)) {
            return 0;
        }
        var members = flood.Visit(source, query.Kind == BoardQueryKind.Component ? 0 : 1, default, out var free);
        return ((members < 0 || query.Kind == BoardQueryKind.Component) ? members : free);
    }

    private static bool InRange(long value, (long Lower, long Upper) range) =>
        value >= range.Lower && value <= range.Upper;

    // Members stay marked across groups. A positive stamp makes boundary marks local to one group.
    private ref struct BoardFlood(CompiledTopology topology, ReadOnlySpan<long> values, (long, long) members,
        (long, long) boundary, Span<int> marks, Span<int> stack, int budget) {
        private readonly ReadOnlySpan<long> m_values = values;
        private readonly Span<int> m_marks = marks;
        private readonly Span<int> m_stack = stack;
        private int m_remaining = budget;

        public bool CanStart(int cell) => cell >= 0 && m_marks[cell] >= 0 && InRange(m_values[cell], members);

        public int EnclosedBeside(int origin, scoped Span<int> cleared) {
            m_marks[origin] = -1;
            var total = 0;

            for (var direction = 0; direction < topology.DirectionCount; direction++) {
                var start = topology.Neighbour(cell: origin, direction: direction);
                if (!CanStart(start)) {
                    continue;
                }
                var size = Visit(start, direction + 1, cleared.IsEmpty ? default : cleared[total..], out var free);
                if (size < 0) {
                    return size;
                }
                if (free == 0) {
                    total += size;
                }
            }
            return total;
        }

        public int Visit(int start, int stamp, scoped Span<int> ordinals, out int free) {
            var pending = 1;
            var settled = 0;
            free = 0;
            m_marks[start] = -1;
            m_stack[0] = start;

            while (pending > 0) {
                var cell = m_stack[--pending];
                if (m_remaining-- == 0) {
                    return -2;
                }
                if (!ordinals.IsEmpty) {
                    ordinals[settled] = cell;
                }
                settled++;

                for (var direction = 0; direction < topology.DirectionCount; direction++) {
                    var next = topology.Neighbour(cell: cell, direction: direction);
                    if (next < 0 || m_marks[next] < 0) {
                        continue;
                    }
                    var value = m_values[next];
                    if (InRange(value, members)) {
                        m_marks[next] = -1;
                        m_stack[pending++] = next;
                    } else if (stamp != 0 && m_marks[next] != stamp && InRange(value, boundary)) {
                        m_marks[next] = stamp;
                        free++;
                    }
                }
            }
            return settled;
        }
    }
    // Dijkstra over a binary heap keyed (distance, cell ordinal): the same settle order as a linear scan (least
    // distance, lowest ordinal on ties), at O((V + E) log V) instead of O(V²) per query. Stale heap entries are
    // skipped on pop; the visit budget counts settled cells.
    private static long PathCost(BoardPathCostQuery query, ReadOnlySpan<long> costs, int source, int target, ArenaScratch scratch) {
        var topology = query.Topology;
        var count = topology.CellCount;
        var capacity = (count * (topology.DirectionCount + 1));
        using var distanceLease = scratch.Rent<long>(length: count);
        using var heapLease = scratch.Rent<PathEntry>(length: capacity);
        using var settledLease = scratch.Rent<byte>(length: count);

        var distances = distanceLease.Span;
        var heap = heapLease.Span;
        var settled = settledLease.Span;
        var order = default(PathEntryOrder);

        distances.Fill(value: long.MaxValue);
        distances[source] = 0;
        var size = 0;

        BinaryHeap.Push(
            count: ref size,
            heap: heap,
            item: new PathEntry(Cell: source, Distance: 0),
            order: order
        );
        for (var visited = 0; (visited <= query.MaxVisits); visited++) {
            var best = -1;
            var distance = long.MaxValue;

            while (size > 0) {
                var candidate = BinaryHeap.Pop(
                    count: ref size,
                    heap: heap,
                    order: order
                );

                if (
                    (settled[candidate.Cell] != 0) ||
                    (candidate.Distance != distances[candidate.Cell])
                ) {
                    continue;
                }
                best = candidate.Cell;
                distance = candidate.Distance;
                break;
            }
            if (
                (best < 0) ||
                (distance > query.MaxCost)
            ) {
                return -1;
            }
            if (visited == query.MaxVisits) {
                return -2;
            }
            if (best == target) {
                return distance;
            }
            settled[best] = 1;
            for (var direction = 0; (direction < topology.DirectionCount); direction++) {
                var neighbour = topology.Neighbour(
                    cell: best,
                    direction: direction
                );

                if (
                    (neighbour < 0) ||
                    (settled[neighbour] != 0) ||
                    (costs[neighbour] < 0) ||
                    (costs[neighbour] > (query.MaxCost - distance))
                ) {
                    continue;
                }
                var relaxed = (distance + costs[neighbour]);

                if (relaxed < distances[neighbour]) {
                    distances[neighbour] = relaxed;
                    BinaryHeap.Push(
                        count: ref size,
                        heap: heap,
                        item: new PathEntry(Cell: neighbour, Distance: relaxed),
                        order: order
                    );
                }
            }
        }
        return -2;
    }
    // One path-cost frontier entry: a cell reached at a distance, possibly stale once a cheaper entry for it exists.
    private readonly record struct PathEntry(long Distance, int Cell);
    // Least distance first, lowest cell ordinal on ties: a total order, so the settle order is the linear scan's.
    private readonly struct PathEntryOrder : IComparer<PathEntry> {
        public int Compare(PathEntry x, PathEntry y) => LexicographicOrder.Compare(
            leftPrimary: x.Distance,
            leftSecondary: x.Cell,
            rightPrimary: y.Distance,
            rightSecondary: y.Cell
        );
    }

    /// <summary>Finds groups beside a filled cell that have no boundary cells in the given inclusive range.
    /// Writes their ordinals into a buffer of at least the topology's cell count and returns the filled length.</summary>
    public static int FindEnclosed(CompiledTopology topology, ReadOnlySpan<long> values, int source, long lower, long upper,
        long boundaryLower, long boundaryUpper, Span<int> cleared, ArenaScratch scratch) {
        var count = topology.CellCount;
        using var marks = scratch.Rent<int>(count);
        using var stack = scratch.Rent<int>(count);
        var flood = new BoardFlood(topology, values, (lower, upper), (boundaryLower, boundaryUpper), marks.Span, stack.Span, count);
        return flood.EnclosedBeside(source, cleared);
    }

    internal static long Attacks(BoardAttacksQuery query, ReadOnlySpan<long> values, long empty, int source, StateArena? arena = null, int rowOrdinal = -1) {
        if ((uint)source >= query.Topology.CellCount) {
            return -1;
        }
        foreach (var direction in query.Directions) {
            foreach (var cell in query.Topology.Ray(origin: source, direction: direction)) {
                long value;
                if (arena is null) {
                    value = values[cell];
                } else {
                    _ = arena.TryReadBoardCell(rowOrdinal, cell, out value);
                }
                if (value != empty) {
                    if (InRange(value, (query.Lower, query.Upper))) {
                        return 1;
                    }
                    break;
                }
            }
        }
        return 0;
    }
    /// <summary>Evaluates one preflighted query. A ray search never revisits its own wrapped origin.</summary>
    /// <param name="query">The compiled query.</param>
    /// <param name="values">One value per cell.</param>
    /// <param name="empty">The unoccupied value for a ray.</param>
    /// <param name="source">The source cell for neighbour, ray and path queries.</param>
    /// <param name="dynamicTarget">The live-resolved destination ordinal for a <see cref="BoardPathCostQuery"/>
    /// or <see cref="BoardJumpDistanceQuery"/> whose TargetIsLive is set; ignored otherwise. The caller
    /// resolves it (a tick-dependent state read) before this otherwise tick-agnostic
    /// evaluation runs.</param>
    /// <param name="scratch">The working storage a flood or search borrows its marks, stacks and queues from, normally
    /// the owning arena's; a throwaway one is made when it is <see langword="null"/>.</param>
    /// <returns>The result in the query's documented integer domain.</returns>
    public static long Evaluate(BoardQuery query, ReadOnlySpan<long> values, long empty, int source, int dynamicTarget = 0, ArenaScratch? scratch = null) {
        var topology = query.Topology;

        if (query is BoardCanonicalQuery) {
            return CanonicalFingerprint(
                topology: topology,
                values: values
            );
        }
        if (query is BoardFingerprintQuery) {
            return Fingerprint(
                topology: topology,
                values: values
            );
        }
        if (query is BoardMaskQuery mask) {
            var result = 0L;

            for (var ordinal = 0; ((ordinal < topology.CellCount) && (ordinal < BoardMask.MaxCells)); ordinal++) {
                if (
                    (values[ordinal] >= mask.Lower) &&
                    (values[ordinal] <= mask.Upper)
                ) {
                    result |= (1L << ordinal);
                }
            }
            return result;
        }
        if (((uint)source) >= topology.CellCount) {
            return -1;
        }
        if (query is BoardNeighbourQuery neighbour) {
            return topology.Neighbour(
                cell: source,
                direction: neighbour.Direction
            );
        }
        scratch ??= new ArenaScratch();
        if (query is BoardComponentQuery component) {
            return Component(
                query: component,
                scratch: scratch,
                source: source,
                values: values
            );
        }
        if (query is BoardPathCostQuery pathCost) {
            return PathCost(
                pathCost,
                values,
                source,
                (pathCost.TargetIsLive
                ? dynamicTarget
                : pathCost.Target),
                scratch
            );
        }
        if (query is BoardJumpDistanceQuery jump) {
            return jump.Evaluate(values, empty, source, (jump.TargetIsLive ? dynamicTarget : jump.Target), scratch);
        }
        if (query is BoardAttacksQuery attacks) {
            return Attacks(attacks, values, empty, source);
        }
        throw new InvalidOperationException(message: $"unhandled board query kind {query.Kind}");
    }
    /// <summary>Returns the union of a mask and every repeated shift of it in the query's direction until no bit
    /// has a neighbour that way: a wrapped topology's ray is the whole cycle each seed bit lies on.</summary>
    /// <param name="query">The compiled topology and direction.</param>
    /// <param name="mask">The seed mask, one bit per cell ordinal.</param>
    public static long RayMask(BoardNeighbourQuery query, long mask) {
        ArgumentNullException.ThrowIfNull(query);
        var filled = mask;
        var frontier = mask;
        // Every step adds at least one new bit or stops, so at most one step per cell.
        for (var step = 0; ((step < query.Topology.CellCount) && (frontier != 0L)); step++) {
            frontier = ShiftMask(
                mask: frontier,
                query: query
            ) & ~filled;
            filled |= frontier;
        }
        return filled;
    }
    /// <summary>Carries every set bit of a cell mask through a point-group element.</summary>
    /// <param name="topology">The compiled topology.</param>
    /// <param name="element">The element ordinal.</param>
    /// <param name="mask">Bit c set for cell ordinal c.</param>
    /// <returns>The image mask.</returns>
    public static long ImageOfMask(CompiledTopology topology, int element, long mask) {
        if (mask == 0L) {
            return 0L;
        }
        if (topology.TryImageMask(
            element: element,
            image: out var image,
            mask: ((ulong)mask)
        )) {
            return ((long)image);
        }
        var remaining = ((ulong)mask);
        var fallback = 0UL;

        while (remaining != 0UL) {
            var cell = System.Numerics.BitOperations.TrailingZeroCount(value: remaining);

            remaining &= (remaining - 1UL);
            if (cell < topology.CellCount) {
                var carried = topology.Image(
                    cell: cell,
                    element: element
                );

                if (carried < BoardMask.MaxCells) {
                    fallback |= (1UL << carried);
                }
            }
        }
        return ((long)fallback);
    }
    /// <summary>Reads a board into scratch storage. Missing cells have the authored empty value.</summary>
    /// <param name="row">The validated board row.</param>
    /// <param name="topology">The compiled addressing.</param>
    /// <param name="values">Scratch storage with at least CellCount entries.</param>
    public static void Read(StateRow row, CompiledTopology topology, Span<long> values) {
        values[..topology.CellCount].Fill(value: ((StateDomain.CellsOf)row.EffectiveDomain).Empty);
        var cells = row.Cells;

        for (var cellIndex = 0; (cellIndex < (cells?.Count ?? 0)); cellIndex++) {
            var cell = cells![cellIndex];

            if (
                (cell.Value.Kind is (CellKind.Bool or CellKind.Fixed or CellKind.Int)) &&
                topology.TryCell(
                    cell.Key.Value,
                    out var index
                )
            ) {
                values[index] = cell.Value.Raw;
            }
        }
    }
    /// <summary>Moves every set bit of a cell mask to its neighbour in the query's direction, dropping a bit whose
    /// cell has no neighbour that way.</summary>
    /// <param name="query">The compiled topology and direction.</param>
    /// <param name="mask">Bit c set for cell ordinal c.</param>
    /// <returns>The shifted mask.</returns>
    public static long ShiftMask(BoardNeighbourQuery query, long mask) {
        if (mask == 0L) {
            return 0L;
        }
        var topology = query.Topology;

        if (topology.TryShiftMask(
            direction: query.Direction,
            mask: ((ulong)mask),
            shifted: out var shifted
        )) {
            return ((long)shifted);
        }
        var remaining = ((ulong)mask);
        var fallback = 0UL;

        while (remaining != 0UL) {
            var cell = System.Numerics.BitOperations.TrailingZeroCount(value: remaining);

            remaining &= (remaining - 1UL);
            if (cell >= topology.CellCount) {
                continue;
            }
            var neighbour = topology.Neighbour(
                cell: cell,
                direction: query.Direction
            );

            if (
                (neighbour >= 0) &&
                (neighbour < BoardMask.MaxCells)
            ) {
                fallback |= (1UL << neighbour);
            }
        }
        return ((long)fallback);
    }
}
