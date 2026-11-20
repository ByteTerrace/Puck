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
        ((((ulong)ordinal) * 0x9E3779B97F4A7C15UL) ^ ((ulong)value));
    // A flood along the topology's directions from the key cell over in-range cells; every settled member is one
    // visit against the budget. Boundary are counted once each through a second mark, so a cell touching the group
    // twice is one boundary cell.
    private static long Component(BoardComponentQuery query, ReadOnlySpan<long> values, int source, ArenaScratch scratch) {
        var topology = query.Topology;
        var count = topology.CellCount;

        if (query.Kind is BoardQueryKind.BoundaryAt or BoardQueryKind.EnclosedAt) {
            return Placement(
                query: query,
                scratch: scratch,
                source: source,
                values: values
            );
        }
        if (
            (values[source] < query.Lower) ||
            (values[source] > query.Upper)
        ) {
            return 0;
        }
        using var markLease = scratch.Rent<byte>(length: count);
        using var stackLease = scratch.Rent<int>(length: count);

        var mark = markLease.Span;
        var stack = stackLease.Span;

        var boundary = (query.Kind == BoardQueryKind.Boundary);
        var size = 0;
        var members = 0L;
        var free = 0L;

        mark[source] = 1;
        stack[size++] = source;
        while (size > 0) {
            var cell = stack[--size];

            if (members == query.MaxVisits) {
                return -2;
            }
            members++;
            for (var direction = 0; (direction < topology.DirectionCount); direction++) {
                var next = topology.Neighbour(
                    cell: cell,
                    direction: direction
                );

                if (
                    (next < 0) ||
                    (mark[next] != 0)
                ) {
                    continue;
                }
                if (
                    (values[next] >= query.Lower) &&
                    (values[next] <= query.Upper)
                ) {
                    mark[next] = 1;
                    stack[size++] = next;
                } else if (
                    boundary &&
                    (values[next] >= query.BoundaryLower) &&
                    (values[next] <= query.BoundaryUpper)
                ) {
                    mark[next] = 2;
                    free++;
                }
            }
        }
        return (boundary
            ? free
            : members
        );
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
    // A value on the empty key cell: BoundaryAt floods the in-range component it would join (the key cell a member)
    // and counts the distinct boundary cells beside it other than the key cell; EnclosedAt floods each in-range
    // component beside the key cell and counts the cells of those left with no boundary cell once the key cell is
    // filled. -1 when the key cell is not empty, -2 when the settled-cell budget runs out across the floods.
    private static long Placement(BoardComponentQuery query, ReadOnlySpan<long> values, int source, ArenaScratch scratch) {
        var topology = query.Topology;
        var count = topology.CellCount;

        if (
            (values[source] < query.BoundaryLower) ||
            (values[source] > query.BoundaryUpper)
        ) {
            return -1;
        }
        using var markLease = scratch.Rent<byte>(length: count);
        using var stackLease = scratch.Rent<int>(length: count);

        var mark = markLease.Span;
        var stack = stackLease.Span;

        mark[source] = 1;
        var settled = 0L;

        if (query.Kind == BoardQueryKind.BoundaryAt) {
            var size = 0;
            var free = 0L;

            stack[size++] = source;
            while (size > 0) {
                var cell = stack[--size];

                if (settled == query.MaxVisits) {
                    return -2;
                }
                settled++;
                for (var direction = 0; (direction < topology.DirectionCount); direction++) {
                    var next = topology.Neighbour(
                        cell: cell,
                        direction: direction
                    );

                    if (
                        (next < 0) ||
                        (mark[next] != 0)
                    ) {
                        continue;
                    }
                    if (
                        (values[next] >= query.Lower) &&
                        (values[next] <= query.Upper)
                    ) {
                        mark[next] = 1;
                        stack[size++] = next;
                    } else if (
                        (values[next] >= query.BoundaryLower) &&
                        (values[next] <= query.BoundaryUpper)
                    ) {
                        mark[next] = 2;
                        free++;
                    }
                }
            }
            return free;
        }
        var enclosed = 0L;

        for (var seed = 0; (seed < topology.DirectionCount); seed++) {
            var start = topology.Neighbour(
                cell: source,
                direction: seed
            );

            if (
                (start < 0) ||
                (mark[start] != 0) ||
                (values[start] < query.Lower) ||
                (values[start] > query.Upper)
            ) {
                continue;
            }
            // Boundary cells seen for earlier components are cleared back to unmarked so each counts its own.
            mark.Replace(
                newValue: ((byte)0),
                oldValue: ((byte)2)
            );
            var size = 0;
            var free = 0L;
            var members = 0L;

            mark[start] = 1;
            stack[size++] = start;
            while (size > 0) {
                var cell = stack[--size];

                if (settled == query.MaxVisits) {
                    return -2;
                }
                settled++;
                members++;
                for (var direction = 0; (direction < topology.DirectionCount); direction++) {
                    var next = topology.Neighbour(
                        cell: cell,
                        direction: direction
                    );

                    if (
                        (next < 0) ||
                        (mark[next] != 0)
                    ) {
                        continue;
                    }
                    if (
                        (values[next] >= query.Lower) &&
                        (values[next] <= query.Upper)
                    ) {
                        mark[next] = 1;
                        stack[size++] = next;
                    } else if (
                        (values[next] >= query.BoundaryLower) &&
                        (values[next] <= query.BoundaryUpper)
                    ) {
                        mark[next] = 2;
                        free++;
                    }
                }
            }
            if (free == 0) {
                enclosed += members;
            }
        }
        return enclosed;
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

    /// <summary>Clears every group whose values lie in <paramref name="lower"/>..<paramref name="upper"/> beside
    /// <paramref name="source"/> that has no cell at <paramref name="empty"/> beside it, writing <paramref name="empty"/>
    /// over its members. The source cell itself counts as filled whatever it holds, so the value just placed there is
    /// what closes the last gap.</summary>
    /// <param name="topology">The board's topology.</param>
    /// <param name="values">One value per cell, rewritten in place.</param>
    /// <param name="source">The cell the placement landed on.</param>
    /// <param name="lower">The enclosed range's inclusive low end.</param>
    /// <param name="upper">The enclosed range's inclusive high end.</param>
    /// <param name="empty">The board's empty value.</param>
    /// <param name="scratch">The working storage the flood borrows its marks and stacks from, normally the owning
    /// arena's; a throwaway one is made when it is <see langword="null"/>.</param>
    /// <returns>How many cells were cleared.</returns>
    public static long ClearEnclosed(CompiledTopology topology, Span<long> values, int source, long lower, long upper, long empty, ArenaScratch? scratch = null) {
        ArgumentNullException.ThrowIfNull(topology);
        scratch ??= new ArenaScratch();
        var count = topology.CellCount;

        if (
            (((uint)source) >= ((uint)count)) ||
            (values[source] == empty)
        ) {
            return 0L;
        }
        using var markLease = scratch.Rent<byte>(length: count);
        using var stackLease = scratch.Rent<int>(length: (2 * count));

        var mark = markLease.Span;
        var stack = stackLease.Span[..count];
        var members = stackLease.Span[count..];

        mark[source] = 1;
        var cleared = 0L;

        for (var seed = 0; (seed < topology.DirectionCount); seed++) {
            var start = topology.Neighbour(
                cell: source,
                direction: seed
            );

            if (
                (start < 0) ||
                (mark[start] != 0) ||
                (values[start] < lower) ||
                (values[start] > upper)
            ) {
                continue;
            }
            var size = 0;
            var memberCount = 0;
            var alive = false;

            mark[start] = 1;
            stack[size++] = start;
            while (size > 0) {
                var cell = stack[--size];

                members[memberCount++] = cell;
                for (var direction = 0; (direction < topology.DirectionCount); direction++) {
                    var next = topology.Neighbour(
                        cell: cell,
                        direction: direction
                    );

                    if (next < 0) {
                        continue;
                    }
                    if (values[next] == empty) {
                        alive = true;
                    } else if (
                        (mark[next] == 0) &&
                        (values[next] >= lower) &&
                        (values[next] <= upper)
                    ) {
                        mark[next] = 1;
                        stack[size++] = next;
                    }
                }
            }
            if (alive) {
                continue;
            }
            for (var index = 0; (index < memberCount); index++) {
                values[members[index]] = empty;
            }
            cleared += memberCount;
        }
        return cleared;
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
            var directions = attacks.Directions;

            for (var directionIndex = 0; (directionIndex < directions.Length); directionIndex++) {
                var direction = directions[directionIndex];
                var rayCell = source;

                for (var distance = 1; (distance < topology.CellCount); distance++) {
                    rayCell = topology.Neighbour(
                        cell: rayCell,
                        direction: direction
                    );
                    if (
                        (rayCell < 0) ||
                        (rayCell == source)
                    ) {
                        break;
                    }
                    if (values[rayCell] != empty) {
                        if (
                            (values[rayCell] >= attacks.Lower) &&
                            (values[rayCell] <= attacks.Upper)
                        ) {
                            return 1;
                        }
                        break;
                    }
                }
            }
            return 0;
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

        for (var distance = 1; (distance < topology.CellCount); distance++) {
            cell = topology.Neighbour(
                cell: cell,
                direction: direction
            );

            if (
                (cell < 0) ||
                (cell == origin)
            ) {
                break;
            }

            word[length++] = values[cell];
        }

        return length;
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
