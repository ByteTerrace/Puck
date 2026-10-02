using Puck.Maths;

namespace Puck.Physics.Fields;

/// <summary>A contact field over a <see cref="FieldLattice"/>'s height columns: the signed distance to the union
/// of column boxes, exact within two cells of a column and a conservative lower bound beyond.</summary>
public sealed class FieldLatticeSolid : IFieldEvaluator, IFieldBounds {
    // The widest column index an unwrapped Q48.16 quotient reaches, far past every column a lattice's int index holds.
    private const long FarColumn = (1L << 47);

    private readonly FieldLattice m_lattice;
    // The reach, two cells, in raws: the farthest any answer reads and the top of every bound.
    private readonly long m_reach;

    /// <summary>Initializes a new instance of the <see cref="FieldLatticeSolid"/> class.</summary>
    /// <param name="lattice">The lattice whose height columns are solid.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lattice"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The lattice's cell is not positive, or its reach,
    /// <see cref="ReachCells"/> cells, lies past the Q48.16 carrier: every answer and bound is read against the reach,
    /// so a solid over such a lattice could not answer without wrapping it.</exception>
    public FieldLatticeSolid(FieldLattice lattice) {
        ArgumentNullException.ThrowIfNull(argument: lattice);

        var cell = lattice.CellSize;

        if (!CellFits(cell: cell)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(lattice), message: $"The lattice's cell {cell} is not positive, or puts its contact reach, {ReachCells} cells, past the Q48.16 carrier.");
        }

        m_lattice = lattice;
        m_reach = (cell.Value * ReachCells);
    }

    /// <summary>The cells a point query reads about its own column on each axis, and the distance, in cells, no answer
    /// exceeds: a lattice's cell times this must fit the Q48.16 carrier.</summary>
    public const int ReachCells = 2;

    /// <summary>Returns whether a lattice cell can carry a solid: positive, with its reach of <see cref="ReachCells"/>
    /// cells inside the Q48.16 carrier. World validation refuses a lattice by this rule, and the constructor throws
    /// for one that reaches it unvalidated.</summary>
    /// <param name="cell">The lattice's cell size.</param>
    /// <returns><see langword="true"/> when a solid over the lattice can answer without wrapping its reach.</returns>
    public static bool CellFits(FixedQ4816 cell) =>
        ((cell.Value > 0L) && ((((Int128)cell.Value) * ReachCells) <= long.MaxValue));

    /// <inheritdoc/>
    public FieldEvaluatorCapabilities Capabilities => new(WarpFree: true);

    // A column's box, exact: the point query and the bounds read it in Int128, where no coordinate of it wraps however
    // near the carrier's ends the lattice, its column tops or the query sit, so the two share one arithmetic.
    private readonly record struct ColumnBox(Int128 MinX, Int128 MaxX, Int128 MinY, Int128 MaxY, Int128 MinZ, Int128 MaxZ) {
        // The deepest a point inside the box can lie, as a lower bound: minus its largest extent.
        public Int128 Depth => -Int128.Max(x: (MaxX - MinX), y: Int128.Max(x: (MaxY - MinY), y: (MaxZ - MinZ)));
    }

    private ColumnBox Column(int x, int z, FixedQ4816 top) {
        var cell = ((Int128)m_lattice.CellSize.Value);
        var origin = m_lattice.Origin;
        var minX = (origin.X.Value + (cell * x));
        var minZ = (origin.Z.Value + (cell * z));

        return new(MinX: minX, MaxX: (minX + cell), MinY: (origin.Y.Value - cell), MaxY: top.Value, MinZ: minZ, MaxZ: (minZ + cell));
    }
    // The length of a gap whose every component is at least zero, or false when one is past the reach: that column's
    // distance is then past the reach, which no answer exceeds, so it cannot lower one. Within the reach every
    // component is representable, and the length is the same exact-square, once-rounded one the point query takes.
    private bool TryGapLength(Int128 x, Int128 y, Int128 z, out FixedQ4816 length) {
        length = default;

        if ((x > m_reach) || (y > m_reach) || (z > m_reach)) {
            return false;
        }

        length = new FixedVector3(X: FixedQ4816.FromRawBits(value: ((long)x)), Y: FixedQ4816.FromRawBits(value: ((long)y)), Z: FixedQ4816.FromRawBits(value: ((long)z))).Length;

        return true;
    }
    // The box distance from a point to a column: the length of the gap outside it plus the depth inside it, the
    // nearest face's (a negative depth lies within half a cell of zero, since the cell bounds the box on x and z).
    // False when the column lies past the reach.
    private bool TryBoxDistance(in FixedVector3 point, in ColumnBox box, out FixedQ4816 distance) {
        var dx = Int128.Max(x: (box.MinX - point.X.Value), y: (point.X.Value - box.MaxX));
        var dy = Int128.Max(x: (box.MinY - point.Y.Value), y: (point.Y.Value - box.MaxY));
        var dz = Int128.Max(x: (box.MinZ - point.Z.Value), y: (point.Z.Value - box.MaxZ));

        if (!TryGapLength(length: out var outside, x: Int128.Max(x: dx, y: Int128.Zero), y: Int128.Max(x: dy, y: Int128.Zero), z: Int128.Max(x: dz, y: Int128.Zero))) {
            distance = default;

            return false;
        }

        distance = (outside + FixedQ4816.FromRawBits(value: ((long)Int128.Min(x: Int128.Max(x: dx, y: Int128.Max(x: dy, y: dz)), y: Int128.Zero))));

        return true;
    }
    // The column index a coordinate falls in, by the point query's own formula: floor((coordinate − origin) / cell),
    // the quotient rounded once to the Q16 grid, to nearest with ties to even, as FixedQ4816's division rounds it. It is
    // taken in Int128, where neither the offset nor the quotient can wrap (|offset| < 2⁶⁴ raws, so the scaled quotient
    // is under 2⁸⁰ for a cell of at least one raw), so it equals that division wherever the division does not wrap and
    // stays monotone in the coordinate where it would: a box's points fall in the columns between its corners'. The
    // cell is positive (the constructor refuses any other), so its raw is the divisor's magnitude. The answer is clamped to
    // ±2⁴⁷, which every unwrapped column already lies within, before it leaves the wide type.
    private static long ColumnOf(FixedQ4816 coordinate, FixedQ4816 origin, FixedQ4816 cell) {
        var offset = (((Int128)coordinate.Value) - origin.Value);

        var (truncated, remainder) = Int128.DivRem(left: (Int128.Abs(value: offset) << FixedQ4816.FractionBitCount), right: cell.Value);
        var magnitude = FixedPointRounding.RoundToNearestTiesToEven(
            distanceToNext: (cell.Value - remainder),
            distanceToTruncated: remainder,
            truncated: truncated
        );
        var quotient = ((offset < Int128.Zero) ? -magnitude : magnitude);

        return ((long)Int128.Clamp(max: FarColumn, min: -FarColumn, value: (quotient >> FixedQ4816.FractionBitCount)));
    }
    // The columns within the reach of [first, last] that the lattice holds, clamped in the wide type before narrowing
    // to the lattice's own int index: an empty range (Start past End) when none is. A clamped column sits far enough
    // past the lattice that the reach never brings it back into it.
    private static (int Start, int End) ColumnRange(long first, long last, int count) => (
        ((int)Math.Clamp(max: count, min: 0L, value: (first - ReachCells))),
        ((int)Math.Clamp(max: (count - 1L), min: -1L, value: (last + ReachCells)))
    );
    private FixedQ4816 Distance(in FixedVector3 point) {
        var cell = m_lattice.CellSize;
        var origin = m_lattice.Origin;
        var fx = ColumnOf(cell: cell, coordinate: point.X, origin: origin.X);
        var fz = ColumnOf(cell: cell, coordinate: point.Z, origin: origin.Z);

        var (firstX, lastX) = ColumnRange(count: m_lattice.Width, first: fx, last: fx);
        var (firstZ, lastZ) = ColumnRange(count: m_lattice.Depth, first: fz, last: fz);
        var best = FixedQ4816.FromRawBits(value: m_reach);

        for (var z = firstZ; (z <= lastZ); z++) {
            for (var x = firstX; (x <= lastX); x++) {
                if (
                    (m_lattice.ColumnHeight(
                    x: x,
                    z: z
                ) is { } top) &&
                    TryBoxDistance(box: Column(top: top, x: x, z: z), distance: out var distance, point: in point) &&
                    (distance < best)
                ) {
                    best = distance;
                }
            }
        }

        return best;
    }

    /// <inheritdoc/>
    /// <remarks>Every answer is at most two cells, where the point query stops looking, and is a box distance to a
    /// column within two cells of the point's own: one, since a box distance moves no faster than its point.</remarks>
    public FixedQ4816 StepScale => FixedQ4816.One;

    /// <inheritdoc/>
    /// <remarks>The point query reads the columns within two cells of the point's own, and a box's points fall in the
    /// columns between its corners', so every column it can read for a point of the box is one this reads. Both read
    /// every column's box and every gap exactly, in one wide arithmetic, so neither wraps where the other does not.
    /// Each column's least distance over the box is the length of the gap between the two boxes, computed by the same
    /// exact-square, once-rounded length the point query takes of a larger gap, so it is at or below every point
    /// answer; a column the box reaches is bounded below by minus its largest extent, deeper than any point inside
    /// it. The upper bound is the two-cell reach no point answer exceeds.</remarks>
    /// <exception cref="ArgumentException"><paramref name="lower"/> exceeds <paramref name="upper"/> on an
    /// axis.</exception>
    public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) {
        distance = FixedInterval.FromPoint(value: FixedQ4816.Zero);

        if (
            !lower.TryDelta(
            delta: out var low,
            origin: FixedPosition.Zero
        ) ||
            !upper.TryDelta(
            delta: out var high,
            origin: FixedPosition.Zero
        )
        ) {
            return false;
        }

        if (
            (low.X > high.X) ||
            (low.Y > high.Y) ||
            (low.Z > high.Z)
        ) {
            throw new ArgumentException(message: $"The box's lower corner {low} exceeds its upper corner {high}.", paramName: nameof(lower));
        }

        var cell = m_lattice.CellSize;
        var origin = m_lattice.Origin;
        var reach = FixedQ4816.FromRawBits(value: m_reach);
        var least = reach;

        var (firstX, lastX) = ColumnRange(count: m_lattice.Width, first: ColumnOf(cell: cell, coordinate: low.X, origin: origin.X), last: ColumnOf(cell: cell, coordinate: high.X, origin: origin.X));
        var (firstZ, lastZ) = ColumnRange(count: m_lattice.Depth, first: ColumnOf(cell: cell, coordinate: low.Z, origin: origin.Z), last: ColumnOf(cell: cell, coordinate: high.Z, origin: origin.Z));

        for (var z = firstZ; (z <= lastZ); z++) {
            for (var x = firstX; (x <= lastX); x++) {
                if (m_lattice.ColumnHeight(
                    x: x,
                    z: z
                ) is not { } top) {
                    continue;
                }

                var box = Column(top: top, x: x, z: z);
                var gx = Int128.Max(x: Int128.Max(x: (box.MinX - high.X.Value), y: (low.X.Value - box.MaxX)), y: Int128.Zero);
                var gy = Int128.Max(x: Int128.Max(x: (box.MinY - high.Y.Value), y: (low.Y.Value - box.MaxY)), y: Int128.Zero);
                var gz = Int128.Max(x: Int128.Max(x: (box.MinZ - high.Z.Value), y: (low.Z.Value - box.MaxZ)), y: Int128.Zero);

                // A box reaching the column is bounded below by its depth, lowered to the carrier's least value where
                // it lies past it: a lower bound may only fall. A gap past the reach cannot lower the bound.
                if ((gx == Int128.Zero) && (gy == Int128.Zero) && (gz == Int128.Zero)) {
                    least = FixedQ4816.Min(x: least, y: FixedQ4816.FromRawBits(value: ((long)Int128.Max(x: box.Depth, y: long.MinValue))));
                } else if (TryGapLength(length: out var column, x: gx, y: gy, z: gz) && (column < least)) {
                    least = column;
                }
            }
        }

        distance = new FixedInterval(
            lower: least,
            upper: reach
        );

        return true;
    }
    /// <inheritdoc/>
    public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
        material = 0;

        if (!position.TryDelta(
            delta: out var point,
            origin: FixedPosition.Zero
        )) {
            distance = FixedQ4816.Zero;

            return false;
        }

        distance = Distance(point: in point);

        return true;
    }
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient) =>
        TryFieldGradient(
            epsilon: FixedQ4816.FromDouble(value: 0.01),
            gradient: out gradient,
            position: position
        );
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient) {
        gradient = default;

        if (!position.TryDelta(
            delta: out var point,
            origin: FixedPosition.Zero
        )) {
            return false;
        }

        // A zero probe asks for the analytic gradient this sampled field has no closed form for; the fallback probe
        // is the solver's own default scale.
        if (epsilon <= FixedQ4816.Zero) {
            epsilon = FixedQ4816.FromDouble(value: 0.01);
        }

        var ex = new FixedVector3(
            X: epsilon,
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.Zero
        );
        var ey = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: epsilon,
            Z: FixedQ4816.Zero
        );
        var ez = new FixedVector3(
            X: FixedQ4816.Zero,
            Y: FixedQ4816.Zero,
            Z: epsilon
        );
        var two = (epsilon + epsilon);
        var px = (point + ex); var mx = (point - ex);
        var py = (point + ey); var my = (point - ey);
        var pz = (point + ez); var mz = (point - ez);

        gradient = new FixedVector3(
            X: ((Distance(point: in px) - Distance(point: in mx)) / two),
            Y: ((Distance(point: in py) - Distance(point: in my)) / two),
            Z: ((Distance(point: in pz) - Distance(point: in mz)) / two)
        );

        return true;
    }
}
