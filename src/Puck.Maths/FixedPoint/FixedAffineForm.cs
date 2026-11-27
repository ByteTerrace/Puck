namespace Puck.Maths;

/// <summary>Whether a comparison of affine forms holds at the points the forms describe: at all of them, at none, or
/// at some (or the forms cannot tell). The low bit says the comparison may fail somewhere and the high bit that it
/// may hold somewhere, so <see cref="FixedAffineForms.And"/> is Kleene's conjunction over the two bits.</summary>
public enum FixedAffineTruth : byte {
    /// <summary>The comparison fails at every point.</summary>
    False = 1,
    /// <summary>The comparison holds at every point.</summary>
    True = 2,
    /// <summary>The comparison may hold at some points and fail at others.</summary>
    Unknown = 3,
}

/// <summary>
/// A reduced affine form of Q48.16 values over three shared noise symbols: the set
/// <c>Center + Symbol0·ε₀ + Symbol1·ε₁ + Symbol2·ε₂ + e</c> for every <c>εᵢ</c> in <c>[−1, 1]</c> and every
/// <c>|e| ≤ Error</c>. The symbols are shared by every form of one evaluation (a box's three axes, seeded by
/// <see cref="FixedAffineForm3.FromBox"/>), so quantities that move together cancel: the shared parts of <c>x − x</c>
/// are exactly zero, a rotated box stays a rotated box, and a blend reads the difference of its two operands as the
/// narrow thing it is.
/// The error is the form's own noise, independent of everything else: every new symbol a nonlinear operation would
/// introduce (the remainder of a product, a root's curvature, a rounding) folds into it, which is Messine's AF1 form.
/// </summary>
/// <remarks>
/// <para><b>What a form holds.</b> A form holds a function <c>v(ε)</c> of the shared symbols when
/// <c>|v(ε) − (Center + Σ Symbolᵢ·εᵢ)| ≤ Error</c> at every <c>ε</c> of the cube. Every operation of
/// <see cref="FixedAffineForms"/> takes forms holding its operands at the same <c>ε</c> and returns one holding the
/// round-to-nearest <see cref="FixedQ4816"/> answer of the carrier operation it names, at that <c>ε</c>: the exact
/// answer's linear part, its nonlinear remainder bounded into the error, each rounded coefficient's half raw added, and
/// a raw for the carrier's own rounding where it rounds. So an evaluation of formulas in forms holds the point
/// evaluation of the same formulas at every point of the box, and its <see cref="Range"/> bounds them all.</para>
/// <para><b>Overflow is the top, and the top absorbs.</b> A form whose range would leave the carrier is
/// <see cref="Entire"/>, the unbounded form, and every operation given it answers it, as
/// <see cref="FixedInterval.Entire"/> does: a bounded result proves no step of its computation left the carrier.</para>
/// <para><b>Deterministic.</b> Every operation is integer arithmetic on raws, bit-identical on every machine.</para>
/// </remarks>
public readonly record struct FixedAffineForm {
    private readonly long m_center;
    private readonly long m_symbol0;
    private readonly long m_symbol1;
    private readonly long m_symbol2;
    // The raw radius of the form's own noise: non-negative for a bounded form, -1 for Entire.
    private readonly long m_error;

    private FixedAffineForm(long center, long symbol0, long symbol1, long symbol2, long error) {
        m_center = center;
        m_symbol0 = symbol0;
        m_symbol1 = symbol1;
        m_symbol2 = symbol2;
        m_error = error;
    }

    /// <summary>Creates the form <c>center + symbol0·ε₀ + symbol1·ε₁ + symbol2·ε₂ ± error</c>.</summary>
    /// <param name="center">The centre.</param>
    /// <param name="symbol0">The coefficient of the first shared symbol.</param>
    /// <param name="symbol1">The coefficient of the second shared symbol.</param>
    /// <param name="symbol2">The coefficient of the third shared symbol.</param>
    /// <param name="error">The radius of the form's own noise, at least zero.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="error"/> is negative.</exception>
    /// <exception cref="ArgumentException">The form's range leaves the carrier.</exception>
    public FixedAffineForm(FixedQ4816 center, FixedQ4816 symbol0, FixedQ4816 symbol1, FixedQ4816 symbol2, FixedQ4816 error) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: error.Value, paramName: nameof(error));

        var form = Make(center: center.Value, symbol0: symbol0.Value, symbol1: symbol1.Value, symbol2: symbol2.Value, error: error.Value);

        if (form.IsUnbounded) {
            throw new ArgumentException(message: "The affine form's range leaves the carrier.", paramName: nameof(center));
        }

        this = form;
    }

    /// <summary>Gets the unbounded form, every real value: the answer to any operation whose range leaves the carrier,
    /// and to any operation given it.</summary>
    public static FixedAffineForm Entire { get; } = new(center: 0L, symbol0: 0L, symbol1: 0L, symbol2: 0L, error: -1L);
    /// <summary>Gets whether this is <see cref="Entire"/>, whose coefficients bound nothing.</summary>
    public bool IsUnbounded => (m_error < 0L);
    /// <summary>Gets the centre; zero for <see cref="Entire"/>.</summary>
    public FixedQ4816 Center => FixedQ4816.FromRawBits(value: m_center);
    /// <summary>Gets the coefficient of the first shared symbol; zero for <see cref="Entire"/>.</summary>
    public FixedQ4816 Symbol0 => FixedQ4816.FromRawBits(value: m_symbol0);
    /// <summary>Gets the coefficient of the second shared symbol; zero for <see cref="Entire"/>.</summary>
    public FixedQ4816 Symbol1 => FixedQ4816.FromRawBits(value: m_symbol1);
    /// <summary>Gets the coefficient of the third shared symbol; zero for <see cref="Entire"/>.</summary>
    public FixedQ4816 Symbol2 => FixedQ4816.FromRawBits(value: m_symbol2);
    /// <summary>Gets the radius of the form's own noise; <see cref="FixedQ4816.MaxValue"/> for <see cref="Entire"/>.</summary>
    public FixedQ4816 Error => (IsUnbounded ? FixedQ4816.MaxValue : FixedQ4816.FromRawBits(value: m_error));
    /// <summary>Gets the radius of the form's range about its centre, the sum of the coefficients' magnitudes and the
    /// error; <see cref="FixedQ4816.MaxValue"/> for <see cref="Entire"/>.</summary>
    public FixedQ4816 Radius => (IsUnbounded ? FixedQ4816.MaxValue : FixedQ4816.FromRawBits(value: ((long)RawRadius)));
    /// <summary>Gets the interval of every value the form holds; <see cref="FixedInterval.Entire"/> for
    /// <see cref="Entire"/>.</summary>
    public FixedInterval Range => (IsUnbounded
        ? FixedInterval.Entire
        : new FixedInterval(lower: FixedQ4816.FromRawBits(value: ((long)RawLow)), upper: FixedQ4816.FromRawBits(value: ((long)RawHigh))));

    internal long RawCenter => m_center;
    internal long RawSymbol0 => m_symbol0;
    internal long RawSymbol1 => m_symbol1;
    internal long RawSymbol2 => m_symbol2;
    internal long RawError => m_error;
    internal Int128 RawRadius => (((((Int128)Math.Abs(value: m_symbol0)) + Math.Abs(value: m_symbol1)) + Math.Abs(value: m_symbol2)) + m_error);
    internal Int128 RawLow => (m_center - RawRadius);
    internal Int128 RawHigh => (m_center + RawRadius);
    // The greatest magnitude of any value the form holds: what a product's or a weighted sum's width is judged by.
    internal Int128 RawMagnitude => (Int128.Abs(value: m_center) + RawRadius);

    /// <summary>Returns the form holding one value, with no noise.</summary>
    /// <param name="value">The value.</param>
    /// <returns><paramref name="value"/> as a form.</returns>
    public static FixedAffineForm FromPoint(FixedQ4816 value) => new(center: value.Value, symbol0: 0L, symbol1: 0L, symbol2: 0L, error: 0L);
    /// <summary>Returns the form holding every value of an interval as its own noise, correlated with nothing.</summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The form centred on the interval's midpoint (rounded down) whose error reaches both ends;
    /// <see cref="Entire"/> for <see cref="FixedInterval.Entire"/>.</returns>
    public static FixedAffineForm FromInterval(FixedInterval interval) {
        if (interval.IsUnbounded) {
            return Entire;
        }

        var center = ((((Int128)interval.Lower.Value) + interval.Upper.Value) >> 1);

        return Make(center: center, symbol0: 0, symbol1: 0, symbol2: 0, error: (interval.Upper.Value - center));
    }
    /// <summary>Returns the form holding every value of <c>[lower, upper]</c> along one shared symbol alone: the
    /// coordinate of a box along the axis that symbol names.</summary>
    /// <param name="lower">The least value.</param>
    /// <param name="upper">The greatest value, at or above <paramref name="lower"/>.</param>
    /// <param name="symbol">The shared symbol, 0, 1 or 2.</param>
    /// <returns>The form <c>c + h·ε_symbol</c> with <c>c</c> the midpoint rounded down and <c>h</c> reaching
    /// <paramref name="upper"/>, so <c>ε_symbol</c> runs the whole interval and the form has no other noise.</returns>
    /// <exception cref="ArgumentException"><paramref name="lower"/> exceeds <paramref name="upper"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="symbol"/> is not 0, 1 or 2.</exception>
    public static FixedAffineForm Symbol(FixedQ4816 lower, FixedQ4816 upper, int symbol) {
        if (lower > upper) {
            throw new ArgumentException(message: $"The symbol's lower end {lower} exceeds its upper end {upper}.", paramName: nameof(lower));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(value: symbol, paramName: nameof(symbol));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: symbol, other: 2, paramName: nameof(symbol));

        var center = ((((Int128)lower.Value) + upper.Value) >> 1);
        var half = (upper.Value - center);

        return Make(
            center: center,
            symbol0: ((symbol == 0) ? half : 0),
            symbol1: ((symbol == 1) ? half : 0),
            symbol2: ((symbol == 2) ? half : 0),
            error: 0
        );
    }
    /// <summary>Returns the coefficient of a shared symbol.</summary>
    /// <param name="symbol">The shared symbol, 0, 1 or 2.</param>
    /// <returns>Its coefficient.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="symbol"/> is not 0, 1 or 2.</exception>
    public FixedQ4816 SymbolOf(int symbol) => symbol switch {
        0 => Symbol0,
        1 => Symbol1,
        2 => Symbol2,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(symbol)),
    };

    /// <summary>Lifts a value to the form that holds it alone.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator FixedAffineForm(FixedQ4816 value) => FromPoint(value: value);

    /// <inheritdoc/>
    public override string ToString() => (IsUnbounded
        ? "(−∞, +∞)"
        : $"{Center} + {Symbol0}·ε₀ + {Symbol1}·ε₁ + {Symbol2}·ε₂ ± {Error}");

    // The form of exact raws, or Entire when its range leaves the carrier. The error must be at least zero.
    internal static FixedAffineForm Make(Int128 center, Int128 symbol0, Int128 symbol1, Int128 symbol2, Int128 error) {
        var radius = (((Int128.Abs(value: symbol0) + Int128.Abs(value: symbol1)) + Int128.Abs(value: symbol2)) + error);

        return ((((center - radius) < long.MinValue) || ((center + radius) > long.MaxValue))
            ? Entire
            : new FixedAffineForm(center: ((long)center), symbol0: ((long)symbol0), symbol1: ((long)symbol1), symbol2: ((long)symbol2), error: ((long)error)));
    }
}

/// <summary>A two-vector of affine forms over the same shared symbols (<see cref="FixedAffineForm"/>).</summary>
/// <param name="X">The first component.</param>
/// <param name="Y">The second component.</param>
public readonly record struct FixedAffineForm2(FixedAffineForm X, FixedAffineForm Y) {
    /// <summary>Lifts a vector to the forms that hold it alone.</summary>
    /// <param name="value">The vector.</param>
    public static implicit operator FixedAffineForm2(FixedVector2 value) => new(X: value.X, Y: value.Y);
}

/// <summary>A three-vector of affine forms over the same shared symbols (<see cref="FixedAffineForm"/>): a box of
/// points carried through a field's point maps with its three axes as the symbols, so a rotation keeps it a rotated
/// box rather than the larger box that encloses it.</summary>
/// <param name="X">The first component.</param>
/// <param name="Y">The second component.</param>
/// <param name="Z">The third component.</param>
public readonly record struct FixedAffineForm3(FixedAffineForm X, FixedAffineForm Y, FixedAffineForm Z) {
    /// <summary>Gets whether any component is <see cref="FixedAffineForm.Entire"/>.</summary>
    public bool IsUnbounded => (X.IsUnbounded || Y.IsUnbounded || Z.IsUnbounded);

    /// <summary>Returns the box <c>[lower, upper]</c> as forms, axis <c>i</c> riding shared symbol <c>i</c> alone, so
    /// every point of the box is the forms' value at one choice of the symbols.</summary>
    /// <param name="lower">The box's least corner.</param>
    /// <param name="upper">The box's greatest corner, at or above <paramref name="lower"/> on every axis.</param>
    /// <returns>The box's three coordinate forms.</returns>
    /// <exception cref="ArgumentException"><paramref name="lower"/> exceeds <paramref name="upper"/> on an
    /// axis.</exception>
    public static FixedAffineForm3 FromBox(FixedVector3 lower, FixedVector3 upper) => new(
        X: FixedAffineForm.Symbol(lower: lower.X, symbol: 0, upper: upper.X),
        Y: FixedAffineForm.Symbol(lower: lower.Y, symbol: 1, upper: upper.Y),
        Z: FixedAffineForm.Symbol(lower: lower.Z, symbol: 2, upper: upper.Z)
    );
    /// <summary>Returns three intervals as forms, each its own noise, correlated with nothing.</summary>
    /// <param name="x">The first component's interval.</param>
    /// <param name="y">The second component's interval.</param>
    /// <param name="z">The third component's interval.</param>
    /// <returns>The three forms (<see cref="FixedAffineForm.FromInterval"/>).</returns>
    public static FixedAffineForm3 FromIntervals(FixedInterval x, FixedInterval y, FixedInterval z) => new(
        X: FixedAffineForm.FromInterval(interval: x),
        Y: FixedAffineForm.FromInterval(interval: y),
        Z: FixedAffineForm.FromInterval(interval: z)
    );

    /// <summary>Lifts a vector to the forms that hold it alone.</summary>
    /// <param name="value">The vector.</param>
    public static implicit operator FixedAffineForm3(FixedVector3 value) => new(X: value.X, Y: value.Y, Z: value.Z);
}
