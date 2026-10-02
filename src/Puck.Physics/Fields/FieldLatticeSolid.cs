using Puck.Maths;

namespace Puck.Physics.Fields;

/// <summary>A contact field over a <see cref="FieldLattice"/>'s height columns: the signed distance to the union
/// of column boxes, exact within two cells of a column and a conservative lower bound beyond.</summary>
public sealed class FieldLatticeSolid : IFieldEvaluator, IFieldBounds {
    private const int Reach = 2;

    private readonly FieldLattice m_lattice;

    public FieldLatticeSolid(FieldLattice lattice) {
        ArgumentNullException.ThrowIfNull(argument: lattice);

        m_lattice = lattice;
    }

    /// <inheritdoc/>
    public FieldEvaluatorCapabilities Capabilities => new(WarpFree: true);

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
    // The column index a coordinate falls in, the point query's own formula: monotone in the coordinate, so a box's
    // points fall in the columns between its corners'.
    private static int ColumnOf(FixedQ4816 coordinate, FixedQ4816 origin, FixedQ4816 cell) =>
        ((int)(FixedQ4816.Floor(value: ((coordinate - origin) / cell)).Value >> 16));
    private FixedQ4816 Distance(in FixedVector3 point) {
        var cell = m_lattice.CellSize;
        var origin = m_lattice.Origin;
        var fx = ColumnOf(cell: cell, coordinate: point.X, origin: origin.X);
        var fz = ColumnOf(cell: cell, coordinate: point.Z, origin: origin.Z);
        var best = (cell * FixedQ4816.FromInteger(value: Reach));

        for (var z = (fz - Reach); (z <= (fz + Reach)); z++) {
            if (
                (z < 0) ||
                (z >= m_lattice.Depth)
            ) {
                continue;
            }

            for (var x = (fx - Reach); (x <= (fx + Reach)); x++) {
                if (
                    (x < 0) ||
                    (x >= m_lattice.Width) ||
                    (m_lattice.ColumnHeight(
                    x: x,
                    z: z
                ) is not { } top)
                ) {
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
        var firstX = Math.Max(val1: 0, val2: (ColumnOf(cell: cell, coordinate: low.X, origin: origin.X) - Reach));
        var lastX = Math.Min(val1: (m_lattice.Width - 1), val2: (ColumnOf(cell: cell, coordinate: high.X, origin: origin.X) + Reach));
        var firstZ = Math.Max(val1: 0, val2: (ColumnOf(cell: cell, coordinate: low.Z, origin: origin.Z) - Reach));
        var lastZ = Math.Min(val1: (m_lattice.Depth - 1), val2: (ColumnOf(cell: cell, coordinate: high.Z, origin: origin.Z) + Reach));

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
                var gap = FixedVector3.Max(
                    left: FixedVector3.Max(
                        left: (min - high),
                        right: (low - max)
                    ),
                    right: FixedVector3.Zero
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
