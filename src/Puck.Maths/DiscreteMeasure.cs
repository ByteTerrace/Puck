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
/// the unbounded exactness of <see cref="RealQuadratic"/>; consumers may use checked conversions at their own storage
/// boundary.
/// </para>
/// </remarks>
public readonly record struct DiscreteMeasure {
    private DiscreteMeasure(RealQuadratic rate, RealQuadratic offset) {
        Offset = offset;
        Rate = rate;
    }

    /// <summary>Gets a value indicating whether the unit-interval allocation repeats periodically.</summary>
    public bool IsPeriodic => Rate.IsRational;
    /// <summary>Gets the larger of the two possible amounts assigned to a unit interval.</summary>
    public BigInteger MaximumAmount => Rate.Ceiling();
    /// <summary>Gets the smaller of the two possible amounts assigned to a unit interval.</summary>
    public BigInteger MinimumAmount => Rate.Floor();
    /// <summary>
    /// Gets the normalized affine offset in <c>[0, 1)</c>, which selects the allocation's origin without changing its rate.
    /// </summary>
    public RealQuadratic Offset { get; }
    /// <summary>
    /// Gets the least positive period of the unit-interval allocation when <see cref="IsPeriodic"/> is true; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public BigInteger? Period => (Rate.IsRational
        ? Rate.Denominator
        : null
    );
    /// <summary>Gets the non-negative exact amount per unit interval.</summary>
    public RealQuadratic Rate { get; }
    /// <summary>Gets the measure that assigns zero to every interval.</summary>
    public static DiscreteMeasure Zero => default;

    private void ThrowIfZeroRate() {
        if (Rate.Sign == 0) {
            throw new InvalidOperationException(message: "the zero measure has no inverse or non-empty interval");
        }
    }

    /// <summary>Returns the non-negative integer amount assigned to unit interval <c>[index, index + 1)</c>.</summary>
    public BigInteger AmountAt(BigInteger index) =>
        (Cumulative(index: (index + BigInteger.One)) - Cumulative(index: index));
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
    /// <summary>Compiles this measure into its allocation-free signed-64-bit execution form.</summary>
    /// <exception cref="OverflowException">A required normalized coefficient or quadratic core-domain radicand exceeds the bounded representation.</exception>
    public CompiledDiscreteMeasure64 CompileInt64() {
        if (TryCompileInt64(
            compiled: out var compiled,
            failure: out var failure
        )) {
            return compiled;
        }

        return failure switch {
            DiscreteMeasureCompilationFailure.IrrationalRate =>
                throw new OverflowException(message: "the irrational rate exceeds the bounded quadratic floor envelope"),
            DiscreteMeasureCompilationFailure.IrrationalOffset =>
                throw new OverflowException(message: "the irrational offset exceeds the bounded quadratic floor envelope"),
            DiscreteMeasureCompilationFailure.CoefficientOutOfRange =>
                throw new OverflowException(message: "a normalized measure coefficient exceeds signed 64-bit storage"),
            _ => throw new InvalidOperationException(message: "the discrete-measure compiler failed without a reason"),
        };
    }
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
    public static DiscreteMeasure Create(RealQuadratic rate, RealQuadratic offset) {
        if (rate.Sign < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(rate),
                message: "the rate must be non-negative"
            );
        }

        // This also validates that two irrational operands inhabit the same quadratic field. A rational operand is
        // compatible with either field, exactly as RealQuadratic arithmetic specifies.
        _ = (rate + offset);

        var normalizedOffset = (offset - RealQuadratic.Rational(value: offset.Floor()));

        return new DiscreteMeasure(
            offset: normalizedOffset,
            rate: rate
        );
    }
    /// <summary>Returns the signed cumulative amount at boundary <paramref name="index"/>: <c>floor(r*index + o)</c>.</summary>
    /// <remarks><c>Cumulative(0)</c> is always zero because <see cref="Offset"/> is normalized into <c>[0, 1)</c>.</remarks>
    public BigInteger Cumulative(BigInteger index) =>
        ((Rate * RealQuadratic.Rational(value: index)) + Offset).Floor();
    /// <summary>
    /// Returns the unique unit-interval index whose mapped output interval contains <paramref name="outputIndex"/>.
    /// Empty input intervals are naturally skipped.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rate is zero, so no output index is assigned.</exception>
    public BigInteger IndexContaining(BigInteger outputIndex) =>
        (LowerBound(amount: (outputIndex + BigInteger.One)) - BigInteger.One);
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

        return ((RealQuadratic.Rational(value: amount) - Offset) / Rate).Ceiling();
    }
    /// <summary>
    /// Maps <c>[start, start + length)</c> to its contiguous output interval. The returned start is the cumulative
    /// boundary at <paramref name="start"/> and the returned length is <see cref="AmountOver"/> for the range.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public (BigInteger Start, BigInteger Length) Map(BigInteger start, BigInteger length) =>
        (
            Start: Cumulative(index: start),
            Length: AmountOver(
            length: length,
            start: start
        )
        );
    /// <summary>Maps half-open interval <c>[start, end)</c> to its exact contiguous output interval.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="end"/> precedes <paramref name="start"/>.</exception>
    public (BigInteger Start, BigInteger Length) MapBetween(BigInteger start, BigInteger end) =>
        (
            Start: Cumulative(index: start),
            Length: AmountBetween(
            end: end,
            start: start
        )
        );
    /// <summary>
    /// Returns <paramref name="start"/> when its unit interval receives a non-zero amount; otherwise returns the first
    /// later unit interval that does. This is useful for sparse rates below one without scanning empty intervals.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rate is zero, so no non-empty interval exists.</exception>
    public BigInteger NextNonemptyIndex(BigInteger start) =>
        IndexContaining(outputIndex: Cumulative(index: start));
    /// <summary>Creates a zero-offset measure with exact rational rate <paramref name="numerator"/>/<paramref name="denominator"/>.</summary>
    public static DiscreteMeasure Rational(BigInteger numerator, BigInteger denominator) =>
        Create(
            rate: RealQuadratic.Rational(
                denominator: denominator,
                numerator: numerator
            ),
            offset: RealQuadratic.Zero
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
            rate: RealQuadratic.Rational(
                denominator: denominator,
                numerator: numerator
            ),
            offset: RealQuadratic.Rational(
                denominator: offsetDenominator,
                numerator: offsetNumerator
            )
        );
    /// <summary>
    /// Translates the measure's input origin by <paramref name="distance"/> unit intervals. For every index <c>n</c>,
    /// the translated measure's amount at <c>n</c> equals this measure's amount at <c>n + distance</c>.
    /// </summary>
    public DiscreteMeasure Translate(BigInteger distance) =>
        Create(
            rate: Rate,
            offset: (Offset + (Rate * RealQuadratic.Rational(value: distance)))
        );
    /// <summary>
    /// Attempts to compile this exact measure into its allocation-free signed-64-bit execution form.
    /// </summary>
    /// <param name="compiled">The compiled measure on success; otherwise the invalid default value.</param>
    /// <param name="failure">The exact reason compilation was unavailable.</param>
    /// <returns><see langword="true"/> when every required rational coefficient fits the bounded representation.</returns>
    /// <remarks>
    /// The compiler accepts every bounded rational measure and real-quadratic measures whose cleared coefficients prove
    /// that the exact signed-long core domain fits the <see cref="Int128"/> root and two-limb <see cref="UInt128"/>
    /// floor kernel. Wider quadratic measures remain available through this unbounded type.
    /// </remarks>
    public bool TryCompileInt64(
        out CompiledDiscreteMeasure64 compiled,
        out DiscreteMeasureCompilationFailure failure) =>
        CompiledDiscreteMeasure64.TryCompile(
            compiled: out compiled,
            failure: out failure,
            source: this
        );
    /// <summary>Attempts to compile this measure, discarding the failure detail.</summary>
    public bool TryCompileInt64(out CompiledDiscreteMeasure64 compiled) =>
        TryCompileInt64(
            compiled: out compiled,
            failure: out _
        );
}
