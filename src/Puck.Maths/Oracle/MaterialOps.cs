using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The construction-time rounding lane of a presentation: <see cref="Exact"/> when every charge the presentation
/// carries is an exact integer of the carrier, <see cref="General"/> otherwise. It is the generalization of
/// <see cref="QuadraticAlgebra{TScalar}"/>'s integer-coefficient classification — a value-independent property of the
/// presentation, decided once and never re-decided per operand.
/// </summary>
public enum ChargeLane {
    /// <summary>Every charge is an exact integer of the carrier, so the charges enter the fused sums as plain integer
    /// multipliers and the rounding collapses to the carrier's own product shift.</summary>
    Exact,
    /// <summary>At least one charge is fractional, so the fused sums accumulate at the wider scale and round there.</summary>
    General,
}

/// <summary>
/// The scalar operations and one-rounding fused sums the presented kernel executes. This base contract fixes the
/// schedule and canonical representation of each operation, but deliberately promises no associativity or
/// distributivity: rounded materials can implement it honestly. Algorithms that require the semiring laws gate on
/// <see cref="IExactSemiringMaterial{TValue, TSelf}"/>.
/// Operations are instance members carried on a struct, so a material may hold runtime data (a prime modulus) that a
/// <see langword="static"/> <see langword="abstract"/> surface could not express, while still devirtualizing for every
/// closed instantiation.
/// </summary>
/// <typeparam name="TValue">The carrier the material operates on.</typeparam>
/// <typeparam name="TSelf">The implementing struct, carried as a curiously-recurring type parameter.</typeparam>
public interface IMaterialOps<TValue, TSelf>
    where TSelf : struct, IMaterialOps<TValue, TSelf> {
    /// <summary>Gets the additive identity of the material.</summary>
    TValue Zero { get; }
    /// <summary>Gets the multiplicative identity of the material.</summary>
    TValue One { get; }

    /// <summary>Adds two values under the material's addition.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum.</returns>
    TValue Add(TValue left, TValue right);
    /// <summary>Multiplies two values under the semiring's multiplication.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The product. Over a rounding carrier this rounds; the kernel never accumulates through it, reaching
    /// <see cref="FusedChargedSum"/> instead.</returns>
    TValue Multiply(TValue left, TValue right);
    /// <summary>Maps a carrier value to the material's canonical representation.</summary>
    /// <param name="value">The value to canonicalize.</param>
    /// <returns>The canonical representation. Materials whose whole carrier is canonical return the value unchanged.</returns>
    /// <remarks>Every public coefficient-admission seam calls this before support pruning or equality decisions. The
    /// default identity implementation keeps caller-authored whole-carrier materials source-compatible.</remarks>
    TValue Canonicalize(TValue value) =>
        value;
    /// <summary>Indicates whether a value is the material's additive identity.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value is <see cref="Zero"/>; otherwise <see langword="false"/>.</returns>
    /// <remarks>Support pruning reads this and nothing else, so a material whose zero is not the carrier's default
    /// (the tropical <c>+∞</c>) is served without a special case in the kernel.</remarks>
    bool IsZero(TValue value);
    /// <summary>Folds the exact value of <c>Σ charges[i]·left[i]·right[i]</c> with exactly one rounding, wrapped to the carrier.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="left">The per-term left coefficients; the same length as <paramref name="charges"/>.</param>
    /// <param name="right">The per-term right coefficients; the same length as <paramref name="charges"/>.</param>
    /// <param name="lane">The presentation's construction-time rounding lane.</param>
    /// <returns>The folded value.</returns>
    /// <remarks>The wide accumulator is private to the material — the wide type never appears in the signature — and
    /// the lane is classified at construction, never per operand. An empty span folds to <see cref="Zero"/>.</remarks>
    TValue FusedChargedSum(ReadOnlySpan<TValue> charges, ReadOnlySpan<TValue> left, ReadOnlySpan<TValue> right, ChargeLane lane);
    /// <summary>Folds the exact value of <c>Σ charges[i]·values[i]</c> with exactly one rounding, wrapped to the carrier.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="values">The per-term values; the same length as <paramref name="charges"/>.</param>
    /// <param name="lane">The presentation's construction-time rounding lane.</param>
    /// <returns>The folded value.</returns>
    /// <remarks>The documented partial evaluation of <see cref="FusedChargedSum"/> at a constant right operand of
    /// <see cref="One"/>; the two agree bit for bit.</remarks>
    TValue FusedChargedLinear(ReadOnlySpan<TValue> charges, ReadOnlySpan<TValue> values, ChargeLane lane);
}

/// <summary>
/// A material whose canonical carrier obeys the commutative-semiring laws exactly: associative commutative addition,
/// associative multiplication, both identities, zero annihilation, and distributivity. Law-dependent algorithms use
/// this marker instead of inferring those properties from <see cref="IMaterialOps{TValue, TSelf}"/>.
/// </summary>
/// <typeparam name="TValue">The carrier the semiring operates on.</typeparam>
/// <typeparam name="TSelf">The implementing struct.</typeparam>
public interface IExactSemiringMaterial<TValue, TSelf> : IMaterialOps<TValue, TSelf>
    where TSelf : struct, IMaterialOps<TValue, TSelf> {
}

/// <summary>A material whose addition has inverses.</summary>
/// <typeparam name="TValue">The carrier the semiring operates on.</typeparam>
/// <typeparam name="TSelf">The implementing struct.</typeparam>
public interface ISignedMaterial<TValue, TSelf> : IMaterialOps<TValue, TSelf>
    where TSelf : struct, IMaterialOps<TValue, TSelf> {
    /// <summary>Returns the additive inverse of a value.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The negation.</returns>
    TValue Negate(TValue value);
    /// <summary>Subtracts one value from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference.</returns>
    TValue Subtract(TValue left, TValue right);
}

/// <summary>
/// A material whose addition is idempotent — <c>a + a = a</c> — which is what licenses a guarded sum over all lengths
/// to stabilize rather than merely to terminate by nilpotence.
/// </summary>
/// <typeparam name="TValue">The carrier the semiring operates on.</typeparam>
/// <typeparam name="TSelf">The implementing struct.</typeparam>
/// <remarks>The marker is read once at construction. It never changes what the kernel computes; it decides only whether
/// a stabilized partial sum may be issued as a certificate, which over a rounding carrier without the marker would be
/// rounding noise rather than a proof.</remarks>
public interface IIdempotentMaterial<TValue, TSelf> : IExactSemiringMaterial<TValue, TSelf>
    where TSelf : struct, IMaterialOps<TValue, TSelf> {
}

/// <summary>
/// A De Morgan material: an idempotent semiring carrying a complement that is an involution and exchanges the
/// semiring's two operations. Complementation lives here and nowhere else, so asking for it at a material that has none
/// is a compile error rather than a runtime refusal.
/// </summary>
/// <typeparam name="TValue">The carrier the semiring operates on.</typeparam>
/// <typeparam name="TSelf">The implementing struct.</typeparam>
public interface IComplementedMaterial<TValue, TSelf> : IIdempotentMaterial<TValue, TSelf>
    where TSelf : struct, IMaterialOps<TValue, TSelf> {
    /// <summary>Returns the De Morgan complement of a value.</summary>
    /// <param name="value">The value to complement.</param>
    /// <returns>The complement.</returns>
    TValue Complement(TValue value);
}

/// <summary>A material whose non-zero values are units, with the non-unit returned as a witness rather than thrown.</summary>
/// <typeparam name="TValue">The carrier the semiring operates on.</typeparam>
/// <typeparam name="TSelf">The implementing struct.</typeparam>
public interface IFieldMaterial<TValue, TSelf> : ISignedMaterial<TValue, TSelf>, IExactSemiringMaterial<TValue, TSelf>
    where TSelf : struct, IMaterialOps<TValue, TSelf> {
    /// <summary>Attempts to invert a value.</summary>
    /// <param name="value">The value to invert.</param>
    /// <param name="inverse">On success, the multiplicative inverse; otherwise <see cref="IMaterialOps{TValue, TSelf}.Zero"/>.</param>
    /// <returns><see langword="true"/> when the value is a unit; otherwise <see langword="false"/>, the value itself
    /// being the non-unit witness.</returns>
    bool TryInvert(TValue value, out TValue inverse);
}

/// <summary>The Boolean material <c>({false, true}, or, and)</c> — reachability, satisfaction, and every question whose
/// answer is a bit. Exact: nothing here rounds.</summary>
public readonly struct BooleanMaterial : IComplementedMaterial<bool, BooleanMaterial> {
    /// <summary>Gets the additive identity, <see langword="false"/>.</summary>
    public bool Zero => false;
    /// <summary>Gets the multiplicative identity, <see langword="true"/>.</summary>
    public bool One => true;

    /// <summary>Returns the disjunction of two values.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns><c>left ∨ right</c>.</returns>
    public bool Add(bool left, bool right) =>
        (left || right);
    /// <summary>Returns the negation of a value.</summary>
    /// <param name="value">The value to complement.</param>
    /// <returns><c>¬value</c>.</returns>
    public bool Complement(bool value) =>
        !value;
    /// <summary>Folds <c>⋁ charges[i] ∧ values[i]</c>.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="values">The per-term values.</param>
    /// <param name="lane">Ignored; the Boolean material never rounds.</param>
    /// <returns>The folded value.</returns>
    public bool FusedChargedLinear(ReadOnlySpan<bool> charges, ReadOnlySpan<bool> values, ChargeLane lane) {
        for (var index = 0; (index < charges.Length); ++index) {
            if (charges[index] && values[index]) { return true; }
        }

        return false;
    }
    /// <summary>Folds <c>⋁ charges[i] ∧ left[i] ∧ right[i]</c>.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="left">The per-term left coefficients.</param>
    /// <param name="right">The per-term right coefficients.</param>
    /// <param name="lane">Ignored; the Boolean material never rounds.</param>
    /// <returns>The folded value.</returns>
    public bool FusedChargedSum(ReadOnlySpan<bool> charges, ReadOnlySpan<bool> left, ReadOnlySpan<bool> right, ChargeLane lane) {
        for (var index = 0; (index < charges.Length); ++index) {
            if (charges[index] && left[index] && right[index]) { return true; }
        }

        return false;
    }
    /// <summary>Indicates whether a value is <see langword="false"/>.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value is <see langword="false"/>.</returns>
    public bool IsZero(bool value) =>
        !value;
    /// <summary>Returns the conjunction of two values.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns><c>left ∧ right</c>.</returns>
    public bool Multiply(bool left, bool right) =>
        (left && right);
}

/// <summary>The two-element field <c>GF(2)</c> carried in a <see cref="ulong"/>: addition is exclusive-or and equals
/// subtraction, multiplication is conjunction. Only the low bit is a member; every operation masks to it.</summary>
public readonly struct ParityMaterial : ISignedMaterial<ulong, ParityMaterial>, IExactSemiringMaterial<ulong, ParityMaterial> {
    /// <summary>Gets the additive identity, <c>0</c>.</summary>
    public ulong Zero => 0UL;
    /// <summary>Gets the multiplicative identity, <c>1</c>.</summary>
    public ulong One => 1UL;

    /// <summary>Returns the exclusive-or of two values.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns><c>left ⊕ right</c>.</returns>
    public ulong Add(ulong left, ulong right) =>
        (left ^ right) & 1UL;
    /// <summary>Returns the canonical low-bit representative.</summary>
    /// <param name="value">The carrier value to reduce.</param>
    /// <returns><c>value mod 2</c>.</returns>
    public ulong Canonicalize(ulong value) =>
        value & 1UL;
    /// <summary>Folds <c>⊕ charges[i]·values[i]</c>.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="values">The per-term values.</param>
    /// <param name="lane">Ignored; <c>GF(2)</c> never rounds.</param>
    /// <returns>The folded value.</returns>
    public ulong FusedChargedLinear(ReadOnlySpan<ulong> charges, ReadOnlySpan<ulong> values, ChargeLane lane) {
        var accumulator = 0UL;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator ^= charges[index] & values[index];
        }

        return accumulator & 1UL;
    }
    /// <summary>Folds <c>⊕ charges[i]·left[i]·right[i]</c>.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="left">The per-term left coefficients.</param>
    /// <param name="right">The per-term right coefficients.</param>
    /// <param name="lane">Ignored; <c>GF(2)</c> never rounds.</param>
    /// <returns>The folded value.</returns>
    public ulong FusedChargedSum(ReadOnlySpan<ulong> charges, ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right, ChargeLane lane) {
        var accumulator = 0UL;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator ^= charges[index] & left[index] & right[index];
        }

        return accumulator & 1UL;
    }
    /// <summary>Indicates whether a value is zero.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the low bit is clear.</returns>
    public bool IsZero(ulong value) =>
        (0UL == (value & 1UL));
    /// <summary>Returns the conjunction of two values.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns><c>left · right</c>.</returns>
    public ulong Multiply(ulong left, ulong right) =>
        left & right & 1UL;
    /// <summary>Returns the value itself; every element of <c>GF(2)</c> is its own additive inverse.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The value, masked to its low bit.</returns>
    public ulong Negate(ulong value) =>
        value & 1UL;
    /// <summary>Returns the exclusive-or of two values; subtraction and addition coincide.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns><c>left ⊕ right</c>.</returns>
    public ulong Subtract(ulong left, ulong right) =>
        (left ^ right) & 1UL;
}

/// <summary>The counting semiring <c>(ℕ, +, ·)</c> over <see cref="BigInteger"/> — walk counts, ambiguity degrees, and
/// every multiplicity that must not overflow. Exact and unbounded.</summary>
/// <remarks>The carrier is the naturals, and admission enforces it: a negative coefficient is refused rather than
/// widened, because the counting reading of a coefficient — how many ways, how many walks, how many parses — has no
/// negative value, and a material that squared <c>-1</c> to <c>1</c> would answer that question wrongly without
/// saying so. <see cref="IntegerMaterial"/> is the signed carrier, and the two are chosen at the type argument.</remarks>
public readonly struct CountingMaterial : IExactSemiringMaterial<BigInteger, CountingMaterial> {
    /// <summary>Gets the additive identity, zero.</summary>
    public BigInteger Zero => BigInteger.Zero;
    /// <summary>Gets the multiplicative identity, one.</summary>
    public BigInteger One => BigInteger.One;

    /// <summary>Adds two counts.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum.</returns>
    public BigInteger Add(BigInteger left, BigInteger right) =>
        (left + right);
    /// <summary>Admits a natural number, refusing a negative count.</summary>
    /// <param name="value">The carrier value to validate.</param>
    /// <returns><paramref name="value"/> when it is a natural number; every natural is already canonical.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public BigInteger Canonicalize(BigInteger value) {
        if (value.Sign < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(value),
                actualValue: value,
                message: "A count is a natural number, so it is never negative; IntegerMaterial is the signed carrier."
            );
        }

        return value;
    }
    /// <summary>Folds <c>Σ charges[i]·values[i]</c> exactly.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="values">The per-term values.</param>
    /// <param name="lane">Ignored; the counting material is exact.</param>
    /// <returns>The folded value.</returns>
    public BigInteger FusedChargedLinear(ReadOnlySpan<BigInteger> charges, ReadOnlySpan<BigInteger> values, ChargeLane lane) {
        var accumulator = BigInteger.Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator += (charges[index] * values[index]);
        }

        return accumulator;
    }
    /// <summary>Folds <c>Σ charges[i]·left[i]·right[i]</c> exactly.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="left">The per-term left coefficients.</param>
    /// <param name="right">The per-term right coefficients.</param>
    /// <param name="lane">Ignored; the counting material is exact.</param>
    /// <returns>The folded value.</returns>
    public BigInteger FusedChargedSum(ReadOnlySpan<BigInteger> charges, ReadOnlySpan<BigInteger> left, ReadOnlySpan<BigInteger> right, ChargeLane lane) {
        var accumulator = BigInteger.Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator += (charges[index] * (left[index] * right[index]));
        }

        return accumulator;
    }
    /// <summary>Indicates whether a count is zero.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the count is zero.</returns>
    public bool IsZero(BigInteger value) =>
        value.IsZero;
    /// <summary>Multiplies two counts.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The product.</returns>
    public BigInteger Multiply(BigInteger left, BigInteger right) =>
        (left * right);
}

/// <summary>
/// The tropical material <c>(min, +)</c> over the house scalar <see cref="FixedQ4816"/> — shortest paths, minimum
/// weights, and the whole algebraic path problem at its cheapest dial setting. Its finite carrier is the nonnegative
/// <see cref="FixedQ4816"/> values below <see cref="FixedQ4816.MaxValue"/>; negative carrier values are not tropical
/// weights. It is exact and closed: <c>min</c> selects a representable value and finite addition saturates deliberately
/// to the infinity sentinel instead of wrapping.
/// </summary>
/// <remarks>The additive identity of <c>(min, +)</c> is the <c>+∞</c> that loses every minimum, represented
/// deterministically as <see cref="FixedQ4816.MaxValue"/> — the greatest value the carrier holds, so no representable
/// value is ever discarded in its favour. It is absorbing under the tropical product: a term with an infinite factor
/// stays infinite. A finite sum beyond the carrier likewise becomes infinity.
/// The choice costs the carrier a single raw code point (<c>long.MaxValue</c>), which is therefore not a usable finite
/// weight; the exchange buys an identity that needs no separate flag and no boxed option.</remarks>
public readonly struct TropicalMaterial : IIdempotentMaterial<FixedQ4816, TropicalMaterial> {
    /// <summary>Gets the additive identity of <c>(min, +)</c>: the <c>+∞</c> represented by <see cref="FixedQ4816.MaxValue"/>.</summary>
    public FixedQ4816 Zero => FixedQ4816.MaxValue;
    /// <summary>Gets the multiplicative identity of <c>(min, +)</c>: the additive zero of the carrier.</summary>
    public FixedQ4816 One => FixedQ4816.Zero;

    /// <summary>Returns the lesser of two weights.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns><c>min(left, right)</c>.</returns>
    public FixedQ4816 Add(FixedQ4816 left, FixedQ4816 right) =>
        FixedQ4816.FromRawBits(value: Math.Min(val1: ValidateWeight(value: left.Value), val2: ValidateWeight(value: right.Value)));
    /// <summary>Admits a nonnegative finite weight or the tropical <c>+∞</c> sentinel.</summary>
    /// <param name="value">The carrier value to validate.</param>
    /// <returns><paramref name="value"/> when it belongs to the tropical carrier.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public FixedQ4816 Canonicalize(FixedQ4816 value) =>
        FixedQ4816.FromRawBits(value: ValidateWeight(value: value.Value));
    /// <summary>Folds <c>min over i of (charges[i] + values[i])</c>, with no rounding.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="values">The per-term values.</param>
    /// <param name="lane">Ignored; the tropical material never rounds.</param>
    /// <returns>The folded value.</returns>
    public FixedQ4816 FusedChargedLinear(ReadOnlySpan<FixedQ4816> charges, ReadOnlySpan<FixedQ4816> values, ChargeLane lane) {
        var accumulator = long.MaxValue;

        for (var index = 0; (index < charges.Length); ++index) {
            var term = TropicalProduct(left: charges[index].Value, right: values[index].Value);

            if (term < accumulator) { accumulator = term; }
        }

        return FixedQ4816.FromRawBits(value: accumulator);
    }
    /// <summary>Folds <c>min over i of (charges[i] + left[i] + right[i])</c>, with no rounding.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="left">The per-term left coefficients.</param>
    /// <param name="right">The per-term right coefficients.</param>
    /// <param name="lane">Ignored; the tropical material never rounds.</param>
    /// <returns>The folded value.</returns>
    public FixedQ4816 FusedChargedSum(ReadOnlySpan<FixedQ4816> charges, ReadOnlySpan<FixedQ4816> left, ReadOnlySpan<FixedQ4816> right, ChargeLane lane) {
        var accumulator = long.MaxValue;

        for (var index = 0; (index < charges.Length); ++index) {
            var term = TropicalProduct(
                left: charges[index].Value,
                right: TropicalProduct(left: left[index].Value, right: right[index].Value)
            );

            if (term < accumulator) { accumulator = term; }
        }

        return FixedQ4816.FromRawBits(value: accumulator);
    }
    /// <summary>Indicates whether a weight is the tropical <c>+∞</c>.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value is <see cref="FixedQ4816.MaxValue"/>.</returns>
    public bool IsZero(FixedQ4816 value) =>
        (long.MaxValue == ValidateWeight(value: value.Value));
    /// <summary>Returns the saturating sum of two nonnegative weights, with <c>+∞</c> absorbing.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns><c>left + right</c>, or <c>+∞</c> when either operand is infinite or the finite sum exceeds the carrier.</returns>
    public FixedQ4816 Multiply(FixedQ4816 left, FixedQ4816 right) =>
        FixedQ4816.FromRawBits(value: TropicalProduct(left: left.Value, right: right.Value));

    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static long TropicalProduct(long left, long right) {
        left = ValidateWeight(value: left);
        right = ValidateWeight(value: right);

        if ((long.MaxValue == left) || (long.MaxValue == right) || (left > ((long.MaxValue - 1L) - right))) {
            return long.MaxValue;
        }

        return (left + right);
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static long ValidateWeight(long value) {
        if (value < 0L) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(value),
                actualValue: value,
                message: "A tropical weight must be nonnegative."
            );
        }

        return value;
    }
}

/// <summary>
/// The house scalar <see cref="FixedQ4816"/> as a material: the one member that accumulates at a wider scale before
/// rounding, and so the one that routes through the shared fused kernels rather than re-deriving them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FusedChargedSum"/> at <see cref="ChargeLane.Exact"/> accumulates <c>Σ cᵢ·(lᵢ·rᵢ)</c> at raw Q32 with the
/// integer charges as plain multipliers and rounds once at shift 16 — the per-blade discipline of
/// <see cref="GeometricAlgebra.GeometricProduct"/> and the integer lane of <see cref="QuadraticAlgebra{TScalar}"/>. At
/// <see cref="ChargeLane.General"/> it accumulates <c>Σ cᵢ·lᵢ·rᵢ</c> at Q48 and rounds once at shift 32 — the
/// fractional lane. Both fold into an unchecked <see cref="Int128"/>, which is sufficient at any term count: every
/// accumulated value is an integer-coefficient polynomial in the raw inputs, so the wrapped fold is congruent to the
/// true value modulo <c>2^128</c>; a rounding shift of <c>s ≤ 32</c> turns that <c>k·2^128</c> into <c>k·2^(128−s)</c>
/// on the rounded result, and <c>2^96</c> and <c>2^112</c> both vanish under the carrier's final 64-bit wrap without
/// changing tie parity. No multi-limb path is reachable, because none can differ: the exact fold and the wrapped fold
/// agree on the low 64 bits of the rounded result at every term count and every operand magnitude.
/// </para>
/// <para>
/// <see cref="FusedChargedLinear"/> is the same statement at a constant right operand of one. At
/// <see cref="ChargeLane.Exact"/> the shift-16 rounding has an identically zero remainder, so the linear fold is exact
/// — the property that makes the companion Möbius step exact on an integer relation.
/// </para>
/// </remarks>
public readonly struct FixedMaterial : ISignedMaterial<FixedQ4816, FixedMaterial> {
    /// <summary>Gets the additive identity, zero.</summary>
    public FixedQ4816 Zero => FixedQ4816.Zero;
    /// <summary>Gets the multiplicative identity, one.</summary>
    public FixedQ4816 One => FixedQ4816.One;

    /// <summary>Adds two scalars, wrapping on overflow. Exact: addition never rounds.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum.</returns>
    public FixedQ4816 Add(FixedQ4816 left, FixedQ4816 right) =>
        (left + right);
    /// <summary>Folds <c>Σ charges[i]·values[i]</c> with exactly one rounding.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="values">The per-term values.</param>
    /// <param name="lane">The presentation's rounding lane.</param>
    /// <returns>The folded value; exact at <see cref="ChargeLane.Exact"/>, one shift-16 rounding otherwise.</returns>
    public FixedQ4816 FusedChargedLinear(ReadOnlySpan<FixedQ4816> charges, ReadOnlySpan<FixedQ4816> values, ChargeLane lane) {
        if (ChargeLane.Exact == lane) {
            var exact = 0L;

            for (var index = 0; (index < charges.Length); ++index) {
                exact = unchecked((exact + ((charges[index].Value >> FixedQ4816.FractionBitCount) * values[index].Value)));
            }

            return FixedQ4816.FromRawBits(value: exact);
        }

        var accumulator = Int128.Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator = unchecked((accumulator + ((Int128)charges[index].Value * values[index].Value)));
        }

        return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: accumulator));
    }
    /// <summary>Folds <c>Σ charges[i]·left[i]·right[i]</c> with exactly one rounding.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="left">The per-term left coefficients.</param>
    /// <param name="right">The per-term right coefficients.</param>
    /// <param name="lane">The presentation's rounding lane.</param>
    /// <returns>The folded value: one ties-to-even rounding of the exact sum, wrapped to the raw carrier.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public FixedQ4816 FusedChargedSum(ReadOnlySpan<FixedQ4816> charges, ReadOnlySpan<FixedQ4816> left, ReadOnlySpan<FixedQ4816> right, ChargeLane lane) {
        var accumulator = Int128.Zero;

        if (ChargeLane.Exact == lane) {
            for (var index = 0; (index < charges.Length); ++index) {
                accumulator = unchecked((accumulator + ((charges[index].Value >> FixedQ4816.FractionBitCount) * ((Int128)left[index].Value * right[index].Value))));
            }

            return FixedQ4816.FromRawBits(value: FixedQ4816.RoundProductSum(productSum: accumulator));
        }

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator = unchecked((accumulator + ((Int128)charges[index].Value * ((Int128)left[index].Value * right[index].Value))));
        }

        return FixedQ4816.FromRawBits(value: FusedArithmetic.RoundQ48SumToRaw(productSum: accumulator));
    }
    /// <summary>Indicates whether a scalar is zero.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the raw value is zero.</returns>
    public bool IsZero(FixedQ4816 value) =>
        (0L == value.Value);
    /// <summary>Multiplies two scalars, rounding once.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The rounded product.</returns>
    public FixedQ4816 Multiply(FixedQ4816 left, FixedQ4816 right) =>
        (left * right);
    /// <summary>Negates a scalar.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The negation.</returns>
    public FixedQ4816 Negate(FixedQ4816 value) =>
        FixedQ4816.FromRawBits(value: unchecked(-value.Value));
    /// <summary>Subtracts one scalar from another, wrapping on underflow.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference.</returns>
    public FixedQ4816 Subtract(FixedQ4816 left, FixedQ4816 right) =>
        (left - right);
}

/// <summary>The ring of integers <c>ℤ</c> over <see cref="BigInteger"/> — exact, unbounded, and the reference material
/// every rounding-side claim is measured against.</summary>
public readonly struct IntegerMaterial : ISignedMaterial<BigInteger, IntegerMaterial>, IExactSemiringMaterial<BigInteger, IntegerMaterial> {
    /// <summary>Gets the additive identity, zero.</summary>
    public BigInteger Zero => BigInteger.Zero;
    /// <summary>Gets the multiplicative identity, one.</summary>
    public BigInteger One => BigInteger.One;

    /// <summary>Adds two integers.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum.</returns>
    public BigInteger Add(BigInteger left, BigInteger right) =>
        (left + right);
    /// <summary>Folds <c>Σ charges[i]·values[i]</c> exactly.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="values">The per-term values.</param>
    /// <param name="lane">Ignored; the integer material is exact.</param>
    /// <returns>The folded value.</returns>
    public BigInteger FusedChargedLinear(ReadOnlySpan<BigInteger> charges, ReadOnlySpan<BigInteger> values, ChargeLane lane) {
        var accumulator = BigInteger.Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator += (charges[index] * values[index]);
        }

        return accumulator;
    }
    /// <summary>Folds <c>Σ charges[i]·left[i]·right[i]</c> exactly.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="left">The per-term left coefficients.</param>
    /// <param name="right">The per-term right coefficients.</param>
    /// <param name="lane">Ignored; the integer material is exact.</param>
    /// <returns>The folded value.</returns>
    public BigInteger FusedChargedSum(ReadOnlySpan<BigInteger> charges, ReadOnlySpan<BigInteger> left, ReadOnlySpan<BigInteger> right, ChargeLane lane) {
        var accumulator = BigInteger.Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator += (charges[index] * (left[index] * right[index]));
        }

        return accumulator;
    }
    /// <summary>Indicates whether an integer is zero.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value is zero.</returns>
    public bool IsZero(BigInteger value) =>
        value.IsZero;
    /// <summary>Multiplies two integers.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The product.</returns>
    public BigInteger Multiply(BigInteger left, BigInteger right) =>
        (left * right);
    /// <summary>Negates an integer.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The negation.</returns>
    public BigInteger Negate(BigInteger value) =>
        -value;
    /// <summary>Subtracts one integer from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference.</returns>
    public BigInteger Subtract(BigInteger left, BigInteger right) =>
        (left - right);
}

/// <summary>The exact rationals as a field material, carried by the rational values of <see cref="QuadraticSurd"/> — no
/// new primitive, and the resolvent lane every exact solve wants.</summary>
/// <remarks>The carrier is the rational surds only, and admission enforces it. The surd type also represents
/// <c>a + b·√d</c> for a nonzero <c>b</c>, and those values are not a field between them: each real quadratic field is
/// closed on its own, but <c>√2</c> and <c>√3</c> live in different ones, so admitting both would let a sum leave the
/// carrier and the field material would stop being a field. A coefficient carrying a square root is therefore refused
/// at admission, where the offending value can still be named, rather than later at an operator that only knows the two
/// operands disagree.</remarks>
public readonly struct RationalMaterial : IFieldMaterial<QuadraticSurd, RationalMaterial> {
    /// <summary>Gets the additive identity, zero.</summary>
    public QuadraticSurd Zero => QuadraticSurd.Zero;
    /// <summary>Gets the multiplicative identity, one.</summary>
    public QuadraticSurd One => QuadraticSurd.One;

    /// <summary>Adds two rationals.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum.</returns>
    public QuadraticSurd Add(QuadraticSurd left, QuadraticSurd right) =>
        (left + right);
    /// <summary>Admits an exact rational, refusing a value that carries a square root.</summary>
    /// <param name="value">The carrier value to validate.</param>
    /// <returns><paramref name="value"/> when it is rational; <see cref="QuadraticSurd"/> already normalizes its own
    /// representation, so a rational is canonical as it stands.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is irrational.</exception>
    public QuadraticSurd Canonicalize(QuadraticSurd value) {
        if (!value.IsRational) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(value),
                actualValue: value,
                message: $"A rational coefficient carries no square root, and √{value.Radicand} leaves the field this material is closed over."
            );
        }

        return value;
    }
    /// <summary>Folds <c>Σ charges[i]·values[i]</c> exactly.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="values">The per-term values.</param>
    /// <param name="lane">Ignored; the rational material is exact.</param>
    /// <returns>The folded value.</returns>
    public QuadraticSurd FusedChargedLinear(ReadOnlySpan<QuadraticSurd> charges, ReadOnlySpan<QuadraticSurd> values, ChargeLane lane) {
        var accumulator = QuadraticSurd.Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator += (charges[index] * values[index]);
        }

        return accumulator;
    }
    /// <summary>Folds <c>Σ charges[i]·left[i]·right[i]</c> exactly.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="left">The per-term left coefficients.</param>
    /// <param name="right">The per-term right coefficients.</param>
    /// <param name="lane">Ignored; the rational material is exact.</param>
    /// <returns>The folded value.</returns>
    public QuadraticSurd FusedChargedSum(ReadOnlySpan<QuadraticSurd> charges, ReadOnlySpan<QuadraticSurd> left, ReadOnlySpan<QuadraticSurd> right, ChargeLane lane) {
        var accumulator = QuadraticSurd.Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator += (charges[index] * (left[index] * right[index]));
        }

        return accumulator;
    }
    /// <summary>Indicates whether a rational is zero.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value is zero.</returns>
    public bool IsZero(QuadraticSurd value) =>
        (0 == value.Sign);
    /// <summary>Multiplies two rationals.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The product.</returns>
    public QuadraticSurd Multiply(QuadraticSurd left, QuadraticSurd right) =>
        (left * right);
    /// <summary>Negates a rational.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The negation.</returns>
    public QuadraticSurd Negate(QuadraticSurd value) =>
        -value;
    /// <summary>Subtracts one rational from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference.</returns>
    public QuadraticSurd Subtract(QuadraticSurd left, QuadraticSurd right) =>
        (left - right);
    /// <summary>Attempts to invert a rational.</summary>
    /// <param name="value">The value to invert.</param>
    /// <param name="inverse">On success, the reciprocal; otherwise zero.</param>
    /// <returns><see langword="true"/> unless the value is zero.</returns>
    public bool TryInvert(QuadraticSurd value, out QuadraticSurd inverse) {
        if (0 == value.Sign) {
            inverse = QuadraticSurd.Zero;

            return false;
        }

        inverse = (QuadraticSurd.One / value);

        return true;
    }
}

/// <summary>
/// A prime field <c>GF(p)</c> as a field material, carrying its modulus as instance data — the case that forces the
/// material to be a struct instance rather than a <see langword="static"/> <see langword="abstract"/> surface.
/// </summary>
/// <remarks>A default-initialized value carries no modulus and is not a member of any field; obtain one from
/// <see cref="Create"/> or the <see cref="PrimeFieldMaterial(PrimeField64)"/> constructor.</remarks>
public readonly struct PrimeFieldMaterial : IFieldMaterial<ulong, PrimeFieldMaterial> {
    private readonly PrimeField64 m_field;

    /// <summary>Wraps a prime field as a material.</summary>
    /// <param name="field">The field whose arithmetic the material carries.</param>
    public PrimeFieldMaterial(PrimeField64 field) =>
        m_field = field;

    /// <summary>Gets the field whose arithmetic this material carries.</summary>
    public PrimeField64 Field => m_field;
    /// <summary>Gets the additive identity, zero.</summary>
    public ulong Zero => 0UL;
    /// <summary>Gets the multiplicative identity, one.</summary>
    public ulong One => 1UL;

    /// <summary>Creates the material of a prime field.</summary>
    /// <param name="modulus">The prime modulus, below <see cref="PrimeField64.MaximumModulus"/>.</param>
    /// <returns>The described material.</returns>
    public static PrimeFieldMaterial Create(ulong modulus) =>
        new(field: PrimeField64.Create(modulus: modulus));

    /// <summary>Adds two residues.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The sum.</returns>
    public ulong Add(ulong left, ulong right) =>
        m_field.Add(left: Canonicalize(value: left), right: Canonicalize(value: right));
    /// <summary>Reduces a carrier value to the unique residue in <c>[0, p)</c>.</summary>
    /// <param name="value">The carrier value to reduce.</param>
    /// <returns>The canonical residue.</returns>
    public ulong Canonicalize(ulong value) =>
        m_field.Reduce(value: value);
    /// <summary>Folds <c>Σ charges[i]·values[i]</c> exactly in the field.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="values">The per-term values.</param>
    /// <param name="lane">Ignored; a prime field is exact.</param>
    /// <returns>The folded value.</returns>
    public ulong FusedChargedLinear(ReadOnlySpan<ulong> charges, ReadOnlySpan<ulong> values, ChargeLane lane) {
        var accumulator = 0UL;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator = m_field.Add(
                left: accumulator,
                right: m_field.Multiply(left: Canonicalize(value: charges[index]), right: Canonicalize(value: values[index]))
            );
        }

        return accumulator;
    }
    /// <summary>Folds <c>Σ charges[i]·left[i]·right[i]</c> exactly in the field.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="left">The per-term left coefficients.</param>
    /// <param name="right">The per-term right coefficients.</param>
    /// <param name="lane">Ignored; a prime field is exact.</param>
    /// <returns>The folded value.</returns>
    public ulong FusedChargedSum(ReadOnlySpan<ulong> charges, ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right, ChargeLane lane) {
        var accumulator = 0UL;

        for (var index = 0; (index < charges.Length); ++index) {
            var term = m_field.Multiply(
                left: Canonicalize(value: charges[index]),
                right: m_field.Multiply(left: Canonicalize(value: left[index]), right: Canonicalize(value: right[index]))
            );

            accumulator = m_field.Add(left: accumulator, right: term);
        }

        return accumulator;
    }
    /// <summary>Indicates whether a residue is zero.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the residue is zero.</returns>
    public bool IsZero(ulong value) =>
        (0UL == Canonicalize(value: value));
    /// <summary>Multiplies two residues.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The product.</returns>
    public ulong Multiply(ulong left, ulong right) =>
        m_field.Multiply(left: Canonicalize(value: left), right: Canonicalize(value: right));
    /// <summary>Negates a residue.</summary>
    /// <param name="value">The value to negate.</param>
    /// <returns>The negation.</returns>
    public ulong Negate(ulong value) =>
        m_field.Negate(value: Canonicalize(value: value));
    /// <summary>Subtracts one residue from another.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference.</returns>
    public ulong Subtract(ulong left, ulong right) =>
        m_field.Subtract(left: Canonicalize(value: left), right: Canonicalize(value: right));
    /// <summary>Attempts to invert a residue.</summary>
    /// <param name="value">The value to invert.</param>
    /// <param name="inverse">On success, the multiplicative inverse; otherwise zero.</param>
    /// <returns><see langword="true"/> unless the residue is zero.</returns>
    public bool TryInvert(ulong value, out ulong inverse) {
        value = Canonicalize(value: value);

        if (0UL == value) {
            inverse = 0UL;

            return false;
        }

        inverse = m_field.Inverse(value: value);

        return true;
    }
}

/// <summary>
/// The most-likely-path material <c>(max, ·)</c> over <see cref="UnitInterval32"/> — the probability twin of
/// <see cref="TropicalMaterial"/>. A coefficient is the best likelihood a route carries, so a quiver readout is the
/// most probable path where the tropical one is the shortest. Both absorbing elements are exact: impossible is
/// <see cref="UnitInterval32.Zero"/>, certain is <see cref="UnitInterval32.One"/>, and multiplication by either rounds
/// nothing.
/// </summary>
/// <remarks>
/// <para>
/// The one member of the unit-interval family whose product rounds. <see cref="UnitInterval32.Multiply(UnitInterval32, UnitInterval32)"/>
/// carries one ties-to-even rounding, so a route of <c>L</c> steps carries <c>L − 1</c> of them and its value depends on
/// the order the steps compose — which is the whole content of the log-domain difference from the tropical material,
/// whose <c>+</c> associates exactly. Where the weights are exact powers of two the products are exact and the two
/// materials name the same route at the same cost, which is the isomorphism stated as a law rather than as an analogy.
/// </para>
/// <para>
/// The fused sums round once per term rather than twice, because a term's three factors are multiplied exactly and
/// rounded together through <see cref="UnitInterval32.Multiply(UnitInterval32, UnitInterval32, UnitInterval32)"/>. The fold
/// itself is a maximum, which selects a value already representable and cannot round, so no wide accumulator exists
/// here and none is possible.
/// </para>
/// </remarks>
public readonly struct MostLikelyPathMaterial : IMaterialOps<UnitInterval32, MostLikelyPathMaterial> {
    /// <summary>Gets the additive identity: the impossible outcome, <see cref="UnitInterval32.Zero"/>.</summary>
    public UnitInterval32 Zero => UnitInterval32.Zero;
    /// <summary>Gets the multiplicative identity: the certain outcome, <see cref="UnitInterval32.One"/>.</summary>
    public UnitInterval32 One => UnitInterval32.One;

    /// <summary>Returns the likelier of two values.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns><c>max(left, right)</c>, which is idempotent and exact.</returns>
    public UnitInterval32 Add(UnitInterval32 left, UnitInterval32 right) =>
        UnitInterval32.Max(x: left, y: right);
    /// <summary>Folds <c>max over i of (charges[i]·values[i])</c>, with one rounding per term.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="values">The per-term values.</param>
    /// <param name="lane">Ignored; the fold has no wide accumulator to classify.</param>
    /// <returns>The folded value.</returns>
    public UnitInterval32 FusedChargedLinear(ReadOnlySpan<UnitInterval32> charges, ReadOnlySpan<UnitInterval32> values, ChargeLane lane) {
        var accumulator = Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator = UnitInterval32.Max(x: accumulator, y: UnitInterval32.Multiply(x: charges[index], y: values[index]));
        }

        return accumulator;
    }
    /// <summary>Folds <c>max over i of (charges[i]·left[i]·right[i])</c>, with one rounding per term.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="left">The per-term left coefficients.</param>
    /// <param name="right">The per-term right coefficients.</param>
    /// <param name="lane">Ignored; the fold has no wide accumulator to classify.</param>
    /// <returns>The folded value.</returns>
    public UnitInterval32 FusedChargedSum(ReadOnlySpan<UnitInterval32> charges, ReadOnlySpan<UnitInterval32> left, ReadOnlySpan<UnitInterval32> right, ChargeLane lane) {
        var accumulator = Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator = UnitInterval32.Max(x: accumulator, y: UnitInterval32.Multiply(x: charges[index], y: left[index], z: right[index]));
        }

        return accumulator;
    }
    /// <summary>Indicates whether a value is the impossible outcome.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value is <see cref="UnitInterval32.Zero"/>.</returns>
    public bool IsZero(UnitInterval32 value) =>
        (UnitInterval32.Zero == value);
    /// <summary>Multiplies two likelihoods, rounding once.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns>The rounded product.</returns>
    public UnitInterval32 Multiply(UnitInterval32 left, UnitInterval32 right) =>
        UnitInterval32.Multiply(x: left, y: right);
}

/// <summary>
/// The fuzzy material <c>(max, min)</c> over <see cref="UnitInterval32"/> — a coefficient is a degree of membership, a
/// quiver readout is the widest bottleneck along a route, and nothing here rounds: both operations select an operand,
/// so the material joins <see cref="TropicalMaterial"/> in the exact club.
/// </summary>
/// <remarks>
/// The one material besides <see cref="BooleanMaterial"/> that carries a De Morgan complement, and the reason the
/// pattern lens's <see cref="PatternComplement"/> is not a Boolean-only surface. The involution is
/// <see cref="UnitInterval32.Complement"/>, the exact <c>1 − x</c>: it is an order-reversing bijection of the closed
/// interval, so it carries the maximum onto the minimum and back at every raw, and both De Morgan laws hold exactly
/// rather than approximately. The other admission condition holds too, since <c>max(1, 1) = 1</c>.
/// </remarks>
public readonly struct FuzzyMaterial : IComplementedMaterial<UnitInterval32, FuzzyMaterial> {
    /// <summary>Gets the additive identity, <see cref="UnitInterval32.Zero"/> — membership in nothing.</summary>
    public UnitInterval32 Zero => UnitInterval32.Zero;
    /// <summary>Gets the multiplicative identity, <see cref="UnitInterval32.One"/> — full membership.</summary>
    public UnitInterval32 One => UnitInterval32.One;

    /// <summary>Returns the greater of two membership degrees.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns><c>max(left, right)</c>.</returns>
    public UnitInterval32 Add(UnitInterval32 left, UnitInterval32 right) =>
        UnitInterval32.Max(x: left, y: right);
    /// <summary>Returns the De Morgan complement of a membership degree.</summary>
    /// <param name="value">The value to complement.</param>
    /// <returns><c>1 − value</c>, exactly.</returns>
    public UnitInterval32 Complement(UnitInterval32 value) =>
        UnitInterval32.Complement(value: value);
    /// <summary>Folds <c>max over i of min(charges[i], values[i])</c>, exactly.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="values">The per-term values.</param>
    /// <param name="lane">Ignored; the fuzzy material never rounds.</param>
    /// <returns>The folded value.</returns>
    public UnitInterval32 FusedChargedLinear(ReadOnlySpan<UnitInterval32> charges, ReadOnlySpan<UnitInterval32> values, ChargeLane lane) {
        var accumulator = Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator = UnitInterval32.Max(x: accumulator, y: UnitInterval32.Min(x: charges[index], y: values[index]));
        }

        return accumulator;
    }
    /// <summary>Folds <c>max over i of min(charges[i], left[i], right[i])</c>, exactly.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="left">The per-term left coefficients.</param>
    /// <param name="right">The per-term right coefficients.</param>
    /// <param name="lane">Ignored; the fuzzy material never rounds.</param>
    /// <returns>The folded value.</returns>
    public UnitInterval32 FusedChargedSum(ReadOnlySpan<UnitInterval32> charges, ReadOnlySpan<UnitInterval32> left, ReadOnlySpan<UnitInterval32> right, ChargeLane lane) {
        var accumulator = Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            var term = UnitInterval32.Min(x: UnitInterval32.Min(x: charges[index], y: left[index]), y: right[index]);

            accumulator = UnitInterval32.Max(x: accumulator, y: term);
        }

        return accumulator;
    }
    /// <summary>Indicates whether a membership degree is zero.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value is <see cref="UnitInterval32.Zero"/>.</returns>
    public bool IsZero(UnitInterval32 value) =>
        (UnitInterval32.Zero == value);
    /// <summary>Returns the lesser of two membership degrees.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns><c>min(left, right)</c>.</returns>
    public UnitInterval32 Multiply(UnitInterval32 left, UnitInterval32 right) =>
        UnitInterval32.Min(x: left, y: right);
}

/// <summary>
/// The bounded-sum material <c>(max, ⊙)</c> over <see cref="UnitInterval32"/>, where <c>a ⊙ b</c> is the amount by which
/// <c>a + b</c> exceeds one. It is the strictest of the three: a route survives only while its steps' shortfalls from
/// certainty stay under one in total, so a single weak step can cut a route off entirely where the fuzzy material would
/// merely narrow it.
/// </summary>
/// <remarks>Exact everywhere. <see cref="UnitInterval32.SumExcess"/> is raw addition and a guarded raw subtraction, both
/// inside the storage, and the fold is a maximum; nothing rounds. The product associates on the nose — every nesting of
/// three factors is <c>max(0, a + b + c − 2)</c> — so the three-factor term the fused sum folds needs no wide
/// accumulator and no special case.</remarks>
public readonly struct BoundedSumMaterial : IIdempotentMaterial<UnitInterval32, BoundedSumMaterial> {
    /// <summary>Gets the additive identity, <see cref="UnitInterval32.Zero"/>, which the product also annihilates at.</summary>
    public UnitInterval32 Zero => UnitInterval32.Zero;
    /// <summary>Gets the multiplicative identity, <see cref="UnitInterval32.One"/>.</summary>
    public UnitInterval32 One => UnitInterval32.One;

    /// <summary>Returns the greater of two values.</summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns><c>max(left, right)</c>.</returns>
    public UnitInterval32 Add(UnitInterval32 left, UnitInterval32 right) =>
        UnitInterval32.Max(x: left, y: right);
    /// <summary>Folds <c>max over i of (charges[i] ⊙ values[i])</c>, exactly.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="values">The per-term values.</param>
    /// <param name="lane">Ignored; the bounded-sum material never rounds.</param>
    /// <returns>The folded value.</returns>
    public UnitInterval32 FusedChargedLinear(ReadOnlySpan<UnitInterval32> charges, ReadOnlySpan<UnitInterval32> values, ChargeLane lane) {
        var accumulator = Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            accumulator = UnitInterval32.Max(x: accumulator, y: UnitInterval32.SumExcess(x: charges[index], y: values[index]));
        }

        return accumulator;
    }
    /// <summary>Folds <c>max over i of (charges[i] ⊙ left[i] ⊙ right[i])</c>, exactly.</summary>
    /// <param name="charges">The per-term charges.</param>
    /// <param name="left">The per-term left coefficients.</param>
    /// <param name="right">The per-term right coefficients.</param>
    /// <param name="lane">Ignored; the bounded-sum material never rounds.</param>
    /// <returns>The folded value.</returns>
    public UnitInterval32 FusedChargedSum(ReadOnlySpan<UnitInterval32> charges, ReadOnlySpan<UnitInterval32> left, ReadOnlySpan<UnitInterval32> right, ChargeLane lane) {
        var accumulator = Zero;

        for (var index = 0; (index < charges.Length); ++index) {
            var term = UnitInterval32.SumExcess(x: UnitInterval32.SumExcess(x: charges[index], y: left[index]), y: right[index]);

            accumulator = UnitInterval32.Max(x: accumulator, y: term);
        }

        return accumulator;
    }
    /// <summary>Indicates whether a value is zero.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value is <see cref="UnitInterval32.Zero"/>.</returns>
    public bool IsZero(UnitInterval32 value) =>
        (UnitInterval32.Zero == value);
    /// <summary>Returns the amount by which the sum of two values exceeds one.</summary>
    /// <param name="left">The multiplicand.</param>
    /// <param name="right">The multiplier.</param>
    /// <returns><c>max(0, left + right − 1)</c>, exactly.</returns>
    public UnitInterval32 Multiply(UnitInterval32 left, UnitInterval32 right) =>
        UnitInterval32.SumExcess(x: left, y: right);
}
