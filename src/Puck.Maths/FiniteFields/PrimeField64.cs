using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Puck.Maths;

/// <summary>
/// The prime field <c>F_p</c> for an odd prime <c>p</c> below <c>2^62</c>. Elements are bare <see cref="ulong"/> values
/// in the range <c>[0, p)</c>; the field object names the structure they live in and carries no element of its own.
/// </summary>
/// <remarks>
/// <para>
/// The modulus bound keeps addition and subtraction a single conditional fold — two representatives sum below
/// <c>2^63</c> and never overflow — while a lone multiplication widens to <see cref="UInt128"/> and reduces once. Every
/// operation expects reduced operands in <c>[0, p)</c>; the preconditions are not enforced on the hot path. Two fields
/// are equal when their moduli agree.
/// </para>
/// <para>
/// Wherever an operation is a CHAIN of multiplications rather than one product — <see cref="Pow(ulong, ulong)"/> and
/// everything built on it, <see cref="TrySqrt(ulong, out ulong)"/>'s descent, and <see cref="IsPrime(ulong)"/>'s witness
/// rounds — the chain runs in <see cref="ScaledResidueRing64"/> instead, converting in once and out once and spending no
/// hardware division in between. The single-product surface stays on the divide, because there the two conversions cost
/// more than they save. The results are identical either way; only the arithmetic differs.
/// </para>
/// <para>
/// A DEFAULT-INITIALIZED value names no field: its modulus is zero, which is neither odd nor prime, and the reduced
/// operand range <c>[0, p)</c> the whole type is stated over is empty there. Every member that performs or asserts
/// field arithmetic — the identities included — therefore throws <see cref="InvalidOperationException"/> rather than
/// answering, so an uninitialized descriptor is diagnosed as itself instead of returning unreduced integer arithmetic
/// from <see cref="Add(ulong, ulong)"/> and an incidental divide by zero from <see cref="Multiply(ulong, ulong)"/>.
/// <see cref="Modulus"/> is the exception and reports the uninitialized state as it stands, so a default value remains
/// printable, comparable and inspectable in a debugger. <see cref="BinaryField{T}"/> and
/// <see cref="QuadraticExtensionField64"/> carry the same policy.
/// </para>
/// <para>
/// This is the odd-characteristic companion to <see cref="BinaryField{T}"/>, and the substrate for the engine's
/// odd-base deterministic permutations and scrambles, odd-radix low-discrepancy sampling nets, procedural incidence
/// structures over a prime alphabet, and exact modular square roots.
/// </para>
/// </remarks>
public readonly record struct PrimeField64 : IBatchInvertible<ulong> {
    /// <summary>The exclusive upper bound on the modulus; a prime must sit strictly below it so two representatives sum without overflowing the carrier.</summary>
    public const ulong MaximumModulus = (1UL << 62);

    /// <summary>Creates a field from its already-validated modulus.</summary>
    /// <param name="modulus">The odd prime modulus.</param>
    private PrimeField64(ulong modulus) {
        Modulus = modulus;
    }

    /// <summary>Prints the descriptor's one datum, the modulus.</summary>
    /// <param name="builder">The builder the record's <c>ToString</c> assembles into.</param>
    /// <returns><see langword="true"/>, because a member was written.</returns>
    /// <remarks>Hand-written because the compiler-synthesized body walks every public readable instance property — the guarded identities <see cref="One"/> and <see cref="Zero"/> included — which would make <c>ToString</c> throw on the default value this type promises stays printable, and would render two constants as if they were carried state.</remarks>
    private bool PrintMembers(StringBuilder builder) {
        builder.Append(value: "Modulus = ");
        builder.Append(value: Modulus);

        return true;
    }
    /// <summary>Refuses a default-initialized descriptor, which names no field at all.</summary>
    /// <remarks>The throw itself sits behind a non-inlined helper so the guard an operation carries is one never-taken compare and branch, and the aggressively inlined bodies of <see cref="Add(ulong, ulong)"/>, <see cref="Subtract(ulong, ulong)"/>, <see cref="Negate(ulong)"/>, <see cref="Multiply(ulong, ulong)"/> and <see cref="Reduce(ulong)"/> do not grow a throw path.</remarks>
    /// <exception cref="InvalidOperationException">The field is default-initialized.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfUninitialized() {
        // Every constructed field has an odd prime modulus, so zero is exactly the uninitialized state; the private
        // constructor is reachable no other way.
        if (0UL == Modulus) { ThrowUninitialized(); }
    }
    /// <summary>Throws the uninitialized-descriptor diagnosis.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUninitialized() =>
        throw new InvalidOperationException(message: "The prime field is default-initialized; construct it with PrimeField64.Create before using it.");

    /// <summary>Adds two field elements.</summary>
    /// <param name="left">The first reduced addend.</param>
    /// <param name="right">The second reduced addend.</param>
    /// <returns>The reduced sum.</returns>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Add(ulong left, ulong right) {
        ThrowIfUninitialized();

        var sum = (left + right);

        return ((sum >= Modulus)
            ? (sum - Modulus)
            : sum
        );
    }
    /// <summary>Inverts every element of a region in place through a single field inversion.</summary>
    /// <param name="values">The reduced, non-zero elements to invert; each is overwritten with its inverse.</param>
    /// <remarks>
    /// The running-product method: a forward pass accumulates the partial products <c>a_0, a_0 a_1, ...</c>, one
    /// inversion turns the whole product over, and a backward pass peels each element off that inverse. The cost is one
    /// inversion plus about three multiplications per element, replacing the <c>n</c> inversions the naive loop would
    /// perform. The partial-product scratch is stack-allocated for small batches and pooled for large ones, so nothing
    /// is allocated on the managed heap; a pooled scratch is cleared of the caller-derived partial products before it
    /// returns to the shared pool.
    /// </remarks>
    /// <exception cref="DivideByZeroException">Any element is zero; the shared product is then zero and has no inverse.</exception>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field. The descriptor is read before the span is, so an EMPTY batch is refused too.</exception>
    public void BatchInverse(Span<ulong> values) {
        ThrowIfUninitialized();

        // The kernel's ~3n products run in Montgomery form: one encode and one decode per element replace a hardware
        // division per product, and the one inversion the kernel makes is the ring's own ladder.
        var ring = new MontgomeryBatchRing(ring: new(modulus: Modulus));

        // Refused before anything is written, so a region holding a zero is handed back untouched.
        if (values.Contains(value: 0UL)) { throw new DivideByZeroException(message: "Zero has no multiplicative inverse."); }

        for (var i = 0; (i < values.Length); ++i) {
            values[i] = ring.Encode(value: values[i]);
        }

        BatchInverseKernel.Invert(
            ring: ring,
            values: values
        );

        for (var i = 0; (i < values.Length); ++i) {
            values[i] = ring.Decode(value: values[i]);
        }
    }

    private readonly struct MontgomeryBatchRing(ScaledResidueRing64 ring) : IBatchInvertible<ulong> {
        private readonly ScaledResidueRing64 m_ring = ring;

        public ulong One => m_ring.One;

        public ulong Decode(ulong value) => m_ring.Decode(value: value);
        public ulong Encode(ulong value) => m_ring.Encode(value: value);
        public ulong Inverse(ulong value) {
            if (0UL == value) { throw new DivideByZeroException(message: "Zero has no multiplicative inverse."); }

            return m_ring.Power(
                exponent: (m_ring.Modulus - 2UL),
                value: value
            );
        }
        public ulong Multiply(ulong left, ulong right) => m_ring.Multiply(
            left: left,
            right: right
        );
    }

    /// <summary>Creates the prime field <c>F_<paramref name="modulus"/></c>.</summary>
    /// <param name="modulus">The field's modulus, which must be an odd prime below <see cref="MaximumModulus"/>.</param>
    /// <returns>The described field.</returns>
    /// <remarks>
    /// Primality is decided exactly by strong-pseudoprime rounds to a fixed set of witness bases. The twelve bases
    /// <c>2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37</c> are a proven complete witness set for every value strictly
    /// below <see cref="PrimeKernels.LeastWitnessFailure"/> — exactly <c>318665857834031151167461</c>, about
    /// <c>3.18 * 10^23</c> — which is past <see cref="ulong"/> and far past this field's <c>2^62</c> ceiling, so the
    /// decision is deterministic rather than probabilistic. Nothing else is precomputed, so constructing a field costs
    /// only the primality test.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="modulus"/> is at or above <see cref="MaximumModulus"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="modulus"/> is even or composite, so the quotient ring is not a field.</exception>
    public static PrimeField64 Create(ulong modulus) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            value: modulus,
            other: MaximumModulus
        );

        // The mask yields zero or one, so the even case is the zero one. Comparing it against two never fired, which let
        // Create(2) through: an even modulus reaches arithmetic that assumes an odd one, where the non-residue walk in
        // TrySqrt does not terminate.
        if (0UL == (modulus & 1UL)) {
            throw new ArgumentException(
                message: "The modulus must be an odd prime; two is served by BinaryField.",
                paramName: nameof(modulus)
            );
        }
        if (!IsPrime(value: modulus)) {
            throw new ArgumentException(
                message: "The modulus must be prime; a composite modulus does not yield a field.",
                paramName: nameof(modulus)
            );
        }

        return new PrimeField64(modulus: modulus);
    }
    /// <summary>Computes the multiplicative inverse of a non-zero field element.</summary>
    /// <param name="value">The reduced, non-zero element to invert.</param>
    /// <returns>The unique element whose product with <paramref name="value"/> is <see cref="One"/>.</returns>
    /// <remarks>The inverse is <c>value^(p - 2)</c>, evaluated by square-and-multiply. The operand must already be reduced; the precondition is not enforced.</remarks>
    /// <exception cref="DivideByZeroException"><paramref name="value"/> is zero.</exception>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field.</exception>
    public ulong Inverse(ulong value) {
        ThrowIfUninitialized();

        if (0UL == value) { throw new DivideByZeroException(message: "Zero has no multiplicative inverse."); }

        return Pow(
            value: value,
            exponent: (Modulus - 2UL)
        );
    }
    /// <summary>Returns a value indicating whether <paramref name="value"/> passes the Baillie–Pomerance–Selfridge–Wagstaff probable-prime test.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> passes both rounds; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// <para>
    /// One base-two round of <see cref="IsStrongProbablePrime(ulong, ulong)"/> composed with
    /// <see cref="IsStrongLucasProbablePrime(ulong)"/>. Both halves are PROBABLE-prime tests, and so is the composition:
    /// passing is not a proof of primality at any size. What the composition buys is that the two halves fail on
    /// unrelated composites — one reads the order of a residue in the multiplicative group, the other a recurrence in
    /// the quadratic extension the value's own Jacobi symbol selects, and the parameter search deliberately picks the
    /// extension in which the value would be inert if it were prime — so a composite would have to be exceptional in two
    /// unrelated ways at once. No such composite is known at any size. The cheaper half runs first: it costs one
    /// exponentiation and rejects all but a vanishing fraction of composites, so the ladder is reached rarely.
    /// </para>
    /// <para>
    /// Below <c>2^64</c> the test is not merely unrefuted but verified counterexample-free, and that region is exactly
    /// <see cref="ulong"/>, so nothing here is extrapolated: the complete set of base-two Fermat pseudoprimes below
    /// <c>2^64</c> was enumerated exhaustively and independently by Feitsma and by Galway — the strong ones are a
    /// derived subset — and no member of that subset is simultaneously a strong Lucas pseudoprime to these parameters. That guarantee rests on a third-party exhaustive
    /// computation — the same epistemic class as the <see cref="PrimeKernels.LeastWitnessFailure"/> bound
    /// (exactly <c>318665857834031151167461</c>) <see cref="IsPrime(ulong)"/>'s twelve-base witness set rests on,
    /// which is Sorenson and Webster's computed value of the twelfth strong-pseudoprime threshold, quoted exactly
    /// rather than rounded.
    /// </para>
    /// <para>
    /// <see cref="IsPrime(ulong)"/> remains the exact decision and the oracle this composition is measured against.
    /// Whether to re-point it here is a separate decision and has not been taken.
    /// </para>
    /// </remarks>
    public static bool IsBaillieProbablePrime(ulong value) =>
        (IsStrongProbablePrime(
            value: value,
            witness: 2UL
        ) && IsStrongLucasProbablePrime(value: value));
    /// <summary>Returns a value indicating whether <paramref name="value"/> is prime, deciding the question exactly for every <see cref="ulong"/>.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is prime; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Strong-pseudoprime rounds to the twelve-base complete witness set, valid past <see cref="ulong.MaxValue"/>. The
    /// even candidates are settled before the rounds begin, so the survivors are odd and one <see cref="ScaledResidueRing64"/>
    /// carries every round's squaring chain: the chains spend no hardware division, and each witness pays exactly one
    /// remainder at entry to reduce it below the modulus. The ring is a
    /// bijective re-encoding of the residues, so comparing a power against the ring's own one and minus one decides
    /// exactly what comparing the ordinary residues against <c>1</c> and <c>value - 1</c> would.
    /// </remarks>
    public static bool IsPrime(ulong value) {
        if (2UL > value) { return false; }
        if (2UL == value) { return true; }
        if (0UL == (value & 1UL)) { return false; }

        // A witness set proven complete for every value strictly below PrimeKernels.LeastWitnessFailure =
        // 318665857834031151167461 (about 3.18 * 10^23), which exceeds ulong.MaxValue. The table is PrimeKernels',
        // shared with the arbitrary-width decision so the two cannot drift.
        var witnesses = PrimeKernels.WitnessBases;
        var oddPart = (value - 1UL);
        var twoExponent = BitOperations.TrailingZeroCount(value: oddPart);

        oddPart >>>= twoExponent;

        var ring = new ScaledResidueRing64(modulus: value);
        var one = ring.One;
        var negativeOne = ring.NegativeOne;

        foreach (var witnessBase in witnesses) {
            var residue = (((ulong)witnessBase) % value);

            if (0UL == residue) { continue; }

            var power = ring.Power(
                value: ring.Encode(value: residue),
                exponent: oddPart
            );

            if (
                (one == power) ||
                (negativeOne == power)
            ) { continue; }

            var composite = true;

            for (var round = 1; (round < twoExponent); ++round) {
                power = ring.Multiply(
                    left: power,
                    right: power
                );

                if (negativeOne == power) {
                    composite = false;

                    break;
                }
            }

            if (composite) { return false; }
        }

        return true;
    }
    /// <summary>Returns a value indicating whether <paramref name="value"/> passes the strong Lucas probable-prime test with Selfridge's Method A parameters.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a strong Lucas probable prime; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// <para>
    /// A PROBABLE-prime test: every prime passes, and so do infinitely many composites — the strong Lucas pseudoprimes,
    /// of which <c>5459</c> is the smallest. Its worth is not its own strength but the independence of its failures from
    /// a Fermat round's, which is what <see cref="IsBaillieProbablePrime(ulong)"/> composes it for.
    /// </para>
    /// <para>
    /// Method A takes the discriminant <c>D</c> to be the first of <c>5, -7, 9, -11, 13, ...</c> whose Jacobi symbol
    /// over the value is <c>-1</c>, then <c>P = 1</c> and <c>Q = (1 - D) / 4</c>. Every candidate is congruent to one
    /// modulo four, which is what makes <c>Q</c> an integer and what fixes its sign — <c>Q</c> is positive exactly when
    /// <c>D</c> is negative — so the sign is read off the candidate's magnitude rather than tracked. The candidates step
    /// by four within each sign, so they sweep every residue class modulo an odd value, and every non-square has a class
    /// whose symbol is <c>-1</c>: the search reaches one. A perfect square reaches none — its symbol is never <c>-1</c>
    /// for any argument at all — but the search still ends there, on the vanishing symbol, because the candidates sweep
    /// every odd magnitude and so meet a factor the square shares. The integer square root ahead of the search is
    /// therefore a cost bound rather than a termination guarantee: without it a square costs Jacobi evaluations
    /// proportional to its least prime factor, which for the square of a large prime is the whole search. It is exact,
    /// and a square above one is composite anyway. The vanishing symbol ends the search in general: the candidate and
    /// the value share a factor, which is a proper divisor of the value unless the value divides the candidate outright.
    /// </para>
    /// <para>
    /// With <c>value + 1 = d * 2^s</c> and <c>d</c> odd, the test accepts when <c>U_d</c> vanishes modulo the value, or
    /// when <c>V_(d * 2^r)</c> does for some <c>r</c> below <c>s</c>. The terms come from the doubling ladder —
    /// <c>U_2k = U_k * V_k</c> and <c>V_2k = V_k^2 - 2 * Q^k</c>, followed where the exponent's bit is set by the
    /// index-incrementing pair <c>U_(2k+1) = (U_2k + V_2k) / 2</c> and <c>V_(2k+1) = (D * U_2k + V_2k) / 2</c> — walked
    /// most-significant-bit first over <c>d</c> with <c>Q^k</c> squared alongside it. That is logarithmic in the value,
    /// where the recurrence's own definition would be linear. Both increment formulas halve, which modulo an odd value
    /// is a multiplication by <c>(value + 1) / 2</c>.
    /// </para>
    /// <para>
    /// The whole ladder runs in one <see cref="ScaledResidueRing64"/>. Its additions, subtractions, and halvings are linear
    /// in the representation and so apply to Montgomery-form elements unchanged, its products are the ring's own, and
    /// zero represents zero — so the acceptance tests read exactly as they would on ordinary residues, and the ladder
    /// spends no hardware division from the first term to the last.
    /// </para>
    /// </remarks>
    public static bool IsStrongLucasProbablePrime(ulong value) {
        if (2UL > value) { return false; }
        if (2UL == value) { return true; }
        if (0UL == (value & 1UL)) { return false; }

        // A square has no discriminant of symbol -1, so its search would run to the vanishing symbol at its least prime
        // factor — the whole search, for the square of a large prime. Settling the square first bounds that cost; it is
        // not what makes the search terminate.
        var root = value.SquareRoot();

        if (value == (root * root)) { return false; }

        var discriminantMagnitude = 5UL;
        ulong discriminantResidue;

        while (true) {
            var magnitudeResidue = (discriminantMagnitude % value);

            // The candidates alternate sign from D = 5 onwards, so the sign is a function of the magnitude alone: it is
            // negative exactly at magnitudes congruent to three modulo four, which is also what leaves every candidate
            // congruent to one modulo four.
            discriminantResidue = (((3UL == (discriminantMagnitude & 3UL)) && (0UL != magnitudeResidue))
                ? (value - magnitudeResidue)
                : magnitudeResidue
            );

            var symbol = discriminantResidue.JacobiSymbol(modulus: value);

            if (-1 == symbol) { break; }
            // A vanishing symbol means the value shares a factor with the candidate. That factor is a proper divisor —
            // so the value is composite — unless the value divides the candidate, which leaves the search uninformed.
            if (
                (0 == symbol) &&
                (0UL != magnitudeResidue)
            ) { return false; }

            discriminantMagnitude += 2UL;
        }

        var isNegativeDiscriminant = (3UL == (discriminantMagnitude & 3UL));
        var ring = new ScaledResidueRing64(modulus: value);
        // Q = (1 - D) / 4: the division is exact in either sign class, and Encode folds an argument that is not yet
        // reduced, so the magnitude goes in as it stands and the negation happens in the ring.
        var q = ring.Encode(value: ((isNegativeDiscriminant
            ? (discriminantMagnitude + 1UL)
            : (discriminantMagnitude - 1UL)) >>> 2));

        if (!isNegativeDiscriminant) {
            q = ring.Subtract(
                left: 0UL,
                right: q
            );
        }

        // The carrier cannot hold value + 1 at its own maximum. The narrow split is in fact indistinguishable there —
        // the wrapped order is zero, whose trailing-zero count is the full width and whose odd part is zero, so the
        // ladder is skipped and the exponent lands on 64 either way — but the widened split states the decomposition
        // instead of resting on that coincidence.
        var order = (((UInt128)value) + UInt128.One);
        var twoExponent = ((int)UInt128.TrailingZeroCount(value: order));
        var oddPart = ((ulong)(order >>> twoExponent));
        var discriminant = ring.Encode(value: discriminantResidue);
        var one = ring.One;
        var qPower = q;
        var u = one; // U_1
        var v = one; // V_1 = P

        for (var bit = (BitOperations.Log2(value: oddPart) - 1); (bit >= 0); --bit) {
            var doubledU = ring.Multiply(
                left: u,
                right: v
            );

            v = ring.Subtract(
                left: ring.Multiply(
                    left: v,
                    right: v
                ),
                right: ring.Add(
                    left: qPower,
                    right: qPower
                )
            );
            u = doubledU;
            qPower = ring.Multiply(
                left: qPower,
                right: qPower
            );

            if (0UL != ((oddPart >>> bit) & 1UL)) {
                var incrementedU = ring.Halve(value: ring.Add(
                    left: u,
                    right: v
                ));

                v = ring.Halve(value: ring.Add(
                    left: ring.Multiply(
                        left: discriminant,
                        right: u
                    ),
                    right: v
                ));
                u = incrementedU;
                qPower = ring.Multiply(
                    left: qPower,
                    right: q
                );
            }
        }

        if (
            (0UL == u) ||
            (0UL == v)
        ) { return true; }

        // The remaining acceptances are the V terms at the doubled indices d * 2^r; each doubling consumes the current
        // Q^(d * 2^r) before squaring it for the next.
        for (var round = 1; (round < twoExponent); ++round) {
            v = ring.Subtract(
                left: ring.Multiply(
                    left: v,
                    right: v
                ),
                right: ring.Add(
                    left: qPower,
                    right: qPower
                )
            );

            if (0UL == v) { return true; }

            qPower = ring.Multiply(
                left: qPower,
                right: qPower
            );
        }

        return false;
    }
    /// <summary>Returns a value indicating whether <paramref name="value"/> passes one strong-probable-prime round to <paramref name="witness"/>.</summary>
    /// <param name="value">The value to test.</param>
    /// <param name="witness">The witness base, reduced modulo <paramref name="value"/> before the round; a base that reduces to zero carries no evidence and passes.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a strong probable prime to <paramref name="witness"/>; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// A PROBABLE-prime test: a failed round proves compositeness, a passed one proves nothing. Writing
    /// <c>value - 1 = d * 2^s</c> with <c>d</c> odd, the round accepts when <c>witness^d</c> is one or when
    /// <c>witness^(d * 2^r)</c> is minus one for some <c>r</c> below <c>s</c> — the two ways a prime modulus allows the
    /// square roots of one. This is the round <see cref="IsPrime(ulong)"/> repeats over its twelve-base witness set and
    /// the first half of <see cref="IsBaillieProbablePrime(ulong)"/>, exposed so that either composition's halves can be
    /// addressed on their own. The squaring chain runs in one <see cref="ScaledResidueRing64"/>.
    /// </remarks>
    public static bool IsStrongProbablePrime(ulong value, ulong witness) {
        if (2UL > value) { return false; }
        if (2UL == value) { return true; }
        if (0UL == (value & 1UL)) { return false; }

        var residue = (witness % value);

        if (0UL == residue) { return true; }

        var oddPart = (value - 1UL);
        var twoExponent = BitOperations.TrailingZeroCount(value: oddPart);

        oddPart >>>= twoExponent;

        var ring = new ScaledResidueRing64(modulus: value);
        var negativeOne = ring.NegativeOne;
        var power = ring.Power(
            value: ring.Encode(value: residue),
            exponent: oddPart
        );

        if (
            (ring.One == power) ||
            (negativeOne == power)
        ) { return true; }

        for (var round = 1; (round < twoExponent); ++round) {
            power = ring.Multiply(
                left: power,
                right: power
            );

            if (negativeOne == power) { return true; }
        }

        return false;
    }
    /// <summary>Computes the quadratic character of a field element by the exponentiation criterion.</summary>
    /// <param name="value">The reduced element to test.</param>
    /// <returns><c>0</c> when <paramref name="value"/> is zero, <c>1</c> when it is a non-zero square, and <c>-1</c> when it is a non-square.</returns>
    /// <remarks>The value <c>value^((p - 1) / 2)</c> is <c>0</c>, <c>1</c>, or <c>p - 1</c>; the last maps to <c>-1</c>.</remarks>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field.</exception>
    public int LegendreCharacter(ulong value) {
        ThrowIfUninitialized();

        // Tested after reduction, so an unreduced multiple of the modulus reads as the zero it is rather than as a
        // non-square through the exponentiation path.
        if (0UL == Reduce(value: value)) { return 0; }

        var power = Pow(
            value: value,
            exponent: ((Modulus - 1UL) >>> 1)
        );

        return ((1UL == power)
            ? 1
            : -1
        );
    }
    /// <summary>Multiplies two field elements.</summary>
    /// <param name="left">The first reduced factor.</param>
    /// <param name="right">The second reduced factor.</param>
    /// <returns>The reduced product.</returns>
    /// <remarks>
    /// The product widens to <see cref="UInt128"/> and is reduced once. Both operands must already be reduced; the
    /// precondition is not enforced. A single product stays on the divide deliberately: <see cref="ScaledResidueRing64"/>
    /// wins only across a chain, and the two conversions a one-shot would pay cost more than the divide they replace.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Multiply(ulong left, ulong right) {
        ThrowIfUninitialized();

        return ((ulong)((((UInt128)left) * right) % Modulus));
    }
    /// <summary>Negates a field element.</summary>
    /// <param name="value">The reduced element to negate.</param>
    /// <returns>The reduced additive inverse.</returns>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Negate(ulong value) {
        ThrowIfUninitialized();

        return ((0UL == value)
            ? 0UL
            : (Modulus - value)
        );
    }
    /// <summary>Raises a field element to a power.</summary>
    /// <param name="value">The reduced element to raise.</param>
    /// <param name="exponent">The exponent; zero yields <see cref="One"/> for every <paramref name="value"/>.</param>
    /// <returns><paramref name="value"/> raised to <paramref name="exponent"/>, reduced.</returns>
    /// <remarks>
    /// Square-and-multiply over the exponent's binary expansion, so the operation count depends on the exponent and the
    /// routine is not constant-time in it. The chain runs in <see cref="ScaledResidueRing64"/> — one conversion in, one
    /// out, and no hardware division in between — which is what makes the whole exponentiation cheaper than the two
    /// divides per exponent bit a direct reduction would spend.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field.</exception>
    public ulong Pow(ulong value, ulong exponent) {
        ThrowIfUninitialized();

        var ring = new ScaledResidueRing64(modulus: Modulus);

        return ring.Decode(value: ring.Power(
            value: ring.Encode(value: value),
            exponent: exponent
        ));
    }
    /// <summary>Reduces an arbitrary unsigned value into the field.</summary>
    /// <param name="value">The value to reduce.</param>
    /// <returns>The representative of <paramref name="value"/> in <c>[0, p)</c>.</returns>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Reduce(ulong value) {
        ThrowIfUninitialized();

        return (value % Modulus);
    }
    /// <summary>Reduces a signed value into the field, folding negatives up by the modulus.</summary>
    /// <param name="value">The value to reduce.</param>
    /// <returns>The representative of <paramref name="value"/> in <c>[0, p)</c>.</returns>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field.</exception>
    public ulong Reduce(long value) {
        ThrowIfUninitialized();

        var folded = (value % ((long)Modulus));

        return ((folded < 0L)
            ? ((ulong)(folded + ((long)Modulus)))
            : ((ulong)folded)
        );
    }
    /// <summary>Subtracts one field element from another.</summary>
    /// <param name="left">The reduced minuend.</param>
    /// <param name="right">The reduced subtrahend.</param>
    /// <returns>The reduced difference.</returns>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Subtract(ulong left, ulong right) {
        ThrowIfUninitialized();

        return ((left >= right)
            ? (left - right)
            : ((left + Modulus) - right)
        );
    }
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
    /// <para>
    /// The whole routine — the character decision, the seeding powers, and the descent's squarings — runs inside one
    /// <see cref="ScaledResidueRing64"/>, so the descent is a single chain with two conversions rather than one hardware
    /// division per multiplication. Every test the descent performs is made against the ring's own one, and the walk
    /// to the smallest non-square against the ring's minus one; the representation's bijectivity makes those the same
    /// decisions as tests against the ordinary <c>1</c> and <c>p - 1</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field.</exception>
    public bool TrySqrt(ulong value, out ulong root) {
        ThrowIfUninitialized();

        if (0UL == value) {
            root = 0UL;

            return true;
        }

        var ring = new ScaledResidueRing64(modulus: Modulus);
        var one = ring.One;
        var subject = ring.Encode(value: value);
        var halfOrder = ((Modulus - 1UL) >>> 1);

        if (one != ring.Power(
            exponent: halfOrder,
            value: subject
        )) {
            root = 0UL;

            return false;
        }

        // p ≡ 3 (mod 4): the root is a single power, with no descent needed.
        if (3UL == (Modulus & 3UL)) {
            root = ring.Decode(value: ring.Power(
                value: subject,
                exponent: ((Modulus + 1UL) >>> 2)
            ));

            return true;
        }

        // p ≡ 1 (mod 4): the nonresidue-assisted descent. Split p - 1 = q * 2^s with q odd.
        var oddPart = (Modulus - 1UL);
        var twoExponent = BitOperations.TrailingZeroCount(value: oddPart);

        oddPart >>>= twoExponent;

        var negativeOne = ring.NegativeOne;
        var nonResidue = 2UL;

        while (negativeOne != ring.Power(
            value: ring.Encode(value: nonResidue),
            exponent: halfOrder
        )) { ++nonResidue; }

        var rootOfUnity = ring.Power(
            value: ring.Encode(value: nonResidue),
            exponent: oddPart
        );
        var candidate = ring.Power(
            exponent: ((oddPart + 1UL) >>> 1),
            value: subject
        );
        var residue = ring.Power(
            exponent: oddPart,
            value: subject
        );
        var order = twoExponent;

        while (one != residue) {
            var squares = residue;
            var lowest = 0;

            while (one != squares) {
                squares = ring.Multiply(
                    left: squares,
                    right: squares
                );
                ++lowest;
            }

            var lift = rootOfUnity;

            for (var step = 0; (step < ((order - lowest) - 1)); ++step) {
                lift = ring.Multiply(
                    left: lift,
                    right: lift
                );
            }

            candidate = ring.Multiply(
                left: candidate,
                right: lift
            );
            rootOfUnity = ring.Multiply(
                left: lift,
                right: lift
            );
            residue = ring.Multiply(
                left: residue,
                right: rootOfUnity
            );
            order = lowest;
        }

        root = ring.Decode(value: candidate);

        return true;
    }

    /// <summary>Gets the field's modulus, so that the field has <c>Modulus</c> elements.</summary>
    public ulong Modulus { get; }
    /// <summary>Gets the multiplicative identity.</summary>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field.</exception>
    public ulong One {
        get {
            ThrowIfUninitialized();

            return 1UL;
        }
    }
    /// <summary>Gets the additive identity.</summary>
    /// <exception cref="InvalidOperationException">The field is default-initialized and names no field.</exception>
    public ulong Zero {
        get {
            ThrowIfUninitialized();

            return 0UL;
        }
    }
}
