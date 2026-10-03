namespace Puck.Maths;

/// <summary>
/// A closed interval <c>[Lower, Upper]</c> of Q48.16 values with outward-rounded arithmetic: every operation returns an
/// interval holding the exact real result of the operation at every pair of real operands inside its inputs, and with
/// it every round-to-nearest <see cref="FixedQ4816"/> result at representable operands inside them, so a bound read
/// from the interval holds for the exact field and for the fixed-point evaluation of it alike.
/// </summary>
/// <remarks>
/// <para><b>Directed, not padded.</b> Each endpoint is the ONE directed rounding of an exact extreme: the floor for the
/// lower, the ceiling for the upper, taken on the exact Int128 product, quotient or root. An endpoint lies within one
/// raw of the exact extreme, and equals it whenever it is representable. The transcendentals read the shipped
/// round-to-nearest kernel at each endpoint and widen it by one raw, because each is pinned within one raw of the true
/// value (<c>scalar.sincos-vs-series</c> at 0.50000001 ULP, <c>scalar.atan2-vs-series</c> at 0.75 ULP).</para>
/// <para><b>Overflow is the top, and the top absorbs.</b> An operation whose exact hull leaves the carrier answers
/// <see cref="Entire"/>, the unbounded interval, rather than a saturated or wrapped endpoint, and every operation given
/// <see cref="Entire"/> answers <see cref="Entire"/>, the bounded ones (a clamp, a minimum, a sine, a product with
/// zero) included. An overflow anywhere in a computation therefore reaches its result: a bounded result proves that no
/// step of it left the carrier, and so that the round-to-nearest point evaluation of the same steps never wrapped. The
/// carrier's extremes are ordinary values of a bounded interval.</para>
/// <para><b>Inclusion isotonic.</b> Shrinking an operand never widens a result: every rule reads only the exact
/// extremes of its operands over the interval, which can only move inward, and a bounded operand inside an unbounded
/// one only moves a result down from <see cref="Entire"/>.</para>
/// </remarks>
public readonly record struct FixedInterval {
    private const int FractionBitCount = FixedQ4816.FractionBitCount;
    private const long RawOne = (1L << FractionBitCount);
    // round(π·2¹⁶) is 205887, an angle just below π; the enclosing raw of the half turn is one above it.
    private const long HalfTurnCeilingRaw = 205888L;
    // The slack, in raws, by which a critical angle (2k + 1)·π/2 or k·π counts as inside an interval. Its position is
    // formed from PiQ61, off by at most half a Q61 unit per π, so at the largest |k| the carrier reaches it is off by
    // under three raws; four keeps every true critical angle counted, at the cost of sometimes counting one that lies
    // just outside, which only widens the answer to the unit bound it would have reached a raw later.
    private const long CriticalAngleSlackRaw = 4L;

    /// <summary>Creates the bounded interval <c>[<paramref name="lower"/>, <paramref name="upper"/>]</c>.</summary>
    /// <param name="lower">The lower endpoint.</param>
    /// <param name="upper">The upper endpoint.</param>
    /// <exception cref="ArgumentException"><paramref name="lower"/> exceeds <paramref name="upper"/>.</exception>
    public FixedInterval(FixedQ4816 lower, FixedQ4816 upper) {
        if (lower > upper) {
            throw new ArgumentException(message: $"An interval's lower endpoint {lower} exceeds its upper endpoint {upper}.", paramName: nameof(lower));
        }

        Lower = lower;
        Upper = upper;
        IsUnbounded = false;
    }

    private FixedInterval(bool unbounded) {
        Lower = FixedQ4816.MinValue;
        Upper = FixedQ4816.MaxValue;
        IsUnbounded = unbounded;
    }

    /// <summary>Gets the unbounded interval, every real value: the answer to any operation whose exact hull leaves the
    /// carrier, and to any operation given it.</summary>
    public static FixedInterval Entire { get; } = new(unbounded: true);
    /// <summary>Gets the lower endpoint; <see cref="FixedQ4816.MinValue"/> when <see cref="IsUnbounded"/>, where it
    /// bounds nothing.</summary>
    public FixedQ4816 Lower { get; }
    /// <summary>Gets the upper endpoint; <see cref="FixedQ4816.MaxValue"/> when <see cref="IsUnbounded"/>, where it
    /// bounds nothing.</summary>
    public FixedQ4816 Upper { get; }
    /// <summary>Gets whether this is <see cref="Entire"/>: a computation that left the carrier, whose endpoints bound
    /// nothing.</summary>
    public bool IsUnbounded { get; }

    /// <summary>Returns the degenerate interval holding one value.</summary>
    /// <param name="value">The value.</param>
    /// <returns><c>[value, value]</c>.</returns>
    public static FixedInterval FromPoint(FixedQ4816 value) => new(
        lower: value,
        upper: value
    );
    /// <summary>Returns the smallest interval holding two values, in either order.</summary>
    /// <param name="first">One value.</param>
    /// <param name="second">The other value.</param>
    /// <returns><c>[min, max]</c> of the two.</returns>
    public static FixedInterval Hull(FixedQ4816 first, FixedQ4816 second) => ((first <= second)
        ? new(lower: first, upper: second)
        : new(lower: second, upper: first));
    /// <summary>Returns the smallest interval holding both intervals.</summary>
    /// <param name="first">One interval.</param>
    /// <param name="second">The other interval.</param>
    /// <returns>The hull of their union; <see cref="Entire"/> when either is.</returns>
    public static FixedInterval Union(FixedInterval first, FixedInterval second) => ((first.IsUnbounded || second.IsUnbounded)
        ? Entire
        : new(
            lower: FixedQ4816.Min(x: first.Lower, y: second.Lower),
            upper: FixedQ4816.Max(x: first.Upper, y: second.Upper)
        ));
    /// <summary>Returns whether the interval holds <paramref name="value"/>.</summary>
    /// <param name="value">The value tested.</param>
    /// <returns><see langword="true"/> when <c>Lower ≤ value ≤ Upper</c>, and always for <see cref="Entire"/>.</returns>
    public bool Contains(FixedQ4816 value) =>
        (IsUnbounded || ((Lower <= value) && (value <= Upper)));
    /// <summary>Returns whether the interval holds every value of <paramref name="other"/>.</summary>
    /// <param name="other">The interval tested.</param>
    /// <returns><see langword="true"/> when <paramref name="other"/> is a subset of this interval: always for
    /// <see cref="Entire"/>, and never for a bounded interval asked about <see cref="Entire"/>.</returns>
    public bool Contains(FixedInterval other) =>
        (IsUnbounded || (!other.IsUnbounded && (Lower <= other.Lower) && (other.Upper <= Upper)));

    /// <summary>Returns the interval of every sum.</summary>
    public static FixedInterval operator +(FixedInterval left, FixedInterval right) => ((left.IsUnbounded || right.IsUnbounded)
        ? Entire
        : Exact(
            lower: (((Int128)left.Lower.Value) + right.Lower.Value),
            upper: (((Int128)left.Upper.Value) + right.Upper.Value)
        ));
    /// <summary>Returns the interval of every negation; <see cref="Entire"/> when the operand reaches the carrier's
    /// minimum, whose negation the carrier does not hold.</summary>
    public static FixedInterval operator -(FixedInterval value) => (value.IsUnbounded
        ? Entire
        : Exact(
            lower: -((Int128)value.Upper.Value),
            upper: -((Int128)value.Lower.Value)
        ));
    /// <summary>Returns the interval of every difference.</summary>
    public static FixedInterval operator -(FixedInterval left, FixedInterval right) => ((left.IsUnbounded || right.IsUnbounded)
        ? Entire
        : Exact(
            lower: (((Int128)left.Lower.Value) - right.Upper.Value),
            upper: (((Int128)left.Upper.Value) - right.Lower.Value)
        ));
    /// <summary>Returns the interval of every product: the floor of the least exact endpoint product and the ceiling
    /// of the greatest.</summary>
    public static FixedInterval operator *(FixedInterval left, FixedInterval right) {
        if (left.IsUnbounded || right.IsUnbounded) {
            return Entire;
        }

        var a = (((Int128)left.Lower.Value) * right.Lower.Value);
        var b = (((Int128)left.Lower.Value) * right.Upper.Value);
        var c = (((Int128)left.Upper.Value) * right.Lower.Value);
        var d = (((Int128)left.Upper.Value) * right.Upper.Value);

        return Exact(
            lower: (Int128.Min(x: Int128.Min(x: a, y: b), y: Int128.Min(x: c, y: d)) >> FractionBitCount),
            upper: Ceiling(value: Int128.Max(x: Int128.Max(x: a, y: b), y: Int128.Max(x: c, y: d)), shift: FractionBitCount)
        );
    }
    /// <summary>Returns the interval of every quotient, or <see cref="Entire"/> when the divisor holds zero.</summary>
    public static FixedInterval operator /(FixedInterval left, FixedInterval right) {
        if (left.IsUnbounded || right.IsUnbounded || right.Contains(value: FixedQ4816.Zero)) {
            return Entire;
        }

        Span<long> numerators = [left.Lower.Value, left.Upper.Value];
        Span<long> denominators = [right.Lower.Value, right.Upper.Value];
        var lower = Int128.MaxValue;
        var upper = Int128.MinValue;

        // A quotient is monotone in each operand over a divisor of one sign, so its extremes sit at the four corners.
        foreach (var numerator in numerators) {
            foreach (var denominator in denominators) {
                var (floor, ceiling) = DirectedQuotient(
                    denominator: denominator,
                    numerator: (((Int128)numerator) << FractionBitCount)
                );

                lower = Int128.Min(x: lower, y: floor);
                upper = Int128.Max(x: upper, y: ceiling);
            }
        }

        return Exact(lower: lower, upper: upper);
    }

    /// <summary>Returns the interval of every absolute value.</summary>
    /// <param name="value">The operand.</param>
    /// <returns><c>|value|</c>, which starts at zero when the operand straddles it; <see cref="Entire"/> when the operand
    /// reaches the carrier's minimum.</returns>
    public static FixedInterval Abs(FixedInterval value) {
        if (value.IsUnbounded) {
            return Entire;
        }

        if (value.Lower.Value >= 0L) {
            return value;
        }

        if (value.Upper.Value <= 0L) {
            return -value;
        }

        return Exact(
            lower: Int128.Zero,
            upper: Int128.Max(x: -((Int128)value.Lower.Value), y: value.Upper.Value)
        );
    }
    /// <summary>Returns the interval of every square, tighter than a product of the operand with itself because both
    /// factors are the same value.</summary>
    /// <param name="value">The operand.</param>
    /// <returns><c>value²</c>.</returns>
    public static FixedInterval Square(FixedInterval value) {
        var magnitude = Abs(value: value);

        if (magnitude.IsUnbounded) {
            return Entire;
        }

        var low = ((Int128)magnitude.Lower.Value);
        var high = ((Int128)magnitude.Upper.Value);

        return Exact(
            lower: ((low * low) >> FractionBitCount),
            upper: Ceiling(shift: FractionBitCount, value: (high * high))
        );
    }
    /// <summary>Returns the interval of every square root, with the non-positive part answering zero as
    /// <see cref="FixedQ4816.Sqrt"/> does.</summary>
    /// <param name="value">The operand.</param>
    /// <returns>The floor root of the lower endpoint and the ceiling root of the upper.</returns>
    public static FixedInterval Sqrt(FixedInterval value) => (value.IsUnbounded
        ? Entire
        : Exact(
            lower: FloorRoot(radicand: (((UInt128)((ulong)Math.Max(val1: 0L, val2: value.Lower.Value))) << FractionBitCount)),
            upper: CeilingRoot(radicand: (((UInt128)((ulong)Math.Max(val1: 0L, val2: value.Upper.Value))) << FractionBitCount))
        ));
    /// <summary>Returns the interval of every lesser of two values.</summary>
    public static FixedInterval Min(FixedInterval first, FixedInterval second) => ((first.IsUnbounded || second.IsUnbounded)
        ? Entire
        : new(
            lower: FixedQ4816.Min(x: first.Lower, y: second.Lower),
            upper: FixedQ4816.Min(x: first.Upper, y: second.Upper)
        ));
    /// <summary>Returns the interval of every greater of two values.</summary>
    public static FixedInterval Max(FixedInterval first, FixedInterval second) => ((first.IsUnbounded || second.IsUnbounded)
        ? Entire
        : new(
            lower: FixedQ4816.Max(x: first.Lower, y: second.Lower),
            upper: FixedQ4816.Max(x: first.Upper, y: second.Upper)
        ));
    /// <summary>Returns the interval of every <see cref="FixedQ4816.Round"/> result, which is monotone, so the rounded
    /// endpoints are exact.</summary>
    /// <param name="value">The operand.</param>
    /// <returns>The rounded endpoints; <see cref="Entire"/> where rounding up passes the carrier.</returns>
    public static FixedInterval Round(FixedInterval value) => (value.IsUnbounded
        ? Entire
        : Exact(
            lower: RoundToEvenInteger(raw: value.Lower.Value),
            upper: RoundToEvenInteger(raw: value.Upper.Value)
        ));
    /// <summary>Returns the interval of every <see cref="FixedQ4816.Floor"/> result, which is monotone, so the floored
    /// endpoints are exact.</summary>
    /// <param name="value">The operand.</param>
    /// <returns>The floored endpoints.</returns>
    public static FixedInterval Floor(FixedInterval value) => (value.IsUnbounded
        ? Entire
        : new(
            lower: FixedQ4816.Floor(value: value.Lower),
            upper: FixedQ4816.Floor(value: value.Upper)
        ));
    /// <summary>Returns the interval of every <see cref="FixedQ4816.Clamp"/> result between two fixed bounds, which is
    /// monotone, so the clamped endpoints are exact.</summary>
    /// <param name="value">The operand.</param>
    /// <param name="minimum">The lower bound, at most <paramref name="maximum"/>.</param>
    /// <param name="maximum">The upper bound.</param>
    /// <returns>The clamped endpoints; <see cref="Entire"/> for an unbounded operand, whose overflow a clamp must not
    /// hide.</returns>
    public static FixedInterval Clamp(FixedInterval value, FixedQ4816 minimum, FixedQ4816 maximum) => (value.IsUnbounded
        ? Entire
        : new(
            lower: FixedQ4816.Clamp(value: value.Lower, minimum: minimum, maximum: maximum),
            upper: FixedQ4816.Clamp(value: value.Upper, minimum: minimum, maximum: maximum)
        ));
    /// <summary>Returns the interval of every Euclidean length <c>√(x² + y²)</c>, decided on the exact sums of squares
    /// and rooted once in each direction, so it holds every <see cref="FixedVector2"/> length the box reaches.</summary>
    public static FixedInterval Magnitude(FixedInterval x, FixedInterval y) => Magnitude(
        x: x,
        y: y,
        z: FromPoint(value: FixedQ4816.Zero)
    );
    /// <summary>Returns the interval of every Euclidean length <c>√(x² + y² + z²)</c>: the floor root of the least exact
    /// sum of squares the box reaches and the ceiling root of the greatest, so it holds every
    /// <see cref="FixedVector3.Length"/> inside the box. <see cref="Entire"/> where the greatest length passes the
    /// carrier, where the point length saturates rather than answering it.</summary>
    public static FixedInterval Magnitude(FixedInterval x, FixedInterval y, FixedInterval z) {
        if (x.IsUnbounded || y.IsUnbounded || z.IsUnbounded) {
            return Entire;
        }

        var least = ((LeastSquare(value: x) + LeastSquare(value: y)) + LeastSquare(value: z));
        var greatest = ((GreatestSquare(value: x) + GreatestSquare(value: y)) + GreatestSquare(value: z));

        return Exact(
            lower: FloorRoot(radicand: least),
            upper: CeilingRoot(radicand: greatest)
        );
    }
    /// <summary>Returns the interval of every power <c>x^y</c> over a non-negative base and a positive exponent.</summary>
    /// <param name="value">The base interval; every value at least zero.</param>
    /// <param name="exponent">The exponent interval; every value above zero.</param>
    /// <returns>The shipped <see cref="FixedQ4816.Pow"/> at the corners, widened by the pinned envelope, or
    /// <see cref="Entire"/> when a base can be negative, an exponent can be zero or below, or a corner saturates.</returns>
    /// <remarks>Over a non-negative base and a positive exponent the exact power rises with the base, and with the
    /// exponent above a base of one and falls with it below, so its extremes sit at the box's corners. The shipped power
    /// at any point of the box is within <c>scalar.pow-envelope</c>'s envelope of the exact one there: half a raw plus
    /// <c>2⁻⁴⁴</c> of the result, plus the exponent's own quantization, below <c>|y|·(⌈|log₂ x|⌉ + 2)·2⁻⁶⁷</c> of the
    /// result in raw units of <c>y</c>, where <c>|log₂ x| ≤ 48</c> over the carrier. So it lies within two envelopes of
    /// the shipped power at the extreme corner, each measured at the greatest result and the greatest exponent; both
    /// corners are widened by three raws, <c>2⁻⁴²</c> of the greatest corner and <c>y_max·2⁻⁵⁹</c> of it, which covers
    /// the two envelopes with room. A base of exactly zero answers exactly zero.</remarks>
    public static FixedInterval Pow(FixedInterval value, FixedInterval exponent) {
        if (value.IsUnbounded || exponent.IsUnbounded || (value.Lower.Value < 0L) || (exponent.Lower.Value <= 0L)) {
            return Entire;
        }

        // Zero to a positive power is exactly zero, on both sides.
        if (value.Upper.Value == 0L) {
            return value;
        }

        Span<long> bases = [value.Lower.Value, value.Upper.Value];
        Span<long> exponents = [exponent.Lower.Value, exponent.Upper.Value];
        var least = long.MaxValue;
        var greatest = long.MinValue;

        foreach (var x in bases) {
            foreach (var y in exponents) {
                var corner = FixedQ4816.Pow(x: FixedQ4816.FromRawBits(value: x), y: FixedQ4816.FromRawBits(value: y)).Value;

                // A saturated corner is a power past the carrier.
                if (corner == long.MaxValue) {
                    return Entire;
                }

                least = Math.Min(val1: least, val2: corner);
                greatest = Math.Max(val1: greatest, val2: corner);
            }
        }

        var margin = ((3 + (((Int128)greatest) >> 42)) + ((((Int128)greatest) * exponent.Upper.Value) >> 59));

        // A power of a non-negative base is never negative.
        return Exact(lower: Int128.Max(x: Int128.Zero, y: (least - margin)), upper: (greatest + margin));
    }
    /// <summary>Returns the interval of every sine.</summary>
    /// <param name="angle">The angle interval, in radians.</param>
    /// <returns>The shipped sine at each endpoint widened by one raw, opened to ±1 wherever the interval reaches a
    /// crest or trough.</returns>
    public static FixedInterval Sin(FixedInterval angle) => Circular(
        angle: angle,
        cosine: false
    );
    /// <summary>Returns the interval of every cosine.</summary>
    /// <param name="angle">The angle interval, in radians.</param>
    /// <returns>The shipped cosine at each endpoint widened by one raw, opened to ±1 wherever the interval reaches a
    /// crest or trough.</returns>
    public static FixedInterval Cos(FixedInterval angle) => Circular(
        angle: angle,
        cosine: true
    );
    /// <summary>Returns the interval of every angle <see cref="FixedQ4816.Atan2"/> names over the box
    /// <c><paramref name="y"/> × <paramref name="x"/></c>.</summary>
    /// <param name="y">The ordinate interval.</param>
    /// <param name="x">The abscissa interval.</param>
    /// <returns>The whole circle when the box holds the origin or crosses the negative real axis below it (where the
    /// angle jumps from −π to π); otherwise the extreme corner angles widened by one raw, because the angle over a box
    /// clear of the origin and the cut takes its extremes at corners.</returns>
    public static FixedInterval Atan2(FixedInterval y, FixedInterval x) {
        var circle = new FixedInterval(
            lower: FixedQ4816.FromRawBits(value: -HalfTurnCeilingRaw),
            upper: FixedQ4816.FromRawBits(value: HalfTurnCeilingRaw)
        );

        if (y.IsUnbounded || x.IsUnbounded) {
            return Entire;
        }

        var holdsZeroOrdinate = y.Contains(value: FixedQ4816.Zero);

        if (
            (holdsZeroOrdinate && x.Contains(value: FixedQ4816.Zero)) ||
            ((x.Lower.Value < 0L) && (y.Lower.Value < 0L) && holdsZeroOrdinate)
        ) {
            return circle;
        }

        Span<long> corners = [
            FixedQ4816.Atan2(y: y.Lower, x: x.Lower).Value,
            FixedQ4816.Atan2(y: y.Lower, x: x.Upper).Value,
            FixedQ4816.Atan2(y: y.Upper, x: x.Lower).Value,
            FixedQ4816.Atan2(y: y.Upper, x: x.Upper).Value,
        ];
        var lower = long.MaxValue;
        var upper = long.MinValue;

        foreach (var corner in corners) {
            lower = Math.Min(val1: lower, val2: corner);
            upper = Math.Max(val1: upper, val2: corner);
        }

        return new(
            lower: FixedQ4816.FromRawBits(value: Math.Max(val1: -HalfTurnCeilingRaw, val2: (lower - 1L))),
            upper: FixedQ4816.FromRawBits(value: Math.Min(val1: HalfTurnCeilingRaw, val2: (upper + 1L)))
        );
    }
    /// <summary>Returns the interval of every arcsine, the operand first clamped to <c>[−1, 1]</c>.</summary>
    /// <param name="value">The operand interval.</param>
    /// <returns><c>asin</c> over the clamped interval, in <c>[−π/2, π/2]</c>.</returns>
    /// <remarks>Formed as <c>atan2(v, √(1 − v²))</c> at each endpoint with the root bracketed by its exact floor and
    /// ceiling: the angle is monotone in the root, so the two brackets hold the exact angle, and each is the shipped
    /// arctangent within one raw.</remarks>
    public static FixedInterval Asin(FixedInterval value) {
        if (value.IsUnbounded) {
            return Entire;
        }

        var (low, high) = ClampToUnit(value: value);

        return new(
            lower: FixedQ4816.FromRawBits(value: ArcEndpoint(lower: true, sine: low)),
            upper: FixedQ4816.FromRawBits(value: ArcEndpoint(lower: false, sine: high))
        );
    }
    /// <summary>Returns the interval of every arccosine, the operand first clamped to <c>[−1, 1]</c>.</summary>
    /// <param name="value">The operand interval.</param>
    /// <returns><c>acos</c> over the clamped interval, in <c>[0, π]</c>.</returns>
    /// <remarks>Formed as <c>atan2(√(1 − v²), v)</c> at each endpoint with the root bracketed by its exact floor and
    /// ceiling, as <see cref="Asin"/> is; the arccosine falls as the operand rises.</remarks>
    public static FixedInterval Acos(FixedInterval value) {
        if (value.IsUnbounded) {
            return Entire;
        }

        var (low, high) = ClampToUnit(value: value);

        return new(
            lower: FixedQ4816.FromRawBits(value: ArcCosineEndpoint(cosine: high, lower: true)),
            upper: FixedQ4816.FromRawBits(value: ArcCosineEndpoint(cosine: low, lower: false))
        );
    }
    /// <inheritdoc/>
    public override string ToString() => (IsUnbounded
        ? "(−∞, +∞)"
        : $"[{Lower}, {Upper}]");

    // The bounded interval of two exact raw endpoints, or the top when either leaves the carrier.
    private static FixedInterval Exact(Int128 lower, Int128 upper) =>
        (((lower < long.MinValue) || (upper > long.MaxValue))
            ? Entire
            : new(
                lower: FixedQ4816.FromRawBits(value: ((long)lower)),
                upper: FixedQ4816.FromRawBits(value: ((long)upper))
            ));
    // FixedQ4816.Round's ties-to-even integer, exact and unsaturated.
    private static Int128 RoundToEvenInteger(long raw) {
        var integer = (raw >> FractionBitCount);
        var fraction = raw & (RawOne - 1L);
        var half = (RawOne >> 1);

        if ((fraction > half) || ((fraction == half) && ((integer & 1L) != 0L))) {
            ++integer;
        }

        return (((Int128)integer) << FractionBitCount);
    }
    // Int128's arithmetic shift is the floor; the ceiling is the negated floor of the negation.
    private static Int128 Ceiling(Int128 value, int shift) =>
        -((-value) >> shift);
    // The floor and ceiling of numerator/denominator for a non-zero denominator.
    private static (Int128 Floor, Int128 Ceiling) DirectedQuotient(Int128 numerator, long denominator) {
        var quotient = Int128.DivRem(left: numerator, right: denominator);
        var floor = quotient.Quotient;
        var ceiling = quotient.Quotient;

        if (quotient.Remainder != Int128.Zero) {
            // Truncation rounds toward zero: a positive exact quotient's floor is the truncation, a negative one's the
            // truncation less one, and the ceiling the other way about.
            if ((quotient.Remainder < Int128.Zero) != (denominator < 0L)) {
                --floor;
            } else {
                ++ceiling;
            }
        }

        return (floor, ceiling);
    }
    private static Int128 FloorRoot(UInt128 radicand) =>
        ((Int128)radicand.SquareRoot());
    private static Int128 CeilingRoot(UInt128 radicand) {
        var root = radicand.SquareRoot();

        if ((root * root) != radicand) {
            ++root;
        }

        return ((Int128)root);
    }
    // The least and greatest exact squares over an interval, in raw² units; a magnitude of 2⁶³ squares to 2¹²⁶.
    private static UInt128 LeastSquare(FixedInterval value) {
        if (value.Contains(value: FixedQ4816.Zero)) {
            return UInt128.Zero;
        }

        var magnitude = ((value.Lower.Value > 0L)
            ? ((ulong)value.Lower.Value)
            : RawMagnitude(value: value.Upper.Value));

        return (((UInt128)magnitude) * magnitude);
    }
    private static UInt128 GreatestSquare(FixedInterval value) {
        var magnitude = Math.Max(val1: RawMagnitude(value: value.Lower.Value), val2: RawMagnitude(value: value.Upper.Value));

        return (((UInt128)magnitude) * magnitude);
    }
    private static ulong RawMagnitude(long value) =>
        ((value < 0L)
            ? (0UL - unchecked((ulong)value))
            : ((ulong)value));
    private static FixedInterval Circular(FixedInterval angle, bool cosine) {
        var unit = new FixedInterval(
            lower: FixedQ4816.FromRawBits(value: -RawOne),
            upper: FixedQ4816.FromRawBits(value: RawOne)
        );

        if (angle.IsUnbounded) {
            return Entire;
        }

        // A span of a whole turn or more reaches both a crest and a trough.
        if ((((Int128)angle.Upper.Value) - angle.Lower.Value) >= ((((Int128)2) * FixedQ4816.PiQ61) >> (FixedQ4816.PiQ61FractionBitCount - FractionBitCount))) {
            return unit;
        }

        var atLower = (cosine
            ? FixedQ4816.Cos(angle: angle.Lower)
            : FixedQ4816.Sin(angle: angle.Lower)).Value;
        var atUpper = (cosine
            ? FixedQ4816.Cos(angle: angle.Upper)
            : FixedQ4816.Sin(angle: angle.Upper)).Value;
        var lower = (Math.Min(val1: atLower, val2: atUpper) - 1L);
        var upper = (Math.Max(val1: atLower, val2: atUpper) + 1L);

        // The extrema sit at multiples of a half turn offset by a quarter turn for the sine, and at the multiples
        // themselves for the cosine: the k-th lies at (2k + offset)·π/2, a crest for k even and a trough for k odd.
        var (reachesCrest, reachesTrough) = ReachesExtrema(
            angle: angle,
            offset: (cosine
                ? 0
                : 1)
        );

        return new(
            lower: FixedQ4816.FromRawBits(value: (reachesTrough
                ? -RawOne
                : Math.Max(val1: -RawOne, val2: lower))),
            upper: FixedQ4816.FromRawBits(value: (reachesCrest
                ? RawOne
                : Math.Min(val1: RawOne, val2: upper)))
        );
    }
    // Whether [lower − slack, upper + slack] reaches an angle (2k + offset)·π/2 with k even (a crest) or odd (a trough),
    // compared at Q61 against k·π + offset·π/2 formed from PiQ61. Twice the angle is compared with (2k + offset)·PiQ61
    // so the half turn's odd last bit never rounds.
    private static (bool Crest, bool Trough) ReachesExtrema(FixedInterval angle, int offset) {
        const int Shift = (FixedQ4816.PiQ61FractionBitCount - FractionBitCount);
        var low = ((((Int128)angle.Lower.Value) - CriticalAngleSlackRaw) << (Shift + 1));
        var high = ((((Int128)angle.Upper.Value) + CriticalAngleSlackRaw) << (Shift + 1));
        var pi = ((Int128)FixedQ4816.PiQ61);
        // The first k whose angle may reach the interval: the floor of low/(2π), stepped back once for safety.
        var first = (Int128.DivRem(left: low, right: (2 * pi)).Quotient - 2);
        var crest = false;
        var trough = false;

        for (var k = first; (k <= (first + 6)); ++k) {
            var position = (((2 * k) + offset) * pi);

            if ((position >= low) && (position <= high)) {
                if (Int128.IsEvenInteger(value: k)) {
                    crest = true;
                } else {
                    trough = true;
                }
            }
        }

        return (crest, trough);
    }
    private static (long Low, long High) ClampToUnit(FixedInterval value) => (
        Math.Clamp(value: value.Lower.Value, min: -RawOne, max: RawOne),
        Math.Clamp(value: value.Upper.Value, min: -RawOne, max: RawOne)
    );
    // √(1 − v²) at Q16 bracketed by its exact floor and ceiling: 1 − v² is exact at Q32.
    private static (long Floor, long Ceiling) UnitComplementRoot(long value) {
        var radicand = ((UInt128)((ulong)((1L << (2 * FractionBitCount)) - (value * value))));

        return (((long)FloorRoot(radicand: radicand)), ((long)CeilingRoot(radicand: radicand)));
    }
    private static long ArcEndpoint(long sine, bool lower) {
        var (floor, ceiling) = UnitComplementRoot(value: sine);
        var atFloor = FixedQ4816.Atan2(y: FixedQ4816.FromRawBits(value: sine), x: FixedQ4816.FromRawBits(value: floor)).Value;
        var atCeiling = FixedQ4816.Atan2(y: FixedQ4816.FromRawBits(value: sine), x: FixedQ4816.FromRawBits(value: ceiling)).Value;

        return (lower
            ? (Math.Min(val1: atFloor, val2: atCeiling) - 1L)
            : (Math.Max(val1: atFloor, val2: atCeiling) + 1L));
    }
    private static long ArcCosineEndpoint(long cosine, bool lower) {
        var (floor, ceiling) = UnitComplementRoot(value: cosine);
        var atFloor = FixedQ4816.Atan2(y: FixedQ4816.FromRawBits(value: floor), x: FixedQ4816.FromRawBits(value: cosine)).Value;
        var atCeiling = FixedQ4816.Atan2(y: FixedQ4816.FromRawBits(value: ceiling), x: FixedQ4816.FromRawBits(value: cosine)).Value;

        return (lower
            ? Math.Max(val1: 0L, val2: (Math.Min(val1: atFloor, val2: atCeiling) - 1L))
            : Math.Min(val1: HalfTurnCeilingRaw, val2: (Math.Max(val1: atFloor, val2: atCeiling) + 1L)));
    }
}
