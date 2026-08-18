using System.Buffers;
using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // ---- the prime field (PrimeField64) ----
    // EVERYTHING here is EXACT. Nothing rounds, saturates or approximates, so the campaign's substrate condition drops
    // out of every claim below and each leg says so in those words. What is live instead is reduction (which modulus
    // fold actually applies), representation (Montgomery form leaking into an answer), the width and carry edges the
    // 2^62 ceiling imposes, and the refusal contracts.

    // The Default modulus ladder: one rung per structural shape the kernels branch on. Every rung is re-proved prime by
    // Oracles.ExactPrimality inside PrimeFieldCreateAndRefusals, so no constant here is trusted on its word — the same
    // distrust BinaryFieldCatalogModuli shows its own five moduli.
    private static readonly ulong[] PrimeFieldModuli = [
        3UL,                          // the smallest odd prime; p ≡ 3 (mod 4); three elements, so every fold is degenerate
        17UL,                         // p ≡ 1 (mod 4) with p − 1 = 2^4: the descent's ODD PART is one
        65_537UL,                     // the Fermat prime F_4; p − 1 = 2^16, the deepest small descent
        2_147_483_647UL,              // M31; p ≡ 3 (mod 4), so TrySqrt takes the direct-power branch at 31 bits
        3_221_225_473UL,              // 3·2^30 + 1; p ≡ 1 (mod 4) with 2-adic valuation 30 — the deep descent
        4_611_686_018_427_387_847UL,  // 2^62 − 57, just under MaximumModulus: the carrier invariant's own corner
    ];
    // The chain ladder — the five rungs the EXPENSIVE laws (Pow, Inverse/BatchInverse, LegendreCharacter, TrySqrt) run
    // at Default. It drops the two mid-size rungs the cheap arithmetic law already sweeps and keeps both TrySqrt
    // branches, both descent depths and both carrier ends.
    private static readonly ulong[] PrimeFieldChainModuli = [
        3UL, 17UL, 2_147_483_647UL, 3_221_225_473UL, 4_611_686_018_427_387_847UL,
    ];
    // The smoke ladder: the degenerate rung, the deep descent and the carrier corner, and nothing else.
    private static readonly ulong[] PrimeFieldSmokeModuli = [3UL, 3_221_225_473UL, 4_611_686_018_427_387_847UL];
    // The Deep ladder: every rung above plus the shapes Default leaves out — the small primes where the guards
    // outnumber the arithmetic, both Mersenne widths, and the largest prime below 2^32.
    private static readonly ulong[] PrimeFieldDeepModuli = [
        3UL, 5UL, 7UL, 11UL, 13UL, 17UL, 97UL, 257UL, 65_537UL,
        1_000_000_007UL, 2_147_483_647UL, 3_221_225_473UL, 4_294_967_291UL,
        2_305_843_009_213_693_951UL,  // M61
        4_611_686_018_427_387_847UL,  // 2^62 − 57
    ];
    // The published psi_k strong pseudoprimes below 2^64, each with the number of LEADING prime bases it survives. The
    // count is derived from the published table itself, not captured from any implementation: psi_k is the LEAST value
    // that is a strong probable prime to the first k prime bases, so a value strictly below psi_(k+1) survives at most
    // k. psi_7 == psi_8 and psi_9 == psi_10 == psi_11, which is why two rows read 8 and 11 rather than the indices the
    // values are tabulated under. Every row is MEASURED in the law by an independent BigInteger round, so a
    // mistranscribed count fails loudly.
    private static readonly (ulong Value, int Bases)[] PrimeFieldWitnessPseudoprimes = [
        (2_047UL, 1),
        (1_373_653UL, 2),
        (25_326_001UL, 3),
        (3_215_031_751UL, 4),
        (2_152_302_898_747UL, 5),
        (3_474_749_660_383UL, 6),
        (341_550_071_728_321UL, 8),
        (3_825_123_056_546_413_051UL, 11),
    ];
    // The carrier's corners, the witness bases themselves, and the ladder's own moduli as primality candidates.
    private static readonly ulong[] PrimeFieldPrimalityLadder = [
        0UL, 1UL, 2UL, 3UL, 4UL, 5UL, 9UL, 25UL, 561UL,
        (1UL << 31), ((1UL << 31) - 1UL), ((1UL << 31) + 1UL),
        4_294_967_291UL, (1UL << 32), ((1UL << 32) + 1UL),
        (1UL << 61), ((1UL << 61) - 1UL),
        (1UL << 62), ((1UL << 62) - 1UL), 4_611_686_018_427_387_847UL,
        (1UL << 63), ((1UL << 63) + 1UL),
        (ulong.MaxValue - 2UL), (ulong.MaxValue - 1UL), ulong.MaxValue,
    ];
    // The odd composites Create must refuse: the unit, three small squares and products, the smallest Carmichael
    // number, and FOUR OF the psi_k strong pseudoprimes — the ones forcing bases four, six, eight and eleven. Eight
    // distinct psi values fit below the 2^62 ceiling; the other four (2047, 1373653, 25326001, 2152302898747) force
    // bases one, two, three and five and are exercised against IsPrime by PrimeFieldWitnessPseudoprimes above rather
    // than against Create's gate. The psi rows are the discriminating ones — a modulus admitted on a short witness set
    // would be a COMPOSITE modulus in a type whose whole contract is that the quotient ring is a field.
    private static readonly ulong[] PrimeFieldCompositeModuli = [
        1UL, 9UL, 15UL, 25UL, 561UL, 3_215_031_751UL, 3_474_749_660_383UL, 341_550_071_728_321UL, 3_825_123_056_546_413_051UL,
    ];
    // The two published pseudoprime populations below 10^4 (Default) and below 10^5 (Deep), ENUMERATED here rather
    // than looked up. Provenance, which is what makes them classical rather than a pin captured off the subject:
    // OEIS A001262, the strong pseudoprimes to base 2, and OEIS A217255, the strong Lucas pseudoprimes for the
    // Selfridge Method A parameters (Baillie and Wagstaff, 1980) — the parameter choice PrimeField64 runs, and without
    // which the second list names nothing.
    private static readonly ulong[] PrimeFieldBaseTwoPseudoprimes = [2_047UL, 3_277UL, 4_033UL, 4_681UL, 8_321UL];
    private static readonly ulong[] PrimeFieldLucasPseudoprimes = [5_459UL, 5_777UL];
    private static readonly ulong[] PrimeFieldDeepBaseTwoPseudoprimes = [
        2_047UL, 3_277UL, 4_033UL, 4_681UL, 8_321UL, 15_841UL, 29_341UL, 42_799UL,
        49_141UL, 52_633UL, 65_281UL, 74_665UL, 80_581UL, 85_489UL, 88_357UL, 90_751UL,
    ];
    private static readonly ulong[] PrimeFieldDeepLucasPseudoprimes = [
        5_459UL, 5_777UL, 10_877UL, 16_109UL, 18_971UL, 22_499UL, 24_569UL,
        25_199UL, 40_309UL, 58_519UL, 75_077UL, 97_439UL,
    ];
    private static readonly ulong[] PrimeFieldCarmichaelNumbers = [561UL, 1_105UL, 1_729UL, 2_465UL, 2_821UL, 6_601UL, 8_911UL];
    // The carriage ladder: the only values where the composition's two halves DISAGREE, which is the only place its
    // carriage is decidable at all. Rows 1-5 are base-two strong pseudoprimes, where the round accepts alone; rows 6-7
    // are strong Lucas pseudoprimes, where the Lucas half accepts alone; the rest are controls where both agree.
    private static readonly ulong[] PrimeFieldCarriageLadder = [
        2_047UL, 3_277UL, 4_033UL, 4_681UL, 8_321UL,
        5_459UL, 5_777UL,
        3UL, 5UL, 97UL, 8_191UL, 4_294_967_291UL,
        9UL, 15UL, 561UL, 1_729UL, 3_215_031_751UL,
    ];
    // The batch-inverse length ladder, which straddles BatchInverse's 512-element stackalloc/ArrayPool boundary in
    // both directions.
    private static readonly int[] PrimeFieldBatchLengths = [0, 1, 2, 3, 511, 512, 513, 1_024];
    private static readonly int[] PrimeFieldShortBatchLengths = [0, 1, 2, 3, 8];

    private const ulong PrimeFieldBatchModulus = 4_611_686_018_427_387_847UL;
    private const int PrimeFieldSieveBound = 20_000;
    private const int PrimeFieldDeepSieveBound = 1_000_000;
    private const ulong PrimeFieldPopulationBound = 10_000UL;
    private const ulong PrimeFieldDeepPopulationBound = 100_000UL;

    /// <summary>Proves the field's whole admission surface: the declared ceiling, every ladder rung re-proved prime by
    /// the oracle before <c>Create</c> is handed it, the three refusal classes with their exception types and parameter
    /// names, and the uniform refusal a DEFAULT-initialized field answers every operation with.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldCreateAndRefusals() {
        // Read the declared ceiling into a local so the comparison is one the RUN makes rather than one the compiler
        // folds away; a folded comparison would make the counterexample unreachable.
        var ceiling = PrimeField64.MaximumModulus;

        if (ceiling != (1UL << 62)) { return $"MaximumModulus is {ceiling}"; }

        // The fifteen-rung ladder at BOTH tiers: re-proving fifteen constants costs microseconds and every other case
        // in this family leans on their primality.
        foreach (var modulus in PrimeFieldDeepModuli) {
            if (modulus >= ceiling) { return $"the ladder rung {modulus} is at or above MaximumModulus"; }
            if (!Oracles.ExactPrimality(value: modulus)) { return $"the ladder rung {modulus} is not prime by the oracle"; }

            var field = PrimeField64.Create(modulus: modulus);

            if (field.Modulus != modulus) { return $"the field over {modulus} reports modulus {field.Modulus}"; }
            if (field.One != 1UL) { return $"the multiplicative identity over {modulus} is {field.One}"; }
            if (field.Zero != 0UL) { return $"the additive identity over {modulus} is {field.Zero}"; }
            if (field != PrimeField64.Create(modulus: modulus)) { return $"two fields over {modulus} are not equal"; }

            // ToString prints the descriptor's one datum and nothing else: the identities are constants of the TYPE,
            // not carried state, so a hand-written PrintMembers keeps them out of the rendering.
            if (field.ToString() != $"PrimeField64 {{ Modulus = {modulus} }}") { return $"the field over {modulus} prints as {field}"; }
        }

        foreach (var refused in ((ReadOnlySpan<ulong>)[ceiling, (ceiling + 1UL), (ulong.MaxValue - 2UL), ulong.MaxValue])) {
            if (!Throws<ArgumentOutOfRangeException>(
                action: () => _ = PrimeField64.Create(modulus: refused),
                paramName: "modulus"
            )) { return $"Create admitted {refused}, at or above the ceiling"; }
        }

        // TWO is the historical corner and the reason the ladder carries it: the guard read `2 == (modulus & 1)`, a
        // comparison a one-bit mask can never satisfy, so Create(2) was reachable and TrySqrt's non-residue walk never
        // returned there.
        foreach (var even in ((ReadOnlySpan<ulong>)[0UL, 2UL, 4UL, 6UL, 65_536UL, (ceiling - 2UL)])) {
            if (!Throws<ArgumentException>(
                action: () => _ = PrimeField64.Create(modulus: even),
                paramName: "modulus"
            )) { return $"Create admitted the even modulus {even}"; }
        }

        foreach (var composite in PrimeFieldCompositeModuli) {
            if (Oracles.ExactPrimality(value: composite)) { return $"the refusal ladder value {composite} is prime by the oracle"; }
            if (!Throws<ArgumentException>(
                action: () => _ = PrimeField64.Create(modulus: composite),
                paramName: "modulus"
            )) { return $"Create admitted the composite modulus {composite}"; }
        }

        // The record struct's private constructor does not stop default(PrimeField64), and a value that names no field
        // refuses EVERY operation rather than answering some of them. The surface is listed by name so a member that
        // quietly answers is reported as itself.
        var empty = default(PrimeField64);
        var batch = new ulong[3];

        if (empty.Modulus != 0UL) { return $"the default field reports modulus {empty.Modulus}"; }

        // The PRINTABILITY half of the promise, stated on ToString itself: a default value formats as its raw state
        // rather than throwing from a guarded identity the synthesized member walk would have read.
        if (empty.ToString() != "PrimeField64 { Modulus = 0 }") { return $"the default field prints as {empty}"; }

        (string Name, Action Call)[] operations = [
            ("One", () => _ = empty.One),
            ("Zero", () => _ = empty.Zero),
            ("Add", () => _ = empty.Add(
                left: 1UL,
                right: 1UL
            )),
            ("Subtract", () => _ = empty.Subtract(
                left: 0UL,
                right: 1UL
            )),
            ("Negate", () => _ = empty.Negate(value: 1UL)),
            ("Multiply", () => _ = empty.Multiply(
                left: 1UL,
                right: 1UL
            )),
            ("Inverse", () => _ = empty.Inverse(value: 1UL)),
            ("LegendreCharacter", () => _ = empty.LegendreCharacter(value: 1UL)),
            ("Pow", () => _ = empty.Pow(
                exponent: 1UL,
                value: 1UL
            )),
            ("Reduce(ulong)", () => _ = empty.Reduce(value: 5UL)),
            ("Reduce(long)", () => _ = empty.Reduce(value: 5L)),
            ("TrySqrt", () => { _ = empty.TrySqrt(
                root: out _,
                value: 0UL
            ); }),
            ("BatchInverse of an empty span", () => empty.BatchInverse(values: Span<ulong>.Empty)),
            ("BatchInverse of three", () => empty.BatchInverse(values: batch.AsSpan())),
        ];

        foreach (var (name, call) in operations) {
            if (!Throws<InvalidOperationException>(action: call)) { return $"the default field answered {name} instead of refusing"; }
        }

        // The EMPTY batch is the row with teeth: the descriptor is read before the span is, so the early return for a
        // zero-length batch cannot let an uninitialized field through unremarked.
        var fields = new PrimeField64[2];

        if (fields[0] != empty) { return "an unassigned field array element does not equal the default"; }
        if (!Throws<InvalidOperationException>(action: () => _ = fields[1].Multiply(
            left: 1UL,
            right: 1UL
        ))) { return "an unassigned field array element answered Multiply"; }

        return null;
    }
    /// <summary>Proves the four one-shot arithmetic members and both <c>Reduce</c> overloads against exact
    /// <see cref="BigInteger"/> residue expressions at every rung of the Default modulus ladder.</summary>
    /// <param name="left">The first operand vector, one raw.</param>
    /// <param name="right">The second operand vector, one raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldArithmeticExact(long[] left, long[] right) =>
        PrimeFieldArithmeticExact(
            left: left,
            moduli: PrimeFieldModuli,
            right: right
        );
    /// <summary>The Deep mirror of <see cref="PrimeFieldArithmeticExact(long[], long[])"/> at the fifteen-rung
    /// ladder.</summary>
    /// <param name="left">The first operand vector, one raw.</param>
    /// <param name="right">The second operand vector, one raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldArithmeticExactDeep(long[] left, long[] right) =>
        PrimeFieldArithmeticExact(
            left: left,
            moduli: PrimeFieldDeepModuli,
            right: right
        );

    private static string? PrimeFieldArithmeticExact(long[] left, long[] right, ulong[] moduli) {
        var firstRaw = left[0];
        var secondRaw = right[0];
        var firstUnsigned = unchecked((ulong)firstRaw);
        var secondUnsigned = unchecked((ulong)secondRaw);

        foreach (var modulus in moduli) {
            var field = PrimeField64.Create(modulus: modulus);
            var wide = new BigInteger(value: modulus);

            if (field.Modulus != modulus) { return $"the field over {modulus} reports modulus {field.Modulus}"; }

            var a = field.Reduce(value: firstUnsigned);
            var b = field.Reduce(value: secondUnsigned);

            if (new BigInteger(value: a) != (new BigInteger(value: firstUnsigned) % wide)) { return $"Reduce({firstUnsigned}) over {modulus} answered {a}"; }
            if (new BigInteger(value: b) != (new BigInteger(value: secondUnsigned) % wide)) { return $"Reduce({secondUnsigned}) over {modulus} answered {b}"; }

            // The signed overload's whole content is that a negative value folds UP by the modulus, and long.MinValue
            // is in the swept edge set, where the C# remainder's sign rule is the one thing that could go wrong.
            var signed = field.Reduce(value: firstRaw);
            var signedExpected = ((((new BigInteger(value: firstRaw) % wide) + wide) % wide));

            if (new BigInteger(value: signed) != signedExpected) { return $"the signed Reduce({firstRaw}) over {modulus} answered {signed}, not {signedExpected}"; }

            var sum = field.Add(
                left: a,
                right: b
            );
            var difference = field.Subtract(
                left: a,
                right: b
            );
            var negation = field.Negate(value: a);
            var product = field.Multiply(
                left: a,
                right: b
            );
            var wideA = new BigInteger(value: a);
            var wideB = new BigInteger(value: b);

            if (new BigInteger(value: sum) != ((wideA + wideB) % wide)) { return $"Add({a},{b}) over {modulus} answered {sum}"; }
            if (new BigInteger(value: difference) != ((((wideA - wideB) % wide) + wide) % wide)) { return $"Subtract({a},{b}) over {modulus} answered {difference}"; }
            if (new BigInteger(value: negation) != ((((-wideA) % wide) + wide) % wide)) { return $"Negate({a}) over {modulus} answered {negation}"; }
            if (new BigInteger(value: product) != ((wideA * wideB) % wide)) { return $"Multiply({a},{b}) over {modulus} answered {product}"; }
            if (
                (sum >= modulus) ||
                (difference >= modulus) ||
                (negation >= modulus) ||
                (product >= modulus)
            ) { return $"an operation over {modulus} answered outside [0, p) at ({a},{b})"; }
            if (
                (signed >= modulus) ||
                (a >= modulus) ||
                (b >= modulus)
            ) { return $"a fold over {modulus} left the field at ({firstRaw},{secondRaw})"; }
            if (field.Add(
                left: a,
                right: field.Zero
            ) != a) { return $"zero is not a right additive identity at {a} over {modulus}"; }
            if (field.Add(
                left: field.Zero,
                right: a
            ) != a) { return $"zero is not a left additive identity at {a} over {modulus}"; }
            if (field.Multiply(
                left: a,
                right: field.One
            ) != a) { return $"one is not a right multiplicative identity at {a} over {modulus}"; }
            if (field.Multiply(
                left: field.One,
                right: a
            ) != a) { return $"one is not a left multiplicative identity at {a} over {modulus}"; }
            if (field.Add(
                left: a,
                right: negation
            ) != field.Zero) { return $"the negation of {a} over {modulus} is not its additive inverse"; }
            if (field.Subtract(
                left: sum,
                right: b
            ) != a) { return $"subtracting {b} did not undo adding it at {a} over {modulus}"; }
        }

        return null;
    }

    /// <summary>Proves <c>Pow</c> against <see cref="BigInteger.ModPow(BigInteger, BigInteger, BigInteger)"/> at five
    /// exponents per rung — the swept full-width exponent, zero, one, <c>p − 2</c> and <c>(p − 1)/2</c>.</summary>
    /// <param name="left">The first operand vector, one raw — the base.</param>
    /// <param name="right">The second operand vector, one raw — the swept exponent.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldPowMatchesModularPower(long[] left, long[] right) =>
        PrimeFieldPowMatchesModularPower(
            left: left,
            moduli: PrimeFieldChainModuli,
            right: right
        );
    /// <summary>The Smoke sentinel of <see cref="PrimeFieldPowMatchesModularPower(long[], long[])"/> at the three-rung
    /// smoke ladder.</summary>
    /// <param name="left">The first operand vector, one raw — the base.</param>
    /// <param name="right">The second operand vector, one raw — the swept exponent.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldPowMatchesModularPowerSmoke(long[] left, long[] right) =>
        PrimeFieldPowMatchesModularPower(
            left: left,
            moduli: PrimeFieldSmokeModuli,
            right: right
        );
    /// <summary>The Deep mirror of <see cref="PrimeFieldPowMatchesModularPower(long[], long[])"/> at the fifteen-rung
    /// ladder.</summary>
    /// <param name="left">The first operand vector, one raw — the base.</param>
    /// <param name="right">The second operand vector, one raw — the swept exponent.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldPowMatchesModularPowerDeep(long[] left, long[] right) =>
        PrimeFieldPowMatchesModularPower(
            left: left,
            moduli: PrimeFieldDeepModuli,
            right: right
        );

    private static string? PrimeFieldPowMatchesModularPower(long[] left, long[] right, ulong[] moduli) {
        var baseRaw = unchecked((ulong)left[0]);
        var sweptExponent = unchecked((ulong)right[0]);
        // Hoisted ABOVE the loop: CA2014 is a build error here, so no stackalloc may sit inside one.
        Span<ulong> exponents = stackalloc ulong[5];

        foreach (var modulus in moduli) {
            var field = PrimeField64.Create(modulus: modulus);
            var wide = new BigInteger(value: modulus);
            var element = field.Reduce(value: baseRaw);
            var wideElement = new BigInteger(value: element);

            exponents[0] = sweptExponent;
            exponents[1] = 0UL;
            exponents[2] = 1UL;
            exponents[3] = (modulus - 2UL);
            exponents[4] = ((modulus - 1UL) >>> 1);

            foreach (var exponent in exponents) {
                var actual = field.Pow(
                    exponent: exponent,
                    value: element
                );
                var expected = BigInteger.ModPow(
                    value: wideElement,
                    exponent: new BigInteger(value: exponent),
                    modulus: wide
                );

                if (new BigInteger(value: actual) != expected) { return $"Pow({element},{exponent}) over {modulus} answered {actual}, not {expected}"; }
                if (actual >= modulus) { return $"Pow({element},{exponent}) over {modulus} left the field"; }
            }

            if (field.Pow(
                exponent: 0UL,
                value: element
            ) != field.One) { return $"the zeroth power of {element} over {modulus} is not one"; }
            if (field.Pow(
                value: field.Zero,
                exponent: 0UL
            ) != field.One) { return $"the zeroth power of zero over {modulus} is not one"; }
            if (field.Pow(
                exponent: 1UL,
                value: element
            ) != element) { return $"the first power moved {element} over {modulus}"; }

            // Fermat's little theorem closes the chain: a truth that depends on the modulus really being prime, and so
            // on Create's own gate.
            var fermat = field.Pow(
                exponent: (modulus - 1UL),
                value: element
            );

            if (fermat != ((0UL == element)
                ? 0UL
                : field.One)) { return $"Fermat's little theorem fails at {element} over {modulus}: {fermat}"; }
        }

        return null;
    }

    /// <summary>Proves <c>Inverse</c> and <c>BatchInverse</c> against per-element
    /// <see cref="BigInteger.ModPow(BigInteger, BigInteger, BigInteger)"/> at exponent <c>p − 2</c>, over a length
    /// ladder that straddles the batch's 512-element scratch boundary in both directions, and proves the pooled
    /// scratch re-enters the shared pool cleared of the caller-derived partial products.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldInverseAndBatch() =>
        PrimeFieldInverseAndBatch(
            longLengths: PrimeFieldBatchLengths,
            moduli: PrimeFieldChainModuli,
            shortLengths: PrimeFieldShortBatchLengths
        );
    /// <summary>The Deep mirror of <see cref="PrimeFieldInverseAndBatch()"/>: the fifteen-rung ladder, with the long
    /// length ladder at EVERY rung rather than only at the carrier corner.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldInverseAndBatchDeep() =>
        PrimeFieldInverseAndBatch(
            longLengths: PrimeFieldBatchLengths,
            moduli: PrimeFieldDeepModuli,
            shortLengths: PrimeFieldBatchLengths
        );

    private static string? PrimeFieldInverseAndBatch(ulong[] moduli, int[] longLengths, int[] shortLengths) {
        foreach (var modulus in moduli) {
            var field = PrimeField64.Create(modulus: modulus);
            var wide = new BigInteger(value: modulus);
            var exponent = (wide - 2);
            var lengths = ((PrimeFieldBatchModulus == modulus)
                ? longLengths
                : shortLengths
            );

            foreach (var length in lengths) {
                var values = new ulong[length];
                var expected = new ulong[length];

                for (var index = 0; (index < length); ++index) {
                    // A deterministic non-zero filling: a fixed odd stride folded into [1, p). No RNG and no clock.
                    var element = (((unchecked((((ulong)index) * 0x9E3779B97F4A7C15UL))) % (modulus - 1UL)) + 1UL);

                    values[index] = element;
                    expected[index] = ((ulong)BigInteger.ModPow(
                        value: new BigInteger(value: element),
                        exponent: exponent,
                        modulus: wide
                    ));
                }

                var batch = ((ulong[])values.Clone());

                field.BatchInverse(values: batch);

                for (var index = 0; (index < length); ++index) {
                    var scalar = field.Inverse(value: values[index]);

                    if (scalar != expected[index]) { return $"Inverse({values[index]}) over {modulus} answered {scalar}, not {expected[index]}"; }
                    if (batch[index] != expected[index]) { return $"BatchInverse at length {length} over {modulus} answered {batch[index]} at index {index}, not {expected[index]}"; }
                    if (batch[index] != scalar) { return $"the batch and the scalar disagree at index {index}, length {length}, modulus {modulus}"; }
                    if (field.Multiply(
                        left: values[index],
                        right: batch[index]
                    ) != field.One) { return $"the batch inverse at index {index}, length {length}, modulus {modulus} is not a multiplicative inverse"; }
                }
            }

            if (!Throws<DivideByZeroException>(action: () => _ = field.Inverse(value: 0UL))) { return $"Inverse admitted zero over {modulus}"; }

            // A refused batch must leave the region bit-for-bit untouched: the forward pass writes only its own
            // scratch, and the throw lands before the backward pass writes anything.
            foreach (var zeroIndex in ((ReadOnlySpan<int>)[0, 3, 7])) {
                var poisoned = new ulong[8];

                for (var index = 0; (index < 8); ++index) { poisoned[index] = ((((ulong)index) % (modulus - 1UL)) + 1UL); }

                poisoned[zeroIndex] = 0UL;

                var snapshot = ((ulong[])poisoned.Clone());

                if (!Throws<DivideByZeroException>(action: () => field.BatchInverse(values: poisoned.AsSpan()))) { return $"BatchInverse admitted a zero at index {zeroIndex} over {modulus}"; }
                if (!poisoned.AsSpan().SequenceEqual(other: snapshot)) { return $"a refused BatchInverse over {modulus} modified the region"; }
            }

            // An EMPTY region is a no-op rather than a refusal — the early return the method opens with, and the one
            // length at which no scratch is taken at all.
            field.BatchInverse(values: Span<ulong>.Empty);
        }

        // The pooled partial-product scratch is CLEARED before it re-enters the shared pool. The fingerprint is the
        // first three running products the forward pass writes — a0, a0·a1, a0·a1·a2, derived here by hand with the
        // same Multiply the kernel runs — and the re-rent happens on the SAME thread with no intervening rent, which
        // is the path ArrayPool<T>.Shared serves from its thread-local slot: an uncleared return would hand this very
        // array straight back, products intact.
        {
            var field = PrimeField64.Create(modulus: PrimeFieldBatchModulus);
            const int PooledLength = 1_024;
            var values = new ulong[PooledLength];

            for (var index = 0; (index < PooledLength); ++index) { values[index] = ((ulong)(index + 2)); }

            var first = values[0];
            var second = field.Multiply(
                left: first,
                right: values[1]
            );
            var third = field.Multiply(
                left: second,
                right: values[2]
            );

            field.BatchInverse(values: values.AsSpan());

            var rented = ArrayPool<ulong>.Shared.Rent(minimumLength: PooledLength);
            var residue = (((rented[0] == first) && (rented[1] == second)) && (rented[2] == third));

            ArrayPool<ulong>.Shared.Return(array: rented);

            if (residue) { return "the pooled scratch re-entered the shared pool still carrying the batch's partial products"; }
        }

        return null;
    }

    /// <summary>Proves <c>LegendreCharacter</c> against the reciprocity descent AND against Euler's criterion in
    /// <see cref="BigInteger"/> — two references that share nothing with each other and nothing with the
    /// subject.</summary>
    /// <param name="left">The first operand vector, one raw.</param>
    /// <param name="right">The second operand vector, one raw — the multiplicativity partner.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldLegendreMatchesReciprocity(long[] left, long[] right) =>
        PrimeFieldLegendreMatchesReciprocity(
            left: left,
            moduli: PrimeFieldChainModuli,
            right: right
        );
    /// <summary>The Deep mirror of <see cref="PrimeFieldLegendreMatchesReciprocity(long[], long[])"/> at the
    /// fifteen-rung ladder.</summary>
    /// <param name="left">The first operand vector, one raw.</param>
    /// <param name="right">The second operand vector, one raw — the multiplicativity partner.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldLegendreMatchesReciprocityDeep(long[] left, long[] right) =>
        PrimeFieldLegendreMatchesReciprocity(
            left: left,
            moduli: PrimeFieldDeepModuli,
            right: right
        );

    private static string? PrimeFieldLegendreMatchesReciprocity(long[] left, long[] right, ulong[] moduli) {
        var raw = unchecked((ulong)left[0]);
        var otherRaw = unchecked((ulong)right[0]);

        foreach (var modulus in moduli) {
            var field = PrimeField64.Create(modulus: modulus);
            var wide = new BigInteger(value: modulus);
            var element = field.Reduce(value: raw);
            var other = field.Reduce(value: otherRaw);
            var wideElement = new BigInteger(value: element);
            var actual = field.LegendreCharacter(value: element);
            var reciprocity = Oracles.JacobiSymbolReciprocity(
                denominator: wide,
                numerator: wideElement
            );

            if (actual != reciprocity) { return $"LegendreCharacter({element}) over {modulus} answered {actual}, reciprocity {reciprocity}"; }

            var euler = BigInteger.ModPow(
                value: wideElement,
                exponent: ((wide - BigInteger.One) / 2),
                modulus: wide
            );
            var eulerCharacter = (wideElement.IsZero
                ? 0
                : (euler.IsOne
                    ? 1
                    : -1
            ));

            if (actual != eulerCharacter) { return $"LegendreCharacter({element}) over {modulus} answered {actual}, Euler {eulerCharacter}"; }
            if ((0 == actual) != (0UL == element)) { return $"the character over {modulus} vanishes at {element}, which is not zero"; }

            var square = field.Multiply(
                left: element,
                right: element
            );

            if (
                (0UL != element) &&
                (1 != field.LegendreCharacter(value: square))
            ) { return $"the square of {element} over {modulus} is not reported as a square"; }
            if (field.LegendreCharacter(value: field.Multiply(
                left: element,
                right: other
            )) != (actual * field.LegendreCharacter(value: other))) { return $"the character is not multiplicative at ({element},{other}) over {modulus}"; }
        }

        return null;
    }

    /// <summary>Proves <c>TrySqrt</c>'s accept/reject decision against the reciprocity descent, checks every returned
    /// root by squaring it back, and asserts that the ladder straddles both of the method's arms.</summary>
    /// <param name="left">The first operand vector, one raw.</param>
    /// <param name="right">The second operand vector, one raw — unused, the statement is unary.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldSquareRootExact(long[] left, long[] right) =>
        PrimeFieldSquareRootExact(
            left: left,
            moduli: PrimeFieldChainModuli,
            right: right
        );
    /// <summary>The Deep mirror of <see cref="PrimeFieldSquareRootExact(long[], long[])"/> at the fifteen-rung
    /// ladder.</summary>
    /// <param name="left">The first operand vector, one raw.</param>
    /// <param name="right">The second operand vector, one raw — unused, the statement is unary.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldSquareRootExactDeep(long[] left, long[] right) =>
        PrimeFieldSquareRootExact(
            left: left,
            moduli: PrimeFieldDeepModuli,
            right: right
        );

    private static string? PrimeFieldSquareRootExact(long[] left, long[] right, ulong[] moduli) {
        _ = right;

        var raw = unchecked((ulong)left[0]);
        var directRungs = 0;
        var descentRungs = 0;

        foreach (var modulus in moduli) {
            if (3UL == (modulus & 3UL)) { ++directRungs; } else { ++descentRungs; }

            var field = PrimeField64.Create(modulus: modulus);
            var element = field.Reduce(value: raw);
            var character = Oracles.JacobiSymbolReciprocity(
                numerator: new BigInteger(value: element),
                denominator: new BigInteger(value: modulus)
            );
            var found = field.TrySqrt(
                value: element,
                out var root
            );

            if (found != (character >= 0)) { return $"TrySqrt({element}) over {modulus} reported {found} where the reciprocity symbol is {character}"; }
            if (field.LegendreCharacter(value: element) != character) { return $"the character TrySqrt decides disagrees with LegendreCharacter at {element} over {modulus}"; }

            if (found) {
                // A square has two roots and the method promises neither one in particular, so the honest check is to
                // square BOTH back rather than to compare against a reference root.
                var sibling = field.Negate(value: root);

                if (root >= modulus) { return $"the root of {element} over {modulus} left the field: {root}"; }
                if (field.Multiply(
                    left: root,
                    right: root
                ) != element) { return $"the root {root} of {element} over {modulus} does not square back"; }
                if (field.Multiply(
                    left: sibling,
                    right: sibling
                ) != element) { return $"the sibling root {sibling} of {element} over {modulus} does not square back"; }
                if (
                    (0UL != element) &&
                    (root == sibling)
                ) { return $"the two roots of {element} over {modulus} coincide"; }
                if (
                    (0UL == element) &&
                    (0UL != root)
                ) { return $"zero rooted to {root} over {modulus}"; }
            } else if (0UL != root) {
                return $"a refused TrySqrt({element}) over {modulus} left {root} behind rather than zero";
            }
        }

        if (
            (0 == directRungs) ||
            (0 == descentRungs)
        ) { return "the ladder no longer straddles TrySqrt's two branches"; }

        return null;
    }

    /// <summary>Proves <c>IsPrime</c> against an exhaustive sieve of Eratosthenes over its whole band, pins the
    /// twelve-base witness set base by base against the published psi_k table, and compares the carrier ladder against
    /// the exact BigInteger decision.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldIsPrimeAgainstSieveAndWitnesses() =>
        PrimeFieldIsPrimeAgainstSieveAndWitnesses(sieveBound: PrimeFieldSieveBound);
    /// <summary>The Deep mirror of <see cref="PrimeFieldIsPrimeAgainstSieveAndWitnesses()"/>, whose exhaustive sieve
    /// band grows fiftyfold.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldIsPrimeAgainstSieveAndWitnessesDeep() =>
        PrimeFieldIsPrimeAgainstSieveAndWitnesses(sieveBound: PrimeFieldDeepSieveBound);

    private static string? PrimeFieldIsPrimeAgainstSieveAndWitnesses(int sieveBound) {
        var sieve = Oracles.PrimeSieve(inclusiveMaximum: sieveBound);

        for (var value = 0; (value <= sieveBound); ++value) {
            if (PrimeField64.IsPrime(value: ((ulong)value)) != sieve[value]) { return $"IsPrime disagrees with the sieve at {value}"; }
        }

        // Each row makes ONE FURTHER BASE load-bearing: truncating the twelve-base set to the row's count would accept
        // that value as prime. Rows like these are the only thing that catches the truncation: cutting IsPrime from
        // twelve bases to eleven went undetected by a 164,000-value replay above 10^12.
        foreach (var (value, bases) in PrimeFieldWitnessPseudoprimes) {
            var survived = 0;

            foreach (var witness in Oracles.StrongPrimeWitnessBases) {
                if (!Oracles.ModularStrongProbablePrime(
                    value: value,
                    witness: witness
                )) { break; }

                ++survived;
            }

            if (survived != bases) { return $"{value} survives {survived} leading prime bases, not the tabulated {bases}"; }
            if (Oracles.ExactPrimality(value: value)) { return $"the oracle called the tabulated pseudoprime {value} prime"; }
            if (PrimeField64.IsPrime(value: value)) { return $"IsPrime accepted the strong pseudoprime {value}, which survives {bases} leading bases; the witness set is short"; }
        }

        foreach (var value in PrimeFieldPrimalityLadder) {
            if (PrimeField64.IsPrime(value: value) != Oracles.ExactPrimality(value: value)) { return $"IsPrime and the oracle disagree at {value}"; }
        }

        return null;
    }

    /// <summary>Proves <c>IsPrime</c> against the exact BigInteger decision over the swept candidate stream, which is
    /// where the exhaustive sieve band cannot reach.</summary>
    /// <param name="left">The first operand vector, one raw.</param>
    /// <param name="right">The second operand vector, one raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldIsPrimeMatchesWitnessOracle(long[] left, long[] right) {
        foreach (var raw in ((ReadOnlySpan<long>)[left[0], right[0]])) {
            var drawn = unchecked((ulong)raw);

            // The forced-odd image so the sweep meets the arithmetic and not only the even short-circuit.
            foreach (var candidate in ((ReadOnlySpan<ulong>)[drawn, (drawn | 1UL)])) {
                if (PrimeField64.IsPrime(value: candidate) != Oracles.ExactPrimality(value: candidate)) { return $"IsPrime and the oracle disagree at {candidate}"; }
            }
        }

        return null;
    }
    /// <summary>Proves one strong-probable-prime round against an independent BigInteger round at six witness bases per
    /// candidate, including the contract's zero-base clause and the every-prime-passes direction.</summary>
    /// <param name="left">The first operand vector, one raw — the candidate.</param>
    /// <param name="right">The second operand vector, one raw — the swept witness base.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldStrongRoundMatchesOracle(long[] left, long[] right) {
        var drawn = unchecked((ulong)left[0]);
        var sweptWitness = unchecked((ulong)right[0]);
        Span<ulong> witnesses = stackalloc ulong[6];

        foreach (var candidate in ((ReadOnlySpan<ulong>)[drawn, (drawn | 1UL)])) {
            witnesses[0] = sweptWitness;
            witnesses[1] = 0UL;
            witnesses[2] = 1UL;
            witnesses[3] = 2UL;
            witnesses[4] = unchecked((candidate - 1UL));
            witnesses[5] = candidate;

            var prime = Oracles.ExactPrimality(value: candidate);

            foreach (var witness in witnesses) {
                var actual = PrimeField64.IsStrongProbablePrime(
                    value: candidate,
                    witness: witness
                );

                if (actual != Oracles.ModularStrongProbablePrime(
                    value: candidate,
                    witness: witness
                )) { return $"IsStrongProbablePrime({candidate},{witness}) disagrees with the BigInteger round"; }
                if (
                    prime &&
                    !actual
                ) { return $"the round rejected the prime {candidate} at base {witness}"; }
            }

            // The zero-base clause, which is the one clause a reader would not guess: a base that reduces to zero
            // carries no evidence and PASSES. It sits BELOW the guards in the subject and in the oracle alike, so it
            // is a statement about odd candidates above two and nothing else — an even candidate is rejected before
            // the base is ever reduced, which the third row states directly.
            var reachesTheArithmetic = ((candidate > 2UL) && (0UL != (candidate & 1UL)));

            if (
                reachesTheArithmetic &&
                !PrimeField64.IsStrongProbablePrime(
                value: candidate,
                witness: 0UL
            )
            ) { return $"the zero base did not pass at {candidate}"; }
            if (
                reachesTheArithmetic &&
                !PrimeField64.IsStrongProbablePrime(
                value: candidate,
                witness: candidate
            )
            ) { return $"a base congruent to zero did not pass at {candidate}"; }
            if (
                (candidate > 2UL) &&
                (0UL == (candidate & 1UL)) &&
                PrimeField64.IsStrongProbablePrime(
                value: candidate,
                witness: 2UL
            )
            ) { return $"the even value {candidate} passed a round"; }
        }

        if (
            PrimeField64.IsStrongProbablePrime(
            value: 0UL,
            witness: 2UL
        ) ||
            PrimeField64.IsStrongProbablePrime(
            value: 1UL,
            witness: 2UL
        )
        ) { return "a value below two passed a round"; }
        if (!PrimeField64.IsStrongProbablePrime(
            value: 2UL,
            witness: 3UL
        )) { return "two was rejected"; }

        return null;
    }
    /// <summary>Proves the strong Lucas test against the companion-matrix oracle over the swept candidate stream, the
    /// every-prime-passes direction, and the rejection of an adversarially large perfect square.</summary>
    /// <param name="left">The first operand vector, one raw.</param>
    /// <param name="right">The second operand vector, one raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldLucasMatchesCompanionMatrix(long[] left, long[] right) =>
        PrimeFieldLucasMatchesCompanionMatrix(
            breadth: 1,
            left: left,
            right: right
        );
    /// <summary>The Deep mirror of <see cref="PrimeFieldLucasMatchesCompanionMatrix(long[], long[])"/>, which runs BOTH
    /// drawn raws rather than one — this family's most expensive oracle.</summary>
    /// <param name="left">The first operand vector, one raw.</param>
    /// <param name="right">The second operand vector, one raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldLucasMatchesCompanionMatrixDeep(long[] left, long[] right) =>
        PrimeFieldLucasMatchesCompanionMatrix(
            breadth: 2,
            left: left,
            right: right
        );

    private static string? PrimeFieldLucasMatchesCompanionMatrix(long[] left, long[] right, int breadth) {
        Span<ulong> candidates = stackalloc ulong[2];

        candidates[0] = unchecked((ulong)left[0]) | 1UL;
        candidates[1] = unchecked((ulong)right[0]) | 1UL;

        for (var index = 0; (index < breadth); ++index) {
            var candidate = candidates[index];

            if (PrimeField64.IsStrongLucasProbablePrime(value: candidate) != Oracles.StrongLucasSelfridge(value: candidate)) {
                return $"IsStrongLucasProbablePrime({candidate}) disagrees with the companion-matrix oracle";
            }
            if (
                Oracles.ExactPrimality(value: candidate) &&
                !PrimeField64.IsStrongLucasProbablePrime(value: candidate)
            ) {
                return $"the strong Lucas test rejected the prime {candidate}";
            }
        }

        // The adversarial square: a large root, so no small factor screens it out. The bound keeps root² inside the
        // carrier — 4294967292² is below 2^64 — so the square is exact and no wrap is involved.
        var root = ((unchecked((ulong)left[0]) % 4_294_967_291UL) + 2UL);
        var square = (root * root);

        if (PrimeField64.IsStrongLucasProbablePrime(value: square)) { return $"the perfect square {square} (root {root}) was accepted"; }
        if (
            PrimeField64.IsStrongLucasProbablePrime(value: 0UL) ||
            PrimeField64.IsStrongLucasProbablePrime(value: 1UL)
        ) { return "a value below two was accepted"; }
        if (!PrimeField64.IsStrongLucasProbablePrime(value: 2UL)) { return "two was rejected"; }

        return null;
    }

    /// <summary>Proves the Baillie–PSW composition's CARRIAGE — that it is exactly the base-two round conjoined with
    /// the strong Lucas test — and its DIFFERENTIAL against the exact BigInteger decision, which below <c>2⁶⁴</c> is a
    /// fact about the carrier rather than an extrapolation.</summary>
    /// <param name="left">The first operand vector, one raw.</param>
    /// <param name="right">The second operand vector, one raw.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldBaillieComposition(long[] left, long[] right) {
        foreach (var raw in ((ReadOnlySpan<long>)[left[0], right[0]])) {
            var value = unchecked((ulong)raw) | 1UL;
            var accepted = PrimeField64.IsBaillieProbablePrime(value: value);
            var half = PrimeField64.IsStrongProbablePrime(
                value: value,
                witness: 2UL
            );
            var lucas = PrimeField64.IsStrongLucasProbablePrime(value: value);

            if (accepted != (half && lucas)) { return $"IsBaillieProbablePrime({value}) is not the conjunction of its two halves (base-two {half}, Lucas {lucas})"; }
            if (accepted != Oracles.ExactPrimality(value: value)) { return $"Baillie-PSW and the exact BigInteger decision disagree at {value}; below 2^64 that is either a defect or the first known counterexample"; }
            if (accepted != PrimeField64.IsPrime(value: value)) { return $"Baillie-PSW and the twelve-base decision disagree at {value}"; }
        }

        return null;
    }
    /// <summary>Proves the composition's CARRIAGE where it is actually decidable: at the published pseudoprime
    /// populations, which are the only values where the two halves disagree.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    /// <remarks>A fixed ladder rather than a swept one, and necessarily so. On any sampled 64-bit stream the two
    /// halves AGREE at every candidate — every prime passes both, every composite fails both, which is precisely the
    /// Baillie–PSW guarantee — so a conjunction, a disjunction and a Lucas-only carriage are indistinguishable there.
    /// The disagreement lives entirely in the two pseudoprime populations.</remarks>
    public static string? PrimeFieldBaillieCarriage() {
        var roundAcceptsAlone = 0;
        var lucasAcceptsAlone = 0;

        foreach (var value in PrimeFieldCarriageLadder) {
            var accepted = PrimeField64.IsBaillieProbablePrime(value: value);
            var half = PrimeField64.IsStrongProbablePrime(
                value: value,
                witness: 2UL
            );
            var lucas = PrimeField64.IsStrongLucasProbablePrime(value: value);

            if (accepted != (half && lucas)) { return $"IsBaillieProbablePrime({value}) is not the conjunction of its two halves (base-two {half}, Lucas {lucas})"; }
            if (
                half &&
                !lucas
            ) { ++roundAcceptsAlone; }
            if (
                lucas &&
                !half
            ) { ++lucasAcceptsAlone; }
        }

        // The ladder must straddle BOTH disagreement directions or the statement above is vacuous: without a row where
        // only the round accepts, a disjunction passes; without a row where only the Lucas half accepts, both a
        // disjunction and a transposition — IsStrongProbablePrime(2, value) is constant true — pass.
        if (0 == roundAcceptsAlone) { return "no carriage row has the base-two round accepting alone, so a disjunction would pass"; }
        if (0 == lucasAcceptsAlone) { return "no carriage row has the Lucas half accepting alone, so a transposed conjunction would pass"; }

        return null;
    }
    /// <summary>Proves both pseudoprime POPULATIONS by ENUMERATING them with the subjects themselves and comparing the
    /// two lists against the published tables — probable-ness pinned as a fact, not apologised for.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldPseudoprimePopulations() =>
        PrimeFieldPseudoprimePopulations(
            baseTwo: PrimeFieldBaseTwoPseudoprimes,
            bound: PrimeFieldPopulationBound,
            lucas: PrimeFieldLucasPseudoprimes
        );
    /// <summary>The Deep mirror of <see cref="PrimeFieldPseudoprimePopulations()"/>, enumerated to <c>10⁵</c> against
    /// the FULL published lists rather than their below-<c>10⁴</c> prefixes.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeFieldPseudoprimePopulationsDeep() =>
        PrimeFieldPseudoprimePopulations(
            baseTwo: PrimeFieldDeepBaseTwoPseudoprimes,
            bound: PrimeFieldDeepPopulationBound,
            lucas: PrimeFieldDeepLucasPseudoprimes
        );

    private static string? PrimeFieldPseudoprimePopulations(ulong bound, ulong[] baseTwo, ulong[] lucas) {
        var sieve = Oracles.PrimeSieve(inclusiveMaximum: ((int)bound));
        var foundBaseTwo = new List<ulong>();
        var foundLucas = new List<ulong>();

        for (var candidate = 3UL; (candidate < bound); candidate += 2UL) {
            if (sieve[((int)candidate)]) { continue; }
            if (PrimeField64.IsStrongProbablePrime(
                value: candidate,
                witness: 2UL
            )) { foundBaseTwo.Add(item: candidate); }
            if (PrimeField64.IsStrongLucasProbablePrime(value: candidate)) { foundLucas.Add(item: candidate); }
        }

        if (!foundBaseTwo.SequenceEqual(second: baseTwo)) {
            return $"the base-two strong pseudoprime population below {bound} is [{string.Join(
            separator: ",",
            values: foundBaseTwo
        )}]";
        }
        if (!foundLucas.SequenceEqual(second: lucas)) {
            return $"the strong Lucas pseudoprime population below {bound} is [{string.Join(
            separator: ",",
            values: foundLucas
        )}]";
        }
        if (foundBaseTwo.Intersect(second: foundLucas).Any()) { return "the two pseudoprime populations are not disjoint, which is the composition's whole thesis"; }

        // The sole-rejecter controls, which make both halves demonstrably load-bearing rather than decorative.
        foreach (var value in baseTwo) {
            if (!PrimeField64.IsStrongProbablePrime(
                value: value,
                witness: 2UL
            )) { return $"the base-two round no longer accepts its own pseudoprime {value}"; }
            if (PrimeField64.IsStrongLucasProbablePrime(value: value)) { return $"the Lucas half accepted the base-two pseudoprime {value}"; }
            if (
                PrimeField64.IsBaillieProbablePrime(value: value) ||
                PrimeField64.IsPrime(value: value)
            ) { return $"the base-two pseudoprime {value} was accepted as prime"; }
        }

        foreach (var value in lucas) {
            if (!PrimeField64.IsStrongLucasProbablePrime(value: value)) { return $"the Lucas test no longer accepts its own pseudoprime {value}"; }
            if (PrimeField64.IsStrongProbablePrime(
                value: value,
                witness: 2UL
            )) { return $"the base-two round accepted the Lucas pseudoprime {value}"; }
            if (
                PrimeField64.IsBaillieProbablePrime(value: value) ||
                PrimeField64.IsPrime(value: value)
            ) { return $"the Lucas pseudoprime {value} was accepted as prime"; }
        }

        // Carmichael numbers are the Fermat weakness both halves are built to survive. Named honestly: all seven die
        // at the base-two STRONG round, so as a probe of the Lucas half this row cannot fail.
        foreach (var value in PrimeFieldCarmichaelNumbers) {
            if (
                PrimeField64.IsBaillieProbablePrime(value: value) ||
                PrimeField64.IsPrime(value: value)
            ) { return $"the Carmichael number {value} was accepted"; }
        }

        foreach (var value in ((ReadOnlySpan<ulong>)[0UL, 1UL, 4UL, 6UL, 8UL, 9UL, 15UL, 21UL, 25UL])) {
            if (
                PrimeField64.IsStrongLucasProbablePrime(value: value) ||
                PrimeField64.IsBaillieProbablePrime(value: value) ||
                PrimeField64.IsPrime(value: value)
            ) { return $"the small composite {value} was accepted"; }
        }

        for (var value = 2UL; (value < 100UL); ++value) {
            if (!sieve[((int)value)]) { continue; }
            if (
                !PrimeField64.IsStrongLucasProbablePrime(value: value) ||
                !PrimeField64.IsBaillieProbablePrime(value: value) ||
                !PrimeField64.IsPrime(value: value)
            ) { return $"the small prime {value} was rejected"; }
        }

        return null;
    }

    // ---- the quadratic extension field (QuadraticExtensionField64) ----
    // EXACT throughout, like the prime field it sits over: nothing here rounds, saturates or approximates, so the
    // campaign's substrate condition drops out of every leg below and each says so in those words. What is live instead
    // is reduction (which modulus fold applies, and at which point in the expression), representation (Montgomery form
    // leaking out of the base field's Pow and Inverse into a returned coordinate), the width edges PrimeField64's
    // UInt128 widening and conditional folds cover, and the refusal contracts of Create, Inverse and BatchInverse.

    /// <summary>One prime of the extension ladder: its modulus and the SMALLEST quadratic non-square over it, both
    /// DECLARED rather than read back from the subject — a reference built from <c>extension.NonSquare</c> would move
    /// with a wrong accessor instead of catching it.</summary>
    /// <param name="Modulus">The odd prime.</param>
    /// <param name="SmallestNonSquare">The least value at or above two whose quadratic character over the modulus
    /// is <c>-1</c>.</param>
    private readonly record struct ExtensionPrime(ulong Modulus, ulong SmallestNonSquare);

    // The lean ladder every SWEPT case runs: the smallest field the type admits, a six-digit prime whose smallest
    // non-square is TWO rather than three (p ≡ 3 mod 8), and the Mersenne prime 2^61 − 1 — the widest modulus below the
    // type's 2^62 ceiling and the only entry whose base products actually fill the UInt128 reduction.
    private static readonly ExtensionPrime[] LeanExtensionPrimes = [
        new(
            Modulus: 7UL,
            SmallestNonSquare: 3UL
        ),
        new(
            Modulus: 1_000_003UL,
            SmallestNonSquare: 2UL
        ),
        new(
            Modulus: 2_305_843_009_213_693_951UL,
            SmallestNonSquare: 3UL
        ),
    ];
    // The full ladder: the two smallest fields, both classes modulo eight at four scales, two Fermat primes, two
    // published NTT moduli with deep two-adic structure, and both Mersenne primes the carrier admits. Every second
    // field is hand-derived from quadratic reciprocity and re-derived at run time by Oracles.SmallestQuadraticNonResidue
    // in ExtensionConstructionAndRefusals, so a mistyped entry fails there by name rather than silently weakening every
    // case that runs the ladder.
    private static readonly ExtensionPrime[] FullExtensionPrimes = [
        new(
            Modulus: 3UL,
            SmallestNonSquare: 2UL
        ),
        new(
            Modulus: 5UL,
            SmallestNonSquare: 2UL
        ),
        new(
            Modulus: 7UL,
            SmallestNonSquare: 3UL
        ),
        new(
            Modulus: 11UL,
            SmallestNonSquare: 2UL
        ),
        new(
            Modulus: 13UL,
            SmallestNonSquare: 2UL
        ),
        new(
            Modulus: 97UL,
            SmallestNonSquare: 5UL
        ),
        new(
            Modulus: 257UL,
            SmallestNonSquare: 3UL
        ),                    // the Fermat prime F_3
        new(
            Modulus: 65_537UL,
            SmallestNonSquare: 3UL
        ),                 // the Fermat prime F_4
        new(
            Modulus: 1_000_003UL,
            SmallestNonSquare: 2UL
        ),
        new(
            Modulus: 998_244_353UL,
            SmallestNonSquare: 3UL
        ),            // 119·2^23 + 1, a published NTT modulus
        new(
            Modulus: 2_013_265_921UL,
            SmallestNonSquare: 11UL
        ),         // 15·2^27 + 1, a published NTT modulus
        new(
            Modulus: 2_147_483_647UL,
            SmallestNonSquare: 3UL
        ),          // M31
        new(
            Modulus: 2_305_843_009_213_693_951UL,
            SmallestNonSquare: 3UL
        ),  // M61
    ];
    // Built ONCE: PrimeField64.Create runs a primality test and QuadraticExtensionField64.Create runs a Legendre
    // exponentiation, neither of which belongs inside a swept loop.
    private static readonly QuadraticExtensionField64[] LeanExtensions = BuildExtensions(primes: LeanExtensionPrimes);
    private static readonly QuadraticExtensionField64[] FullExtensions = BuildExtensions(primes: FullExtensionPrimes);
    // The exponent ladder the polynomial reference runs: zero, the low exponents whose MSB-first and LSB-first schedules
    // differ most, a Fibonacci-spaced middle, and a byte boundary whose chain is eight squarings deep.
    private static readonly ulong[] ExtensionExponentLadder = [0UL, 1UL, 2UL, 3UL, 5UL, 8UL, 13UL, 255UL];
    // The batch lengths: the trivial ones, the last two the 512-element stack arm serves, and two above it that force
    // the pooled arm.
    private static readonly int[] ExtensionBatchLengths = [1, 2, 3, 511, 512, 513, 600];

    // Exhaustive Create sweeps run only where the whole residue set is cheap; above it a fixed square ladder.
    private const ulong ExtensionExhaustiveCeiling = 97UL;

    private static QuadraticExtensionField64[] BuildExtensions(ExtensionPrime[] primes) {
        var built = new QuadraticExtensionField64[primes.Length];

        for (var index = 0; (index < primes.Length); ++index) {
            built[index] = QuadraticExtensionField64.Create(
                baseField: PrimeField64.Create(modulus: primes[index].Modulus),
                nonSquare: primes[index].SmallestNonSquare
            );
        }

        return built;
    }
    /// <summary>Folds a sampled raw onto a reduced base-field element. C#'s OWN remainder on the reinterpreted word —
    /// no Puck.Maths call — so the operand fold cannot inherit a defect from the reduction the laws are about to
    /// test. Every result is below <c>2^62</c>, so it also casts to a non-negative <see cref="long"/> losslessly.</summary>
    /// <param name="raw">The sampled raw.</param>
    /// <param name="modulus">The ladder prime to fold onto.</param>
    /// <returns>The reduced base-field element.</returns>
    private static ulong ExtensionResidue(long raw, ulong modulus) =>
        (unchecked((ulong)raw) % modulus);

    /// <summary>Proves the extension's additive group, its product, its two identities and the base-field embedding
    /// EXACTLY at every swept element pair, against schoolbook polynomial reduction and unbounded coordinate
    /// arithmetic.</summary>
    /// <param name="full">Whether to run the full prime ladder rather than the lean one.</param>
    /// <returns>The claim body.</returns>
    public static Func<long[], long[], string?> ExtensionRingExact(bool full) {
        var primes = (full
            ? FullExtensionPrimes
            : LeanExtensionPrimes
        );
        var extensions = (full
            ? FullExtensions
            : LeanExtensions
        );

        return (left, right) => {
            Span<ulong> oracleLeft = stackalloc ulong[2];
            Span<ulong> oracleRight = stackalloc ulong[2];
            Span<ulong> oracleTail = stackalloc ulong[2];
            Span<ulong> oracleProduct = stackalloc ulong[2];

            for (var entry = 0; (entry < primes.Length); ++entry) {
                var modulus = primes[entry].Modulus;
                var nonSquare = primes[entry].SmallestNonSquare;
                var extension = extensions[entry];
                var big = new BigInteger(value: modulus);
                var a1 = ExtensionResidue(
                    raw: left[0],
                    modulus: modulus
                );
                var b1 = ExtensionResidue(
                    raw: left[1],
                    modulus: modulus
                );
                var a2 = ExtensionResidue(
                    raw: right[0],
                    modulus: modulus
                );
                var b2 = ExtensionResidue(
                    raw: right[1],
                    modulus: modulus
                );
                var x = new QuadraticExtensionField64.Element(
                    A: a1,
                    B: b1
                );
                var y = new QuadraticExtensionField64.Element(
                    A: a2,
                    B: b2
                );

                if (
                    (x.A != a1) ||
                    (x.B != b1)
                ) { return $"the element ({a1},{b1}) over F_{modulus} reads back as ({x.A},{x.B})"; }

                var sum = extension.Add(
                    left: x,
                    right: y
                );
                var expectedSumA = ((ulong)((((BigInteger)a1) + a2) % big));
                var expectedSumB = ((ulong)((((BigInteger)b1) + b2) % big));

                if (
                    (sum.A != expectedSumA) ||
                    (sum.B != expectedSumB)
                ) { return $"({a1},{b1}) + ({a2},{b2}) over F_{modulus} is ({sum.A},{sum.B}), expected ({expectedSumA},{expectedSumB})"; }

                var difference = extension.Subtract(
                    left: x,
                    right: y
                );
                var expectedDifferenceA = ((ulong)((((((BigInteger)a1) - a2) % big) + big) % big));
                var expectedDifferenceB = ((ulong)((((((BigInteger)b1) - b2) % big) + big) % big));

                if (
                    (difference.A != expectedDifferenceA) ||
                    (difference.B != expectedDifferenceB)
                ) { return $"({a1},{b1}) − ({a2},{b2}) over F_{modulus} is ({difference.A},{difference.B}), expected ({expectedDifferenceA},{expectedDifferenceB})"; }

                var negation = extension.Negate(value: y);
                var expectedNegationA = ((ulong)((big - a2) % big));
                var expectedNegationB = ((ulong)((big - b2) % big));

                if (
                    (negation.A != expectedNegationA) ||
                    (negation.B != expectedNegationB)
                ) { return $"−({a2},{b2}) over F_{modulus} is ({negation.A},{negation.B}), expected ({expectedNegationA},{expectedNegationB})"; }

                // The product, by SCHOOLBOOK polynomial multiplication modulo t² − d: no closed pair formula anywhere on
                // the reference side.
                oracleLeft[0] = a1;
                oracleLeft[1] = b1;
                oracleRight[0] = a2;
                oracleRight[1] = b2;
                oracleTail[0] = ((modulus - nonSquare) % modulus);
                oracleTail[1] = 0UL;

                Oracles.PrimeFieldPolynomialProduct(
                    left: oracleLeft,
                    modulus: modulus,
                    result: oracleProduct,
                    right: oracleRight,
                    tail: oracleTail
                );

                var product = extension.Multiply(
                    left: x,
                    right: y
                );

                if (product.A != oracleProduct[0]) { return $"({a1},{b1})·({a2},{b2}) over F_{modulus}(√{nonSquare}) has base part {product.A}, the polynomial oracle says {oracleProduct[0]}"; }
                if (product.B != oracleProduct[1]) { return $"({a1},{b1})·({a2},{b2}) over F_{modulus}(√{nonSquare}) has root part {product.B}, the polynomial oracle says {oracleProduct[1]}"; }

                var zero = extension.Zero;
                var one = extension.One;

                if (
                    (zero.A != 0UL) ||
                    (zero.B != 0UL)
                ) { return $"Zero over F_{modulus} is ({zero.A},{zero.B})"; }
                if (
                    (one.A != 1UL) ||
                    (one.B != 0UL)
                ) { return $"One over F_{modulus} is ({one.A},{one.B})"; }
                if (extension.Add(
                    left: x,
                    right: zero
                ) != x) { return $"Zero is not a right additive identity at ({a1},{b1}) over F_{modulus}"; }
                if (extension.Add(
                    left: zero,
                    right: x
                ) != x) { return $"Zero is not a left additive identity at ({a1},{b1}) over F_{modulus}"; }
                if (extension.Multiply(
                    left: x,
                    right: one
                ) != x) { return $"One is not a right multiplicative identity at ({a1},{b1}) over F_{modulus}"; }
                if (extension.Multiply(
                    left: one,
                    right: x
                ) != x) { return $"One is not a left multiplicative identity at ({a1},{b1}) over F_{modulus}"; }
                if (extension.Add(
                    left: y,
                    right: negation
                ) != zero) { return $"({a2},{b2}) plus its negation over F_{modulus} is not Zero"; }
                if (difference != extension.Add(
                    left: x,
                    right: negation
                )) { return $"Subtract and Add-of-Negate disagree at ({a1},{b1}) − ({a2},{b2}) over F_{modulus}"; }

                // FromBase is the ring embedding of F_p, with both base values re-formed in arbitrary width.
                var lifted = extension.FromBase(value: a1);
                var otherLifted = extension.FromBase(value: a2);

                if (
                    (lifted.A != a1) ||
                    (lifted.B != 0UL)
                ) { return $"FromBase({a1}) over F_{modulus} is ({lifted.A},{lifted.B})"; }
                if (extension.Add(
                    left: lifted,
                    right: otherLifted
                ) != extension.FromBase(value: expectedSumA)) { return $"FromBase does not carry the base sum of {a1} and {a2} over F_{modulus}"; }
                if (extension.Multiply(
                    left: lifted,
                    right: otherLifted
                ) != extension.FromBase(value: ((ulong)((((BigInteger)a1) * a2) % big)))) { return $"FromBase does not carry the base product of {a1} and {a2} over F_{modulus}"; }
            }

            return null;
        };
    }
    /// <summary>Proves the norm, the trace and the conjugation EXACTLY at every swept element pair — each against its
    /// own shared-nothing reference, then the three against one another through the characteristic equation, the
    /// defining equations of the conjugation, and the automorphism laws.</summary>
    /// <param name="full">Whether to run the full prime ladder rather than the lean one.</param>
    /// <returns>The claim body.</returns>
    public static Func<long[], long[], string?> ExtensionNormTraceFrobeniusExact(bool full) {
        var primes = (full
            ? FullExtensionPrimes
            : LeanExtensionPrimes
        );
        var extensions = (full
            ? FullExtensions
            : LeanExtensions
        );

        return (left, right) => {
            for (var entry = 0; (entry < primes.Length); ++entry) {
                var modulus = primes[entry].Modulus;
                var nonSquare = primes[entry].SmallestNonSquare;
                var extension = extensions[entry];
                var big = new BigInteger(value: modulus);
                var a1 = ExtensionResidue(
                    raw: left[0],
                    modulus: modulus
                );
                var b1 = ExtensionResidue(
                    raw: left[1],
                    modulus: modulus
                );
                var a2 = ExtensionResidue(
                    raw: right[0],
                    modulus: modulus
                );
                var b2 = ExtensionResidue(
                    raw: right[1],
                    modulus: modulus
                );
                var x = new QuadraticExtensionField64.Element(
                    A: a1,
                    B: b1
                );
                var y = new QuadraticExtensionField64.Element(
                    A: a2,
                    B: b2
                );

                var (oracleNorm, oracleResidual) = Oracles.QuadraticExtensionConjugateProduct(
                    a: a1,
                    b: b1,
                    modulus: modulus,
                    nonSquare: nonSquare
                );

                if (0UL != oracleResidual) { return $"the conjugate product of ({a1},{b1}) over F_{modulus}(√{nonSquare}) has a non-zero root part {oracleResidual}"; }

                var norm = extension.Norm(value: x);

                if (norm != oracleNorm) { return $"N({a1},{b1}) over F_{modulus}(√{nonSquare}) is {norm}, the conjugate-product oracle says {oracleNorm}"; }

                // Multiplicativity, EXACT: no sublattice fold anywhere, at every element of every ladder prime.
                var (otherNorm, _) = Oracles.QuadraticExtensionConjugateProduct(
                    a: a2,
                    b: b2,
                    modulus: modulus,
                    nonSquare: nonSquare
                );
                var product = extension.Multiply(
                    left: x,
                    right: y
                );
                var productNorm = extension.Norm(value: product);
                var expectedProductNorm = ((ulong)((((BigInteger)oracleNorm) * otherNorm) % big));

                if (productNorm != expectedProductNorm) { return $"N of the product of ({a1},{b1}) and ({a2},{b2}) over F_{modulus} is {productNorm}, the product of the norms is {expectedProductNorm}"; }

                var trace = extension.Trace(value: x);
                var expectedTrace = ((ulong)((2 * ((BigInteger)a1)) % big));

                if (trace != expectedTrace) { return $"Tr({a1},{b1}) over F_{modulus} is {trace}, expected {expectedTrace}"; }

                var conjugate = extension.Frobenius(value: x);
                var expectedConjugateB = ((ulong)((big - b1) % big));

                if (
                    (conjugate.A != a1) ||
                    (conjugate.B != expectedConjugateB)
                ) { return $"the conjugate of ({a1},{b1}) over F_{modulus} is ({conjugate.A},{conjugate.B}), expected ({a1},{expectedConjugateB})"; }

                // x² = Tr(x)·x − N(x): the characteristic equation, a statement about the two coefficients TOGETHER
                // that neither one's own reference makes.
                var square = extension.Multiply(
                    left: x,
                    right: x
                );
                var characteristic = extension.Subtract(
                    left: extension.Multiply(
                        left: extension.FromBase(value: trace),
                        right: x
                    ),
                    right: extension.FromBase(value: norm)
                );

                if (square != characteristic) { return $"({a1},{b1}) over F_{modulus} does not satisfy its own characteristic equation"; }

                if (extension.Multiply(
                    left: x,
                    right: conjugate
                ) != extension.FromBase(value: norm)) { return $"the conjugate product of ({a1},{b1}) over F_{modulus} is not the lifted norm"; }
                if (extension.Add(
                    left: x,
                    right: conjugate
                ) != extension.FromBase(value: trace)) { return $"the conjugate sum of ({a1},{b1}) over F_{modulus} is not the lifted trace"; }
                if (extension.Frobenius(value: conjugate) != x) { return $"the conjugation is not an involution at ({a1},{b1}) over F_{modulus}"; }
                if ((conjugate == x) != (0UL == b1)) { return $"the conjugation fixes ({a1},{b1}) over F_{modulus} exactly when the root part is zero — it does not"; }
                if (extension.Frobenius(value: extension.Add(
                    left: x,
                    right: y
                )) != extension.Add(
                    left: conjugate,
                    right: extension.Frobenius(value: y)
                )) { return $"the conjugation is not additive at ({a1},{b1}) and ({a2},{b2}) over F_{modulus}"; }
                if (extension.Frobenius(value: product) != extension.Multiply(
                    left: conjugate,
                    right: extension.Frobenius(value: y)
                )) { return $"the conjugation is not multiplicative at ({a1},{b1}) and ({a2},{b2}) over F_{modulus}"; }
            }

            return null;
        };
    }
    /// <summary>Proves inversion against the extended Euclidean algorithm at every swept element, with the round trip,
    /// the involution and the zero refusal beside it.</summary>
    /// <param name="left">The first operand vector; its leading lane is the base-field part.</param>
    /// <param name="right">The second operand vector; its leading lane is the root coefficient.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ExtensionInverseExact(long[] left, long[] right) {
        for (var entry = 0; (entry < LeanExtensionPrimes.Length); ++entry) {
            var modulus = LeanExtensionPrimes[entry].Modulus;
            var nonSquare = LeanExtensionPrimes[entry].SmallestNonSquare;
            var extension = LeanExtensions[entry];
            var big = new BigInteger(value: modulus);
            var a = ExtensionResidue(
                raw: left[0],
                modulus: modulus
            );
            var b = ExtensionResidue(
                raw: right[0],
                modulus: modulus
            );

            // A zero element has no inverse; One is the substitute, so every sampled operand reaches a defined
            // comparison and the refusal is pinned by name below rather than skipped asymmetrically.
            if (
                (0UL == a) &&
                (0UL == b)
            ) { a = 1UL; }

            var x = new QuadraticExtensionField64.Element(
                A: a,
                B: b
            );

            var (oracleNorm, _) = Oracles.QuadraticExtensionConjugateProduct(
                a: a,
                b: b,
                modulus: modulus,
                nonSquare: nonSquare
            );
            var inverseNorm = Oracles.ModularInverse(
                value: new BigInteger(value: oracleNorm),
                modulus: big
            );
            var expectedA = ((ulong)((((BigInteger)a) * inverseNorm) % big));
            var expectedB = ((ulong)((big - ((((BigInteger)b) * inverseNorm) % big)) % big));
            var inverse = extension.Inverse(value: x);

            if (
                (inverse.A != expectedA) ||
                (inverse.B != expectedB)
            ) { return $"({a},{b})⁻¹ over F_{modulus}(√{nonSquare}) is ({inverse.A},{inverse.B}), the Euclid oracle says ({expectedA},{expectedB})"; }
            if (extension.Multiply(
                left: x,
                right: inverse
            ) != extension.One) { return $"({a},{b}) times its inverse over F_{modulus} is not One"; }
            if (extension.Inverse(value: inverse) != x) { return $"inversion is not an involution at ({a},{b}) over F_{modulus}"; }
            if (extension.Inverse(value: extension.One) != extension.One) { return $"One is not its own inverse over F_{modulus}"; }

            // The refusal, visited on EVERY iteration whatever the domain drew.
            if (!Throws<DivideByZeroException>(action: () => _ = extension.Inverse(value: extension.Zero))) { return $"Inverse(Zero) over F_{modulus} did not throw DivideByZeroException"; }
        }

        return null;
    }
    /// <summary>Proves the power against a most-significant-bit-first polynomial reference, against the two theorems
    /// that identify the <c>p</c>-power map with the conjugation and the <c>(p + 1)</c>-power with the norm, against
    /// the multiplicative group's order where it fits the carrier, and at the exponent poles.</summary>
    /// <param name="left">The first operand vector; its leading lane is the base-field part.</param>
    /// <param name="right">The second operand vector; its leading lane is the root coefficient.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ExtensionPowExact(long[] left, long[] right) {
        for (var entry = 0; (entry < LeanExtensionPrimes.Length); ++entry) {
            var modulus = LeanExtensionPrimes[entry].Modulus;
            var nonSquare = LeanExtensionPrimes[entry].SmallestNonSquare;
            var extension = LeanExtensions[entry];
            var big = new BigInteger(value: modulus);
            var a = ExtensionResidue(
                raw: left[0],
                modulus: modulus
            );
            var b = ExtensionResidue(
                raw: right[0],
                modulus: modulus
            );
            var x = new QuadraticExtensionField64.Element(
                A: a,
                B: b
            );

            // The polynomial-reference ladder runs at the SMALLEST prime only; the large primes carry the two theorem
            // legs below, whose references are O(1).
            if (0 == entry) {
                var iterated = extension.One;

                foreach (var exponent in ExtensionExponentLadder) {
                    var (expectedA, expectedB) = Oracles.QuadraticExtensionPower(
                        a: a,
                        b: b,
                        exponent: exponent,
                        modulus: modulus,
                        nonSquare: nonSquare
                    );
                    var power = extension.Pow(
                        exponent: exponent,
                        value: x
                    );

                    if (
                        (power.A != expectedA) ||
                        (power.B != expectedB)
                    ) { return $"({a},{b})^{exponent} over F_{modulus}(√{nonSquare}) is ({power.A},{power.B}), the MSB-first oracle says ({expectedA},{expectedB})"; }
                }

                // The schedule leg: Pow against iterated Multiply at the low exponents.
                for (var exponent = 0; (exponent <= 8); ++exponent) {
                    if (extension.Pow(
                        exponent: ((ulong)exponent),
                        value: x
                    ) != iterated) { return $"({a},{b})^{exponent} over F_{modulus} disagrees with {exponent} iterated products"; }

                    iterated = extension.Multiply(
                        left: iterated,
                        right: x
                    );
                }
            }

            // The p-power map IS the conjugation, because d is a non-square and so d^((p − 1)/2) is −1.
            var conjugatePower = extension.Pow(
                exponent: modulus,
                value: x
            );
            var expectedConjugateB = ((ulong)((big - b) % big));

            if (
                (conjugatePower.A != a) ||
                (conjugatePower.B != expectedConjugateB)
            ) { return $"({a},{b})^{modulus} over F_{modulus}(√{nonSquare}) is ({conjugatePower.A},{conjugatePower.B}), the conjugate is ({a},{expectedConjugateB})"; }

            // x^(p+1) = x·x^p = x·conj(x) = N(x), lifted. It holds at the zero element too, so no exclusion is wanted.
            var (oracleNorm, _) = Oracles.QuadraticExtensionConjugateProduct(
                a: a,
                b: b,
                modulus: modulus,
                nonSquare: nonSquare
            );
            var normPower = extension.Pow(
                exponent: (modulus + 1UL),
                value: x
            );

            if (
                (normPower.A != oracleNorm) ||
                (0UL != normPower.B)
            ) { return $"({a},{b})^{(modulus + 1UL)} over F_{modulus} is ({normPower.A},{normPower.B}), the lifted norm is ({oracleNorm},0)"; }

            // The multiplicative group's order, where p² − 1 still fits the carrier.
            if (
                (modulus < (1UL << 32)) &&
                ((0UL != a) || (0UL != b))
            ) {
                var order = ((modulus * modulus) - 1UL);

                if (extension.Pow(
                    exponent: order,
                    value: x
                ) != extension.One) { return $"({a},{b})^{order} over F_{modulus} is not One"; }
            }

            if (extension.Pow(
                exponent: 0UL,
                value: x
            ) != extension.One) { return $"({a},{b})^0 over F_{modulus} is not One"; }
            if (extension.Pow(
                value: extension.Zero,
                exponent: 0UL
            ) != extension.One) { return $"Zero^0 over F_{modulus} is not One"; }
            if (extension.Pow(
                exponent: 1UL,
                value: x
            ) != x) { return $"({a},{b})^1 over F_{modulus} moved"; }
            if (extension.Pow(
                value: extension.Zero,
                exponent: 255UL
            ) != extension.Zero) { return $"Zero^255 over F_{modulus} is not Zero"; }
        }

        return null;
    }
    /// <summary>Proves batch inversion element by element against the extended Euclidean algorithm across both scratch
    /// arms, with the positional round trip, the single-element agreement with <c>Inverse</c>, the empty no-op and the
    /// ATOMIC refusal beside it.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ExtensionBatchInverseExact() {
        var rng = Pcg32XshRr.Create(
            state: 0xE47EUL,
            stream: 11UL
        );
        var values = new QuadraticExtensionField64.Element[600];
        var originals = new QuadraticExtensionField64.Element[600];

        for (var entry = 0; (entry < LeanExtensionPrimes.Length); ++entry) {
            var modulus = LeanExtensionPrimes[entry].Modulus;
            var nonSquare = LeanExtensionPrimes[entry].SmallestNonSquare;
            var extension = LeanExtensions[entry];
            var big = new BigInteger(value: modulus);

            foreach (var length in ExtensionBatchLengths) {
                for (var index = 0; (index < length); ++index) {
                    var a = NextField(
                        modulus: modulus,
                        rng: ref rng
                    );
                    var b = NextField(
                        modulus: modulus,
                        rng: ref rng
                    );

                    // Only the zero element has a vanishing norm in a genuine field; the refusal is pinned below.
                    if (
                        (0UL == a) &&
                        (0UL == b)
                    ) { a = 1UL; }

                    originals[index] = new QuadraticExtensionField64.Element(
                        A: a,
                        B: b
                    );
                    values[index] = originals[index];
                }

                extension.BatchInverse(values: values.AsSpan(
                    length: length,
                    start: 0
                ));

                for (var index = 0; (index < length); ++index) {
                    var original = originals[index];

                    var (oracleNorm, _) = Oracles.QuadraticExtensionConjugateProduct(
                        a: original.A,
                        b: original.B,
                        nonSquare: nonSquare,
                        modulus: modulus
                    );
                    var inverseNorm = Oracles.ModularInverse(
                        value: new BigInteger(value: oracleNorm),
                        modulus: big
                    );
                    var expectedA = ((ulong)((((BigInteger)original.A) * inverseNorm) % big));
                    var expectedB = ((ulong)((big - ((((BigInteger)original.B) * inverseNorm) % big)) % big));

                    if (
                        (values[index].A != expectedA) ||
                        (values[index].B != expectedB)
                    ) { return $"batch length {length} over F_{modulus}: element {index} inverted to ({values[index].A},{values[index].B}), the Euclid oracle says ({expectedA},{expectedB})"; }
                    if (extension.Multiply(
                        left: values[index],
                        right: original
                    ) != extension.One) { return $"batch length {length} over F_{modulus}: element {index} does not round-trip to One"; }
                }

                if (
                    (1 == length) &&
                    (values[0] != extension.Inverse(value: originals[0]))
                ) { return $"the single-element batch over F_{modulus} disagrees with Inverse"; }
            }

            // The empty span returns without throwing and without writing.
            var untouched = values[0];

            extension.BatchInverse(values: Span<QuadraticExtensionField64.Element>.Empty);

            if (values[0] != untouched) { return $"the empty batch over F_{modulus} wrote to the span"; }

            // The refusal is ATOMIC: nothing in the span moves. Every component is reduced, so the only zero-norm
            // element in the batch is the Zero the refusal is about.
            for (var index = 0; (index < 8); ++index) {
                values[index] = new QuadraticExtensionField64.Element(
                    A: (((ulong)(index + 1)) % modulus),
                    B: (((ulong)index) % modulus)
                );
            }

            values[5] = extension.Zero;

            for (var index = 0; (index < 8); ++index) { originals[index] = values[index]; }

            if (!Throws<DivideByZeroException>(action: () => extension.BatchInverse(values: values.AsSpan(
                length: 8,
                start: 0
            )))) { return $"a batch carrying Zero over F_{modulus} did not throw DivideByZeroException"; }

            for (var index = 0; (index < 8); ++index) {
                if (values[index] != originals[index]) { return $"the refused batch over F_{modulus} modified element {index}"; }
            }
        }

        return null;
    }
    /// <summary>Proves the smallest-non-square walk against Euler's criterion AND against the ladder's own declared
    /// table, the canonical factory against the validated one, <c>Create</c>'s whole accept/refuse decision against the
    /// character, the reduced-generator bound and the default-base-field refusal at every factory, the defining field
    /// invariant by exhaustion at the small primes, the default extension's uniform refusal, and the extension's record
    /// identity.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ExtensionConstructionAndRefusals() {
        for (var entry = 0; (entry < FullExtensionPrimes.Length); ++entry) {
            var modulus = FullExtensionPrimes[entry].Modulus;
            var declared = FullExtensionPrimes[entry].SmallestNonSquare;
            var big = new BigInteger(value: modulus);
            var field = PrimeField64.Create(modulus: modulus);
            var smallest = QuadraticExtensionField64.SmallestNonSquare(baseField: field);
            var oracleSmallest = ((ulong)Oracles.SmallestQuadraticNonResidue(
                budget: 1024,
                modulus: big
            ));

            if (smallest != oracleSmallest) { return $"the smallest non-square of F_{modulus} is {smallest}, the Euler-criterion oracle says {oracleSmallest}"; }
            if (smallest != declared) { return $"the smallest non-square of F_{modulus} is {smallest}, the declared ladder says {declared} — where the Euler-criterion oracle above agrees with the subject, correct the TABLE and not the subject"; }

            // Minimality, which the member's NAME claims and leg 1 alone would not reach: nothing below it is a
            // non-residue, it is one, and its own power is exactly p − 1 rather than merely not one.
            for (var candidate = 2UL; (candidate < smallest); ++candidate) {
                if (-1 == Oracles.PrimeFieldCharacter(
                    value: new BigInteger(value: candidate),
                    modulus: big
                )) { return $"{candidate} is a non-residue below the reported smallest {smallest} over F_{modulus}"; }
            }

            if (-1 != Oracles.PrimeFieldCharacter(
                value: new BigInteger(value: smallest),
                modulus: big
            )) { return $"the reported smallest non-square {smallest} over F_{modulus} is not a non-residue"; }
            if (BigInteger.ModPow(
                value: new BigInteger(value: smallest),
                exponent: ((big - BigInteger.One) / 2),
                modulus: big
            ) != (big - BigInteger.One)) { return $"the criterion power of {smallest} over F_{modulus} is neither 1 nor p − 1, so the modulus is not the prime the ladder declares"; }

            var canonical = QuadraticExtensionField64.CreateCanonical(baseField: field);

            if (canonical.NonSquare != declared) { return $"CreateCanonical over F_{modulus} stored the non-square {canonical.NonSquare}, expected {declared}"; }
            if (canonical.BaseField != field) { return $"CreateCanonical over F_{modulus} did not carry its base field"; }
            if (canonical != QuadraticExtensionField64.Create(
                baseField: field,
                nonSquare: declared
            )) { return $"CreateCanonical over F_{modulus} is not Create over the smallest non-square"; }

            // ToString prints the descriptor's two data and nothing else: the identities are constants of the TYPE,
            // not carried state, so a hand-written PrintMembers keeps them out of the rendering.
            if (canonical.ToString() != $"QuadraticExtensionField64 {{ BaseField = PrimeField64 {{ Modulus = {modulus} }}, NonSquare = {declared} }}") { return $"the extension over F_{modulus} prints as {canonical}"; }

            // The literal zero is refused everywhere — the arm LegendreCharacter's own zero short-circuit is the whole of.
            if (!Throws<ArgumentException>(
                action: () => _ = QuadraticExtensionField64.Create(
                    baseField: field,
                    nonSquare: 0UL
                ),
                paramName: "nonSquare"
            )) { return $"Create(F_{modulus}, 0) did not refuse with ArgumentException naming nonSquare"; }

            // Accept/refuse against the character: exhaustive below the ceiling, a square ladder above it.
            if (modulus <= ExtensionExhaustiveCeiling) {
                for (var candidate = 1UL; (candidate < modulus); ++candidate) {
                    if (ExtensionCreateDisagrees(
                        big: big,
                        candidate: candidate,
                        field: field,
                        modulus: modulus
                    ) is { } detail) { return detail; }
                }
            } else {
                for (var seed = 2UL; (seed <= 8UL); ++seed) {
                    if (ExtensionCreateDisagrees(
                        big: big,
                        candidate: ((seed * seed) % modulus),
                        field: field,
                        modulus: modulus
                    ) is { } squareDetail) { return squareDetail; }
                }

                if (ExtensionCreateDisagrees(
                    big: big,
                    candidate: declared,
                    field: field,
                    modulus: modulus
                ) is { } nonSquareDetail) { return nonSquareDetail; }
            }

            // The reduced-generator bound, enforced BEFORE the character is ever consulted. The modulus itself is the
            // row that matters: p folds to zero in the residue ring the character exponentiates in, the resulting zero
            // power reads as a non-square, and admitting it would build F_p[t]/(t²) — the dual numbers, whose non-zero
            // surd has a vanishing norm — behind a type that promises a field.
            foreach (var unreduced in ((ReadOnlySpan<ulong>)[modulus, (2UL * modulus), (modulus + declared), ulong.MaxValue])) {
                if (!Throws<ArgumentOutOfRangeException>(
                    action: () => _ = QuadraticExtensionField64.Create(
                        baseField: field,
                        nonSquare: unreduced
                    ),
                    paramName: "nonSquare"
                )) {
                    return $"Create(F_{modulus}, {unreduced}) admitted an unreduced generator, or refused naming {RefusedParameter(action: () => _ = QuadraticExtensionField64.Create(
                    baseField: field,
                    nonSquare: unreduced
                ))}";
                }
            }

            // The invariant the bound exists to protect, stated exhaustively where that is affordable: over EVERY
            // generator Create accepts, every non-zero element of F_{p²} has a non-zero norm and a genuine inverse.
            if (
                (modulus <= ExtensionInvariantCeiling) &&
                (ExtensionFieldInvariant(
                field: field,
                modulus: modulus
            ) is { } invariantDetail)
            ) { return invariantDetail; }
        }

        // Every factory names the DESCRIPTOR it was handed when that descriptor names no field, rather than blaming the
        // generator or failing later on a zero modulus.
        var unbound = default(PrimeField64);

        if (!Throws<ArgumentException>(
            action: () => _ = QuadraticExtensionField64.Create(
                baseField: unbound,
                nonSquare: 3UL
            ),
            paramName: "baseField"
        )) { return "Create over a default base field did not refuse naming baseField"; }
        if (!Throws<ArgumentException>(
            action: () => _ = QuadraticExtensionField64.CreateCanonical(baseField: unbound),
            paramName: "baseField"
        )) { return "CreateCanonical over a default base field did not refuse naming baseField"; }
        if (!Throws<ArgumentException>(
            action: () => _ = QuadraticExtensionField64.SmallestNonSquare(baseField: unbound),
            paramName: "baseField"
        )) { return "SmallestNonSquare over a default base field did not refuse naming baseField"; }

        // The default EXTENSION is its own descriptor rather than a wrapper the base field's guard happens to cover, so
        // it is stated separately and over its whole semantic surface.
        var emptyExtension = default(QuadraticExtensionField64);
        var element = new QuadraticExtensionField64.Element(
            A: 1UL,
            B: 1UL
        );
        var elements = new QuadraticExtensionField64.Element[3];

        if (emptyExtension.NonSquare != 0UL) { return $"the default extension reports non-square {emptyExtension.NonSquare}"; }
        if (emptyExtension.BaseField != unbound) { return "the default extension does not carry a default base field"; }

        // The PRINTABILITY half of the promise, stated on ToString itself: a default value formats as its raw state —
        // the nested default base field included — rather than throwing from a guarded identity the synthesized member
        // walk would have read, and misattributing the failure to the PRIME field.
        if (emptyExtension.ToString() != "QuadraticExtensionField64 { BaseField = PrimeField64 { Modulus = 0 }, NonSquare = 0 }") { return $"the default extension prints as {emptyExtension}"; }

        (string Name, Action Call)[] extensionOperations = [
            ("One", () => _ = emptyExtension.One),
            ("Zero", () => _ = emptyExtension.Zero),
            ("Add", () => _ = emptyExtension.Add(
                left: element,
                right: element
            )),
            ("Subtract", () => _ = emptyExtension.Subtract(
                left: element,
                right: element
            )),
            ("Negate", () => _ = emptyExtension.Negate(value: element)),
            ("Multiply", () => _ = emptyExtension.Multiply(
                left: element,
                right: element
            )),
            ("Inverse", () => _ = emptyExtension.Inverse(value: element)),
            ("Pow", () => _ = emptyExtension.Pow(
                exponent: 3UL,
                value: element
            )),
            ("Norm", () => _ = emptyExtension.Norm(value: element)),
            ("Trace", () => _ = emptyExtension.Trace(value: element)),
            ("Frobenius", () => _ = emptyExtension.Frobenius(value: element)),
            ("FromBase", () => _ = emptyExtension.FromBase(value: 1UL)),
            ("BatchInverse of an empty span", () => emptyExtension.BatchInverse(values: Span<QuadraticExtensionField64.Element>.Empty)),
            ("BatchInverse of three", () => emptyExtension.BatchInverse(values: elements.AsSpan())),
        ];

        foreach (var (name, call) in extensionOperations) {
            if (!Throws<InvalidOperationException>(action: call)) { return $"the default extension answered {name} instead of refusing"; }
        }

        var extensions = new QuadraticExtensionField64[2];

        if (extensions[0] != emptyExtension) { return "an unassigned extension array element does not equal the default"; }
        if (!Throws<InvalidOperationException>(action: () => _ = extensions[1].Multiply(
            left: element,
            right: element
        ))) { return "an unassigned extension array element answered Multiply"; }

        // Identity: the base field and the non-square TOGETHER. The quadratic residues modulo seven are {1, 2, 4}, so
        // five is a second non-square beside three and Create admits both. The shared generator has to be a non-square
        // over BOTH primes: the residues modulo eleven are {1, 3, 4, 5, 9} — three among them — and six is the least
        // value outside both residue sets.
        var seven = PrimeField64.Create(modulus: 7UL);
        var eleven = PrimeField64.Create(modulus: 11UL);

        if (QuadraticExtensionField64.Create(
            baseField: seven,
            nonSquare: 3UL
        ) != QuadraticExtensionField64.Create(
            baseField: seven,
            nonSquare: 3UL
        )) { return "two extensions over the same field and non-square are unequal"; }
        if (QuadraticExtensionField64.Create(
            baseField: seven,
            nonSquare: 3UL
        ) == QuadraticExtensionField64.Create(
            baseField: seven,
            nonSquare: 5UL
        )) { return "two extensions over F_7 with different non-squares are equal"; }
        if (QuadraticExtensionField64.Create(
            baseField: seven,
            nonSquare: 6UL
        ) == QuadraticExtensionField64.Create(
            baseField: eleven,
            nonSquare: 6UL
        )) { return "extensions over F_7 and F_11 with the same non-square are equal"; }

        return null;
    }

    // The ceiling the field invariant is stated exhaustively below. The sweep is quadratic in the modulus and runs once
    // per accepted generator, so 13 keeps the whole statement in the low thousands of inversions while still crossing
    // both residue classes of p modulo four.
    private const ulong ExtensionInvariantCeiling = 13UL;

    // The DEFINING field invariant, over the primes small enough to state it by exhaustion: for every generator Create
    // accepts, every non-zero element of F_{p²} has a non-zero norm and a genuine inverse, and only the zero element
    // has a vanishing norm. This is exactly the statement the dual-numbers quotient fails — its non-zero surd (0, 1)
    // has norm zero — so it is what enforcing the reduced-generator bound is worth.
    private static string? ExtensionFieldInvariant(PrimeField64 field, ulong modulus) {
        for (var candidate = 1UL; (candidate < modulus); ++candidate) {
            QuadraticExtensionField64 extension;

            try {
                extension = QuadraticExtensionField64.Create(
                    baseField: field,
                    nonSquare: candidate
                );
            } catch (ArgumentException) {
                continue;
            }

            var one = extension.One;

            for (var a = 0UL; (a < modulus); ++a) {
                for (var b = 0UL; (b < modulus); ++b) {
                    var element = new QuadraticExtensionField64.Element(
                        A: a,
                        B: b
                    );
                    var norm = extension.Norm(value: element);

                    if (
                        (0UL == a) &&
                        (0UL == b)
                    ) {
                        if (0UL != norm) { return $"the zero of F_{modulus}² at d = {candidate} has norm {norm}"; }

                        continue;
                    }

                    if (0UL == norm) { return $"the non-zero element ({a}, {b}) of F_{modulus}² at d = {candidate} has a vanishing norm, so the quotient is not a field"; }

                    var inverse = extension.Inverse(value: element);

                    if (extension.Multiply(
                        left: element,
                        right: inverse
                    ) != one) { return $"the inverse of ({a}, {b}) over F_{modulus} at d = {candidate} does not multiply back to one"; }
                }
            }
        }

        return null;
    }
    // One candidate's accept/refuse decision against the character oracle. Extracted so the exhaustive arm and the
    // ladder arm make the IDENTICAL statement rather than two nearly identical ones.
    private static string? ExtensionCreateDisagrees(PrimeField64 field, ulong modulus, BigInteger big, ulong candidate) {
        var character = Oracles.PrimeFieldCharacter(
            value: new BigInteger(value: candidate),
            modulus: big
        );

        try {
            var extension = QuadraticExtensionField64.Create(
                baseField: field,
                nonSquare: candidate
            );

            if (-1 != character) { return $"Create(F_{modulus}, {candidate}) succeeded at character {character}"; }
            if (extension.NonSquare != candidate) { return $"Create(F_{modulus}, {candidate}) stored {extension.NonSquare}"; }
            if (extension.BaseField != field) { return $"Create(F_{modulus}, {candidate}) did not carry its base field"; }
        } catch (ArgumentException exception) {
            if (-1 == character) { return $"Create(F_{modulus}, {candidate}) refused a genuine non-square"; }
            if ("nonSquare" != exception.ParamName) { return $"Create(F_{modulus}, {candidate}) refused naming {exception.ParamName}"; }
        }

        return null;
    }

    /// <summary>The subject extension product at one lean ladder entry, sampled raws in and reduced components
    /// out.</summary>
    /// <param name="entry">The lean ladder index.</param>
    /// <returns>The binary element operation.</returns>
    public static BinaryElemOp ExtensionProduct(int entry) =>
        (u1, v1, u2, v2) => {
            var modulus = LeanExtensionPrimes[entry].Modulus;
            var product = LeanExtensions[entry].Multiply(
                left: new QuadraticExtensionField64.Element(
                    A: ExtensionResidue(
                        modulus: modulus,
                        raw: u1
                    ),
                    B: ExtensionResidue(
                        modulus: modulus,
                        raw: v1
                    )
                ),
                right: new QuadraticExtensionField64.Element(
                    A: ExtensionResidue(
                        modulus: modulus,
                        raw: u2
                    ),
                    B: ExtensionResidue(
                        modulus: modulus,
                        raw: v2
                    )
                )
            );

            return (((long)product.A), ((long)product.B));
        };
    /// <summary>The reference for <see cref="ExtensionProduct(int)"/> — schoolbook polynomial multiplication modulo
    /// <c>t² − d</c>, with no closed pair formula anywhere.</summary>
    /// <param name="entry">The lean ladder index.</param>
    /// <returns>The binary element operation.</returns>
    public static BinaryElemOp ExtensionProductOracle(int entry) =>
        (u1, v1, u2, v2) => {
            var modulus = LeanExtensionPrimes[entry].Modulus;
            var nonSquare = LeanExtensionPrimes[entry].SmallestNonSquare;
            Span<ulong> left = stackalloc ulong[2];
            Span<ulong> right = stackalloc ulong[2];
            Span<ulong> tail = stackalloc ulong[2];
            Span<ulong> result = stackalloc ulong[2];

            left[0] = ExtensionResidue(
                modulus: modulus,
                raw: u1
            );
            left[1] = ExtensionResidue(
                modulus: modulus,
                raw: v1
            );
            right[0] = ExtensionResidue(
                modulus: modulus,
                raw: u2
            );
            right[1] = ExtensionResidue(
                modulus: modulus,
                raw: v2
            );
            tail[0] = ((modulus - nonSquare) % modulus);
            tail[1] = 0UL;

            Oracles.PrimeFieldPolynomialProduct(
                left: left,
                modulus: modulus,
                result: result,
                right: right,
                tail: tail
            );

            return (((long)result[0]), ((long)result[1]));
        };

}
