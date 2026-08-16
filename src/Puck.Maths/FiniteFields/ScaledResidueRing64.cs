using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The residue ring <c>Z/nZ</c> for an odd modulus <c>n</c> below <c>2^64</c>, carried in Montgomery form so that a
/// chain of modular multiplications performs no hardware division.
/// </summary>
/// <remarks>
/// <para>
/// A residue <c>a</c> is represented by <c>a * R mod n</c>, where the radix <c>R</c> is <c>2^64</c>. In that
/// representation a product reduces by REDC — two widening multiplies, a truncated multiply by
/// <see cref="ModulusInverse"/>, two additions, and one conditional subtraction — instead of by the <c>128 / 64</c>
/// divide a direct <c>(a * b) % n</c> costs. The saving belongs to the chain, not to
/// one product: <see cref="Encode(ulong)"/> and <see cref="Decode(ulong)"/> each spend a REDC of their own, so a lone
/// product is cheaper left on the divide. Convert once, stay in the ring, convert back once. The additive operations —
/// <see cref="Add(ulong, ulong)"/>, <see cref="Subtract(ulong, ulong)"/>, and <see cref="Halve(ulong)"/> — are linear in
/// the representation, so they apply to Montgomery-form elements unchanged and a recurrence mixing them with products
/// never has to leave the ring.
/// </para>
/// <para>
/// Only oddness is required — nothing here presumes the modulus prime, which is what admits the ring as the arithmetic
/// of a primality test on a candidate not yet decided, rather than only of one already settled. Elements are bare
/// <see cref="ulong"/> values in <c>[0, Modulus)</c>, so the ring object names the representation and carries no element
/// of its own, the convention <see cref="PrimeField64"/> also follows.
/// </para>
/// </remarks>
internal readonly struct ScaledResidueRing64 {
    /// <summary>Creates the ring over an odd modulus.</summary>
    /// <param name="modulus">The modulus, which must be odd and greater than one. The precondition is not enforced.</param>
    /// <remarks>
    /// Three derived constants are all the ring needs, and none of them is repeated per operation: the negated 2-adic
    /// inverse from <see cref="UnsignedNumberFunctions.ModularInverse{T}(T)"/> — a division-free Newton–Hensel
    /// iteration rather than a reduction — and the remainders of the radix and of its square.
    /// </remarks>
    public ScaledResidueRing64(ulong modulus) {
        // The radix is one above a value the carrier can hold, so it is reduced as (R - 1) and lifted afterwards; an odd
        // modulus never divides the radix, so the reduced value is never zero and the lift cannot carry out of range.
        var one = ((ulong.MaxValue % modulus) + 1UL);

        Modulus = modulus;
        ModulusInverse = unchecked((0UL - modulus.ModularInverse()));
        One = one;
        RadixSquared = ((ulong)((((UInt128)one) * one) % modulus));
    }

    /// <summary>Gets the ring's modulus, so that the ring has <c>Modulus</c> elements.</summary>
    public ulong Modulus { get; }
    /// <summary>Gets the value <c>m'</c> satisfying <c>Modulus * m' ≡ -1</c> modulo the radix, the factor REDC folds the low half away with.</summary>
    public ulong ModulusInverse { get; }
    /// <summary>Gets the Montgomery form of <c>-1</c>.</summary>
    public ulong NegativeOne => (Modulus - One);
    /// <summary>Gets the Montgomery form of <c>1</c>, which is the radix reduced.</summary>
    public ulong One { get; }
    /// <summary>Gets the square of the radix reduced, the factor <see cref="Encode(ulong)"/> multiplies by.</summary>
    public ulong RadixSquared { get; }

    /// <summary>Adds two ring elements.</summary>
    /// <param name="left">The first reduced addend, in Montgomery form.</param>
    /// <param name="right">The second reduced addend, in Montgomery form.</param>
    /// <returns>The reduced sum.</returns>
    /// <remarks>
    /// The representation is linear — <c>aR + bR</c> is <c>(a + b)R</c> — so a sum needs no REDC of its own, only the
    /// conditional fold every modular addition needs. Above <c>2^63</c> the untruncated sum no longer fits the carrier,
    /// so the wrap is detected rather than assumed away, and folding a wrapped sum lands on the right value anyway,
    /// because the radix vanishes modulo the carrier.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Add(ulong left, ulong right) {
        var sum = (left + right);
        var fold = unchecked((0UL - ((sum < left) | (sum >= Modulus)).As<ulong>()));

        return (sum - (Modulus & fold));
    }
    /// <summary>Recovers the ordinary residue a Montgomery-form element stands for.</summary>
    /// <param name="value">The reduced Montgomery-form element.</param>
    /// <returns>The residue in <c>[0, Modulus)</c>.</returns>
    /// <remarks>One REDC against the ordinary one strips exactly one factor of the radix, which is the whole conversion.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Decode(ulong value) =>
        Multiply(
            left: value,
            right: 1UL
        );
    /// <summary>Converts an ordinary residue into Montgomery form.</summary>
    /// <param name="value">The residue to convert.</param>
    /// <returns>The reduced Montgomery form of <paramref name="value"/>.</returns>
    /// <remarks>
    /// One REDC against <see cref="RadixSquared"/>. That factor is itself reduced, so the product stays inside
    /// <see cref="Multiply(ulong, ulong)"/>'s admissible range for every <see cref="ulong"/>: an argument that is not
    /// yet reduced is folded rather than mishandled.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Encode(ulong value) =>
        Multiply(
            left: value,
            right: RadixSquared
        );
    /// <summary>Halves a ring element.</summary>
    /// <param name="value">The reduced element to halve, in Montgomery form.</param>
    /// <returns>The reduced product of <paramref name="value"/> and the inverse of two.</returns>
    /// <remarks>
    /// Halving is a multiplication by the inverse of two, which for an odd modulus is <c>(Modulus + 1) / 2</c>, and the
    /// representation is linear, so it applies to a Montgomery-form element unchanged. An even element halves outright;
    /// an odd one is lifted by the odd modulus first, which changes nothing modulo it. Folding the lift into the shifted
    /// half — rather than adding the modulus and then shifting — is what keeps the whole operation inside the carrier
    /// for a modulus above <c>2^63</c>, and <c>(Modulus &gt;&gt; 1) + 1</c> is <c>(Modulus + 1) / 2</c> written so that
    /// the largest odd modulus does not overflow it either.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Halve(ulong value) =>
        ((value >>> 1) + (((Modulus >>> 1) + 1UL) & unchecked((0UL - (value & 1UL)))));
    /// <summary>Multiplies two ring elements.</summary>
    /// <param name="left">The first factor, in Montgomery form.</param>
    /// <param name="right">The second factor, in Montgomery form.</param>
    /// <returns>The reduced Montgomery-form product.</returns>
    /// <remarks>
    /// REDC, branchlessly. The reduction is exact whenever the product of the operands stays below
    /// <c>2^64 * Modulus</c> — two reduced operands always do, as does one arbitrary operand against a reduced one — and
    /// the precondition is not enforced on this hot path.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Multiply(ulong left, ulong right) {
        var product = (((UInt128)left) * right); // UInt128 multiplication, not Math.BigMul: the JIT expands the widening multiply inline and beats the BCL helper.
        var low = ((ulong)product);
        var factor = unchecked((low * ModulusInverse));
        // The correction's low half is the negation of the product's, so the two cancel exactly and the low half
        // contributes nothing but its carry, which is set precisely when the product's low half is non-zero.
        var addend = (((ulong)((((UInt128)factor) * Modulus) >>> 64)) + low.IsNonZero());
        var sum = (((ulong)(product >>> 64)) + addend);
        // The true quotient is 65 bits wide -- sum, plus the radix when that addition wrapped -- and below twice the
        // modulus either way, so one conditional subtraction lands it in range, and a wrapped difference is already the
        // exact answer because the radix vanishes modulo the carrier.
        var inRange = unchecked((0UL - ((sum >= addend) & (sum < Modulus)).As<ulong>()));

        return ((sum - Modulus) + (Modulus & inRange));
    }
    /// <summary>Raises a ring element to a power.</summary>
    /// <param name="value">The reduced Montgomery-form base.</param>
    /// <param name="exponent">The exponent; zero yields <see cref="One"/> for every <paramref name="value"/>.</param>
    /// <returns>The reduced Montgomery-form power.</returns>
    /// <remarks>
    /// Square-and-multiply over the exponent's binary expansion, least significant bit first, so the operation count
    /// depends on the exponent and the routine is not constant-time in it. The most-significant-bit-first walk that
    /// would make the per-bit multiply unconditional is slower here (measured): its branchless select lands inside the
    /// squaring dependency chain, which is the critical path.
    /// </remarks>
    public ulong Power(ulong value, ulong exponent) {
        var power = value;
        var result = One;

        while (0UL != exponent) {
            if (0UL != (exponent & 1UL)) {
                result = Multiply(
                    left: result,
                    right: power
                );
            }

            exponent >>>= 1;

            if (0UL != exponent) {
                power = Multiply(
                    left: power,
                    right: power
                );
            }
        }

        return result;
    }
    /// <summary>Subtracts one ring element from another.</summary>
    /// <param name="left">The reduced minuend, in Montgomery form.</param>
    /// <param name="right">The reduced subtrahend, in Montgomery form.</param>
    /// <returns>The reduced difference.</returns>
    /// <remarks>
    /// The counterpart to <see cref="Add(ulong, ulong)"/>, and linear for the same reason. A borrowed difference is
    /// already exact once the modulus is added back, because the radix vanishes modulo the carrier.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Subtract(ulong left, ulong right) {
        var difference = (left - right);
        var borrow = unchecked((0UL - (left < right).As<ulong>()));

        return (difference + (Modulus & borrow));
    }
}
