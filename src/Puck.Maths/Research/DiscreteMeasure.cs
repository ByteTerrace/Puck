using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// An exact integer-valued measure on intervals of the integer line, obtained by flooring an affine rate at interval
/// boundaries.
/// </summary>
/// <remarks>
/// <para>
/// For non-negative <see cref="Rate"/> <c>r</c> and normalized <see cref="Offset"/> <c>o</c> in <c>[0, 1)</c>, the
/// boundary function is <c>B(n) = floor(r*n + o)</c>. The amount assigned to <c>[a, b)</c> is
/// <c>B(b) - B(a)</c>. Consequently adjacent ranges compose exactly, direct lookup agrees with walking every unit
/// interval, and no fractional remainder is mutable state.
/// </para>
/// <para>
/// The same value can describe jobs per frame, output samples per input frame, quota allocation, packet pacing, spawn
/// density, or a one-dimensional point set. A rational rate produces a periodic allocation; an irrational quadratic
/// rate produces an exactly seekable aperiodic allocation. Every unit interval receives either
/// <c>floor(r)</c> or <c>ceiling(r)</c> units, and the amount on any range differs from its ideal real-valued amount by
/// less than one unit.
/// </para>
/// <para>
/// A default-initialized value is the valid zero measure. All results are <see cref="BigInteger"/> so the object keeps
/// the unbounded exactness of <see cref="QuadraticSurd"/>; consumers may use checked conversions at their own storage
/// boundary.
/// </para>
/// </remarks>
public readonly record struct DiscreteMeasure {
    private DiscreteMeasure(QuadraticSurd rate, QuadraticSurd offset) {
        Offset = offset;
        Rate = rate;
    }

    /// <summary>The non-negative exact amount per unit interval.</summary>
    public QuadraticSurd Rate { get; }

    /// <summary>The measure that assigns zero to every interval.</summary>
    public static DiscreteMeasure Zero => default;

    /// <summary>
    /// The normalized affine offset in <c>[0, 1)</c>. It selects the allocation's origin without changing its rate.
    /// </summary>
    public QuadraticSurd Offset { get; }

    /// <summary>Whether the unit-interval allocation repeats periodically.</summary>
    public bool IsPeriodic => Rate.IsRational;

    /// <summary>
    /// The least positive period of the unit-interval allocation when <see cref="IsPeriodic"/> is true; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public BigInteger? Period => Rate.IsRational ? Rate.Denominator : null;

    /// <summary>The smaller of the two possible amounts assigned to a unit interval.</summary>
    public BigInteger MinimumAmount => Rate.Floor();

    /// <summary>The larger of the two possible amounts assigned to a unit interval.</summary>
    public BigInteger MaximumAmount => Rate.Ceiling();

    /// <summary>Creates an exact discrete measure from a non-negative rational or quadratic-surd rate.</summary>
    /// <param name="rate">The non-negative amount per unit interval.</param>
    /// <param name="offset">
    /// The allocation origin. Only its fractional part matters; the stored <see cref="Offset"/> is normalized into
    /// <c>[0, 1)</c>.
    /// </param>
    /// <returns>The normalized measure.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rate"/> is negative.</exception>
    /// <exception cref="ArgumentException">
    /// The irrational parts of <paramref name="rate"/> and <paramref name="offset"/> belong to different quadratic
    /// fields.
    /// </exception>
    public static DiscreteMeasure Create(QuadraticSurd rate, QuadraticSurd offset) {
        if (rate.Sign < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(rate),
                message: "the rate must be non-negative"
            );
        }

        // This also validates that two irrational operands inhabit the same quadratic field. A rational operand is
        // compatible with either field, exactly as QuadraticSurd arithmetic specifies.
        _ = (rate + offset);

        var normalizedOffset = (offset - QuadraticSurd.Rational(value: offset.Floor()));

        return new DiscreteMeasure(rate: rate, offset: normalizedOffset);
    }

    /// <summary>Creates a zero-offset measure with exact rational rate <paramref name="numerator"/>/<paramref name="denominator"/>.</summary>
    public static DiscreteMeasure Rational(BigInteger numerator, BigInteger denominator) =>
        Create(
            rate: QuadraticSurd.Rational(numerator: numerator, denominator: denominator),
            offset: QuadraticSurd.Zero
        );

    /// <summary>
    /// Creates an exact rational-rate measure with an independently specified exact rational allocation offset.
    /// </summary>
    public static DiscreteMeasure Rational(
        BigInteger numerator,
        BigInteger denominator,
        BigInteger offsetNumerator,
        BigInteger offsetDenominator) =>
        Create(
            rate: QuadraticSurd.Rational(numerator: numerator, denominator: denominator),
            offset: QuadraticSurd.Rational(numerator: offsetNumerator, denominator: offsetDenominator)
        );

    /// <summary>
    /// Attempts to compile this exact measure into its allocation-free signed-64-bit execution form.
    /// </summary>
    /// <param name="compiled">The compiled measure on success; otherwise the invalid default value.</param>
    /// <param name="failure">The exact reason compilation was unavailable.</param>
    /// <returns><see langword="true"/> when every required rational coefficient fits the bounded representation.</returns>
    /// <remarks>
    /// The current compiled backend accepts rational rates and offsets. Quadratic measures remain available through
    /// this unbounded type until a separately proven bounded quadratic rank/select compiler exists.
    /// </remarks>
    public bool TryCompileInt64(
        out CompiledDiscreteMeasure64 compiled,
        out DiscreteMeasureCompilationFailure failure) =>
        CompiledDiscreteMeasure64.TryCompile(source: this, compiled: out compiled, failure: out failure);

    /// <summary>Attempts to compile this measure, discarding the failure detail.</summary>
    public bool TryCompileInt64(out CompiledDiscreteMeasure64 compiled) =>
        TryCompileInt64(compiled: out compiled, failure: out _);

    /// <summary>Compiles this measure into its allocation-free signed-64-bit execution form.</summary>
    /// <exception cref="NotSupportedException">The rate or offset is irrational.</exception>
    /// <exception cref="OverflowException">A required normalized coefficient exceeds signed 64-bit storage.</exception>
    public CompiledDiscreteMeasure64 CompileInt64() {
        if (TryCompileInt64(compiled: out var compiled, failure: out var failure)) {
            return compiled;
        }

        return failure switch {
            DiscreteMeasureCompilationFailure.IrrationalRate =>
                throw new NotSupportedException(message: "the bounded compiler does not yet support irrational rates"),
            DiscreteMeasureCompilationFailure.IrrationalOffset =>
                throw new NotSupportedException(message: "the bounded compiler does not yet support irrational offsets"),
            DiscreteMeasureCompilationFailure.CoefficientOutOfRange =>
                throw new OverflowException(message: "a normalized measure coefficient exceeds signed 64-bit storage"),
            _ => throw new InvalidOperationException(message: "the discrete-measure compiler failed without a reason"),
        };
    }

    /// <summary>Returns the signed cumulative amount at boundary <paramref name="index"/>: <c>floor(r*index + o)</c>.</summary>
    /// <remarks><c>Cumulative(0)</c> is always zero because <see cref="Offset"/> is normalized into <c>[0, 1)</c>.</remarks>
    public BigInteger Cumulative(BigInteger index) =>
        ((Rate * QuadraticSurd.Rational(value: index)) + Offset).Floor();

    /// <summary>Returns the non-negative integer amount assigned to unit interval <c>[index, index + 1)</c>.</summary>
    public BigInteger AmountAt(BigInteger index) =>
        (Cumulative(index: (index + BigInteger.One)) - Cumulative(index: index));

    /// <summary>Returns the exact integer amount assigned to <c>[start, start + length)</c>.</summary>
    /// <param name="start">The first integer boundary of the range.</param>
    /// <param name="length">The non-negative number of unit intervals in the range.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public BigInteger AmountOver(BigInteger start, BigInteger length) {
        if (length.Sign < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(length),
                message: "the range length must be non-negative"
            );
        }

        return (Cumulative(index: (start + length)) - Cumulative(index: start));
    }

    /// <summary>Returns the exact integer amount assigned to half-open interval <c>[start, end)</c>.</summary>
    /// <param name="start">The interval's inclusive integer boundary.</param>
    /// <param name="end">The interval's exclusive integer boundary.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="end"/> precedes <paramref name="start"/>.</exception>
    public BigInteger AmountBetween(BigInteger start, BigInteger end) {
        if (end < start) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(end),
                message: "the end boundary must not precede the start boundary"
            );
        }

        return (Cumulative(index: end) - Cumulative(index: start));
    }

    /// <summary>
    /// Maps <c>[start, start + length)</c> to its contiguous output interval. The returned start is the cumulative
    /// boundary at <paramref name="start"/> and the returned length is <see cref="AmountOver"/> for the range.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public (BigInteger Start, BigInteger Length) Map(BigInteger start, BigInteger length) =>
        (
            Start: Cumulative(index: start),
            Length: AmountOver(start: start, length: length)
        );

    /// <summary>Maps half-open interval <c>[start, end)</c> to its exact contiguous output interval.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="end"/> precedes <paramref name="start"/>.</exception>
    public (BigInteger Start, BigInteger Length) MapBetween(BigInteger start, BigInteger end) =>
        (
            Start: Cumulative(index: start),
            Length: AmountBetween(start: start, end: end)
        );

    /// <summary>
    /// Translates the measure's input origin by <paramref name="distance"/> unit intervals. For every index <c>n</c>,
    /// the translated measure's amount at <c>n</c> equals this measure's amount at <c>n + distance</c>.
    /// </summary>
    public DiscreteMeasure Translate(BigInteger distance) =>
        Create(
            rate: Rate,
            offset: (Offset + (Rate * QuadraticSurd.Rational(value: distance)))
        );

    /// <summary>
    /// Returns the least integer boundary whose cumulative amount is at least <paramref name="amount"/>.
    /// </summary>
    /// <remarks>
    /// This is the exact monotone lower-bound inverse of <see cref="Cumulative"/>. When the rate exceeds one, a
    /// boundary may jump over the requested amount; the first boundary after that jump is still returned.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The rate is zero, so the cumulative function has no inverse.</exception>
    public BigInteger LowerBound(BigInteger amount) {
        ThrowIfZeroRate();

        return ((QuadraticSurd.Rational(value: amount) - Offset) / Rate).Ceiling();
    }

    /// <summary>
    /// Returns the unique unit-interval index whose mapped output interval contains <paramref name="outputIndex"/>.
    /// Empty input intervals are naturally skipped.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rate is zero, so no output index is assigned.</exception>
    public BigInteger IndexContaining(BigInteger outputIndex) =>
        (LowerBound(amount: (outputIndex + BigInteger.One)) - BigInteger.One);

    /// <summary>
    /// Returns <paramref name="start"/> when its unit interval receives a non-zero amount; otherwise returns the first
    /// later unit interval that does. This is useful for sparse rates below one without scanning empty intervals.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rate is zero, so no non-empty interval exists.</exception>
    public BigInteger NextNonemptyIndex(BigInteger start) =>
        IndexContaining(outputIndex: Cumulative(index: start));

    private void ThrowIfZeroRate() {
        if (Rate.Sign == 0) {
            throw new InvalidOperationException(message: "the zero measure has no inverse or non-empty interval");
        }
    }
}
