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
        var words = BitWords.WordsFor(members: count);
        using var visited = scratch.Rent<ulong>(words);
        using var seen = scratch.Rent<ulong>(words);
        using var stack = scratch.Rent<int>(count);
        var flood = new BoardFlood(query.Topology, values, (query.Lower, query.Upper), boundary, visited.Span[..words], seen.Span[..words], stack.Span, query.MaxVisits);

        if (enclosed) {
            return flood.EnclosedBeside(source, default);
        }
        if (!placement && !flood.CanStart(source)) {
            return 0;
        }
        var members = flood.Visit(source, query.Kind != BoardQueryKind.Component, out var free);
        return ((members < 0 || query.Kind == BoardQueryKind.Component) ? members : free);
    }

    private static bool InRange(long value, (long Lower, long Upper) range) =>
        value >= range.Lower && value <= range.Upper;

    // A flood over word bitsets (BitWords): `visited` marks every member settled, and the origin of an enclosure test,
    // across groups, so a group reached from two sides floods once; `seen` marks the boundary cells one group counted,
    // cleared when the next group starts, so a liberty beside two members counts once. The stack holds a group's members
    // in the order it settles them, which a caller collecting a group reads back.
    private ref struct BoardFlood(CompiledTopology topology, ReadOnlySpan<long> values, (long, long) members,
        (long, long) boundary, Span<ulong> visited, Span<ulong> seen, Span<int> stack, int budget) {
        private readonly ReadOnlySpan<long> m_values = values;
        private readonly Span<ulong> m_visited = visited;
        private readonly Span<ulong> m_seen = seen;
        private readonly Span<int> m_stack = stack;
        private int m_remaining = budget;

        public bool CanStart(int cell) => cell >= 0 && !BitWords.Contains(words: m_visited, index: cell) && InRange(m_values[cell], members);

        // The members of every group beside the origin with no boundary cell, counted, and set in `cleared` when it is not
        // empty; negative when the visit budget ran out.
        public int EnclosedBeside(int origin, scoped Span<ulong> cleared) {
            BitWords.Add(words: m_visited, index: origin);
            var total = 0;

            for (var direction = 0; direction < topology.DirectionCount; direction++) {
                var start = topology.Neighbour(cell: origin, direction: direction);
                if (!CanStart(start)) {
                    continue;
                }
                var size = Visit(start, true, out var free);
                if (size < 0) {
                    return size;
                }
                if (free == 0) {
                    total += size;
                    if (!cleared.IsEmpty) {
                        foreach (var cell in m_stack[..size]) { BitWords.Add(words: cleared, index: cell); }
                    }
                }
            }
            return total;
        }

        // Floods the group holding `start`, leaving its members in the stack's first `settled` entries in the order it
        // settled them, and counts its boundary cells in `free` when `countsBoundary`.
        public int Visit(int start, bool countsBoundary, out int free) {
            var pending = 1;
            var settled = 0;
            free = 0;
            if (countsBoundary) { m_seen.Clear(); }
            BitWords.Add(words: m_visited, index: start);
            m_stack[0] = start;

            // The stack grows from the front as members are pushed and is read from `settled` up: a member, once
            // settled, stays at its settled position, so the group's members end in m_stack[0..settled).
            while (pending > settled) {
                var cell = m_stack[settled];
                if (m_remaining-- == 0) {
                    return -2;
                }
                settled++;

                for (var direction = 0; direction < topology.DirectionCount; direction++) {
                    var next = topology.Neighbour(cell: cell, direction: direction);
                    if (next < 0 || BitWords.Contains(words: m_visited, index: next)) {
                        continue;
                    }
                    var value = m_values[next];
                    if (InRange(value, members)) {
                        BitWords.Add(words: m_visited, index: next);
                        m_stack[pending++] = next;
                    } else if (countsBoundary && !BitWords.Contains(words: m_seen, index: next) && InRange(value, boundary)) {
                        BitWords.Add(words: m_seen, index: next);
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

    /// <summary>Finds groups beside a filled cell that have no boundary cells in the given inclusive range, and sets
    /// their members in <paramref name="cleared"/>, a cell a bit (<see cref="BitWords"/>), so a caller reads them in
    /// ascending order without sorting.</summary>
    /// <param name="topology">The compiled topology.</param>
    /// <param name="values">One value per cell.</param>
    /// <param name="source">The filled cell the groups lie beside.</param>
    /// <param name="lower">The inclusive lower bound of a group cell's value.</param>
    /// <param name="upper">The inclusive upper bound of a group cell's value.</param>
    /// <param name="boundaryLower">The inclusive lower bound of a boundary cell's value.</param>
    /// <param name="boundaryUpper">The inclusive upper bound of a boundary cell's value.</param>
    /// <param name="cleared">The cleared set, at least <c>BitWords.WordsFor(topology.CellCount)</c> words, cleared.</param>
    /// <param name="scratch">The working storage the flood borrows its bitsets and stack from.</param>
    /// <returns>The members set.</returns>
    public static int FindEnclosed(CompiledTopology topology, ReadOnlySpan<long> values, int source, long lower, long upper,
        long boundaryLower, long boundaryUpper, Span<ulong> cleared, ArenaScratch scratch) {
        ArgumentNullException.ThrowIfNull(argument: topology);
        ArgumentNullException.ThrowIfNull(argument: scratch);

        var count = topology.CellCount;
        var words = BitWords.WordsFor(members: count);
        using var visited = scratch.Rent<ulong>(words);
        using var seen = scratch.Rent<ulong>(words);
        using var stack = scratch.Rent<int>(count);
        var flood = new BoardFlood(topology, values, (lower, upper), (boundaryLower, boundaryUpper), visited.Span[..words], seen.Span[..words], stack.Span, count);
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
