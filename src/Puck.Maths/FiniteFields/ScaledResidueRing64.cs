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
    /// <summary>The exclusive bound on a modulus whose chains may carry residues lazily, below four times the modulus, between REDCs lifted by the modulus, adding two such results with no fold.</summary>
    /// <remarks>
    /// <c>2^60</c>. The subtractive quotient <c>hi(a b) - hi(m n)</c> of <see cref="Reduce(ulong, ulong, ulong, ulong)"/>
    /// lies in <c>(-n, a b / 2^64)</c>, so lifted by the modulus it lies in <c>(0, a b / 2^64 + n)</c>. Two operands
    /// below <c>4n</c> keep that below <c>16 n^2 / 2^64 + n</c>, which is at most <c>2n</c> exactly when <c>16n</c> is
    /// at most <c>2^64</c>, so the sum of two such results is below <c>4n</c> again. The prime kernels' lazy chains
    /// carry their residues that way below this bound, and <see cref="Canonical(ulong, ulong)"/> recovers the residue
    /// wherever one is compared.
    /// </remarks>
    public const ulong LazyModulusCeiling = (1UL << 60);
    /// <summary>The exclusive bound on a modulus whose chains may carry signed representatives within the modulus of zero between products reduced by <see cref="ReduceSigned(long, long, long, ulong, ulong)"/>, <see cref="ReduceSignedSquareSubtract(long, long, ulong, ulong)"/> and <see cref="ReduceSignedSquare(long, int, ulong, ulong)"/>, with no fold after any reduction.</summary>
    /// <remarks>
    /// <c>2^63</c>, the largest bound with <c>2n</c> at most <c>2^64</c>. An operand within <c>n</c> of zero squares and
    /// doubles to below <c>2 n^2</c>, which is then below <c>2^64 n</c>, so the square over the radix lies in
    /// <c>[0, n)</c>; two such operands multiply to within <c>n^2</c> of zero, so the product over the radix lies within
    /// <c>n / 2</c> of zero; and an unsigned correction factor keeps each reduced value within <c>n</c> of zero again.
    /// Every representative is then below <c>2^63</c> in magnitude, so a signed word holds it, and the residue is the
    /// representative plus the modulus under its sign.
    /// </remarks>
    public const ulong SignedModulusCeiling = (1UL << 63);

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
    /// <summary>Reduces a lazily carried residue below four times an odd modulus into <c>[0, modulus)</c>.</summary>
    /// <param name="value">The value, below <c>4 * modulus</c>.</param>
    /// <param name="modulus">The odd modulus, below <c>2^62</c>, so that four times it fits the carrier.</param>
    /// <returns>The residue of <paramref name="value"/> in <c>[0, modulus)</c>.</returns>
    /// <remarks>
    /// Two folds, by twice and once the modulus, each a mask: a value below <c>4n</c> is below <c>2n</c> after the
    /// first and below <c>n</c> after the second. The widest operand the lazy chains of <see cref="PrimeKernels"/>
    /// carry is below <c>4n</c>, which is what fixes the count.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Canonical(ulong value, ulong modulus) =>
        Fold(
            modulus: modulus,
            value: Fold(
                modulus: (modulus << 1),
                value: value
            )
        );
    /// <summary>Moves a reduced residue to its representative of least magnitude, in <c>[-(modulus - 1) / 2, (modulus - 1) / 2]</c>.</summary>
    /// <param name="value">The reduced residue, in <c>[0, modulus)</c>.</param>
    /// <param name="modulus">The odd modulus.</param>
    /// <returns><paramref name="value"/> less the modulus when it is past half the modulus; otherwise <paramref name="value"/>.</returns>
    /// <remarks>A constant a signed chain subtracts inside <see cref="ReduceSigned(long, long, long, ulong, ulong)"/> takes this form, which is what holds the chain's bound. The fold is a mask, as <see cref="Fold(ulong, ulong)"/>'s is.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Center(ulong value, ulong modulus) =>
        unchecked(((long)(value - (modulus & (0UL - (value > (modulus >>> 1)).As<ulong>())))));
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
    /// The prime kernels' chains below <see cref="SignedModulusCeiling"/> carry unreduced representatives instead, a
    /// REDC lifted by the modulus below <see cref="LazyModulusCeiling"/> or a signed one,
    /// <see cref="ReduceSigned(long, long, long, ulong, ulong)"/>, and have no correction at all.
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
    /// <summary>Multiplies two signed representatives within an odd modulus below <c>2^63</c> of zero, reduces the product by REDC and subtracts a centered constant inside the reduction.</summary>
    /// <param name="left">The first factor, in <c>(-modulus, modulus)</c>.</param>
    /// <param name="right">The second factor, in <c>(-modulus, modulus)</c>.</param>
    /// <param name="subtrahend">The constant, in <c>[-(modulus - 1) / 2, (modulus - 1) / 2]</c>, as <see cref="Center(ulong, ulong)"/> returns it.</param>
    /// <param name="modulus">The odd modulus, below <see cref="SignedModulusCeiling"/>.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>, <see cref="ModulusInverse"/>.</param>
    /// <returns>A value congruent to <c>left * right / 2^64 - subtrahend</c> modulo <paramref name="modulus"/>, in <c>(-modulus, modulus)</c>.</returns>
    /// <remarks>
    /// <para>
    /// For the signed product <c>T</c> the factor <c>q = lo(T) n^-1</c> is read unsigned, so <c>q n</c> agrees with
    /// <c>T</c> in its low word and <c>(T - q n) / 2^64</c> is the exact difference of <c>T</c>'s signed high word and
    /// the unsigned high word of <c>q n</c>, which lies in <c>[0, n)</c>. Two operands within <c>n</c> of zero multiply
    /// to below <c>n^2</c> in magnitude, which is below <c>2^63 n</c> under the ceiling, so the signed high word lies in
    /// <c>[-(n + 1) / 2, (n - 1) / 2]</c> and, less the centered constant, in <c>[-n, n)</c>. One fold by the sign
    /// lands that in <c>[0, n)</c>, and the difference with the correction then lies in <c>(-n, n)</c>: the bound the
    /// operands started under, so a chain of these products needs no other fold.
    /// </para>
    /// <para>
    /// The fold runs on the high word, which is ready while the factor's two truncated multiplies and the correction's
    /// high multiply, the critical path, are still running, so the step's critical path is the reduction's own. The
    /// signed high word is the unsigned one less each factor under the other's sign mask, modulo <c>2^64</c>: the word of
    /// a negative factor is the factor plus <c>2^64</c>. No signed widening multiply runs, since
    /// <see cref="Math.BigMul(long, long, out long)"/> and the <see cref="Int128"/> product hand their low word back
    /// through memory; the low word is a separate truncated multiply. The constant and the correction terms are
    /// gathered and negated before the high multiply is added, which keeps the high word out of the register
    /// <c>MULX</c> reads implicitly, so the trace ladder gives it no stack slot.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReduceSigned(long left, long right, long subtrahend, ulong modulus, ulong inverse) {
        var first = unchecked(((ulong)left));
        var second = unchecked(((ulong)right));
        var factor = unchecked(((first * second) * inverse));
        var high = unchecked(((0UL - (((SignMask(value: first) & second) + (SignMask(value: second) & first)) + ((ulong)subtrahend))) + MultiplyHigh(left: first, right: second)));

        high = unchecked((high + (SignMask(value: high) & modulus)));

        return unchecked(((long)(high - MultiplyHigh(left: factor, right: modulus))));
    }
    /// <summary>Squares a signed representative within an odd modulus below <c>2^63</c> of zero, reduces the square by REDC and subtracts a centered constant inside the reduction: <see cref="ReduceSigned(long, long, long, ulong, ulong)"/> of a value with itself.</summary>
    /// <param name="value">The representative, in <c>(-modulus, modulus)</c>.</param>
    /// <param name="subtrahend">The constant, in <c>[-(modulus - 1) / 2, (modulus - 1) / 2]</c>, as <see cref="Center(ulong, ulong)"/> returns it.</param>
    /// <param name="modulus">The odd modulus, below <see cref="SignedModulusCeiling"/>.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>, <see cref="ModulusInverse"/>.</param>
    /// <returns>A value congruent to <c>value^2 / 2^64 - subtrahend</c> modulo <paramref name="modulus"/>, in <c>(-modulus, modulus)</c>.</returns>
    /// <remarks>
    /// The interval proof is <see cref="ReduceSigned(long, long, long, ulong, ulong)"/>'s, with the square's high word
    /// in <c>[0, n / 2)</c>. The factor is formed before the high word, in that order in the source as well: the JIT
    /// emits in source order and the core issues the oldest ready instruction first, so the factor's two truncated
    /// multiplies, the critical path, run ahead of the high multiply off it. Beside the cross product in the trace
    /// ladder the JIT gives the factor a stack slot, and this order still measures faster than the high word first,
    /// which keeps every term in a register.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReduceSignedSquareSubtract(long value, long subtrahend, ulong modulus, ulong inverse) {
        var word = unchecked(((ulong)value));
        var factor = unchecked(((word * word) * inverse));
        var high = unchecked(((0UL - ((SignMask(value: word) & (word << 1)) + ((ulong)subtrahend))) + MultiplyHigh(left: word, right: word)));

        high = unchecked((high + (SignMask(value: high) & modulus)));

        return unchecked(((long)(high - MultiplyHigh(left: modulus, right: factor))));
    }
    /// <summary>Squares a signed representative within an odd modulus below <c>2^63</c> of zero, doubled or not, and reduces the square by REDC with no fold.</summary>
    /// <param name="value">The representative, in <c>(-modulus, modulus)</c>.</param>
    /// <param name="doubling">One to double the square before the reduction, zero to keep it.</param>
    /// <param name="modulus">The odd modulus, below <see cref="SignedModulusCeiling"/>.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>, <see cref="ModulusInverse"/>.</param>
    /// <returns><c>(2^doubling * value^2 - q * modulus) / 2^64</c> for the factor <c>q</c> that makes the division exact, congruent to <c>2^doubling * value^2 / 2^64</c> modulo <paramref name="modulus"/>, in <c>(-modulus, modulus)</c>.</returns>
    /// <remarks>
    /// <para>
    /// The square <c>T = 2^b x^2</c> is never negative and, for <c>|x| &lt; n</c>, below <c>2 n^2</c>, which is below
    /// <c>2^64 n</c> when <c>n</c> is below <c>2^63</c>; so its high word lies in <c>[0, n)</c>. The factor
    /// <c>q = lo(T) n^-1</c> is read unsigned, so the high word of <c>q n</c> lies in <c>[0, n)</c> as well, the two
    /// products agree in their low words, and <c>(T - q n) / 2^64</c> is the exact difference of their high words, in
    /// <c>(-n, n)</c>: the bound the operand started under, so a square-and-double chain from a reduced seed needs no
    /// fold at all. Every representative is below <c>2^63</c> in magnitude, so a signed word holds it.
    /// </para>
    /// <para>
    /// No product is formed from the doubled operand. The low word of <c>T</c> is that of <c>x^2</c> shifted, so the
    /// factor is <c>lo(x^2)</c> times the inverse shifted, a shift that depends on the bit alone and leaves the
    /// chain's critical path two truncated multiplies, one high multiply and one subtraction. The high word of
    /// <c>x^2</c> is the unsigned one less <c>2x</c> for a negative <c>x</c>, since the word of a negative <c>x</c> is
    /// <c>x + 2^64</c>, and the doubling shifts the carry of the low word into it.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReduceSignedSquare(long value, int doubling, ulong modulus, ulong inverse) {
        var word = unchecked(((ulong)value));
        var low = unchecked((word * word));
        var factor = unchecked((low * (inverse << doubling)));
        var high = unchecked((MultiplyHigh(left: word, right: word) - (SignMask(value: word) & (word << 1))));

        return unchecked(((long)(((high << doubling) | ((low >>> 63) & ((ulong)doubling))) - MultiplyHigh(left: factor, right: modulus))));
    }
    /// <summary>Squares a representative within an odd modulus of zero carried as a word and a borrow mask, and reduces the square by REDC with no fold.</summary>
    /// <param name="value">The word: the representative itself when <paramref name="borrow"/> is zero, the representative plus <c>2^64</c> when it is all ones.</param>
    /// <param name="borrow">The borrow mask, zero or all ones.</param>
    /// <param name="modulus">The odd modulus.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>, <see cref="ModulusInverse"/>.</param>
    /// <returns>The reduced square <c>(x^2 - q * modulus) / 2^64</c>, congruent to <c>x^2 / 2^64</c> modulo <paramref name="modulus"/> and in <c>(-modulus, modulus)</c>, carried the same way: its word and its borrow mask.</returns>
    /// <remarks>
    /// <para>
    /// The 65-bit form of <see cref="ReduceSignedSquare(long, int, ulong, ulong)"/> for a modulus at any size: the
    /// representative <c>x = w - 2^64 b</c> of a word <c>w</c> and a borrow bit <c>b</c> lies in <c>(-n, n)</c>, which no
    /// single word holds once <c>n</c> passes <c>2^63</c>. The square is below <c>n^2</c>, so its high word lies in
    /// <c>[0, n)</c>; <c>w^2</c> exceeds it by <c>2^65 x + 2^128</c> when <c>b</c> is set, so the square's low word is
    /// that of <c>w^2</c> and its high word that of <c>w^2</c> less <c>2w</c>, modulo <c>2^64</c>. With the factor read
    /// unsigned the high word of <c>q n</c> lies in <c>[0, n)</c> too, so the reduced square, the difference of the two
    /// high words, lies in <c>(-n, n)</c> and borrows exactly when the first is below the second.
    /// </para>
    /// <para>
    /// The borrow is the only fold, and nothing waits on it: the next square reads it only to correct its high word,
    /// off the chain's critical path, which is two truncated multiplies, one high multiply and one subtraction. The
    /// residue is the word plus the modulus under the borrow mask, in <c>[0, n)</c>.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (ulong Value, ulong Borrow) ReduceBorrowedSquare(ulong value, ulong borrow, ulong modulus, ulong inverse) {
        var factor = unchecked(((value * value) * inverse));
        var high = unchecked((MultiplyHigh(left: value, right: value) - (borrow & (value << 1))));
        var correction = MultiplyHigh(left: factor, right: modulus);

        return (unchecked((high - correction)), unchecked((0UL - (high < correction).As<ulong>())));
    }

    /// <summary>Returns all ones for a word whose sign bit is set, and zero otherwise.</summary>
    /// <param name="value">The word, read as a two's-complement integer.</param>
    /// <returns><c>2^64 - 1</c> when <paramref name="value"/> is negative read signed; otherwise zero.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SignMask(ulong value) => unchecked(((ulong)(((long)value) >> 63)));

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
    /// <summary>Subtracts one residue from another modulo an odd modulus, with the fold a conditional move that stays one.</summary>
    /// <param name="left">The reduced minuend.</param>
    /// <param name="right">The reduced subtrahend.</param>
    /// <param name="modulus">The odd modulus.</param>
    /// <returns>The reduced difference, equal to <see cref="Subtract(ulong, ulong, ulong)"/>.</returns>
    /// <remarks>
    /// RyuJIT if-converts a select only outside loops, so inlined into a loop body the ternary becomes a branch and
    /// <see cref="Subtract(ulong, ulong, ulong)"/> is written as a mask (<c>setb</c>, <c>movzx</c>, <c>neg</c>,
    /// <c>and</c>, <c>add</c>), which costs the chain about four cycles over a conditional move. A call is the one
    /// boundary the JIT does not cross, so this leaf, never inlined, compiles to <c>cmp</c>, <c>cmovb</c>, <c>ret</c>:
    /// the call keeps the select a conditional move on a loop's critical path. It earns its call only on the last
    /// subtraction of a chain, whose result nothing else waits behind; on both subtractions of a reduction the
    /// measured gain turned layout-sensitive. <see cref="Subtract(ulong, ulong, ulong)"/> stays the inlined mask for
    /// everything else.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ulong SubtractBySelect(ulong left, ulong right, ulong modulus) {
        var difference = (left - right);

        return ((left < right) ? (difference + modulus) : difference);
    }
}
