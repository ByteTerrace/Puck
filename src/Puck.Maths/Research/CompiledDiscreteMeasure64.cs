using System.Numerics;

namespace Puck.Maths;

/// <summary>The reason an exact <see cref="DiscreteMeasure"/> could not enter the bounded compiled representation.</summary>
public enum DiscreteMeasureCompilationFailure {
    /// <summary>Compilation succeeded.</summary>
    None,
    /// <summary>The source rate is quadratic irrational and exceeds the bounded quadratic backend's coefficient envelope.</summary>
    IrrationalRate,
    /// <summary>The source offset is quadratic irrational and exceeds the bounded quadratic backend's coefficient envelope.</summary>
    IrrationalOffset,
    /// <summary>A normalized coefficient does not fit signed 64-bit storage.</summary>
    CoefficientOutOfRange,
}

/// <summary>
/// An allocation-free, bounded execution form of <see cref="DiscreteMeasure"/> over signed 64-bit indices and results.
/// </summary>
/// <remarks>
/// <para>
/// The rational backend separates the rate into an integral part plus a proper fraction. The bounded quadratic backend
/// clears the source denominators once at compilation and admits it only when every signed-long core-domain query has a
/// root fitting <see cref="Int128"/>. Its exact square-root operand is held in two <see cref="UInt128"/> limbs; neither
/// backend uses <see cref="BigInteger"/> at runtime.
/// </para>
/// <para>
/// Every <c>Try</c> method returns <see langword="false"/> when its mathematical result lies outside signed 64-bit
/// storage, or when an inverse has no answer inside the signed-64-bit index domain. Throwing counterparts distinguish
/// invalid arguments from result overflow. Range amounts are evaluated directly from 128-bit boundaries, so they can
/// succeed even when either cumulative endpoint would not fit a <see cref="long"/>.
/// </para>
/// <para>
/// The default value is invalid and rejects queries. Quadratic inputs outside the proved fixed-width envelope fail
/// compilation explicitly; wider exact inputs remain available through <see cref="DiscreteMeasure"/>.
/// </para>
/// </remarks>
public readonly record struct CompiledDiscreteMeasure64 {
    private readonly BackendKind m_backend;
    private readonly long m_quadraticRateRational;
    private readonly long m_quadraticRateSurd;
    private readonly long m_quadraticOffsetRational;
    private readonly long m_quadraticOffsetSurd;
    private readonly long m_quadraticDenominator;
    private readonly ulong m_quadraticRadicand;
    private readonly long m_period;

    private CompiledDiscreteMeasure64(
        long integralRate,
        long fractionalRateNumerator,
        long fractionalRateDenominator,
        long offsetNumerator,
        long offsetDenominator) {
        IntegralRate = integralRate;
        FractionalRateNumerator = fractionalRateNumerator;
        FractionalRateDenominator = fractionalRateDenominator;
        OffsetNumerator = offsetNumerator;
        OffsetDenominator = offsetDenominator;
        m_period = fractionalRateDenominator;
        m_backend = BackendKind.Rational;
    }

    private CompiledDiscreteMeasure64(
        long rateRational,
        long rateSurd,
        long offsetRational,
        long offsetSurd,
        long denominator,
        ulong radicand,
        long period) {
        m_quadraticRateRational = rateRational;
        m_quadraticRateSurd = rateSurd;
        m_quadraticOffsetRational = offsetRational;
        m_quadraticOffsetSurd = offsetSurd;
        m_quadraticDenominator = denominator;
        m_quadraticRadicand = radicand;
        m_period = period;
        m_backend = BackendKind.Quadratic;
    }

    /// <summary>Whether this value was produced by successful compilation.</summary>
    public bool IsValid => (m_backend != BackendKind.Invalid);
    /// <summary>Whether the unit-interval allocation repeats periodically.</summary>
    public bool IsPeriodic => IsValid
        ? (m_period > 0L)
        : throw new InvalidOperationException(message: "the compiled measure is default-initialized");
    /// <summary>Whether this value uses the bounded real-quadratic floor kernel.</summary>
    public bool IsQuadratic => m_backend == BackendKind.Quadratic;
    /// <summary>The integral part of the non-negative rate for the rational backend; zero for the quadratic backend.</summary>
    public long IntegralRate { get; }
    /// <summary>The numerator of the rate's reduced proper fractional part for the rational backend.</summary>
    public long FractionalRateNumerator { get; }
    /// <summary>The positive denominator of the rate's reduced proper fractional part.</summary>
    public long FractionalRateDenominator { get; }
    /// <summary>The numerator of the normalized rational offset in <c>[0, 1)</c>.</summary>
    public long OffsetNumerator { get; }
    /// <summary>The positive denominator of the normalized rational offset.</summary>
    public long OffsetDenominator { get; }
    /// <summary>The exact period of the unit-interval allocation.</summary>
    public long Period => !IsValid
        ? throw new InvalidOperationException(message: "the compiled measure is default-initialized")
        : IsPeriodic
            ? m_period
            : throw new InvalidOperationException(message: "an irrational-rate measure is aperiodic");

    internal static bool TryCompile(
        DiscreteMeasure source,
        out CompiledDiscreteMeasure64 compiled,
        out DiscreteMeasureCompilationFailure failure) {
        if (!source.Rate.IsRational || !source.Offset.IsRational) {
            return TryCompileQuadratic(source, out compiled, out failure);
        }

        var integralRate = source.Rate.Floor();
        var fractionalNumerator = (
            source.Rate.RationalNumerator -
            (integralRate * source.Rate.Denominator)
        );

        if (!TryNonNegativeInt64(value: integralRate, result: out var boundedIntegralRate) ||
            !TryNonNegativeInt64(value: fractionalNumerator, result: out var boundedFractionalNumerator) ||
            !TryPositiveInt64(value: source.Rate.Denominator, result: out var boundedFractionalDenominator) ||
            !TryNonNegativeInt64(value: source.Offset.RationalNumerator, result: out var boundedOffsetNumerator) ||
            !TryPositiveInt64(value: source.Offset.Denominator, result: out var boundedOffsetDenominator)) {
            compiled = default;
            failure = DiscreteMeasureCompilationFailure.CoefficientOutOfRange;
            return false;
        }

        compiled = new CompiledDiscreteMeasure64(
            integralRate: boundedIntegralRate,
            fractionalRateNumerator: boundedFractionalNumerator,
            fractionalRateDenominator: boundedFractionalDenominator,
            offsetNumerator: boundedOffsetNumerator,
            offsetDenominator: boundedOffsetDenominator
        );
        failure = DiscreteMeasureCompilationFailure.None;
        return true;
    }

    private static bool TryCompileQuadratic(
        DiscreteMeasure source,
        out CompiledDiscreteMeasure64 compiled,
        out DiscreteMeasureCompilationFailure failure) {
        var denominator = LeastCommonMultiple(source.Rate.Denominator, source.Offset.Denominator);
        var rateScale = (denominator / source.Rate.Denominator);
        var offsetScale = (denominator / source.Offset.Denominator);
        var rateRational = (source.Rate.RationalNumerator * rateScale);
        var rateSurd = (source.Rate.SurdNumerator * rateScale);
        var offsetRational = (source.Offset.RationalNumerator * offsetScale);
        var offsetSurd = (source.Offset.SurdNumerator * offsetScale);
        var radicand = source.Rate.IsRational ? source.Offset.Radicand : source.Rate.Radicand;

        if (!TryInt64Coefficient(rateRational, out var boundedRateRational) ||
            !TryInt64Coefficient(rateSurd, out var boundedRateSurd) ||
            !TryInt64Coefficient(offsetRational, out var boundedOffsetRational) ||
            !TryInt64Coefficient(offsetSurd, out var boundedOffsetSurd) ||
            !TryPositiveInt64(denominator, out var boundedDenominator) ||
            (radicand <= BigInteger.One) || (radicand > ulong.MaxValue)) {
            compiled = default;
            failure = DiscreteMeasureCompilationFailure.CoefficientOutOfRange;
            return false;
        }

        // Cumulative, unit lookup, and inverse probes need at most one boundary beyond either signed-long endpoint.
        // Prove at compile time that those core-domain products fit the bounded exact floor kernel. Wider range endpoints
        // are checked per query and may still return false without throwing.
        var maximumIndexMagnitude = ((BigInteger.One << 63) + 1);
        var maximumRationalMagnitude =
            (BigInteger.Abs(rateRational) * maximumIndexMagnitude) + BigInteger.Abs(offsetRational);
        var maximumSurdMagnitude =
            (BigInteger.Abs(rateSurd) * maximumIndexMagnitude) + BigInteger.Abs(offsetSurd);
        var maximumRootRadicand = (maximumSurdMagnitude * maximumSurdMagnitude * radicand);
        var maximumBoundedRootRadicand = (BigInteger)Int128.MaxValue * Int128.MaxValue;
        if ((maximumRationalMagnitude > (BigInteger)Int128.MaxValue) ||
            (maximumRootRadicand > maximumBoundedRootRadicand)) {
            compiled = default;
            failure = source.Rate.IsRational
                ? DiscreteMeasureCompilationFailure.IrrationalOffset
                : DiscreteMeasureCompilationFailure.IrrationalRate;
            return false;
        }

        var period = source.Rate.IsRational && (source.Rate.Denominator <= long.MaxValue)
            ? (long)source.Rate.Denominator
            : 0L;
        compiled = new CompiledDiscreteMeasure64(
            boundedRateRational,
            boundedRateSurd,
            boundedOffsetRational,
            boundedOffsetSurd,
            boundedDenominator,
            (ulong)radicand,
            period
        );
        failure = DiscreteMeasureCompilationFailure.None;
        return true;
    }

    /// <summary>Attempts to return the cumulative amount at signed integer boundary <paramref name="index"/>.</summary>
    public bool TryCumulative(long index, out long cumulative) {
        cumulative = 0L;
        return TryBoundary(index, out var boundary) && TryInt64(value: boundary, result: out cumulative);
    }

    /// <summary>Returns the cumulative amount at signed integer boundary <paramref name="index"/>.</summary>
    /// <exception cref="InvalidOperationException">This value is default-initialized.</exception>
    /// <exception cref="OverflowException">The exact cumulative amount does not fit a <see cref="long"/>.</exception>
    public long Cumulative(long index) {
        ThrowIfInvalid();
        if (TryCumulative(index: index, cumulative: out var cumulative)) { return cumulative; }
        throw new OverflowException(message: "the cumulative amount exceeds signed 64-bit storage");
    }

    /// <summary>Attempts to return the amount assigned to unit interval <c>[index, index + 1)</c>.</summary>
    /// <remarks><paramref name="index"/> may be <see cref="long.MaxValue"/>; its exclusive boundary is held in 128 bits.</remarks>
    public bool TryAmountAt(long index, out long amount) {
        ThrowIfInvalid();

        if (m_backend == BackendKind.Quadratic) {
            amount = 0L;
            var first = (Int128)index;
            return TryBoundary(first, out var start) &&
                TryBoundary(first + Int128.One, out var end) &&
                TryDifferenceInt64(end, start, out amount);
        }

        // One floor division, not two boundary evaluations: advance the proper-fraction remainder by p directly.
        // Since 0 <= remainder,p < q, the advance crosses at most one q boundary.
        var scaledFraction = (((Int128)FractionalRateNumerator) * index);
        var (_, remainder) = FloorDivRem(
            numerator: scaledFraction,
            denominator: FractionalRateDenominator
        );
        var advanced = (((Int128)remainder) + FractionalRateNumerator);
        var fractionalAdvance = Int128.Zero;
        if (advanced >= FractionalRateDenominator) {
            advanced -= FractionalRateDenominator;
            fractionalAdvance = Int128.One;
        }
        var offsetChange = (
            (OffsetCarry(fractionalRemainder: ((long)advanced)) ? Int128.One : Int128.Zero) -
            (OffsetCarry(fractionalRemainder: remainder) ? Int128.One : Int128.Zero)
        );

        return TryInt64(
            value: (((Int128)IntegralRate) + fractionalAdvance + offsetChange),
            result: out amount
        );
    }

    /// <summary>Returns the amount assigned to unit interval <c>[index, index + 1)</c>.</summary>
    /// <exception cref="InvalidOperationException">This value is default-initialized.</exception>
    /// <exception cref="OverflowException">The exact amount does not fit a <see cref="long"/>.</exception>
    public long AmountAt(long index) {
        if (TryAmountAt(index: index, amount: out var amount)) { return amount; }
        throw new OverflowException(message: "the unit-interval amount exceeds signed 64-bit storage");
    }

    /// <summary>Attempts to return the amount assigned to <c>[start, start + length)</c>.</summary>
    /// <returns>
    /// <see langword="false"/> when <paramref name="length"/> is negative or the exact amount exceeds signed 64-bit
    /// storage.
    /// </returns>
    public bool TryAmountOver(long start, long length, out long amount) {
        ThrowIfInvalid();
        if (length < 0) {
            amount = 0;
            return false;
        }

        var first = ((Int128)start);
        var end = (first + length);
        amount = 0L;
        return TryBoundary(first, out var firstBoundary) &&
            TryBoundary(end, out var endBoundary) &&
            TryDifferenceInt64(endBoundary, firstBoundary, out amount);
    }

    /// <summary>Returns the amount assigned to <c>[start, start + length)</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">This value is default-initialized.</exception>
    /// <exception cref="OverflowException">The exact range amount does not fit a <see cref="long"/>.</exception>
    public long AmountOver(long start, long length) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: length);
        if (TryAmountOver(start: start, length: length, amount: out var amount)) { return amount; }
        throw new OverflowException(message: "the range amount exceeds signed 64-bit storage");
    }

    /// <summary>Attempts to return the amount assigned to half-open interval <c>[start, end)</c>.</summary>
    public bool TryAmountBetween(long start, long end, out long amount) {
        ThrowIfInvalid();
        if (end < start) {
            amount = 0;
            return false;
        }

        amount = 0L;
        return TryBoundary(start, out var firstBoundary) &&
            TryBoundary(end, out var endBoundary) &&
            TryDifferenceInt64(endBoundary, firstBoundary, out amount);
    }

    /// <summary>Returns the amount assigned to half-open interval <c>[start, end)</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="end"/> precedes <paramref name="start"/>.</exception>
    /// <exception cref="InvalidOperationException">This value is default-initialized.</exception>
    /// <exception cref="OverflowException">The exact interval amount does not fit a <see cref="long"/>.</exception>
    public long AmountBetween(long start, long end) {
        if (end < start) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(end),
                message: "the end boundary must not precede the start boundary"
            );
        }
        if (TryAmountBetween(start: start, end: end, amount: out var amount)) { return amount; }
        throw new OverflowException(message: "the interval amount exceeds signed 64-bit storage");
    }

    /// <summary>Attempts to map <c>[start, start + length)</c> to a signed-64-bit output interval.</summary>
    public bool TryMap(long start, long length, out long mappedStart, out long mappedLength) {
        ThrowIfInvalid();
        if ((length < 0) ||
            !TryCumulative(index: start, cumulative: out mappedStart) ||
            !TryAmountOver(start: start, length: length, amount: out mappedLength)) {
            mappedStart = 0;
            mappedLength = 0;
            return false;
        }
        return true;
    }

    /// <summary>Maps <c>[start, start + length)</c> to a signed-64-bit output interval.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">This value is default-initialized.</exception>
    /// <exception cref="OverflowException">The mapped start or length does not fit a <see cref="long"/>.</exception>
    public (long Start, long Length) Map(long start, long length) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: length);
        if (TryMap(start: start, length: length, mappedStart: out var mappedStart, mappedLength: out var mappedLength)) {
            return (Start: mappedStart, Length: mappedLength);
        }
        throw new OverflowException(message: "the mapped interval exceeds signed 64-bit storage");
    }

    /// <summary>
    /// Attempts to find the least signed-64-bit boundary whose cumulative amount is at least <paramref name="amount"/>.
    /// </summary>
    /// <remarks>The bounded inverse uses at most 64 monotone boundary probes and performs no allocation.</remarks>
    public bool TryLowerBound(long amount, out long index) {
        ThrowIfInvalid();
        if (IsZeroRate) {
            index = 0;
            return false;
        }

        return TryLowerBoundCore(amount: amount, index: out index);
    }

    /// <summary>Returns the least signed-64-bit boundary whose cumulative amount is at least <paramref name="amount"/>.</summary>
    /// <exception cref="InvalidOperationException">The rate is zero or this value is default-initialized.</exception>
    /// <exception cref="OverflowException">The mathematical lower bound lies outside the signed-64-bit index domain.</exception>
    public long LowerBound(long amount) {
        ThrowIfInvalid();
        if (IsZeroRate) { throw new InvalidOperationException(message: "the zero measure has no inverse"); }
        if (TryLowerBoundCore(amount: amount, index: out var index)) { return index; }
        throw new OverflowException(message: "the lower-bound index lies outside signed 64-bit storage");
    }

    /// <summary>Attempts to return the signed-64-bit input interval that owns <paramref name="outputIndex"/>.</summary>
    public bool TryIndexContaining(long outputIndex, out long inputIndex) {
        ThrowIfInvalid();
        if (IsZeroRate ||
            !TryLowerBoundCore(amount: ((Int128)outputIndex + Int128.One), index: out var upper) ||
            (upper == long.MinValue)) {
            inputIndex = 0;
            return false;
        }

        inputIndex = (upper - 1L);
        return true;
    }

    /// <summary>Returns the signed-64-bit input interval that owns <paramref name="outputIndex"/>.</summary>
    /// <exception cref="InvalidOperationException">The rate is zero or this value is default-initialized.</exception>
    /// <exception cref="OverflowException">The owning interval lies outside the signed-64-bit index domain.</exception>
    public long IndexContaining(long outputIndex) {
        ThrowIfInvalid();
        if (IsZeroRate) { throw new InvalidOperationException(message: "the zero measure assigns no output index"); }
        if (TryIndexContaining(outputIndex: outputIndex, inputIndex: out var inputIndex)) { return inputIndex; }
        throw new OverflowException(message: "the owning interval lies outside signed 64-bit storage");
    }

    private bool IsZeroRate => m_backend switch {
        BackendKind.Rational => (IntegralRate == 0L) && (FractionalRateNumerator == 0L),
        BackendKind.Quadratic => (m_quadraticRateRational == 0L) && (m_quadraticRateSurd == 0L),
        _ => false,
    };

    private bool TryBoundary(Int128 index, out Int128 boundary) {
        ThrowIfInvalid();

        if (m_backend == BackendKind.Quadratic) {
            try {
                var rationalNumerator = checked(((Int128)m_quadraticRateRational * index) + m_quadraticOffsetRational);
                var surdNumerator = checked(((Int128)m_quadraticRateSurd * index) + m_quadraticOffsetSurd);
                return TryQuadraticFloor(
                    rationalNumerator,
                    surdNumerator,
                    m_quadraticRadicand,
                    m_quadraticDenominator,
                    out boundary
                );
            } catch (OverflowException) {
                boundary = Int128.Zero;
                return false;
            }
        }

        var whole = (((Int128)IntegralRate) * index);
        var scaledFraction = (((Int128)FractionalRateNumerator) * index);
        var (fractionalQuotient, fractionalRemainder) = FloorDivRem(
            numerator: scaledFraction,
            denominator: FractionalRateDenominator
        );
        var carry = (OffsetCarry(fractionalRemainder: fractionalRemainder) ? Int128.One : Int128.Zero);

        boundary = (whole + fractionalQuotient + carry);
        return true;
    }

    private bool OffsetCarry(long fractionalRemainder) =>
        (
            ((((Int128)fractionalRemainder) * OffsetDenominator) +
             (((Int128)OffsetNumerator) * FractionalRateDenominator)) >=
            (((Int128)FractionalRateDenominator) * OffsetDenominator)
        );

    private bool TryLowerBoundCore(Int128 amount, out long index) {
        var minimum = ((Int128)long.MinValue);
        var maximum = ((Int128)long.MaxValue);
        if (!TryBoundary(minimum, out var minimumBoundary)) {
            index = 0;
            return false;
        }

        if (minimumBoundary >= amount) {
            if (TryBoundary(minimum - Int128.One, out var precedingBoundary) && precedingBoundary < amount) {
                index = long.MinValue;
                return true;
            }

            index = 0;
            return false;
        }
        if (!TryBoundary(maximum, out var maximumBoundary) || maximumBoundary < amount) {
            index = 0;
            return false;
        }

        var lower = minimum;
        var upper = maximum;
        while ((upper - lower) > Int128.One) {
            var middle = (lower + ((upper - lower) >> 1));
            if (!TryBoundary(middle, out var middleBoundary)) {
                index = 0;
                return false;
            }
            if (middleBoundary >= amount) {
                upper = middle;
            } else {
                lower = middle;
            }
        }

        index = ((long)upper);
        return true;
    }

    private void ThrowIfInvalid() {
        if (!IsValid) {
            throw new InvalidOperationException(message: "the compiled measure is default-initialized");
        }
    }

    private static bool TryQuadraticFloor(
        Int128 rationalNumerator,
        Int128 surdNumerator,
        ulong radicand,
        long denominator,
        out Int128 floor) {
        var surdMagnitude = UnsignedMagnitude(surdNumerator);
        var surdSquare = UInt256.Multiply(surdMagnitude, surdMagnitude);
        if (!surdSquare.TryMultiply(radicand, out var rootRadicand)) {
            floor = Int128.Zero;
            return false;
        }

        var rootFloorUnsigned = rootRadicand.SquareRoot();
        if (rootFloorUnsigned > (UInt128)Int128.MaxValue) {
            floor = Int128.Zero;
            return false;
        }

        var rootFloor = (Int128)rootFloorUnsigned;
        var exactRoot = UInt256.Multiply(rootFloorUnsigned, rootFloorUnsigned) == rootRadicand;
        try {
            var lowerNumerator = surdNumerator >= Int128.Zero
                ? checked(rationalNumerator + rootFloor)
                : checked(rationalNumerator - rootFloor - (exactRoot ? Int128.Zero : Int128.One));
            var candidate = FloorDivRem(lowerNumerator, denominator).Quotient;
            var threshold = checked(((candidate + Int128.One) * denominator) - rationalNumerator);
            bool reachesNext;

            if (surdNumerator >= Int128.Zero) {
                if (threshold <= Int128.Zero) {
                    reachesNext = true;
                } else {
                    var magnitude = (UInt128)threshold;
                    reachesNext = rootRadicand >= UInt256.Multiply(magnitude, magnitude);
                }
            } else {
                if (threshold > Int128.Zero) {
                    reachesNext = false;
                } else {
                    var magnitude = UnsignedMagnitude(threshold);
                    reachesNext = rootRadicand <= UInt256.Multiply(magnitude, magnitude);
                }
            }

            floor = reachesNext ? checked(candidate + Int128.One) : candidate;
            return true;
        } catch (OverflowException) {
            floor = Int128.Zero;
            return false;
        }
    }

    private static UInt128 UnsignedMagnitude(Int128 value) => value >= Int128.Zero
        ? (UInt128)value
        : ((UInt128)(-(value + Int128.One)) + UInt128.One);

    private static (Int128 Quotient, long Remainder) FloorDivRem(Int128 numerator, long denominator) {
        var quotient = (numerator / denominator);
        var remainder = (numerator % denominator);
        if (remainder < Int128.Zero) {
            quotient -= Int128.One;
            remainder += denominator;
        }
        return (Quotient: quotient, Remainder: ((long)remainder));
    }

    private static bool TryInt64(Int128 value, out long result) {
        if ((value < long.MinValue) || (value > long.MaxValue)) {
            result = 0L;
            return false;
        }
        result = ((long)value);
        return true;
    }

    private static bool TryDifferenceInt64(Int128 left, Int128 right, out long result) {
        try {
            return TryInt64(checked(left - right), out result);
        } catch (OverflowException) {
            result = 0L;
            return false;
        }
    }

    private static bool TryNonNegativeInt64(BigInteger value, out long result) {
        if ((value.Sign < 0) || (value > long.MaxValue)) {
            result = 0L;
            return false;
        }
        result = ((long)value);
        return true;
    }

    private static bool TryPositiveInt64(BigInteger value, out long result) {
        if ((value.Sign <= 0) || (value > long.MaxValue)) {
            result = 0L;
            return false;
        }
        result = ((long)value);
        return true;
    }

    private static bool TryInt64Coefficient(BigInteger value, out long result) {
        if ((value < long.MinValue) || (value > long.MaxValue)) {
            result = 0L;
            return false;
        }
        result = (long)value;
        return true;
    }

    private static BigInteger LeastCommonMultiple(BigInteger left, BigInteger right) =>
        ((left / BigInteger.GreatestCommonDivisor(left, right)) * right);

    private enum BackendKind : byte {
        Invalid,
        Rational,
        Quadratic,
    }

    private readonly record struct UInt256(UInt128 High, UInt128 Low) : IComparable<UInt256> {
        public static UInt256 Multiply(UInt128 left, UInt128 right) {
            var leftLow = (ulong)left;
            var leftHigh = (ulong)(left >> 64);
            var rightLow = (ulong)right;
            var rightHigh = (ulong)(right >> 64);
            var lowProduct = ((UInt128)leftLow * rightLow);
            var leftCross = ((UInt128)leftHigh * rightLow);
            var rightCross = ((UInt128)leftLow * rightHigh);
            var middle = (lowProduct >> 64) + (ulong)leftCross + (ulong)rightCross;
            var low = ((UInt128)(ulong)lowProduct) | (middle << 64);
            var high = ((UInt128)leftHigh * rightHigh) +
                (leftCross >> 64) + (rightCross >> 64) + (middle >> 64);
            return new UInt256(high, low);
        }

        public bool TryMultiply(ulong factor, out UInt256 result) {
            var lowProduct = Multiply(Low, factor);
            var highProduct = Multiply(High, factor);
            if (highProduct.High != UInt128.Zero ||
                UInt128.MaxValue - lowProduct.High < highProduct.Low) {
                result = default;
                return false;
            }
            result = new UInt256(lowProduct.High + highProduct.Low, lowProduct.Low);
            return true;
        }

        public UInt128 SquareRoot() {
            if (High == UInt128.Zero) {
                return Low.SquareRoot();
            }

            var remainder = default(UInt256);
            var root = UInt128.Zero;
            var pairIndex = ((127 + BitLength(High)) >> 1);
            for (; pairIndex >= 0; --pairIndex) {
                remainder = remainder.ShiftLeftTwoBits().WithLowBits(TwoBits(pairIndex));
                root <<= 1;
                var trial = new UInt256(root >> 127, (root << 1) | UInt128.One);
                if (remainder >= trial) {
                    remainder -= trial;
                    root += UInt128.One;
                }
            }
            return root;
        }

        private static int BitLength(UInt128 value) {
            var high = ((ulong)(value >> 64));
            return high != 0UL
                ? (128 - BitOperations.LeadingZeroCount(high))
                : (64 - BitOperations.LeadingZeroCount((ulong)value));
        }

        public int CompareTo(UInt256 other) {
            var highComparison = High.CompareTo(other.High);
            return highComparison != 0 ? highComparison : Low.CompareTo(other.Low);
        }

        public static bool operator <(UInt256 left, UInt256 right) => left.CompareTo(right) < 0;
        public static bool operator >(UInt256 left, UInt256 right) => left.CompareTo(right) > 0;
        public static bool operator <=(UInt256 left, UInt256 right) => left.CompareTo(right) <= 0;
        public static bool operator >=(UInt256 left, UInt256 right) => left.CompareTo(right) >= 0;

        public static UInt256 operator -(UInt256 left, UInt256 right) {
            var borrow = left.Low < right.Low ? UInt128.One : UInt128.Zero;
            return new UInt256(left.High - right.High - borrow, left.Low - right.Low);
        }

        private UInt256 ShiftLeftTwoBits() => new(
            (High << 2) | (Low >> 126),
            Low << 2
        );

        private UInt256 WithLowBits(uint bits) => new(High, Low | bits);

        private uint TwoBits(int pairIndex) {
            var bitIndex = (pairIndex * 2);
            return bitIndex < 128
                ? (uint)((Low >> bitIndex) & 3)
                : (uint)((High >> (bitIndex - 128)) & 3);
        }
    }
}
