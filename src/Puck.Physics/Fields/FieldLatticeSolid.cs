using Puck.Maths;

namespace Puck.Physics.Fields;

/// <summary>A contact field over a <see cref="FieldLattice"/>'s height columns: the signed distance to the union
/// of column boxes, exact within two cells of a column and a conservative lower bound beyond.</summary>
public sealed class FieldLatticeSolid : IFieldEvaluator, IFieldBounds {
    private const int Reach = 2;
    // The widest column index an unwrapped Q48.16 quotient reaches, far past every column a lattice's int index holds.
    private const long FarColumn = (1L << 47);

    private readonly FieldLattice m_lattice;

    public FieldLatticeSolid(FieldLattice lattice) {
        ArgumentNullException.ThrowIfNull(argument: lattice);

        m_lattice = lattice;
    }

    /// <inheritdoc/>
    public FieldEvaluatorCapabilities Capabilities => new(WarpFree: true);

    // The gap between a box and a column on one axis, exact in a wide type, which two coordinates near the carrier's
    // ends leave, and clamped to the reach: a gap at the reach puts the column at or past the reach, where no point
    // answer falls below, so the clamp never lowers the bound past a point answer, and the narrowed gap never wraps.
    private static FixedQ4816 Gap(FixedQ4816 low, FixedQ4816 high, FixedQ4816 columnMinimum, FixedQ4816 columnMaximum, FixedQ4816 reach) {
        var gap = Int128.Max(
            x: Int128.Max(
                x: (((Int128)columnMinimum.Value) - high.Value),
                y: (((Int128)low.Value) - columnMaximum.Value)
            ),
            y: Int128.Zero
        );

        return FixedQ4816.FromRawBits(value: ((long)Int128.Min(x: gap, y: reach.Value)));
    }
    private static FixedQ4816 BoxDistance(in FixedVector3 point, in FixedVector3 min, in FixedVector3 max) {
        var d = FixedVector3.Max(
            left: (min - point),
            right: (point - max)
        );
        var outside = FixedVector3.Max(
            left: d,
            right: FixedVector3.Zero
        );
        var inside = FixedQ4816.Min(
            x: FixedVector3.MaxComponent(value: d),
            y: FixedQ4816.Zero
        );

        return (outside.Length + inside);
    }
    // The column index a coordinate falls in, by the point query's own formula: floor((coordinate − origin) / cell),
    // the quotient rounded once to the Q16 grid, to nearest with ties to even, as FixedQ4816's division rounds it. It is
    // taken in Int128, where neither the offset nor the quotient can wrap (|offset| < 2⁶⁴ raws, so the scaled quotient
    // is under 2⁸⁰ for a cell of at least one raw), so it equals that division wherever the division does not wrap and
    // stays monotone in the coordinate where it would: a box's points fall in the columns between its corners'. The
    // cell is positive (the topology validates it), so its raw is the divisor's magnitude. The answer is clamped to
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
        ((int)Math.Clamp(max: count, min: 0L, value: (first - Reach))),
        ((int)Math.Clamp(max: (count - 1L), min: -1L, value: (last + Reach)))
    );
    private FixedQ4816 Distance(in FixedVector3 point) {
        var cell = m_lattice.CellSize;
        var origin = m_lattice.Origin;
        var fx = ColumnOf(cell: cell, coordinate: point.X, origin: origin.X);
        var fz = ColumnOf(cell: cell, coordinate: point.Z, origin: origin.Z);

        var (firstX, lastX) = ColumnRange(count: m_lattice.Width, first: fx, last: fx);
        var (firstZ, lastZ) = ColumnRange(count: m_lattice.Depth, first: fz, last: fz);
        var best = (cell * FixedQ4816.FromInteger(value: Reach));

        for (var z = firstZ; (z <= lastZ); z++) {
            for (var x = firstX; (x <= lastX); x++) {
                if (m_lattice.ColumnHeight(
                    x: x,
                    z: z
                ) is not { } top) {
                    continue;
                }

                var min = new FixedVector3(
                    X: (origin.X + (cell * FixedQ4816.FromInteger(value: x))),
                    Y: (origin.Y - cell),
                    Z: (origin.Z + (cell * FixedQ4816.FromInteger(value: z)))
                );
                var max = new FixedVector3(
                    X: (min.X + cell),
                    Y: top,
                    Z: (min.Z + cell)
                );
                var distance = BoxDistance(
                    max: in max,
                    min: in min,
                    point: in point
                );

                if (distance < best) {
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
    /// columns between its corners', so every column it can read for a point of the box is one this reads. Each
    /// column's least distance over the box is the length of the gap between the two boxes, computed by the same
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
        var reach = (cell * FixedQ4816.FromInteger(value: Reach));
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

                var min = new FixedVector3(
                    X: (origin.X + (cell * FixedQ4816.FromInteger(value: x))),
                    Y: (origin.Y - cell),
                    Z: (origin.Z + (cell * FixedQ4816.FromInteger(value: z)))
                );
                var max = new FixedVector3(
                    X: (min.X + cell),
                    Y: top,
                    Z: (min.Z + cell)
                );
                var gap = new FixedVector3(
                    X: Gap(columnMaximum: max.X, columnMinimum: min.X, high: high.X, low: low.X, reach: reach),
                    Y: Gap(columnMaximum: max.Y, columnMinimum: min.Y, high: high.Y, low: low.Y, reach: reach),
                    Z: Gap(columnMaximum: max.Z, columnMinimum: min.Z, high: high.Z, low: low.Z, reach: reach)
                );
                var column = ((gap == FixedVector3.Zero)
                    ? -FixedVector3.MaxComponent(value: (max - min))
                    : gap.Length
                );

                if (column < least) {
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
