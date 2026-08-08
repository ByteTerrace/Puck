using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The CLOSED unit interval on the <c>2⁻³²</c> grid: a real number in <c>[0, 1]</c> stored as <see cref="Value"/> /
/// <c>2³²</c> in a <see cref="ulong"/>, under the single invariant <c>Value ≤ 2³²</c>. Unlike <see cref="UnitFraction32"/>
/// — which shares the grid but stops one unit short — one IS representable here, so the type has a multiplicative
/// identity, an exact absorbing pair (zero and one), and closure under multiplication at both ends.
/// </summary>
/// <remarks>
/// <para>
/// THE EXTRA BIT. A binary type with <c>F</c> fraction bits needs <c>w ≥ F + 1</c> bits of storage to contain the value
/// one, so every closed unit type leaves bits unused; this one spends the thirty-third bit on the point one and leaves
/// the remaining thirty-one as seam identity (a sampler draw is a value here with no representation event),
/// vectorization headroom (products of raws below <c>2³²</c> fit 64-bit arithmetic, so <c>32×32→64</c> lanes
/// vectorize), and a one-compare validity invariant.
/// </para>
/// <para>
/// WHY THIS GRID. Q1.31 was rejected because it is coarser than the sampler grid, so every seam crossing would round.
/// Q1.63 was rejected because nothing consumes <c>2⁻⁶³</c> and narrowing back to the sampler grid would double-round.
/// A denominator of <c>2³² − 1</c> was rejected because a non-binary denominator poisons the conversions and turns
/// multiplication into a divide-and-correct.
/// </para>
/// <para>
/// NO ARITHMETIC OPERATORS. Not because the combining operations are inexact — <see cref="Max"/>, <see cref="Min"/> and
/// <see cref="Complement"/> are exact at every raw — but because the operator SPELLINGS already mean something else in
/// this family, on the very type that shares this grid: <see cref="UnitFraction32"/>'s <c>~</c> is the BITWISE complement,
/// <c>2³² − 1 − raw</c>, one unit away from the arithmetic <c>2³² − raw</c> that <see cref="Complement"/> means here,
/// and its <c>+</c> and <c>-</c> WRAP where <see cref="AddSaturating"/> and <see cref="SumExcess"/> clamp. A bare
/// <c>*</c> would silently round and a bare <c>+</c> would silently saturate, so every combining operation is a named
/// method that says which. The comparison operators carry no such collision and are exact, so they are the only ones
/// offered. The surface is deliberately the minimum its consumers need and grows on demand; there is no
/// <see cref="INumber{TSelf}"/> surface, because the closed interval is not a ring.
/// </para>
/// </remarks>
public readonly record struct UnitInterval32
    : IComparable,
      IComparable<UnitInterval32>,
      IComparisonOperators<UnitInterval32, UnitInterval32, bool> {
    /// <summary>The number of fractional bits (<c>32</c>) — the same grid <see cref="UnitFraction32"/> uses. The storage
    /// is sixty-four bits wide because containing one costs a thirty-third.</summary>
    public const int FractionBitCount = 32;

    private const ulong FractionBitMask = (RawOne - 1UL);
    // The widest canonical rendering is '0' + '.' + 32 fraction digits; the point one renders as the single digit '1'.
    private const int MaximumFormattedLength = (2 + FractionBitCount);
    // The narrowing to FixedQ4816's sixteen fraction bits: its shift, its discarded-bit mask, and its half-ULP.
    private const int NarrowShift = (FractionBitCount - FixedQ4816.FractionBitCount);
    private const ulong NarrowBitMask = ((1UL << NarrowShift) - 1UL);
    private const ulong NarrowHalf = (1UL << (NarrowShift - 1));
    private const long NarrowOneRaw = (1L << FixedQ4816.FractionBitCount);
    private const ulong RawHalf = (1UL << (FractionBitCount - 1));
    // The raw representation of one — in range here, unlike in the half-open fraction types.
    private const ulong RawOne = (1UL << FractionBitCount);
    // The three-factor product's scale: three raws carry three copies of the grid, so their exact product sits at
    // 2^-96 and ONE shift of 64 brings it back; the tie is half of what that shift discards.
    private const int TripleShift = (2 * FractionBitCount);
    private const ulong TripleHalf = (1UL << (TripleShift - 1));

    private UnitInterval32(ulong value) {
        Value = value;
    }

    /// <summary>Gets the raw underlying storage — the represented real number scaled by <c>2³²</c>, always at most
    /// <c>2³²</c>. The default value is zero, which satisfies the invariant, so <see langword="default"/> is a legal
    /// instance.</summary>
    public ulong Value { get; }

    /// <summary>Indicates whether <paramref name="x"/> is less than <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is less than <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator <(UnitInterval32 x, UnitInterval32 y) =>
        (x.Value < y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is less than or equal to <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is less than or equal to <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator <=(UnitInterval32 x, UnitInterval32 y) =>
        (x.Value <= y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is greater than <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is greater than <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator >(UnitInterval32 x, UnitInterval32 y) =>
        (x.Value > y.Value);
    /// <summary>Indicates whether <paramref name="x"/> is greater than or equal to <paramref name="y"/>.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="x"/> is greater than or equal to <paramref name="y"/>; otherwise <see langword="false"/>.</returns>
    public static bool operator >=(UnitInterval32 x, UnitInterval32 y) =>
        (x.Value >= y.Value);

    /// <summary>Gets the value one — the closed interval's upper endpoint and the multiplicative identity of
    /// <see cref="Multiply(UnitInterval32, UnitInterval32)"/>.</summary>
    public static UnitInterval32 One => new(value: RawOne);
    /// <summary>Gets the value zero — the closed interval's lower endpoint and the annihilator of
    /// <see cref="Multiply(UnitInterval32, UnitInterval32)"/>. Equal to <see langword="default"/>.</summary>
    public static UnitInterval32 Zero => default;

    /// <summary>Adds two values, saturating at <see cref="One"/> instead of leaving the interval.</summary>
    /// <param name="x">The first addend.</param>
    /// <param name="y">The second addend.</param>
    /// <returns>The lesser of <c><paramref name="x"/> + <paramref name="y"/></c> and <see cref="One"/>. The sum of two
    /// raws is at most <c>2³³</c>, so the addition itself is exact and only the clamp loses information.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UnitInterval32 AddSaturating(UnitInterval32 x, UnitInterval32 y) =>
        new(value: Math.Min(
        val1: (x.Value + y.Value),
        val2: RawOne
    ));
    /// <summary>Returns the distance from <paramref name="value"/> to <see cref="One"/>.</summary>
    /// <param name="value">The value to complement.</param>
    /// <returns><c><see cref="One"/> − <paramref name="value"/></c>. Exact at every raw and involutive, because the
    /// invariant makes the subtraction total; it carries the endpoints onto each other.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UnitInterval32 Complement(UnitInterval32 value) =>
        new(value: (RawOne - value.Value));
    /// <summary>Constructs a value from a raw storage bit pattern.</summary>
    /// <param name="value">The pre-scaled raw value to wrap, interpreted as the real number <c><paramref name="value"/> / 2³²</c>.</param>
    /// <returns>A <see cref="UnitInterval32"/> whose <see cref="Value"/> equals <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> exceeds <c>2³²</c> and so is outside the
    /// closed unit interval. Use <see cref="TryCreate(ulong, out UnitInterval32)"/> to test instead of throw.</exception>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UnitInterval32 Create(ulong value) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: value,
            other: RawOne
        );

        return new(value: value);
    }
    /// <summary>Converts a <see cref="double"/> to a <see cref="UnitInterval32"/>, rounding to nearest with ties to even
    /// and saturating into the interval.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The nearest representable value. Inputs at or above one clamp to <see cref="One"/>, negative and
    /// below-zero inputs clamp to <see cref="Zero"/>, and not-a-number becomes <see cref="Zero"/> — the
    /// <see cref="FixedQ4816.FromDouble(double)"/> precedent.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UnitInterval32 FromDouble(double value) {
        var scaled = double.Round(
            x: (value * RawOne),
            mode: MidpointRounding.ToEven
        );

        // Saturate on the scaled value rather than casting a clamped double: 2^32 is exactly representable, so both
        // endpoints are reached exactly, and the surviving cast is always of an in-range integral double.
        if (double.IsNaN(d: scaled)) { return Zero; }
        if (scaled >= RawOne) { return One; }
        if (scaled <= 0d) { return Zero; }

        return new(value: ((ulong)scaled));
    }
    /// <summary>Converts a <see cref="FixedQ4816"/> to a <see cref="UnitInterval32"/>, clamping into the interval.</summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The same real number when it lies in <c>[0, 1]</c>, otherwise the endpoint it passed. Widening sixteen
    /// fraction bits to thirty-two is exact, so nothing rounds on this direction; only the clamp loses information.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UnitInterval32 FromFixedQ4816(FixedQ4816 value) {
        var raw = value.Value;

        if (raw <= 0L) { return Zero; }
        if (raw >= NarrowOneRaw) { return One; }

        return new(value: (((ulong)raw) << NarrowShift));
    }
    /// <summary>Embeds a <see cref="UnitFraction32"/> into the closed interval.</summary>
    /// <param name="value">The value to embed.</param>
    /// <returns>The same real number. The two types share the <c>2⁻³²</c> grid and <see cref="UnitFraction32"/> is exactly
    /// the part of this one below <see cref="One"/>, so a sampler draw becomes a carrier value with no representation
    /// event.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UnitInterval32 FromUnitFraction32(UnitFraction32 value) =>
        new(value: value.Value);
    /// <summary>Returns the greater of two values.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns>Whichever of <paramref name="x"/> and <paramref name="y"/> is greater.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UnitInterval32 Max(UnitInterval32 x, UnitInterval32 y) =>
        new(value: Math.Max(
        val1: x.Value,
        val2: y.Value
    ));
    /// <summary>Returns the lesser of two values.</summary>
    /// <param name="x">The first value to compare.</param>
    /// <param name="y">The second value to compare.</param>
    /// <returns>Whichever of <paramref name="x"/> and <paramref name="y"/> is lesser.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UnitInterval32 Min(UnitInterval32 x, UnitInterval32 y) =>
        new(value: Math.Min(
        val1: x.Value,
        val2: y.Value
    ));
    /// <summary>Multiplies two values, rounding the exact product to nearest with ties to even — exactly one rounding.</summary>
    /// <param name="x">The multiplicand.</param>
    /// <param name="y">The multiplier.</param>
    /// <returns>The rounded product, which never leaves <c>[0, 1]</c>. <see cref="One"/> is a two-sided identity and
    /// <see cref="Zero"/> a two-sided annihilator, both exactly, because neither case rounds.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UnitInterval32 Multiply(UnitInterval32 x, UnitInterval32 y) {
        // The one place the extra bit costs anything: one times one is 2^64 exactly, which no 64-bit product holds, so
        // the exact product is taken in 128 bits. A UInt128 multiply of two widened ulongs is the shape the JIT expands
        // to a single widening multiply on .NET 10 (measured about twice as fast as Math.BigMul) — do not "optimize"
        // this into BigMul.
        var product = (((UInt128)x.Value) * y.Value);
        var truncated = ((ulong)(product >> FractionBitCount));
        var remainder = ((ulong)product) & FractionBitMask;

        return new(value: FixedPointRounding.RoundHalfToEven(
            remainder: remainder,
            threshold: RawHalf,
            truncated: truncated
        ));
    }
    /// <summary>Multiplies three values, rounding the exact product to nearest with ties to even — exactly one rounding
    /// for the whole product, not one per pair.</summary>
    /// <param name="x">The first factor.</param>
    /// <param name="y">The second factor.</param>
    /// <param name="z">The third factor.</param>
    /// <returns>The rounded product, which never leaves <c>[0, 1]</c>.</returns>
    /// <remarks>Nesting two <see cref="Multiply(UnitInterval32, UnitInterval32)"/> calls would round twice and is a
    /// DIFFERENT value at some operands; a fused sum whose terms are charge, left and right needs this one, because its
    /// contract is one rounding per returned coefficient. Three raws are at most <c>2⁹⁶</c> together, so the exact
    /// product still fits in 128 bits and no wide accumulator is needed.</remarks>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UnitInterval32 Multiply(UnitInterval32 x, UnitInterval32 y, UnitInterval32 z) {
        // The first multiply is the widening shape the pairwise product uses; the second is a genuine 128-bit one, for
        // which no narrower form exists — the product needs 96 bits and the shift needs all of them.
        var product = ((((UInt128)x.Value) * y.Value) * z.Value);
        var truncated = ((ulong)(product >> TripleShift));
        // Here the shift discards exactly the low word, so the narrowing cast IS the remainder and the pairwise
        // product's mask has nothing left to do.
        var remainder = ((ulong)product);

        return new(value: FixedPointRounding.RoundHalfToEven(
            remainder: remainder,
            threshold: TripleHalf,
            truncated: truncated
        ));
    }
    /// <summary>Returns the amount by which the sum of two values exceeds <see cref="One"/>.</summary>
    /// <param name="x">The first addend.</param>
    /// <param name="y">The second addend.</param>
    /// <returns><c><paramref name="x"/> + <paramref name="y"/> − <see cref="One"/></c> when that is positive, otherwise
    /// <see cref="Zero"/>. Exact at every raw: the sum is at most <c>2³³</c>, so neither the addition nor the guarded
    /// subtraction can leave the storage.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static UnitInterval32 SumExcess(UnitInterval32 x, UnitInterval32 y) {
        var sum = (x.Value + y.Value);
        // Branchless: the mask is all-ones exactly when the sum clears one, so an underflowing difference is discarded
        // rather than wrapped.
        var keep = (0UL - Convert.ToUInt64(value: (sum > RawOne)));

        return new(value: (sum - RawOne) & keep);
    }
    /// <summary>Tries to construct a value from a raw storage bit pattern.</summary>
    /// <param name="value">The pre-scaled raw value to wrap, interpreted as the real number <c><paramref name="value"/> / 2³²</c>.</param>
    /// <param name="result">When this method returns, the constructed value on success or <see cref="Zero"/> on failure.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is at most <c>2³²</c> and so lies in the closed unit
    /// interval; otherwise <see langword="false"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static bool TryCreate(ulong value, out UnitInterval32 result) {
        if (value > RawOne) {
            result = default;

            return false;
        }

        result = new(value: value);

        return true;
    }
    /// <summary>Compares this instance with a boxed <see cref="UnitInterval32"/> and indicates their relative order.</summary>
    /// <param name="obj">The object to compare with this instance, or <see langword="null"/>.</param>
    /// <returns>A negative value, zero, or a positive value according to whether this instance precedes, equals, or follows <paramref name="obj"/>; a <see langword="null"/> <paramref name="obj"/> sorts first.</returns>
    /// <exception cref="ArgumentException"><paramref name="obj"/> is neither <see langword="null"/> nor a <see cref="UnitInterval32"/>.</exception>
    public int CompareTo(object? obj) {
        if (obj is null) { return 1; }
        if (obj is UnitInterval32 other) { return CompareTo(other: other); }

        throw new ArgumentException(
            message: $"Object must be of type {nameof(UnitInterval32)}.",
            paramName: nameof(obj)
        );
    }
    /// <summary>Compares this instance with another <see cref="UnitInterval32"/> and indicates their relative order.</summary>
    /// <param name="other">The value to compare with this instance.</param>
    /// <returns>A negative value, zero, or a positive value according to whether this instance precedes, equals, or follows <paramref name="other"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public int CompareTo(UnitInterval32 other) =>
        Value.CompareTo(value: other.Value);
    /// <summary>Converts this value to a <see cref="FixedQ4816"/>, rounding to nearest with ties to even — exactly one
    /// rounding.</summary>
    /// <returns>The value on <see cref="FixedQ4816"/>'s coarser sixteen-bit fraction grid. The narrowing discards
    /// sixteen bits and is therefore NOT injective: every raw within half a ULP of one — the top <c>2¹⁵</c> of them —
    /// carries up onto the exact <c>1.0</c>, so a value strictly below one can convert to one.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public FixedQ4816 ToFixedQ4816() {
        var truncated = (Value >> NarrowShift);
        var remainder = Value & NarrowBitMask;

        return FixedQ4816.FromRawBits(value: ((long)FixedPointRounding.RoundHalfToEven(
            remainder: remainder,
            threshold: NarrowHalf,
            truncated: truncated
        )));
    }
    /// <summary>Returns the exact decimal string representation of this value.</summary>
    /// <returns>The exact, invariant-culture decimal expansion (a <c>/2³²</c> fraction always terminates within
    /// thirty-two digits). <see cref="One"/> renders as <c>"1"</c> and <see cref="Zero"/> as <c>"0"</c>.</returns>
    public override string ToString() {
        Span<char> buffer = stackalloc char[MaximumFormattedLength];
        var fraction = Value & FractionBitMask;
        var charsWritten = 0;

        // Renders without routing through double. The integer part is one exactly at the upper endpoint, where the
        // fraction bits are all clear, so the two branches never both fire.
        buffer[charsWritten++] = ((RawOne == Value) ? '1' : '0');

        if (0UL != fraction) {
            // The buffer is sized to the widest expansion, so the write cannot come up short.
            charsWritten += FixedPointText.WriteFractionDigits(
                fraction: fraction,
                fractionBitCount: FractionBitCount,
                destination: buffer[charsWritten..]
            );
        }

        return new string(value: buffer[..charsWritten]);
    }
    /// <summary>Tries to narrow this value into a <see cref="UnitFraction32"/>.</summary>
    /// <param name="result">When this method returns, the same real number on success or the default value on failure.</param>
    /// <returns><see langword="true"/> when this value is strictly below <see cref="One"/>, in which case the narrowing
    /// is exact; otherwise <see langword="false"/>, because <see cref="UnitFraction32"/> has no representable one. The
    /// partial inverse of <see cref="FromUnitFraction32(UnitFraction32)"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public bool TryToUnitFraction32(out UnitFraction32 result) {
        if (RawOne == Value) {
            result = default;

            return false;
        }

        result = UnitFraction32.FromRawBits(value: ((uint)Value));

        return true;
    }
}
