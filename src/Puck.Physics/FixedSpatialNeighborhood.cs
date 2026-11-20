using Puck.Abstractions.Collections;
using Puck.Maths;

namespace Puck.Physics;

/// <summary>A position keyed by a stable, nonnegative caller-owned slot.</summary>
/// <param name="Index">The slot, smaller than the neighborhood's capacity.</param>
/// <param name="Position">The sampled position, frozen until the next rebuild.</param>
public readonly record struct FixedSpatialPoint(int Index, FixedVector3 Position);
/// <summary>A perceived neighbor ranked by exact squared distance, then stable slot.</summary>
/// <param name="Index">The caller-owned slot.</param>
/// <param name="SquaredDistanceRaw">Squared Q48.16 raw coordinates, without a rounding or narrowing step.</param>
public readonly record struct FixedSpatialNeighbor(int Index, UInt128 SquaredDistanceRaw);
/// <summary>Deterministic structural work for one neighborhood query.</summary>
/// <param name="CellLookups">Cell searches performed, at most 27.</param>
/// <param name="CandidatesExamined">Points inspected, including self and points outside the sphere.</param>
/// <param name="AvailableCandidates">Points in the intersected grid cells, before spherical filtering.</param>
/// <param name="NeighborsWritten">Results retained in the caller's buffer.</param>
public readonly record struct FixedNeighborhoodWork(int CellLookups, int CandidatesExamined, int AvailableCandidates, int NeighborsWritten) {
    /// <summary>Whether the work budget left some cell occupants unexamined. This does not assert that an unseen
    /// occupant would have passed the spherical filter.</summary>
    public bool BudgetLimited => (CandidatesExamined < AvailableCandidates);
}
/// <summary>A reusable fixed-point grid for bounded local perception, not a collision broadphase.</summary>
/// <remarks>
/// Rebuild copies the input before sorting it by cell and stable slot. Queries examine at most 27 cells and the
/// caller's candidate budget, including in a coincident crowd. A deterministic round-robin across occupied cells
/// prevents a dense first cell consuming all attention. The sample ordinal's remainder selects the first cell;
/// its quotient by the occupied-cell count rotates starting occupants independently. Using the same remainder
/// for both would permanently hide some occupants at small budgets. Advancing the ordinal is a simulation
/// decision and must be checkpointed if it is not derived from taped time.
/// Results are the nearest retained members of the examined sample, not necessarily the globally nearest members.
/// Neither rebuild nor query allocates after construction. This mutable workspace is not thread-safe.
/// </remarks>
public sealed class FixedSpatialNeighborhood {
    private readonly Cell[] m_cells;
    private readonly Point[] m_points;
    private readonly MarkedIndexSet m_seen = new();

    private int m_cellCount;
    private int m_count;

    /// <summary>Allocates the complete workspace. The cell width bounds the radius accepted by each query.</summary>
    /// <param name="capacity">Maximum input count and exclusive upper bound on stable slots.</param>
    /// <param name="cellWidth">A strictly positive grid width.</param>
    /// <exception cref="ArgumentOutOfRangeException">Capacity is negative or cell width is not positive.</exception>
    public FixedSpatialNeighborhood(int capacity, FixedQ4816 cellWidth) {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            cellWidth,
            FixedQ4816.Zero
        );
        CellWidth = cellWidth;
        m_points = new Point[capacity];
        m_cells = new Cell[capacity];
    }

    /// <summary>Gets the allocated slot bound.</summary>
    public int Capacity => m_points.Length;
    /// <summary>Gets the grid width and maximum query radius.</summary>
    public FixedQ4816 CellWidth { get; }
    /// <summary>Gets the number of points in the current frozen image.</summary>
    public int Count => m_count;

    private Key CellFor(in FixedVector3 position) => new(
        X: Floor(raw: position.X.Value),
        Y: Floor(raw: position.Y.Value),
        Z: Floor(raw: position.Z.Value)
    );
    private static int Compare(in FixedSpatialNeighbor left, in FixedSpatialNeighbor right) {
        return LexicographicOrder.Compare(
            leftPrimary: left.SquaredDistanceRaw,
            leftSecondary: left.Index,
            rightPrimary: right.SquaredDistanceRaw,
            rightSecondary: right.Index
        );
    }
    private int FindCell(Key key) {
        var found = m_cells.AsSpan(length: m_cellCount, start: 0).BinarySearch(comparable: new CellKey(Key: key));

        return ((found >= 0) ? found : -1);
    }
    private long Floor(long raw) => raw.FloorDivide(divisor: CellWidth.Value);
    // The nearest neighbours kept so far, as a heap whose first item is the farthest of them, so a nearer candidate
    // replaces it.
    private static void Retain(FixedSpatialNeighbor candidate, Span<FixedSpatialNeighbor> heap, ref int count) {
        if (count < heap.Length) {
            BinaryHeap.Push(count: ref count, heap: heap, item: candidate, order: default(FarthestFirst));
        } else if (Compare(left: candidate, right: heap[0]) < 0) {
            BinaryHeap.ReplaceFirst(count: count, heap: heap, item: candidate, order: default(FarthestFirst));
        }
    }
    private static bool TryDistance(in FixedVector3 left, in FixedVector3 right, UInt128 radius, UInt128 limit, out UInt128 squared) {
        var x = ((UInt128)Int128.Abs(value: (((Int128)left.X.Value) - right.X.Value)));
        var y = ((UInt128)Int128.Abs(value: (((Int128)left.Y.Value) - right.Y.Value)));
        var z = ((UInt128)Int128.Abs(value: (((Int128)left.Z.Value) - right.Z.Value)));

        squared = 0;
        if (
            (x > radius) ||
            (y > radius) ||
            (z > radius)
        ) { return false; }
        // Each term is below 2^126 after the axis checks, so all three fit UInt128 together.
        squared = (((x * x) + (y * y)) + (z * z));
        return (squared <= limit);
    }
    private static bool TryOffset(long value, int offset, out long result) {
        result = unchecked((value + offset));
        return !(((offset < 0) && (value == long.MinValue)) || ((offset > 0) && (value == long.MaxValue)));
    }

    /// <summary>Samples nearby cells under a hard inspection budget and returns the nearest sampled points.</summary>
    /// <param name="origin">The query center, not necessarily present in the image.</param>
    /// <param name="radius">Inclusive spherical radius in [0, CellWidth].</param>
    /// <param name="excludedIndex">A slot to exclude, or -1 for none.</param>
    /// <param name="candidateBudget">Maximum inspected points, including filtered or excluded points.</param>
    /// <param name="sampleOrdinal">Deterministic attention phase, normally derived from observer and sensing step.</param>
    /// <param name="destination">Retained neighbors, sorted by exact distance then slot; unused tail is unspecified.</param>
    /// <returns>Structural work and the number of valid destination entries.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Radius or budget is outside its admitted range.</exception>
    public FixedNeighborhoodWork Query(in FixedVector3 origin, FixedQ4816 radius, int excludedIndex,
        int candidateBudget, ulong sampleOrdinal, Span<FixedSpatialNeighbor> destination) {
        ArgumentOutOfRangeException.ThrowIfNegative(candidateBudget);
        if (
            (radius < FixedQ4816.Zero) ||
            (radius > CellWidth)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(radius));
        }
        // Constant-sized query workspace, independent of population and of destination length.
        Span<Cell> cells = stackalloc Cell[27];
        Span<int> consumed = stackalloc int[27];

        consumed.Clear();
        var center = CellFor(position: origin);
        var found = 0;
        var lookups = 0;
        var available = 0;

        for (var x = -1; (x <= 1); x++) {
            for (var y = -1; (y <= 1); y++) {
                for (var z = -1; (z <= 1); z++) {
                    // At raw-coordinate extrema a neighboring cell can lie outside Int64's coordinate space.
                    if (
                        !TryOffset(
                        center.X,
                        x,
                        out var cx
                    ) ||
                        !TryOffset(
                        center.Y,
                        y,
                        out var cy
                    ) ||
                        !TryOffset(
                        center.Z,
                        z,
                        out var cz
                    )
                    ) {
                        continue;
                    }
                    lookups++;
                    var cellIndex = FindCell(key: new Key(
                        X: cx,
                        Y: cy,
                        Z: cz
                    ));

                    if (cellIndex >= 0) {
                        cells[found++] = m_cells[cellIndex];
                        available += m_cells[cellIndex].Count;
                    }
                }
            }
        }
        var examined = 0;
        var written = 0;

        if (
            (found == 0) ||
            destination.IsEmpty
        ) {
            return new FixedNeighborhoodWork(
                AvailableCandidates: available,
                CandidatesExamined: examined,
                CellLookups: lookups,
                NeighborsWritten: written
            );
        }
        var cursor = ((int)(sampleOrdinal % ((ulong)found)));
        var radiusRaw = ((UInt128)((ulong)radius.Value));
        var radiusSquared = (radiusRaw * radiusRaw);

        while (
            (examined < candidateBudget) &&
            (examined < available)
        ) {
            ref readonly var cell = ref cells[cursor];

            if (consumed[cursor] < cell.Count) {
                var start = ((int)((sampleOrdinal / ((ulong)found)) % ((ulong)cell.Count)));
                var offset = ((int)((((long)start) + consumed[cursor]++) % cell.Count));
                ref readonly var point = ref m_points[(cell.Start + offset)];

                examined++;
                if (
                    (point.Index != excludedIndex) &&
                    TryDistance(
                    origin,
                    point.Position,
                    radiusRaw,
                    radiusSquared,
                    out var squared
                )
                ) {
                    Retain(
                        new FixedSpatialNeighbor(
                            Index: point.Index,
                            SquaredDistanceRaw: squared
                        ),
                        destination,
                        ref written
                    );
                }
            }
            cursor = (((cursor + 1) == found)
                ? 0
                : (cursor + 1)
            );
        }
        // Retain keeps the farthest first: sorting that heap last-first gives ascending results in place.
        BinaryHeap.SortDescending(count: written, heap: destination, order: default(FarthestFirst));
        return new FixedNeighborhoodWork(
            AvailableCandidates: available,
            CandidatesExamined: examined,
            CellLookups: lookups,
            NeighborsWritten: written
        );
    }
    /// <summary>Replaces the image with the supplied points. Input order does not affect query results.</summary>
    /// <param name="points">Distinct stable slots and their positions; the input is never mutated.</param>
    /// <exception cref="ArgumentException">Input exceeds capacity, repeats a slot, or names an out-of-range slot.
    /// A refused rebuild leaves an empty image.</exception>
    public void Rebuild(ReadOnlySpan<FixedSpatialPoint> points) {
        m_count = 0;
        m_cellCount = 0;
        if (points.Length > Capacity) {
            throw new ArgumentException(
                message: "Point count exceeds neighborhood capacity.",
                paramName: nameof(points)
            );
        }
        m_seen.Clear();
        for (var ordinal = 0; (ordinal < points.Length); ordinal++) {
            var point = points[ordinal];

            if (
                (((uint)point.Index) >= ((uint)Capacity)) ||
                !m_seen.Add(index: point.Index)
            ) {
                throw new ArgumentException(
                    message: "Neighborhood slots must be distinct and inside capacity.",
                    paramName: nameof(points)
                );
            }
            m_points[ordinal] = new Point(
                CellFor(position: point.Position),
                point.Index,
                point.Position
            );
        }
        m_points.AsSpan(
            0,
            points.Length
        ).Sort();
        for (var ordinal = 0; (ordinal < points.Length);) {
            var start = ordinal;
            var key = m_points[ordinal++].Key;

            while (
                (ordinal < points.Length) &&
                (m_points[ordinal].Key == key)
            ) {
                ordinal++;
            }
            m_cells[m_cellCount++] = new Cell(
                Count: (ordinal - start),
                Key: key,
                Start: start
            );
        }
        m_count = points.Length;
    }

    private readonly record struct Key(long X, long Y, long Z) : IComparable<Key> {
        public int CompareTo(Key other) {
            var order = X.CompareTo(value: other.X);

            if (order == 0) { order = Y.CompareTo(value: other.Y); }
            return ((order == 0)
                ? Z.CompareTo(value: other.Z)
                : order
            );
        }
    }
    private readonly record struct Point(Key Key, int Index, FixedVector3 Position) : IComparable<Point> {
        public int CompareTo(Point other) {
            return LexicographicOrder.Compare(
                leftPrimary: Key,
                leftSecondary: Index,
                rightPrimary: other.Key,
                rightSecondary: other.Index
            );
        }
    }
    private readonly record struct Cell(Key Key, int Start, int Count);
    private readonly record struct CellKey(Key Key) : IComparable<Cell> {
        public int CompareTo(Cell other) => Key.CompareTo(other: other.Key);
    }
    private readonly struct FarthestFirst : IComparer<FixedSpatialNeighbor> {
        public int Compare(FixedSpatialNeighbor x, FixedSpatialNeighbor y) => FixedSpatialNeighborhood.Compare(left: y, right: x);
    }
}
