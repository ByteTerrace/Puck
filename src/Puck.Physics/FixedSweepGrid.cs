using Puck.Abstractions.Collections;
using Puck.Maths;

namespace Puck.Physics;

/// <summary>One sphere a <see cref="FixedSweepGrid"/> holds: a caller-owned slot, its centre and its bounding radius.</summary>
/// <param name="Index">The caller's slot, nonnegative and smaller than the grid's capacity.</param>
/// <param name="Center">The sphere's centre, in metres.</param>
/// <param name="Radius">The sphere's radius, in metres, at least zero.</param>
/// <param name="Active">Whether the sphere is among the candidates of an active-only pair search
/// (<see cref="FixedSweepGrid.BeginPairs"/>).</param>
public readonly record struct FixedSweepSphere(int Index, FixedVector3 Center, FixedQ4816 Radius, bool Active = true) {
    /// <summary>Gets the sphere's world-axis bounds: the cube of its radius about its centre.</summary>
    public FixedAabb Bounds => FixedAabb.Around(center: Center, radius: Radius);
}
/// <summary>The one deterministic broadphase over bounding spheres: an X sweep over spheres sorted by least X then
/// slot, crossed with a Y/Z grid whose cells are as wide as two of the largest radii. It answers the pairs whose bounds
/// meet, in sweep order and under a per-sphere candidate budget, and the spheres whose bounds meet a box.</summary>
/// <remarks>
/// <para>Two spheres whose bounds meet have centres in the same or adjacent Y/Z cells, so a pair search merges the
/// nine neighbouring cells' sweep-ordered lists and a box query visits the cells its grown box covers. A box whose
/// cell range outnumbers the occupied cells visits the occupied cells instead, so a large box never walks an empty
/// lattice.</para>
/// <para>A pair candidate is a pair whose bounds meet on all three axes; the budget is charged only for such a
/// candidate, after the cull, so a crowd sharing one X and spread along Y or Z cannot spend a sphere's budget on
/// spheres it cannot touch. Every choice is a function of the spheres alone: the sweep order is total (least X, then
/// slot), each cell's list keeps that order, and results never depend on input or dictionary order.</para>
/// <para>Neither rebuild nor query allocates after the grid has seen its largest image. This mutable workspace is not
/// thread-safe, and one pair search runs at a time.</para>
/// </remarks>
public sealed class FixedSweepGrid {
    private const int NeighbourCells = 9;

    private readonly Row[] m_rows;
    private readonly (long Y, long Z)[] m_cellKeys;
    private readonly int[] m_cellOf;
    private readonly int[] m_entries;
    private readonly int[] m_activeEntries;
    private readonly int[] m_cellStart;
    private readonly int[] m_cellHead;
    private readonly int[] m_activeStart;
    private readonly int[] m_activeHead;
    private readonly Dictionary<(long Y, long Z), int> m_cellIds = [];
    private readonly MarkedIndexSet m_seen = new();
    private readonly int[] m_cursor = new int[NeighbourCells];
    private readonly int[] m_end = new int[NeighbourCells];

    private int m_count;
    private int m_cellCount;
    private long m_width = 2L;
    private long m_maximumRadius;
    private int m_left = -1;
    private int m_lastLeft = -1;
    private bool m_activeOnly;
    private int m_budget;
    private int m_inspected;

    /// <summary>Initializes a new instance of the <see cref="FixedSweepGrid"/> class with its whole workspace.</summary>
    /// <param name="capacity">The most spheres one image holds, and the exclusive upper bound on their slots.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public FixedSweepGrid(int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);
        m_rows = new Row[capacity];
        m_cellKeys = new (long Y, long Z)[capacity];
        m_cellOf = new int[capacity];
        m_entries = new int[capacity];
        m_activeEntries = new int[capacity];
        m_cellStart = new int[(capacity + 1)];
        m_cellHead = new int[capacity];
        m_activeStart = new int[(capacity + 1)];
        m_activeHead = new int[capacity];
        _ = m_cellIds.EnsureCapacity(capacity: capacity);
    }

    /// <summary>Gets the most spheres one image holds.</summary>
    public int Capacity => m_rows.Length;
    /// <summary>Gets the number of spheres in the current image.</summary>
    public int Count => m_count;
    /// <summary>Gets the greatest radius in the current image, zero when it is empty.</summary>
    public FixedQ4816 MaximumRadius => FixedQ4816.FromRawBits(value: m_maximumRadius);
    /// <summary>Gets the sweep-ordered pairs the pair searches since the last rebuild have looked at, before the Y/Z
    /// cull: the deterministic work the X sweep and grid left.</summary>
    public long PairVisits { get; private set; }
    /// <summary>Gets a value indicating whether the latest pair search stopped because its candidate budget was spent
    /// with another candidate still in reach.</summary>
    public bool PairsLimited { get; private set; }

    /// <summary>Returns a sphere of the current image by its sweep ordinal.</summary>
    /// <param name="ordinal">The sweep ordinal, in [0, <see cref="Count"/>): spheres in order of least X, then slot.</param>
    /// <returns>The sphere.</returns>
    public FixedSweepSphere Sphere(int ordinal) => m_rows[ordinal].Sphere;
    /// <summary>Replaces the image with the supplied spheres and sorts them into sweep order.</summary>
    /// <param name="spheres">Spheres with distinct slots inside capacity and radii at least zero; never mutated.</param>
    /// <param name="minimumCellWidth">Optional broad-phase cell width for point or small-sphere range queries.</param>
    /// <exception cref="ArgumentException">The input exceeds capacity, repeats a slot, names a slot outside capacity
    /// or carries a negative radius. A refused rebuild leaves an empty image.</exception>
    public void Rebuild(ReadOnlySpan<FixedSweepSphere> spheres, FixedQ4816 minimumCellWidth = default) {
        m_count = 0;
        m_cellCount = 0;
        m_maximumRadius = 0L;
        m_width = 2L;
        m_left = -1;
        m_lastLeft = -1;
        PairVisits = 0L;
        PairsLimited = false;
        m_cellIds.Clear();
        if (spheres.Length > Capacity) {
            throw new ArgumentException(message: "Sphere count exceeds sweep grid capacity.", paramName: nameof(spheres));
        }
        m_seen.Clear();
        var maximumRadius = 0L;

        for (var ordinal = 0; (ordinal < spheres.Length); ordinal++) {
            var sphere = spheres[ordinal];

            if ((((uint)sphere.Index) >= ((uint)Capacity)) || !m_seen.Add(index: sphere.Index) || (sphere.Radius < FixedQ4816.Zero)) {
                throw new ArgumentException(message: "Sweep grid slots must be distinct and inside capacity, and radii at least zero.", paramName: nameof(spheres));
            }
            maximumRadius = Math.Max(val1: maximumRadius, val2: sphere.Radius.Value);
            m_rows[ordinal] = new Row(Bounds: sphere.Bounds, Sphere: sphere);
        }
        Array.Sort(array: m_rows, index: 0, length: spheres.Length);
        m_maximumRadius = maximumRadius;
        // Two spheres whose bounds meet on Y and Z lie within the sum of their radii, never more than this width, so
        // their centres fall in the same or adjacent cells. Raw units keep the floor division exact on either side of 0.
        m_width = Math.Max(Math.Max(2L, minimumCellWidth.Value), maximumRadius.AddSaturating(right: maximumRadius));
        m_count = spheres.Length;

        Array.Clear(array: m_cellStart, index: 0, length: (m_count + 1));
        Array.Clear(array: m_activeStart, index: 0, length: (m_count + 1));
        for (var ordinal = 0; (ordinal < m_count); ordinal++) {
            var center = m_rows[ordinal].Sphere.Center;
            var key = (center.Y.Value.FloorDivide(divisor: m_width), center.Z.Value.FloorDivide(divisor: m_width));

            if (!m_cellIds.TryGetValue(key: key, value: out var cell)) {
                cell = m_cellCount++;
                m_cellIds[key] = cell;
                m_cellKeys[cell] = key;
            }
            m_cellOf[ordinal] = cell;
            m_cellStart[(cell + 1)]++;
            if (m_rows[ordinal].Sphere.Active) { m_activeStart[(cell + 1)]++; }
        }
        for (var cell = 0; (cell < m_cellCount); cell++) {
            m_cellStart[(cell + 1)] += m_cellStart[cell];
            m_activeStart[(cell + 1)] += m_activeStart[cell];
            m_cellHead[cell] = m_cellStart[cell];
            m_activeHead[cell] = m_activeStart[cell];
        }
        // Scattered in sweep order, so each cell's slice stays in sweep order.
        for (var ordinal = 0; (ordinal < m_count); ordinal++) {
            var cell = m_cellOf[ordinal];

            m_entries[m_cellHead[cell]++] = ordinal;
            if (m_rows[ordinal].Sphere.Active) { m_activeEntries[m_activeHead[cell]++] = ordinal; }
        }
        for (var cell = 0; (cell < m_cellCount); cell++) {
            m_cellHead[cell] = m_cellStart[cell];
            m_activeHead[cell] = m_activeStart[cell];
        }
    }
    /// <summary>Starts the pair search for one sphere: its candidates are the later spheres in sweep order whose bounds
    /// meet its own, taken in sweep order by <see cref="NextPair"/>. Searches run in nondecreasing sweep ordinal, so
    /// every pair is found from its earlier sphere only; a sphere skipped is never searched later.</summary>
    /// <param name="leftOrdinal">The searching sphere's sweep ordinal, at least the previous search's.</param>
    /// <param name="candidateBudget">The most candidates the search returns, at least zero.</param>
    /// <param name="activeOnly">Whether the candidates are the spheres rebuilt as active only, so a pair of two inactive
    /// spheres is neither visited nor charged.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="leftOrdinal"/> lies outside the image or
    /// <paramref name="candidateBudget"/> is negative.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="leftOrdinal"/> precedes the previous search's.</exception>
    public void BeginPairs(int leftOrdinal, int candidateBudget, bool activeOnly) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: ((uint)leftOrdinal), other: ((uint)m_count), paramName: nameof(leftOrdinal));
        ArgumentOutOfRangeException.ThrowIfNegative(value: candidateBudget);
        if (leftOrdinal < m_lastLeft) {
            throw new InvalidOperationException(message: "Sweep grid pair searches run in nondecreasing sweep ordinal.");
        }
        m_left = leftOrdinal;
        m_lastLeft = leftOrdinal;
        m_budget = candidateBudget;
        m_inspected = 0;
        PairsLimited = false;
        m_activeOnly = activeOnly;
        var entries = (m_activeOnly ? m_activeEntries : m_entries);
        var heads = (m_activeOnly ? m_activeHead : m_cellHead);
        var starts = (m_activeOnly ? m_activeStart : m_cellStart);
        var key = m_cellKeys[m_cellOf[leftOrdinal]];
        var neighbour = 0;

        for (var y = -1L; (y <= 1L); y++) {
            for (var z = -1L; (z <= 1L); z++) {
                if (!m_cellIds.TryGetValue(key: ((key.Y + y), (key.Z + z)), value: out var cell)) {
                    m_cursor[neighbour] = 0;
                    m_end[neighbour++] = 0;
                    continue;
                }
                var head = heads[cell];
                var end = starts[(cell + 1)];

                // Every search after this one starts later in sweep order, so the head drops what precedes it for all.
                while ((head < end) && (entries[head] <= leftOrdinal)) { head++; }
                heads[cell] = head;
                m_cursor[neighbour] = head;
                m_end[neighbour++] = end;
            }
        }
    }
    /// <summary>Takes the next candidate of the current pair search: the earliest later sphere in sweep order whose X
    /// interval starts within the searching sphere's and whose bounds meet its own on Y and Z.</summary>
    /// <param name="rightOrdinal">The candidate's sweep ordinal; meaningful only on <see langword="true"/>.</param>
    /// <returns><see langword="true"/> with a candidate, charged against the search's budget; <see langword="false"/>
    /// when the sweep window is done or, with <see cref="PairsLimited"/> set, when a further candidate found the budget
    /// spent.</returns>
    /// <exception cref="InvalidOperationException">No pair search was started since the last rebuild.</exception>
    public bool NextPair(out int rightOrdinal) {
        if (m_left < 0) {
            throw new InvalidOperationException(message: "Start a sweep grid pair search with BeginPairs.");
        }
        var entries = (m_activeOnly ? m_activeEntries : m_entries);
        ref readonly var left = ref m_rows[m_left];

        while (true) {
            var nearest = -1;

            rightOrdinal = m_count;
            for (var neighbour = 0; (neighbour < NeighbourCells); neighbour++) {
                if ((m_cursor[neighbour] < m_end[neighbour]) && (entries[m_cursor[neighbour]] < rightOrdinal)) {
                    rightOrdinal = entries[m_cursor[neighbour]];
                    nearest = neighbour;
                }
            }
            if ((nearest < 0) || (m_rows[rightOrdinal].Bounds.Minimum.X > left.Bounds.Maximum.X)) {
                rightOrdinal = -1;
                return false;
            }
            m_cursor[nearest]++;
            PairVisits++;
            if (!m_rows[rightOrdinal].Bounds.Intersects(other: left.Bounds)) {
                continue;
            }
            if (m_inspected >= m_budget) {
                PairsLimited = true;
                rightOrdinal = -1;
                return false;
            }
            m_inspected++;
            return true;
        }
    }
    /// <summary>Writes the slots of every sphere whose bounds meet a box, cells in key order and each cell in sweep
    /// order.</summary>
    /// <param name="box">The box; <see cref="FixedAabb.Everywhere"/> answers every sphere and an empty box none.</param>
    /// <param name="destination">The span the slots are written to, at least <see cref="Count"/> long.</param>
    /// <returns>The number of slots written.</returns>
    public int Query(in FixedAabb box, Span<int> destination) {
        if ((m_count == 0) || box.IsEmpty) { return 0; }
        // A sphere meeting the box has its centre within its radius of it, never more than the largest radius.
        var firstY = box.Minimum.Y.Value.SubtractSaturating(right: m_maximumRadius).FloorDivide(divisor: m_width);
        var lastY = box.Maximum.Y.Value.AddSaturating(right: m_maximumRadius).FloorDivide(divisor: m_width);
        var firstZ = box.Minimum.Z.Value.SubtractSaturating(right: m_maximumRadius).FloorDivide(divisor: m_width);
        var lastZ = box.Maximum.Z.Value.AddSaturating(right: m_maximumRadius).FloorDivide(divisor: m_width);
        var cells = ((((Int128)lastY) - firstY) + 1) * ((((Int128)lastZ) - firstZ) + 1);
        var count = 0;

        if (cells > m_cellCount) {
            for (var cell = 0; (cell < m_cellCount); cell++) {
                var key = m_cellKeys[cell];

                if ((key.Y >= firstY) && (key.Y <= lastY) && (key.Z >= firstZ) && (key.Z <= lastZ)) { Visit(cell: cell, box: in box, destination: destination, count: ref count); }
            }
        } else {
            for (var y = firstY; (y <= lastY); y++) {
                for (var z = firstZ; (z <= lastZ); z++) {
                    if (m_cellIds.TryGetValue(key: (y, z), value: out var cell)) { Visit(cell: cell, box: in box, destination: destination, count: ref count); }
                }
            }
        }
        return count;
    }

    private void Visit(int cell, in FixedAabb box, Span<int> destination, ref int count) {
        var low = m_cellStart[cell];
        var high = m_cellStart[(cell + 1)];
        var end = high;
        // A cell's slice is sorted by least X, and a sphere whose least X lies more than a cell width before the box
        // ends before it: its X interval is at most two of the largest radii wide.
        var lowest = box.Minimum.X.Value.SubtractSaturating(right: m_width);

        low += PartitionPoint.Of(
            isBefore: static (state, entry) => (state.Rows[entry].Bounds.Minimum.X.Value < state.Lowest),
            span: m_entries.AsSpan(length: (high - low), start: low),
            state: (Rows: m_rows, Lowest: lowest)
        );
        for (var position = low; (position < end); position++) {
            ref readonly var row = ref m_rows[m_entries[position]];

            if (row.Bounds.Minimum.X > box.Maximum.X) { break; }
            if (row.Bounds.Intersects(other: in box)) { destination[count++] = row.Sphere.Index; }
        }
    }

    private readonly record struct Row(FixedAabb Bounds, FixedSweepSphere Sphere) : IComparable<Row> {
        public int CompareTo(Row other) => LexicographicOrder.Compare(
            leftPrimary: Bounds.Minimum.X,
            leftSecondary: Sphere.Index,
            rightPrimary: other.Bounds.Minimum.X,
            rightSecondary: other.Sphere.Index
        );
    }
}
