using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

/// <summary>
/// The residue ring <c>Z/nZ</c> for an odd modulus <c>n</c> below <c>2^64</c>, carried in Montgomery form so that a
/// chain of modular multiplications performs no hardware division.
/// </summary>
/// <remarks>
/// <para>
/// A residue <c>a</c> is represented by <c>a * R mod n</c>, where the radix <c>R</c> is <c>2^64</c>. In that
/// representation a product reduces by REDC — two high-half multiplies, two truncated multiplies, one subtraction, and
/// one masked correction — instead of by the <c>128 / 64</c> divide a direct <c>(a * b) % n</c> costs. The saving
/// belongs to the chain, not to one product: <see cref="Encode(ulong)"/> and <see cref="Decode(ulong)"/> each spend a
/// REDC of their own, so a lone product is cheaper left on the divide. Convert once, stay in the ring, convert back
/// once. The additive operations — <see cref="Add(ulong, ulong)"/> and <see cref="Subtract(ulong, ulong)"/> — are
/// linear in the representation, so they apply to Montgomery-form elements unchanged and a recurrence mixing them with
/// products never has to leave the ring.
/// </para>
/// <para>
/// Only oddness is required — nothing here presumes the modulus prime, which is what admits the ring as the arithmetic
/// of a primality test on a candidate not yet decided, rather than only of one already settled. Elements are bare
/// <see cref="ulong"/> values in <c>[0, Modulus)</c>, so the ring object names the representation and carries no element
/// of its own, the convention <see cref="PrimeField64"/> also follows.
/// </para>
/// <para>
/// Every operation also exists as a static kernel taking the modulus and its inverse as arguments. A hot loop copies
/// the two constants into locals once and calls the kernels, so both stay in registers across the whole chain instead
/// of being re-read through the ring on every product.
/// </para>
/// </remarks>
public readonly struct ScaledResidueRing64 {
    /// <summary>The exclusive bound on a modulus whose chains may carry residues lazily, below eight times the modulus, between products reduced by <see cref="ReduceLazy(ulong, ulong, ulong, ulong)"/>.</summary>
    /// <remarks><c>2^58</c>: the lifted REDC of a product of two operands below <c>8n</c> is below <c>64 n^2 / 2^64 + n</c>, which is below <c>2n</c> exactly when <c>64n</c> is below <c>2^64</c>.</remarks>
    public const ulong LazyModulusCeiling = (1UL << 58);
    /// <summary>The exclusive bound on a modulus whose chains may carry residues below twice the modulus between products reduced by <see cref="ReduceLazy(ulong, ulong, ulong, ulong)"/>, folding each sum and difference once by twice the modulus.</summary>
    /// <remarks><c>2^62</c>: the lifted REDC of a product of two operands below <c>2n</c> is below <c>4 n^2 / 2^64 + n</c>, which is below <c>2n</c> exactly when <c>4n</c> is below <c>2^64</c>; that is also what keeps a sum of two such operands inside the carrier.</remarks>
    public const ulong FoldedLazyModulusCeiling = (1UL << 62);

    /// <summary>Creates the ring over an odd modulus.</summary>
    /// <param name="modulus">The modulus, which must be odd and greater than one. The precondition is not enforced.</param>
    /// <remarks>
    /// Three derived constants are all the ring needs, and none of them is repeated per operation: the 2-adic inverse
    /// from <see cref="UnsignedNumberFunctions.ModularInverse{T}(T)"/> — a division-free Newton–Hensel iteration rather
    /// than a reduction — and the remainders of the radix and of its square.
    /// </remarks>
    public ScaledResidueRing64(ulong modulus) {
        var one = RadixResidue(modulus: modulus);

        Modulus = modulus;
        ModulusInverse = modulus.ModularInverse();
        One = one;
        RadixSquared = ((ulong)((((UInt128)one) * one) % modulus));
    }

    /// <summary>Gets the ring's modulus, so that the ring has <c>Modulus</c> elements.</summary>
    public ulong Modulus { get; }
    /// <summary>Gets the value <c>m'</c> satisfying <c>Modulus * m' ≡ 1</c> modulo the radix, the factor REDC cancels the low half with.</summary>
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
    /// <remarks>See <see cref="Add(ulong, ulong, ulong)"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Add(ulong left, ulong right) =>
        Add(
            left: left,
            modulus: Modulus,
            right: right
        );
    /// <summary>Adds two residues modulo an odd modulus.</summary>
    /// <param name="left">The first reduced addend.</param>
    /// <param name="modulus">The odd modulus.</param>
    /// <param name="right">The second reduced addend.</param>
    /// <returns>The reduced sum.</returns>
    /// <remarks>
    /// <para>
    /// The representation is linear — <c>aR + bR</c> is <c>(a + b)R</c> — so a sum needs no REDC of its own, only the
    /// fold every modular addition needs. The fold is decided by one comparison against the room the modulus leaves
    /// above the right addend, <c>modulus - right</c>: a left addend at or past it has a sum at or past the modulus,
    /// and the truncated sum less the modulus is then the residue even when the untruncated sum left the carrier,
    /// because the radix vanishes modulo the carrier. So a modulus above <c>2^63</c> needs no wrap detection of its own.
    /// </para>
    /// <para>
    /// The fold is a mask rather than a conditional. The JIT turns a select inside a loop into a branch, never a
    /// conditional move, and a data-dependent branch on a multiplication chain mispredicts about half the time; the
    /// mask costs a serial run of set, widen, negate and mask instructions instead, which is still the cheaper form.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Add(ulong left, ulong right, ulong modulus) {
        var fold = unchecked((0UL - (left >= (modulus - right)).As<ulong>()));

        return ((left + right) - (modulus & fold));
    }
    /// <summary>Folds a value below twice an odd modulus into <c>[0, modulus)</c>; see <see cref="Canonical(ulong, ulong)"/> for wider operands.</summary>
    /// <param name="value">The value, below <c>2 * modulus</c>.</param>
    /// <param name="modulus">The odd modulus.</param>
    /// <returns><paramref name="value"/> less the modulus when it is at or past it; otherwise <paramref name="value"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Fold(ulong value, ulong modulus) =>
        (value - (modulus & unchecked((0UL - (value >= modulus).As<ulong>()))));
    /// <summary>Reduces a lazily carried residue below eight times an odd modulus into <c>[0, modulus)</c>.</summary>
    /// <param name="value">The value, below <c>8 * modulus</c>, so the modulus is below <see cref="LazyModulusCeiling"/>.</param>
    /// <param name="modulus">The odd modulus, below <see cref="LazyModulusCeiling"/>.</param>
    /// <returns>The residue of <paramref name="value"/> in <c>[0, modulus)</c>.</returns>
    /// <remarks>
    /// Three folds, by four times, twice and once the modulus, each a mask: a value below <c>8n</c> is below <c>4n</c>
    /// after the first, below <c>2n</c> after the second and below <c>n</c> after the third. The widest operand the
    /// lazy chains of <see cref="PrimeKernels"/> carry is below <c>8n</c>, which is what fixes the count.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Canonical(ulong value, ulong modulus) {
        var twice = (modulus << 1);

        return Fold(
            modulus: modulus,
            value: Fold(
                modulus: twice,
                value: Fold(
                    modulus: (twice << 1),
                    value: value
                )
            )
        );
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
    /// <summary>Multiplies two ring elements.</summary>
    /// <param name="left">The first factor, in Montgomery form.</param>
    /// <param name="right">The second factor, in Montgomery form.</param>
    /// <returns>The reduced Montgomery-form product.</returns>
    /// <remarks>See <see cref="Reduce(ulong, ulong, ulong, ulong)"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Multiply(ulong left, ulong right) =>
        Reduce(
            inverse: ModulusInverse,
            left: left,
            modulus: Modulus,
            right: right
        );
    /// <summary>Returns the high half of the 128-bit product of two words.</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <returns><c>floor(left * right / 2^64)</c>.</returns>
    /// <remarks>
    /// BMI2's flag-free <c>MULX</c> where the processor has it, which leaves the low half to an ordinary truncated
    /// multiply the out-of-order core runs beside it; <see cref="Math.BigMul(ulong, ulong, out ulong)"/> otherwise. Both
    /// return the same bits, and the choice is a JIT-time constant.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong MultiplyHigh(ulong left, ulong right) =>
        (Bmi2.X64.IsSupported
            ? Bmi2.X64.MultiplyNoFlags(
                left: left,
                right: right
            )
            : Math.BigMul(
                a: left,
                b: right,
                low: out _
            ));
    /// <summary>Returns the radix <c>2^64</c> reduced modulo an odd modulus, the Montgomery form of one.</summary>
    /// <param name="modulus">The odd modulus, greater than one.</param>
    /// <returns><c>2^64 mod modulus</c>.</returns>
    /// <remarks>
    /// Above <c>2^63</c> the radix lies between the modulus and twice it, so the remainder is the carrier's own negation
    /// of the modulus and no division runs. Below, the radix is reduced as <c>R - 1</c> and lifted afterwards; an odd
    /// modulus never divides the radix, so the reduced value is never zero and the lift cannot carry out of range.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong RadixResidue(ulong modulus) =>
        ((modulus > (1UL << 63))
            ? unchecked((0UL - modulus))
            : ((ulong.MaxValue % modulus) + 1UL));
    /// <summary>Multiplies two Montgomery-form residues and reduces the product by REDC.</summary>
    /// <param name="left">The first factor.</param>
    /// <param name="right">The second factor.</param>
    /// <param name="modulus">The odd modulus.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>, <see cref="ModulusInverse"/>.</param>
    /// <returns>The reduced product <c>left * right / 2^64</c> modulo <paramref name="modulus"/>, in <c>[0, modulus)</c>.</returns>
    /// <remarks>
    /// <para>
    /// The subtractive form. The factor <c>m = lo(left * right) * inverse</c> makes <c>m * modulus</c> agree with the
    /// product in its low half, so the two differ by an exact multiple of the radix and the quotient is the difference
    /// of their high halves. Each high half is below the modulus whenever the product is below <c>2^64 * modulus</c> —
    /// two reduced operands always are, as is one arbitrary operand against a reduced one — so the difference lies in
    /// <c>(-modulus, modulus)</c> and one masked addition of the modulus lands it in range. The precondition is not
    /// enforced on this hot path. A residue in <c>[0, modulus)</c> is unique, so this form returns the same bits as any
    /// other exact reduction.
    /// </para>
    /// <para>
    /// The correction is a mask rather than a conditional: the comparison sits on a multiplication chain's critical
    /// path, where a data-dependent branch mispredicts about half the time, and the JIT turns a select inside a loop
    /// into a branch rather than a conditional move. The low half of the product is a separate truncated multiply
    /// rather than the second output of the widening one: <see cref="Math.BigMul(ulong, ulong, out ulong)"/> and the
    /// <see cref="UInt128"/> product both hand that half back through memory, and it heads the chain's critical path.
    /// Chains over a modulus below <see cref="LazyModulusCeiling"/> run <see cref="ReduceLazy(ulong, ulong, ulong, ulong)"/>
    /// instead, which has no correction at all.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Reduce(ulong left, ulong right, ulong modulus, ulong inverse) {
        var factor = unchecked(((left * right) * inverse));
        var high = MultiplyHigh(
            left: left,
            right: right
        );
        var correction = MultiplyHigh(
            left: factor,
            right: modulus
        );
        var borrow = unchecked((0UL - (high < correction).As<ulong>()));

        return ((high - correction) + (modulus & borrow));
    }
    /// <summary>Multiplies two lazily carried residues whose product is below <c>2^64</c> times the modulus and reduces it by REDC lifted by the modulus, to a value below twice the modulus.</summary>
    /// <param name="left">The first factor: below <c>8 * modulus</c> for a modulus below <see cref="LazyModulusCeiling"/>, below <c>2 * modulus</c> for one below <see cref="FoldedLazyModulusCeiling"/>.</param>
    /// <param name="right">The second factor, under the same bound as <paramref name="left"/>.</param>
    /// <param name="modulus">The odd modulus, below <see cref="FoldedLazyModulusCeiling"/>.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>, <see cref="ModulusInverse"/>.</param>
    /// <returns>A value below <c>2 * modulus</c> congruent to <c>left * right / 2^64</c> modulo <paramref name="modulus"/>.</returns>
    /// <remarks>
    /// <para>
    /// The subtractive quotient of <see cref="Reduce(ulong, ulong, ulong, ulong)"/>, <c>hi(left * right) - hi(m * modulus)</c>,
    /// lies in <c>(-modulus, left * right / 2^64)</c>, so adding the modulus lands it in <c>(0, 2 * modulus)</c>
    /// whenever the product is below <c>2^64 * modulus</c>. Two operands below <c>8 * modulus</c> satisfy that below
    /// <see cref="LazyModulusCeiling"/>, where <c>64 * modulus</c> is below <c>2^64</c>; two below <c>2 * modulus</c>
    /// satisfy it below <see cref="FoldedLazyModulusCeiling"/>. A chain that keeps its operands under the bound its
    /// modulus allows needs no comparison between its products, and <see cref="Canonical(ulong, ulong)"/> or
    /// <see cref="Fold(ulong, ulong)"/> recovers the unique residue wherever one is compared. The modulus is added to
    /// the product's high half, which is ready before the critical multiply, so the chain's step is the three
    /// multiplies and one subtraction. The high half is formed and lifted before the factor, in that order in the
    /// source as well: the JIT allocates in source order, and this order keeps the interleaved four-chain walk of
    /// <see cref="PrimeKernels"/> free of stack traffic, where forming the factor first sends it through the stack on
    /// the critical path.
    /// </para>
    /// <para>
    /// The result is not unique: a chain that reads a value back as a residue must fold it first, and the values two
    /// chains carry differ even when the residues they stand for agree. The folded result equals
    /// <see cref="Reduce(ulong, ulong, ulong, ulong)"/>'s, so a decision read from either form is the same decision.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReduceLazy(ulong left, ulong right, ulong modulus, ulong inverse) {
        var lifted = (MultiplyHigh(
            left: left,
            right: right
        ) + modulus);
        var factor = unchecked(((left * right) * inverse));

        return (lifted - MultiplyHigh(
            left: factor,
            right: modulus
        ));
    }
    /// <summary>Multiplies two Montgomery-form residues of a modulus below <c>2^32</c> and reduces the product lazily, by REDC with the full radix <c>2^64</c>.</summary>
    /// <param name="left">The first factor, in <c>[1, modulus]</c>.</param>
    /// <param name="right">The second factor, in <c>[1, modulus]</c>.</param>
    /// <param name="modulus">The odd modulus, below <c>2^32</c>.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>.</param>
    /// <returns>The product <c>left * right / 2^64</c> modulo <paramref name="modulus"/>, in <c>[1, modulus]</c>, where <paramref name="modulus"/> itself stands for zero.</returns>
    /// <remarks>
    /// Both factors are at most the modulus, so their product fits the low word and the high word is zero. The factor
    /// <c>m = product * inverse</c> then makes <c>m * modulus</c> agree with the product in its low word, so the product
    /// is <c>-floor(m * modulus / 2^64)</c> times the radix modulo the modulus, and that high half is below the
    /// modulus: <c>modulus - high</c> is the residue in <c>[1, modulus]</c>, with no correction step. Every residue
    /// class has exactly one representative in that range, so equality tests against representatives in it are exact.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReduceNarrow(ulong left, ulong right, ulong modulus, ulong inverse) =>
        (modulus - MultiplyHigh(
            left: unchecked(((left * right) * inverse)),
            right: modulus
        ));
    /// <summary>Raises a ring element to a power.</summary>
    /// <param name="exponent">The exponent; zero yields <see cref="One"/> for every <paramref name="value"/>.</param>
    /// <param name="value">The reduced Montgomery-form base.</param>
    /// <returns>The reduced Montgomery-form power.</returns>
    /// <remarks>
    /// Square-and-multiply over the exponent's binary expansion, least significant bit first, so the operation count
    /// depends on the exponent and the routine is not constant-time in it. An odd exponent starts the accumulator at
    /// the base rather than at <see cref="One"/>, which spends no product on the identity. The most-significant-bit-first walk that would make the per-bit multiply unconditional is slower here: its
    /// select lands inside the squaring dependency chain, which is the critical path, while the per-bit branch here is
    /// read off the exponent and resolves ahead of the chain. The squared power is the left factor of both products,
    /// the operand <c>MULX</c> reads implicitly, and the base is not the first argument, which arrives in that register
    /// and would otherwise give the squaring chain a stack home.
    /// </remarks>
    public ulong Power(ulong exponent, ulong value) {
        var modulus = Modulus;
        var inverse = ModulusInverse;
        var power = value;
        var result = ((0UL != (exponent & 1UL)) ? value : One);

        while (0UL != (exponent >>>= 1)) {
            power = Reduce(
                inverse: inverse,
                left: power,
                modulus: modulus,
                right: power
            );

            if (0UL != (exponent & 1UL)) {
                result = Reduce(
                    inverse: inverse,
                    left: power,
                    modulus: modulus,
                    right: result
                );
            }
        }

        return result;
    }
    /// <summary>Subtracts one ring element from another.</summary>
    /// <param name="left">The reduced minuend, in Montgomery form.</param>
    /// <param name="right">The reduced subtrahend, in Montgomery form.</param>
    /// <returns>The reduced difference.</returns>
    /// <remarks>See <see cref="Subtract(ulong, ulong, ulong)"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Subtract(ulong left, ulong right) =>
        Subtract(
            left: left,
            modulus: Modulus,
            right: right
        );
    /// <summary>Subtracts one residue from another modulo an odd modulus.</summary>
    /// <param name="left">The reduced minuend.</param>
    /// <param name="right">The reduced subtrahend.</param>
    /// <param name="modulus">The odd modulus.</param>
    /// <returns>The reduced difference.</returns>
    /// <remarks>
    /// The counterpart to <see cref="Add(ulong, ulong, ulong)"/>, and linear for the same reason. A borrowed difference
    /// is already exact once the modulus is added back, because the radix vanishes modulo the carrier. The fold is a
    /// mask, for the reason <see cref="Add(ulong, ulong, ulong)"/> gives.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Subtract(ulong left, ulong right, ulong modulus) {
        var difference = (left - right);
        var borrow = unchecked((0UL - (left < right).As<ulong>()));

        return (difference + (modulus & borrow));
    }
}
