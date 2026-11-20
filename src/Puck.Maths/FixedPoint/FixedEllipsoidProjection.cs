using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The distance from a point to an axis-aligned ellipse or ellipsoid centred at the origin, answered in Q48.16 as a side
/// and an interval that holds the distance, with a scalar read from the interval. The side comes from the shape's own
/// implicit test, <c>sum((p_i / e_i)²)</c> against one, rounded both ways; a test rounding cannot decide answers "on the
/// surface". The interval comes from one bracketed root (Eberly, "Distance from a Point to an Ellipse, an Ellipsoid, or a
/// Hyperellipsoid"): with <c>m</c> a least axis, <c>gap_i = (e_i - e_m)(e_i + e_m)</c> and
/// <c>w_i(u) = e_i p_i / (u + gap_i)</c>, <c>F(u) = sum(w_i²) - 1</c> falls strictly for <c>u &gt; 0</c> and the foot at
/// <c>u</c> is <c>q_i = e_i w_i</c>, each coordinate falling as <c>u</c> rises. <c>u</c> is measured from the least axis's
/// pole, so no step subtracts two like quantities, and it is carried with 48 fractional bits in 128-bit integers, where
/// the Q48.16 parameter could not resolve a foot beside the major axis inside the shape. Each sign of <c>F</c> is taken
/// only when <c>w_i</c> rounded down and rounded up agree on it, and the search stops at a sign it cannot take, so the
/// root always lies in the bracket the search holds.
/// </summary>
/// <remarks>The shader's root (<c>sdfEllipse2D</c> in <c>field/sdf-ellipse.hlsli</c>) runs the same bracket, pole
/// branch and halving in binary32 after scaling every length by one power of two, and claims no interval.</remarks>
/// <param name="Side">The side: one outside, minus one inside, zero where the implicit test cannot decide.</param>
/// <param name="Lower">The least the distance's magnitude can be.</param>
/// <param name="Upper">The most the distance's magnitude can be: the distance to a point the solve put on the surface,
/// plus what rounding can leave that point off it.</param>
/// <param name="Distance">The signed distance as the search left it, <see cref="Side"/> times the distance to the foot
/// it put on the surface, rounded to nearest: within <see cref="Error"/> of the true distance, and not claimed
/// 1-Lipschitz.</param>
public readonly record struct FixedEllipsoidProjection(int Side, FixedQ4816 Lower, FixedQ4816 Upper, FixedQ4816 Distance) {
    /// <summary>The halvings the root's bracket takes at most: a few geometric halvings across the bracket's
    /// exponent range, then arithmetic ones across the 80 or so bits a bracket of normalized lengths spans.</summary>
    public const int RootSteps = 96;

    // The largest raw a length is solved at: every product of two stays below 2^62 in Q32, so three squared products sum
    // inside 128 bits. A larger point or shape is solved scaled down by a power of two and its interval widened by the
    // move (WidenedBy).
    private const int LengthBits = 31;
    // The fraction bits of a ratio w_i, and the bits of one in its square.
    private const int RatioBits = 62;
    private static readonly UInt128 RatioOne = (UInt128.One << RatioBits);
    private static readonly UInt128 SquareOne = (UInt128.One << (2 * RatioBits));
    // The fraction bits past a raw a foot and its distance are formed at, so the distance rounds to a raw once.
    private const int FootBits = 16;
    // The raws a foot can lie off the surface, rounding included: its coordinates round at FootBits past a raw, and the
    // ratios and their root at 2^-62 of a radius under 2^31 raws, far under one raw together.
    private const long FootSlack = 1L;

    /// <summary>Gets the error of <see cref="Distance"/>: the width of the interval.</summary>
    public FixedQ4816 Error => (Upper - Lower);

    /// <summary>Gets the signed interval that holds the distance: <c>[Lower, Upper]</c> outside, <c>[-Upper, -Lower]</c>
    /// inside, and <c>[-Upper, Upper]</c> on an undecided side.</summary>
    public FixedInterval Signed => ((Side > 0) ? new FixedInterval(lower: Lower, upper: Upper) : ((Side < 0) ? new FixedInterval(lower: -Upper, upper: -Lower) : new FixedInterval(lower: -Upper, upper: Upper)));

    /// <summary>Returns the distance from a point to the ellipse of semi-axes (<paramref name="semiX"/>,
    /// <paramref name="semiY"/>) about the origin.</summary>
    /// <param name="point">The point, in the ellipse's frame.</param>
    /// <param name="semiX">The semi-axis along X; a value below one raw is taken as one raw.</param>
    /// <param name="semiY">The semi-axis along Y; a value below one raw is taken as one raw.</param>
    /// <returns>The side, the interval and the scalar.</returns>
    public static FixedEllipsoidProjection Project(FixedVector2 point, FixedQ4816 semiX, FixedQ4816 semiY) {
        Span<long> radii = [Radius(raw: semiX.Value), Radius(raw: semiY.Value)];
        Span<long> coordinates = [Magnitude(raw: point.X.Value), Magnitude(raw: point.Y.Value)];

        return Solve(radii: radii, point: coordinates);
    }
    /// <summary>Returns the distance from a point to the ellipsoid of radii <paramref name="radii"/> about the
    /// origin.</summary>
    /// <param name="point">The point, in the ellipsoid's frame.</param>
    /// <param name="radii">The radii along X, Y and Z; a component below one raw is taken as one raw.</param>
    /// <returns>The side, the interval and the scalar.</returns>
    public static FixedEllipsoidProjection Project(FixedVector3 point, FixedVector3 radii) {
        Span<long> axes = [Radius(raw: radii.X.Value), Radius(raw: radii.Y.Value), Radius(raw: radii.Z.Value)];
        Span<long> coordinates = [Magnitude(raw: point.X.Value), Magnitude(raw: point.Y.Value), Magnitude(raw: point.Z.Value)];

        return Solve(radii: axes, point: coordinates);
    }

    private static long Radius(long raw) => Math.Max(val1: raw, val2: 1L);
    private static long Magnitude(long raw) => ((raw == long.MinValue) ? long.MaxValue : Math.Abs(value: raw));

    // The side from the original raws, then the interval from raws no wider than LengthBits: scaled down by a power of two
    // where they are wider, which moves the point and the surface by at most the rounding (the distance is 1-Lipschitz in
    // the point, and radii moved by at most d move the surface by at most d).
    private static FixedEllipsoidProjection Solve(Span<long> radii, Span<long> point) {
        var side = SideOf(radii: radii, point: point);

        var widest = 0L;

        foreach (var raw in radii) { widest = Math.Max(val1: widest, val2: raw); }
        foreach (var raw in point) { widest = Math.Max(val1: widest, val2: raw); }

        if (side == 0) {
            // Within the implicit test's rounding of the surface: the squared ratios' error, under 2^-59, moves a point
            // by under that share of the widest length.
            return new(Side: 0, Lower: FixedQ4816.Zero, Upper: FixedQ4816.FromRawBits(value: (FootSlack + (widest >> 59))), Distance: FixedQ4816.Zero);
        }

        var shift = Math.Max(val1: 0, val2: ((64 - BitOperations.LeadingZeroCount(value: (ulong)widest)) - LengthBits));
        var widen = 0L;

        if (shift > 0) {
            var moved = UInt128.Zero;
            var radiusMoved = 0L;

            for (var axis = 0; (axis < radii.Length); axis++) {
                var scaledRadius = Math.Max(val1: RoundShift(raw: radii[axis], shift: shift), val2: 1L);
                var scaledPoint = RoundShift(raw: point[axis], shift: shift);
                var pointMove = Int128.Abs(value: (((Int128)point[axis]) - (((Int128)scaledPoint) << shift)));

                radiusMoved = Math.Max(val1: radiusMoved, val2: (long)Int128.Abs(value: (((Int128)radii[axis]) - (((Int128)scaledRadius) << shift))));
                moved += (UInt128)(pointMove * pointMove);
                (radii[axis], point[axis]) = (scaledRadius, scaledPoint);
            }
            widen = Saturate(value: (((Int128)CeilingRoot(radicand: moved)) + radiusMoved));
        }

        var scaledSide = ((shift > 0) ? SideOf(radii: radii, point: point) : side);
        var (lower, upper, nearest) = ((scaledSide == 0) ? (0L, FootSlack, 0L) : Interval(radii: radii, point: point, side: scaledSide));

        if (shift > 0) {
            lower = Saturate(value: ((((Int128)lower) << shift) - widen));
            upper = Saturate(value: ((((Int128)upper) << shift) + widen));
            nearest = Saturate(value: (((Int128)nearest) << shift));
        }
        lower = Math.Clamp(value: lower, min: 0L, max: upper);

        return new(
            Side: side,
            Lower: FixedQ4816.FromRawBits(value: lower),
            Upper: FixedQ4816.FromRawBits(value: upper),
            Distance: FixedQ4816.FromRawBits(value: (side * Math.Clamp(value: nearest, min: lower, max: upper)))
        );
    }

    // The implicit test's sign, sum((p_i / e_i)²) - 1, rounded both ways: zero where the two roundings disagree.
    private static int SideOf(ReadOnlySpan<long> radii, ReadOnlySpan<long> point) {
        var low = UInt128.Zero;
        var high = UInt128.Zero;

        for (var axis = 0; (axis < radii.Length); axis++) {
            if (point[axis] == 0L) { continue; }
            if (point[axis] > radii[axis]) { return 1; }

            var (down, up) = Ratio(numerator: point[axis], denominator: radii[axis]);

            low += (down * down);
            high += (up * up);
        }

        return SignOf(low: low, high: high) ?? 0;
    }
    // A sum of squared ratios against one: one above, minus one below, zero exactly at one, null where the roundings
    // straddle it.
    private static int? SignOf(UInt128 low, UInt128 high) =>
        ((low > SquareOne) ? 1 : ((high < SquareOne) ? -1 : (((low == SquareOne) && (high == SquareOne)) ? 0 : null)));
    // numerator / denominator, for 0 <= numerator <= denominator < 2^96, at RatioBits fraction bits: the floor and the
    // ceiling, from two exact 31-bit quotient steps.
    private static (UInt128 Down, UInt128 Up) Ratio(Int128 numerator, Int128 denominator) {
        var first = ((numerator << 31) / denominator);
        var rest = ((numerator << 31) - (first * denominator));
        var second = ((rest << 31) / denominator);
        var remainder = ((rest << 31) - (second * denominator));
        var down = ((((UInt128)first) << 31) | ((UInt128)second));

        return (down, ((remainder == Int128.Zero) ? down : (down + UInt128.One)));
    }

    // The root's bracket and the interval it gives, for raws below 2^LengthBits on a decided side.
    private static (long Lower, long Upper, long Nearest) Interval(ReadOnlySpan<long> radii, ReadOnlySpan<long> point, int side) {
        var count = radii.Length;
        var least = radii[0];

        for (var axis = 1; (axis < count); axis++) { least = Math.Min(val1: least, val2: radii[axis]); }

        // Q48 throughout: a product of two Q16 raws is Q32, shifted 16 more.
        Span<Int128> gaps = stackalloc Int128[count];
        Span<Int128> numerators = stackalloc Int128[count];
        var start = Int128.Zero;

        for (var axis = 0; (axis < count); axis++) {
            gaps[axis] = ((((Int128)(radii[axis] - least)) * (radii[axis] + least)) << 16);
            numerators[axis] = ((((Int128)radii[axis]) * point[axis]) << 16);
            // The lower end: e_m p_m, the largest over tied least axes, where that w is one.
            if (gaps[axis] == Int128.Zero) { start = Int128.Max(x: start, y: numerators[axis]); }
        }

        var pole = ((((Int128)least) * least) << 16);
        Int128 low;
        Int128 high;

        if (side > 0) {
            var reach = UInt128.Zero;

            for (var axis = 0; (axis < count); axis++) {
                var product = (UInt128)(((Int128)radii[axis]) * point[axis]);

                reach += (product * product);
            }
            low = Int128.Max(x: start, y: pole);
            high = (((Int128)CeilingRoot(radicand: reach)) << 16);
        } else {
            if (start == Int128.Zero) {
                // Every least axis's coordinate is zero: the root is the pole, u = 0, when the other terms' squares there
                // sum to one or less (inside the evolute's cusp), and otherwise lies above it, where F is positive.
                var (sum, beyond) = PoleSum(gaps: gaps, numerators: numerators);

                if (!beyond && ((SignOf(low: sum.Low, high: sum.High) ?? -1) <= 0)) {
                    return PoleFoot(gaps: gaps, numerators: numerators, point: point, radii: radii, sum: sum.High);
                }
            }
            low = start;
            high = pole;
        }

        for (var step = 0; (step < RootSteps) && (low < high); step++) {
            var middle = Middle(low: low, high: high);

            if ((middle <= low) || (middle >= high)) { break; }

            var sign = SignAt(gaps: gaps, numerators: numerators, u: middle);

            if (sign is not int taken) { break; }
            if (taken > 0) {
                low = middle;
            } else if (taken < 0) {
                high = middle;
            } else {
                (low, high) = (middle, middle);
            }
        }

        // The root lies in [low, high], so the foot lies in the box between the feet at its two ends.
        var gapSquared = UInt128.Zero;

        for (var axis = 0; (axis < count); axis++) {
            var product = (((Int128)radii[axis]) * numerators[axis]);
            var near = (((high + gaps[axis]) == Int128.Zero) ? Int128.Zero : FixedDirectedRounding.FloorQuotient(numerator: product, denominator: (high + gaps[axis])));
            var far = (((low + gaps[axis]) == Int128.Zero) ? Int128.Zero : FixedDirectedRounding.CeilingQuotient(numerator: product, denominator: (low + gaps[axis])));
            var outside = Int128.Max(x: Int128.Zero, y: Int128.Max(x: (near - point[axis]), y: (point[axis] - far)));

            gapSquared += (UInt128)(outside * outside);
        }

        var lower = (long)gapSquared.SquareRoot();
        var (nearest, upper) = MiddleFoot(gaps: gaps, numerators: numerators, point: point, radii: radii, u: (low + ((high - low) / 2)));

        return (lower, upper, nearest);
    }
    // The bracket's next trial: geometric while its ends differ by more than two to one, arithmetic after.
    private static Int128 Middle(Int128 low, Int128 high) {
        if ((low > Int128.Zero) && (high > (2 * low))) {
            var spread = (((int)Int128.Log2(value: high) - (int)Int128.Log2(value: low)) / 2);

            if (spread > 0) { return (low << spread); }
        }

        return (low + ((high - low) / 2));
    }
    // F's sign at u, taken only where every ratio rounded down and up agree; null otherwise. A ratio at or above one makes
    // F positive without squaring it.
    private static int? SignAt(ReadOnlySpan<Int128> gaps, ReadOnlySpan<Int128> numerators, Int128 u) {
        var low = UInt128.Zero;
        var high = UInt128.Zero;

        for (var axis = 0; (axis < gaps.Length); axis++) {
            if (numerators[axis] == Int128.Zero) { continue; }

            var denominator = (u + gaps[axis]);

            if (numerators[axis] > denominator) { return 1; }

            var (down, up) = Ratio(numerator: numerators[axis], denominator: denominator);

            low += (down * down);
            high += (up * up);
        }

        return SignOf(low: low, high: high);
    }
    // The other axes' squared ratios at the pole, u = 0, rounded both ways, and whether one of them reaches past one.
    private static ((UInt128 Low, UInt128 High) Sum, bool Beyond) PoleSum(ReadOnlySpan<Int128> gaps, ReadOnlySpan<Int128> numerators) {
        var low = UInt128.Zero;
        var high = UInt128.Zero;

        for (var axis = 0; (axis < gaps.Length); axis++) {
            if ((gaps[axis] == Int128.Zero) || (numerators[axis] == Int128.Zero)) { continue; }
            if (numerators[axis] > gaps[axis]) { return ((low, high), true); }

            var (down, up) = Ratio(numerator: numerators[axis], denominator: gaps[axis]);

            low += (down * down);
            high += (up * up);
        }

        return ((low, high), false);
    }
    // The foot at the pole: the other axes' coordinates at u = 0, and the rest, e_m sqrt(1 - sum), on one least axis.
    private static (long Lower, long Upper, long Nearest) PoleFoot(ReadOnlySpan<Int128> gaps, ReadOnlySpan<Int128> numerators, ReadOnlySpan<long> point, ReadOnlySpan<long> radii, UInt128 sum) {
        var squared = UInt128.Zero;
        var placed = false;

        for (var axis = 0; (axis < gaps.Length); axis++) {
            Int128 foot;

            if (gaps[axis] != Int128.Zero) {
                foot = RoundedQuotient(numerator: ((((Int128)radii[axis]) * numerators[axis]) << FootBits), denominator: gaps[axis]);
            } else if (!placed) {
                placed = true;

                var rest = ((sum >= SquareOne) ? UInt128.Zero : (SquareOne - sum));

                foot = (Int128)((((UInt128)radii[axis]) * rest.SquareRoot()) >> (RatioBits - FootBits));
            } else {
                foot = Int128.Zero;
            }

            var difference = (foot - (((Int128)point[axis]) << FootBits));

            squared += (UInt128)(difference * difference);
        }

        var (distance, upper) = Measured(squared: squared);

        return (Math.Max(val1: 0L, val2: (distance - FootSlack)), (upper + FootSlack), distance);
    }
    // The foot at u scaled radially onto the surface, and its distance from the point: to nearest, and rounded up with
    // FootSlack for the foot's own rounding.
    private static (long Nearest, long Upper) MiddleFoot(ReadOnlySpan<Int128> gaps, ReadOnlySpan<Int128> numerators, ReadOnlySpan<long> point, ReadOnlySpan<long> radii, Int128 u) {
        var count = gaps.Length;
        Span<UInt128> ratios = stackalloc UInt128[count];
        var sum = UInt128.Zero;

        for (var axis = 0; (axis < count); axis++) {
            var denominator = (u + gaps[axis]);

            ratios[axis] = ((numerators[axis] == Int128.Zero) ? UInt128.Zero : ((numerators[axis] >= denominator) ? RatioOne : Ratio(numerator: numerators[axis], denominator: denominator).Down));
            sum += (ratios[axis] * ratios[axis]);
        }

        var length = sum.SquareRoot();
        var squared = UInt128.Zero;

        for (var axis = 0; (axis < count); axis++) {
            var foot = ((length == UInt128.Zero) ? Int128.Zero : RoundedQuotient(numerator: (Int128)((((UInt128)radii[axis]) * ratios[axis]) << FootBits), denominator: (Int128)length));
            var difference = (foot - (((Int128)point[axis]) << FootBits));

            squared += (UInt128)(difference * difference);
        }

        var (nearest, upper) = Measured(squared: squared);

        return (nearest, (upper + FootSlack));
    }

    private static Int128 RoundedQuotient(Int128 numerator, Int128 denominator) => ((numerator + (denominator / 2)) / denominator);
    // A squared length at FootBits more fraction bits than a raw: its root to the nearest raw, and rounded up.
    private static (long Nearest, long Upper) Measured(UInt128 squared) {
        var root = CeilingRoot(radicand: squared);
        var floor = squared.SquareRoot();
        var half = (UInt128.One << (FootBits - 1));

        return ((long)((floor + half) >> FootBits), (long)((root + ((UInt128.One << FootBits) - UInt128.One)) >> FootBits));
    }
    private static UInt128 CeilingRoot(UInt128 radicand) {
        var root = radicand.SquareRoot();

        return (((root * root) < radicand) ? (root + UInt128.One) : root);
    }
    private static long RoundShift(long raw, int shift) => (long)((((Int128)raw) + (Int128.One << (shift - 1))) >> shift);
    private static long Saturate(Int128 value) => (long)Int128.Clamp(value: value, min: Int128.Zero, max: long.MaxValue);
}
