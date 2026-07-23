using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The prime field <c>F_p</c> for an odd prime <c>p</c> below <c>2^62</c>. Elements are bare <see cref="ulong"/> values
/// in the range <c>[0, p)</c>; the field object names the structure they live in and carries no element of its own.
/// </summary>
/// <remarks>
/// <para>
/// The modulus bound keeps addition and subtraction a single conditional fold — two representatives sum below
/// <c>2^63</c> and never overflow — while multiplication widens to <see cref="UInt128"/> and reduces once. Every
/// operation expects reduced operands in <c>[0, p)</c>; the preconditions are not enforced on the hot path. Two fields
/// are equal when their moduli agree.
/// </para>
/// <para>
/// This is the odd-characteristic companion to <see cref="BinaryField{T}"/>, and the substrate for the engine's
/// odd-base deterministic permutations and scrambles, odd-radix low-discrepancy sampling nets, procedural incidence
/// structures over a prime alphabet, and exact modular square roots.
/// </para>
/// </remarks>
public readonly record struct PrimeField64 {
    /// <summary>The largest modulus the field admits; a prime must sit strictly below it so two representatives sum without overflowing the carrier.</summary>
    public const ulong MaximumModulus = (1UL << 62);

    /// <summary>Creates a field from its already-validated modulus.</summary>
    /// <param name="modulus">The odd prime modulus.</param>
    private PrimeField64(ulong modulus) {
        Modulus = modulus;
    }

    /// <summary>Creates the prime field <c>F_<paramref name="modulus"/></c>.</summary>
    /// <param name="modulus">The field's modulus, which must be an odd prime below <see cref="MaximumModulus"/>.</param>
    /// <returns>The described field.</returns>
    /// <remarks>
    /// Primality is decided exactly by strong-pseudoprime rounds to a fixed set of witness bases. The twelve bases
    /// <c>2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37</c> are a proven complete witness set for every value below
    /// <c>3.3 * 10^24</c>, which is past <see cref="ulong"/> and far past this field's <c>2^62</c> ceiling, so the
    /// decision is deterministic rather than probabilistic. Nothing else is precomputed, so constructing a field costs
    /// only the primality test.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="modulus"/> is at or above <see cref="MaximumModulus"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="modulus"/> is even or composite, so the quotient ring is not a field.</exception>
    public static PrimeField64 Create(ulong modulus) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: modulus, other: MaximumModulus);

        if (2UL == (modulus & 1UL)) {
            throw new ArgumentException(message: "The modulus must be odd; the two-element field is served by BinaryField.", paramName: nameof(modulus));
        }
        if (!IsPrime(value: modulus)) {
            throw new ArgumentException(message: "The modulus must be prime; a composite modulus does not yield a field.", paramName: nameof(modulus));
        }

        return new PrimeField64(modulus: modulus);
    }
    /// <summary>Gets whether a value is prime, deciding the question exactly for every <see cref="ulong"/>.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is prime; otherwise <see langword="false"/>.</returns>
    /// <remarks>Strong-pseudoprime rounds to the twelve-base complete witness set, valid past <see cref="ulong.MaxValue"/>.</remarks>
    public static bool IsPrime(ulong value) {
        if (2UL > value) { return false; }
        if (2UL == value) { return true; }
        if (0UL == (value & 1UL)) { return false; }

        // A witness set proven complete for every value below 3.317 * 10^24, which exceeds ulong.MaxValue.
        ReadOnlySpan<ulong> witnesses = [2UL, 3UL, 5UL, 7UL, 11UL, 13UL, 17UL, 19UL, 23UL, 29UL, 31UL, 37UL];
        var oddPart = (value - 1UL);
        var twoExponent = BitOperations.TrailingZeroCount(value: oddPart);

        oddPart >>>= twoExponent;

        foreach (var witness in witnesses) {
            var residue = (witness % value);

            if (0UL == residue) { continue; }

            var power = ModularPower(value: residue, exponent: oddPart, modulus: value);

            if ((1UL == power) || ((value - 1UL) == power)) { continue; }

            var composite = true;

            for (var round = 1; (round < twoExponent); ++round) {
                power = ModularProduct(left: power, right: power, modulus: value);

                if ((value - 1UL) == power) {
                    composite = false;

                    break;
                }
            }

            if (composite) { return false; }
        }

        return true;
    }

    /// <summary>Gets the field's modulus, so that the field has <c>Modulus</c> elements.</summary>
    public ulong Modulus { get; }
    /// <summary>Gets the multiplicative identity.</summary>
    public ulong One => 1UL;
    /// <summary>Gets the additive identity.</summary>
    public ulong Zero => 0UL;

    /// <summary>Adds two field elements.</summary>
    /// <param name="left">The first reduced addend.</param>
    /// <param name="right">The second reduced addend.</param>
    /// <returns>The reduced sum.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Add(ulong left, ulong right) {
        var sum = (left + right);

        return ((sum >= Modulus) ? (sum - Modulus) : sum);
    }
    /// <summary>Inverts every element of a region in place through a single field inversion.</summary>
    /// <param name="values">The reduced, non-zero elements to invert; each is overwritten with its inverse.</param>
    /// <remarks>
    /// The running-product method: a forward pass accumulates the partial products <c>a_0, a_0 a_1, ...</c>, one
    /// inversion turns the whole product over, and a backward pass peels each element off that inverse. The cost is one
    /// inversion plus about three multiplications per element, replacing the <c>n</c> inversions the naive loop would
    /// perform. The partial-product scratch is stack-allocated for small batches and pooled for large ones, so nothing
    /// is allocated on the managed heap.
    /// </remarks>
    /// <exception cref="DivideByZeroException">Any element is zero; the shared product is then zero and has no inverse.</exception>
    public void BatchInverse(Span<ulong> values) {
        var count = values.Length;

        if (0 == count) { return; }

        const int StackThreshold = 512;
        var pooled = ((count > StackThreshold) ? ArrayPool<ulong>.Shared.Rent(minimumLength: count) : null);
        Span<ulong> stackScratch = stackalloc ulong[((pooled is null) ? StackThreshold : 0)];
        var prefix = ((pooled is null) ? stackScratch : pooled.AsSpan());

        try {
            var running = 1UL;

            for (var index = 0; (index < count); ++index) {
                running = Multiply(left: running, right: values[index]);
                prefix[index] = running;
            }

            var inverse = Inverse(value: running);

            for (var index = (count - 1); (index >= 1); --index) {
                var element = values[index];

                values[index] = Multiply(left: inverse, right: prefix[index - 1]);
                inverse = Multiply(left: inverse, right: element);
            }

            values[0] = inverse;
        }
        finally {
            if (pooled is not null) { ArrayPool<ulong>.Shared.Return(array: pooled); }
        }
    }
    /// <summary>Computes the multiplicative inverse of a non-zero field element.</summary>
    /// <param name="value">The reduced, non-zero element to invert.</param>
    /// <returns>The unique element whose product with <paramref name="value"/> is <see cref="One"/>.</returns>
    /// <remarks>The inverse is <c>value^(p - 2)</c>, evaluated by square-and-multiply. The operand must already be reduced; the precondition is not enforced.</remarks>
    /// <exception cref="DivideByZeroException"><paramref name="value"/> is zero.</exception>
    public ulong Inverse(ulong value) {
        if (0UL == value) { throw new DivideByZeroException("Zero has no multiplicative inverse."); }

        return Pow(value: value, exponent: (Modulus - 2UL));
    }
    /// <summary>Computes the quadratic character of a field element by the exponentiation criterion.</summary>
    /// <param name="value">The reduced element to test.</param>
    /// <returns><c>0</c> when <paramref name="value"/> is zero, <c>1</c> when it is a non-zero square, and <c>-1</c> when it is a non-square.</returns>
    /// <remarks>The value <c>value^((p - 1) / 2)</c> is <c>0</c>, <c>1</c>, or <c>p - 1</c>; the last maps to <c>-1</c>.</remarks>
    public int LegendreCharacter(ulong value) {
        if (0UL == value) { return 0; }

        var power = Pow(value: value, exponent: ((Modulus - 1UL) >>> 1));

        return ((1UL == power) ? 1 : -1);
    }
    /// <summary>Multiplies two field elements.</summary>
    /// <param name="left">The first reduced factor.</param>
    /// <param name="right">The second reduced factor.</param>
    /// <returns>The reduced product.</returns>
    /// <remarks>The product widens to <see cref="UInt128"/> and is reduced once. Both operands must already be reduced; the precondition is not enforced.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Multiply(ulong left, ulong right) =>
        ModularProduct(left: left, right: right, modulus: Modulus);
    /// <summary>Negates a field element.</summary>
    /// <param name="value">The reduced element to negate.</param>
    /// <returns>The reduced additive inverse.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Negate(ulong value) =>
        ((0UL == value) ? 0UL : (Modulus - value));
    /// <summary>Raises a field element to a power.</summary>
    /// <param name="value">The reduced element to raise.</param>
    /// <param name="exponent">The exponent; zero yields <see cref="One"/> for every <paramref name="value"/>.</param>
    /// <returns><paramref name="value"/> raised to <paramref name="exponent"/>, reduced.</returns>
    /// <remarks>Square-and-multiply over the exponent's binary expansion, so the operation count depends on the exponent and the routine is not constant-time in it.</remarks>
    public ulong Pow(ulong value, ulong exponent) =>
        ModularPower(value: value, exponent: exponent, modulus: Modulus);
    /// <summary>Reduces an arbitrary unsigned value into the field.</summary>
    /// <param name="value">The value to reduce.</param>
    /// <returns>The representative of <paramref name="value"/> in <c>[0, p)</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Reduce(ulong value) =>
        (value % Modulus);
    /// <summary>Reduces a signed value into the field, folding negatives up by the modulus.</summary>
    /// <param name="value">The value to reduce.</param>
    /// <returns>The representative of <paramref name="value"/> in <c>[0, p)</c>.</returns>
    public ulong Reduce(long value) {
        var folded = (value % ((long)Modulus));

        return ((folded < 0L) ? ((ulong)(folded + ((long)Modulus))) : ((ulong)folded));
    }
    /// <summary>Subtracts one field element from another.</summary>
    /// <param name="left">The reduced minuend.</param>
    /// <param name="right">The reduced subtrahend.</param>
    /// <returns>The reduced difference.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Subtract(ulong left, ulong right) =>
        ((left >= right) ? (left - right) : ((left + Modulus) - right));
    /// <summary>Attempts to compute a square root of a field element.</summary>
    /// <param name="value">The reduced element to take the root of.</param>
    /// <param name="root">When this method returns <see langword="true"/>, one of the two square roots of <paramref name="value"/> (the other is its negation); when it returns <see langword="false"/>, zero.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a square and a root was found; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// <para>
    /// Zero roots to zero. When the modulus is congruent to three modulo four, a square's root is the direct power
    /// <c>value^((p + 1) / 4)</c>. Otherwise the modulus is congruent to one modulo four and the root comes from the
    /// nonresidue-assisted descent: writing <c>p - 1 = q * 2^s</c> with <c>q</c> odd, the algorithm seeds a root of the
    /// odd part and a <c>2^s</c>-th root of unity built from the smallest non-square, then repeatedly squares a running
    /// residue to locate the least power of two at which it becomes one, correcting the root by the matching power of
    /// that root of unity until the residue is one. Each correction strictly lowers that power, so the loop always
    /// halts. The method decides the character itself, so a non-square is reported rather than throwing.
    /// </para>
    /// </remarks>
    public bool TrySqrt(ulong value, out ulong root) {
        if (0UL == value) {
            root = 0UL;

            return true;
        }
        if (1 != LegendreCharacter(value: value)) {
            root = 0UL;

            return false;
        }

        // p ≡ 3 (mod 4): the root is a single power, with no descent needed.
        if (3UL == (Modulus & 3UL)) {
            root = Pow(value: value, exponent: ((Modulus + 1UL) >>> 2));

            return true;
        }

        // p ≡ 1 (mod 4): the nonresidue-assisted descent. Split p - 1 = q * 2^s with q odd.
        var oddPart = (Modulus - 1UL);
        var twoExponent = BitOperations.TrailingZeroCount(value: oddPart);

        oddPart >>>= twoExponent;

        var nonResidue = 2UL;

        while (1 != -LegendreCharacter(value: nonResidue)) { ++nonResidue; }

        var rootOfUnity = Pow(value: nonResidue, exponent: oddPart);
        var candidate = Pow(value: value, exponent: ((oddPart + 1UL) >>> 1));
        var residue = Pow(value: value, exponent: oddPart);
        var order = twoExponent;

        while (1UL != residue) {
            var squares = residue;
            var lowest = 0;

            while (1UL != squares) {
                squares = Multiply(left: squares, right: squares);
                ++lowest;
            }

            var lift = rootOfUnity;

            for (var step = 0; (step < ((order - lowest) - 1)); ++step) { lift = Multiply(left: lift, right: lift); }

            candidate = Multiply(left: candidate, right: lift);
            rootOfUnity = Multiply(left: lift, right: lift);
            residue = Multiply(left: residue, right: rootOfUnity);
            order = lowest;
        }

        root = candidate;

        return true;
    }

    /// <summary>Computes <c>left * right mod modulus</c> for operands below <c>2^62</c> by widening to <see cref="UInt128"/>.</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <param name="modulus">The modulus.</param>
    /// <returns>The reduced product.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ModularProduct(ulong left, ulong right, ulong modulus) =>
        ((ulong)((((UInt128)left) * right) % modulus));
    /// <summary>Computes <c>value^exponent mod modulus</c> by square-and-multiply.</summary>
    /// <param name="value">The base, reduced modulo <paramref name="modulus"/>.</param>
    /// <param name="exponent">The exponent.</param>
    /// <param name="modulus">The modulus.</param>
    /// <returns>The reduced power.</returns>
    private static ulong ModularPower(ulong value, ulong exponent, ulong modulus) {
        var power = value;
        var result = 1UL;

        while (0UL != exponent) {
            if (0UL != (exponent & 1UL)) { result = ModularProduct(left: result, right: power, modulus: modulus); }

            exponent >>>= 1;

            if (0UL != exponent) { power = ModularProduct(left: power, right: power, modulus: modulus); }
        }

        return result;
    }
}
