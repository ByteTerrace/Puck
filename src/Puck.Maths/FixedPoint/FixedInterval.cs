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
/// <para><b>The carrier's extremes are unbounded.</b> A <see cref="Lower"/> of <see cref="FixedQ4816.MinValue"/>
/// stands for −∞ and an <see cref="Upper"/> of <see cref="FixedQ4816.MaxValue"/> for +∞; an exact endpoint past the
/// carrier saturates to that sentinel rather than wrapping. A point operation that wraps the carrier leaves every
/// interval's guarantee, because its answer is no longer the rounding of the exact one.</para>
/// <para><b>Inclusion isotonic.</b> Shrinking an operand never widens a result: every rule reads only the exact
/// extremes of its operands over the interval, which can only move inward.</para>
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

    /// <summary>Creates the interval <c>[<paramref name="lower"/>, <paramref name="upper"/>]</c>.</summary>
    /// <param name="lower">The lower endpoint; <see cref="FixedQ4816.MinValue"/> stands for −∞.</param>
    /// <param name="upper">The upper endpoint; <see cref="FixedQ4816.MaxValue"/> stands for +∞.</param>
    /// <exception cref="ArgumentException"><paramref name="lower"/> exceeds <paramref name="upper"/>.</exception>
    public FixedInterval(FixedQ4816 lower, FixedQ4816 upper) {
        if (lower > upper) {
            throw new ArgumentException(message: $"An interval's lower endpoint {lower} exceeds its upper endpoint {upper}.", paramName: nameof(lower));
        }

        Lower = lower;
        Upper = upper;
    }

    /// <summary>Gets the interval holding every value, <c>[−∞, +∞]</c>.</summary>
    public static FixedInterval Entire => new(
        lower: FixedQ4816.MinValue,
        upper: FixedQ4816.MaxValue
    );
    /// <summary>Gets the lower endpoint; <see cref="FixedQ4816.MinValue"/> stands for −∞.</summary>
    public FixedQ4816 Lower { get; }
    /// <summary>Gets the upper endpoint; <see cref="FixedQ4816.MaxValue"/> stands for +∞.</summary>
    public FixedQ4816 Upper { get; }
    /// <summary>Gets whether the interval is unbounded below.</summary>
    public bool IsUnboundedBelow => (Lower.Value == long.MinValue);
    /// <summary>Gets whether the interval is unbounded above.</summary>
    public bool IsUnboundedAbove => (Upper.Value == long.MaxValue);

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
    /// <returns>The hull of their union.</returns>
    public static FixedInterval Union(FixedInterval first, FixedInterval second) => new(
        lower: FixedQ4816.Min(x: first.Lower, y: second.Lower),
        upper: FixedQ4816.Max(x: first.Upper, y: second.Upper)
    );
    /// <summary>Returns whether the interval holds <paramref name="value"/>.</summary>
    /// <param name="value">The value tested.</param>
    /// <returns><see langword="true"/> when <c>Lower ≤ value ≤ Upper</c>.</returns>
    public bool Contains(FixedQ4816 value) =>
        ((Lower <= value) && (value <= Upper));
    /// <summary>Returns whether the interval holds every value of <paramref name="other"/>.</summary>
    /// <param name="other">The interval tested.</param>
    /// <returns><see langword="true"/> when <paramref name="other"/> is a subset of this interval.</returns>
    public bool Contains(FixedInterval other) =>
        ((Lower <= other.Lower) && (other.Upper <= Upper));

    /// <summary>Returns the interval of every sum.</summary>
    public static FixedInterval operator +(FixedInterval left, FixedInterval right) => new(
        lower: ((left.IsUnboundedBelow || right.IsUnboundedBelow)
            ? FixedQ4816.MinValue
            : Saturate(value: (((Int128)left.Lower.Value) + right.Lower.Value))),
        upper: ((left.IsUnboundedAbove || right.IsUnboundedAbove)
            ? FixedQ4816.MaxValue
            : Saturate(value: (((Int128)left.Upper.Value) + right.Upper.Value)))
    );
    /// <summary>Returns the interval of every negation.</summary>
    public static FixedInterval operator -(FixedInterval value) => new(
        lower: (value.IsUnboundedAbove
            ? FixedQ4816.MinValue
            : Saturate(value: -((Int128)value.Upper.Value))),
        upper: (value.IsUnboundedBelow
            ? FixedQ4816.MaxValue
            : Saturate(value: -((Int128)value.Lower.Value)))
    );
    /// <summary>Returns the interval of every difference, formed directly rather than as a sum with the negation, so a
    /// subtrahend reaching one raw above the carrier's minimum does not negate onto the unbounded sentinel.</summary>
    public static FixedInterval operator -(FixedInterval left, FixedInterval right) => new(
        lower: ((left.IsUnboundedBelow || right.IsUnboundedAbove)
            ? FixedQ4816.MinValue
            : Saturate(value: (((Int128)left.Lower.Value) - right.Upper.Value))),
        upper: ((left.IsUnboundedAbove || right.IsUnboundedBelow)
            ? FixedQ4816.MaxValue
            : Saturate(value: (((Int128)left.Upper.Value) - right.Lower.Value)))
    );
    /// <summary>Returns the interval of every product: the floor of the least exact endpoint product and the ceiling
    /// of the greatest.</summary>
    public static FixedInterval operator *(FixedInterval left, FixedInterval right) {
        if (IsZero(value: left) || IsZero(value: right)) {
            return FromPoint(value: FixedQ4816.Zero);
        }

        if (left.IsUnboundedBelow || left.IsUnboundedAbove || right.IsUnboundedBelow || right.IsUnboundedAbove) {
            return Entire;
        }

        var a = (((Int128)left.Lower.Value) * right.Lower.Value);
        var b = (((Int128)left.Lower.Value) * right.Upper.Value);
        var c = (((Int128)left.Upper.Value) * right.Lower.Value);
        var d = (((Int128)left.Upper.Value) * right.Upper.Value);

        return new(
            lower: FloorShift(value: Int128.Min(x: Int128.Min(x: a, y: b), y: Int128.Min(x: c, y: d)), shift: FractionBitCount),
            upper: CeilingShift(value: Int128.Max(x: Int128.Max(x: a, y: b), y: Int128.Max(x: c, y: d)), shift: FractionBitCount)
        );
    }
    /// <summary>Returns the interval of every quotient, or <see cref="Entire"/> when the divisor holds zero or either
    /// operand is unbounded.</summary>
    public static FixedInterval operator /(FixedInterval left, FixedInterval right) {
        if (right.Contains(value: FixedQ4816.Zero) || left.IsUnboundedBelow || left.IsUnboundedAbove || right.IsUnboundedBelow || right.IsUnboundedAbove) {
            return Entire;
        }

        Span<long> numerators = [left.Lower.Value, left.Upper.Value];
        Span<long> denominators = [right.Lower.Value, right.Upper.Value];
        var lower = long.MaxValue;
        var upper = long.MinValue;

        // A quotient is monotone in each operand over a divisor of one sign, so its extremes sit at the four corners.
        foreach (var numerator in numerators) {
            foreach (var denominator in denominators) {
                var (floor, ceiling) = DirectedQuotient(
                    denominator: denominator,
                    numerator: (((Int128)numerator) << FractionBitCount)
                );

                lower = Math.Min(val1: lower, val2: floor);
                upper = Math.Max(val1: upper, val2: ceiling);
            }
        }

        return new(
            lower: FixedQ4816.FromRawBits(value: lower),
            upper: FixedQ4816.FromRawBits(value: upper)
        );
    }

    /// <summary>Returns the interval of every absolute value.</summary>
    /// <param name="value">The operand.</param>
    /// <returns><c>|value|</c>, which starts at zero when the operand straddles it.</returns>
    public static FixedInterval Abs(FixedInterval value) {
        if (value.Lower.Value >= 0L) {
            return value;
        }

        if (value.Upper.Value <= 0L) {
            return -value;
        }

        var negated = -value;

        return new(
            lower: FixedQ4816.Zero,
            upper: FixedQ4816.Max(x: negated.Upper, y: value.Upper)
        );
    }
    /// <summary>Returns the interval of every square, tighter than a product of the operand with itself because both
    /// factors are the same value.</summary>
    /// <param name="value">The operand.</param>
    /// <returns><c>value²</c>.</returns>
    public static FixedInterval Square(FixedInterval value) {
        var magnitude = Abs(value: value);
        var low = ((Int128)magnitude.Lower.Value);

        return new(
            lower: FloorShift(shift: FractionBitCount, value: (low * low)),
            upper: (magnitude.IsUnboundedAbove
                ? FixedQ4816.MaxValue
                : CeilingShift(value: (((Int128)magnitude.Upper.Value) * magnitude.Upper.Value), shift: FractionBitCount))
        );
    }
    /// <summary>Returns the interval of every square root, with the non-positive part answering zero as
    /// <see cref="FixedQ4816.Sqrt"/> does.</summary>
    /// <param name="value">The operand.</param>
    /// <returns>The floor root of the lower endpoint and the ceiling root of the upper.</returns>
    public static FixedInterval Sqrt(FixedInterval value) => new(
        lower: FixedQ4816.FromRawBits(value: FloorRoot(radicand: (((UInt128)((ulong)Math.Max(val1: 0L, val2: value.Lower.Value))) << FractionBitCount))),
        upper: (value.IsUnboundedAbove
            ? FixedQ4816.MaxValue
            : FixedQ4816.FromRawBits(value: CeilingRoot(radicand: (((UInt128)((ulong)Math.Max(val1: 0L, val2: value.Upper.Value))) << FractionBitCount))))
    );
    /// <summary>Returns the interval of every lesser of two values.</summary>
    public static FixedInterval Min(FixedInterval first, FixedInterval second) => new(
        lower: FixedQ4816.Min(x: first.Lower, y: second.Lower),
        upper: FixedQ4816.Min(x: first.Upper, y: second.Upper)
    );
    /// <summary>Returns the interval of every greater of two values.</summary>
    public static FixedInterval Max(FixedInterval first, FixedInterval second) => new(
        lower: FixedQ4816.Max(x: first.Lower, y: second.Lower),
        upper: FixedQ4816.Max(x: first.Upper, y: second.Upper)
    );
    /// <summary>Returns the interval of every <see cref="FixedQ4816.Round"/> result, which is monotone, so the rounded
    /// endpoints are exact.</summary>
    /// <param name="value">The operand.</param>
    /// <returns>The rounded endpoints; an unbounded end stays unbounded.</returns>
    public static FixedInterval Round(FixedInterval value) => new(
        lower: (value.IsUnboundedBelow
            ? FixedQ4816.MinValue
            : RoundToEvenInteger(raw: value.Lower.Value)),
        upper: (value.IsUnboundedAbove
            ? FixedQ4816.MaxValue
            : RoundToEvenInteger(raw: value.Upper.Value))
    );
    /// <summary>Returns the interval of every <see cref="FixedQ4816.Floor"/> result, which is monotone, so the floored
    /// endpoints are exact.</summary>
    /// <param name="value">The operand.</param>
    /// <returns>The floored endpoints; an unbounded end stays unbounded.</returns>
    public static FixedInterval Floor(FixedInterval value) => new(
        lower: FixedQ4816.Floor(value: value.Lower),
        upper: (value.IsUnboundedAbove
            ? FixedQ4816.MaxValue
            : FixedQ4816.Floor(value: value.Upper))
    );
    /// <summary>Returns the interval of every <see cref="FixedQ4816.Clamp"/> result between two fixed bounds, which is
    /// monotone, so the clamped endpoints are exact.</summary>
    /// <param name="value">The operand.</param>
    /// <param name="minimum">The lower bound, at most <paramref name="maximum"/>.</param>
    /// <param name="maximum">The upper bound.</param>
    /// <returns>The clamped endpoints.</returns>
    public static FixedInterval Clamp(FixedInterval value, FixedQ4816 minimum, FixedQ4816 maximum) => new(
        lower: FixedQ4816.Clamp(value: value.Lower, minimum: minimum, maximum: maximum),
        upper: FixedQ4816.Clamp(value: value.Upper, minimum: minimum, maximum: maximum)
    );
    /// <summary>Returns the interval of every Euclidean length <c>√(x² + y²)</c>, decided on the exact sums of squares
    /// and rooted once in each direction, so it holds every <see cref="FixedVector2"/> length the box reaches.</summary>
    public static FixedInterval Magnitude(FixedInterval x, FixedInterval y) => Magnitude(
        x: x,
        y: y,
        z: FromPoint(value: FixedQ4816.Zero)
    );
    /// <summary>Returns the interval of every Euclidean length <c>√(x² + y² + z²)</c>: the floor root of the least exact
    /// sum of squares the box reaches and the ceiling root of the greatest, so it holds every
    /// <see cref="FixedVector3.Length"/> inside the box.</summary>
    public static FixedInterval Magnitude(FixedInterval x, FixedInterval y, FixedInterval z) {
        var least = ((LeastSquare(value: x) + LeastSquare(value: y)) + LeastSquare(value: z));

        if (x.IsUnboundedBelow || x.IsUnboundedAbove || y.IsUnboundedBelow || y.IsUnboundedAbove || z.IsUnboundedBelow || z.IsUnboundedAbove) {
            return new(
                lower: FixedQ4816.FromRawBits(value: FloorRoot(radicand: least)),
                upper: FixedQ4816.MaxValue
            );
        }

        var greatest = ((GreatestSquare(value: x) + GreatestSquare(value: y)) + GreatestSquare(value: z));

        return new(
            lower: FixedQ4816.FromRawBits(value: FloorRoot(radicand: least)),
            upper: FixedQ4816.FromRawBits(value: CeilingRoot(radicand: greatest))
        );
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
        var holdsZeroOrdinate = y.Contains(value: FixedQ4816.Zero);

        if (
            (holdsZeroOrdinate && x.Contains(value: FixedQ4816.Zero)) ||
            ((x.Lower.Value < 0L) && (y.Lower.Value < 0L) && holdsZeroOrdinate) ||
            y.IsUnboundedBelow || y.IsUnboundedAbove || x.IsUnboundedBelow || x.IsUnboundedAbove
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
        var (low, high) = ClampToUnit(value: value);

        return new(
            lower: FixedQ4816.FromRawBits(value: ArcCosineEndpoint(cosine: high, lower: true)),
            upper: FixedQ4816.FromRawBits(value: ArcCosineEndpoint(cosine: low, lower: false))
        );
    }
    /// <inheritdoc/>
    public override string ToString() => $"[{Lower}, {Upper}]";

    // FixedQ4816.Round's ties-to-even integer, saturated where the rounded value passes the carrier rather than thrown.
    private static FixedQ4816 RoundToEvenInteger(long raw) {
        var integer = (raw >> FractionBitCount);
        var fraction = raw & (RawOne - 1L);
        var half = (RawOne >> 1);

        if ((fraction > half) || ((fraction == half) && ((integer & 1L) != 0L))) {
            ++integer;
        }

        return Saturate(value: (((Int128)integer) << FractionBitCount));
    }
    private static bool IsZero(FixedInterval value) =>
        ((value.Lower.Value == 0L) && (value.Upper.Value == 0L));
    private static FixedQ4816 Saturate(Int128 value) => FixedQ4816.FromRawBits(value: ((value > long.MaxValue)
        ? long.MaxValue
        : ((value < long.MinValue)
            ? long.MinValue
            : ((long)value))));
    // Int128's arithmetic shift is the floor; the ceiling is the negated floor of the negation.
    private static FixedQ4816 FloorShift(Int128 value, int shift) =>
        Saturate(value: (value >> shift));
    private static FixedQ4816 CeilingShift(Int128 value, int shift) =>
        Saturate(value: -((-value) >> shift));
    // The floor and ceiling of numerator/denominator for a non-zero denominator, saturated to the carrier.
    private static (long Floor, long Ceiling) DirectedQuotient(Int128 numerator, long denominator) {
        var quotient = Int128.DivRem(left: numerator, right: denominator);
        var truncated = quotient.Quotient;
        var floor = truncated;
        var ceiling = truncated;

        if (quotient.Remainder != Int128.Zero) {
            // Truncation rounds toward zero: a positive exact quotient's floor is the truncation, a negative one's the
            // truncation less one, and the ceiling the other way about.
            if ((quotient.Remainder < Int128.Zero) != (denominator < 0L)) {
                --floor;
            } else {
                ++ceiling;
            }
        }

        return (Saturate(value: floor).Value, Saturate(value: ceiling).Value);
    }
    private static long FloorRoot(UInt128 radicand) {
        var root = radicand.SquareRoot();

        return ((root > ((UInt128)long.MaxValue))
            ? long.MaxValue
            : ((long)root));
    }
    private static long CeilingRoot(UInt128 radicand) {
        var root = radicand.SquareRoot();

        if ((root * root) != radicand) {
            ++root;
        }

        return ((root > ((UInt128)long.MaxValue))
            ? long.MaxValue
            : ((long)root));
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

        // A span of a whole turn or more reaches both a crest and a trough, as does an unbounded one.
        if (
            angle.IsUnboundedBelow || angle.IsUnboundedAbove ||
            ((((Int128)angle.Upper.Value) - angle.Lower.Value) >= ((((Int128)2) * FixedQ4816.PiQ61) >> (FixedQ4816.PiQ61FractionBitCount - FractionBitCount)))
        ) {
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

        return (FloorRoot(radicand: radicand), CeilingRoot(radicand: radicand));
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
