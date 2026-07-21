using System.Numerics;

namespace Puck.Maths;

/// <summary>The reason an exact <see cref="DiscreteMeasure"/> could not enter the bounded compiled representation.</summary>
public enum DiscreteMeasureCompilationFailure {
    /// <summary>Compilation succeeded.</summary>
    None,
    /// <summary>The source rate is quadratic irrational; the current bounded backend is rational.</summary>
    IrrationalRate,
    /// <summary>The source offset is quadratic irrational; the current bounded backend is rational.</summary>
    IrrationalOffset,
    /// <summary>A normalized coefficient does not fit signed 64-bit storage.</summary>
    CoefficientOutOfRange,
}

/// <summary>
/// An allocation-free, bounded execution form of <see cref="DiscreteMeasure"/> over signed 64-bit indices and results.
/// </summary>
/// <remarks>
/// <para>
/// Compilation separates the rational rate into an integral part plus a proper fraction and retains the normalized
/// offset as a second proper fraction. A query evaluates those two denominators independently with <see cref="Int128"/>
/// intermediates, so it never constructs a common denominator and never uses <see cref="BigInteger"/> at runtime.
/// </para>
/// <para>
/// Every <c>Try</c> method returns <see langword="false"/> when its mathematical result lies outside signed 64-bit
/// storage, or when an inverse has no answer inside the signed-64-bit index domain. Throwing counterparts distinguish
/// invalid arguments from result overflow. Range amounts are evaluated directly from 128-bit boundaries, so they can
/// succeed even when either cumulative endpoint would not fit a <see cref="long"/>.
/// </para>
/// <para>
/// The default value is invalid and rejects queries. The only current backend is rational; the private backend tag
/// reserves the execution envelope for a future quadratic compiler without making unproven automaticity or
/// performance claims today.
/// </para>
/// </remarks>
public readonly record struct CompiledDiscreteMeasure64 {
    private readonly BackendKind m_backend;

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
        m_backend = BackendKind.Rational;
    }

    /// <summary>Whether this value was produced by successful compilation.</summary>
    public bool IsValid => (m_backend != BackendKind.Invalid);
    /// <summary>The integral part of the non-negative rate.</summary>
    public long IntegralRate { get; }
    /// <summary>The numerator of the rate's reduced proper fractional part.</summary>
    public long FractionalRateNumerator { get; }
    /// <summary>The positive denominator of the rate's reduced proper fractional part.</summary>
    public long FractionalRateDenominator { get; }
    /// <summary>The numerator of the normalized rational offset in <c>[0, 1)</c>.</summary>
    public long OffsetNumerator { get; }
    /// <summary>The positive denominator of the normalized rational offset.</summary>
    public long OffsetDenominator { get; }
    /// <summary>The exact period of the unit-interval allocation.</summary>
    public long Period => IsValid
        ? FractionalRateDenominator
        : throw new InvalidOperationException(message: "the compiled measure is default-initialized");

    internal static bool TryCompile(
        DiscreteMeasure source,
        out CompiledDiscreteMeasure64 compiled,
        out DiscreteMeasureCompilationFailure failure) {
        if (!source.Rate.IsRational) {
            compiled = default;
            failure = DiscreteMeasureCompilationFailure.IrrationalRate;
            return false;
        }
        if (!source.Offset.IsRational) {
            compiled = default;
            failure = DiscreteMeasureCompilationFailure.IrrationalOffset;
            return false;
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

    /// <summary>Attempts to return the cumulative amount at signed integer boundary <paramref name="index"/>.</summary>
    public bool TryCumulative(long index, out long cumulative) =>
        TryInt64(value: Boundary(index: index), result: out cumulative);

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
        return TryInt64(
            value: (Boundary(index: end) - Boundary(index: first)),
            result: out amount
        );
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

        return TryInt64(
            value: (Boundary(index: end) - Boundary(index: start)),
            result: out amount
        );
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

    private bool IsZeroRate => ((IntegralRate == 0L) && (FractionalRateNumerator == 0L));

    private Int128 Boundary(Int128 index) {
        ThrowIfInvalid();

        var whole = (((Int128)IntegralRate) * index);
        var scaledFraction = (((Int128)FractionalRateNumerator) * index);
        var (fractionalQuotient, fractionalRemainder) = FloorDivRem(
            numerator: scaledFraction,
            denominator: FractionalRateDenominator
        );
        var carry = (OffsetCarry(fractionalRemainder: fractionalRemainder) ? Int128.One : Int128.Zero);

        return (whole + fractionalQuotient + carry);
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
        var minimumBoundary = Boundary(index: minimum);

        if (minimumBoundary >= amount) {
            if (Boundary(index: (minimum - Int128.One)) < amount) {
                index = long.MinValue;
                return true;
            }

            index = 0;
            return false;
        }
        if (Boundary(index: maximum) < amount) {
            index = 0;
            return false;
        }

        var lower = minimum;
        var upper = maximum;
        while ((upper - lower) > Int128.One) {
            var middle = (lower + ((upper - lower) >> 1));
            if (Boundary(index: middle) >= amount) {
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

    private enum BackendKind : byte {
        Invalid,
        Rational,
    }
}
