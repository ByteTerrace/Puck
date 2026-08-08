using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// The prime wing's SCALE statements — the Montgomery chains, Baillie-PSW, Jacobi symbol and full-width factorization
/// sweeps. What sets them apart from the wing's Default cases is volume: exhaustive sweeps and contiguous bands rather
/// than sampled streams, so they sit at the <c>Exhaustive</c> tier where minutes are the budget rather than the
/// exception.
/// </summary>
/// <remarks>
/// <para>
/// The oracles here are deliberately not the nearest sibling. The composite-modulus Jacobi regime stands against
/// <see cref="JacobiSymbolByFactorAndEuler(BigInteger, ulong)"/>, the symbol's DEFINITION: factor the odd modulus and
/// multiply the Legendre symbols of its prime powers, each decided by Euler's criterion. It reaches no reciprocity
/// step at all — where <c>NumberTheoryFunctions.JacobiSymbol</c> would have shared both the reciprocity descent and
/// <c>BinaryIntegerFunctions.FloorModulo</c> with the subject. The exhaustive composition sweep below <c>2^32</c>
/// likewise runs against a SEGMENTED SIEVE OF ERATOSTHENES written in this file rather than a second Puck.Maths
/// primality kernel: crossed-out multiples and surviving flags, sharing with the subject not merely no code but no
/// idea.
/// </para>
/// <para>
/// Every operand stream is produced by <see cref="NextRandom(ref ulong)"/>, a SplitMix64 written here from published
/// constants. No sweep borrows a Puck.Maths generator to make the values it then judges.
/// </para>
/// </remarks>
internal static class PrimalityScaleClaims {
    /// <summary>The four values that make <see cref="PrimeField64.IsPrime(ulong)"/>'s TWELFTH witness base
    /// load-bearing: each is the least value that is a strong probable prime to every one of the first <c>k</c> prime
    /// bases, so a witness set truncated to <c>k</c> accepts it. The base count on each row is the number of leading
    /// bases the value actually survives, measured, not the psi index it is tabulated under.</summary>
    private static readonly (ulong Value, int Bases)[] WitnessPseudoprimes = [
        (3_215_031_751UL, 4),
        (3_474_749_660_383UL, 6),
        (341_550_071_728_321UL, 8),
        (3_825_123_056_546_413_051UL, 11),
    ];

    /// <summary>The complete population of base-two strong pseudoprimes below <c>10^5</c> — OEIS A001262 — transcribed
    /// from the literature and derived nowhere in this repository.</summary>
    private static readonly ulong[] BaseTwoPseudoprimes = [
        2_047UL, 3_277UL, 4_033UL, 4_681UL, 8_321UL, 15_841UL, 29_341UL, 42_799UL,
        49_141UL, 52_633UL, 65_281UL, 74_665UL, 80_581UL, 85_489UL, 88_357UL, 90_751UL,
    ];

    /// <summary>The complete population of strong Lucas pseudoprimes below <c>10^5</c> for the Selfridge Method A
    /// parameters — OEIS A217255, Baillie and Wagstaff (1980) — which is exactly the parameter choice
    /// <see cref="PrimeField64.IsStrongLucasProbablePrime(ulong)"/> runs.</summary>
    private static readonly ulong[] LucasPseudoprimes = [
        5_459UL, 5_777UL, 10_877UL, 16_109UL, 18_971UL, 22_499UL, 24_569UL,
        25_199UL, 40_309UL, 58_519UL, 75_077UL, 97_439UL,
    ];

    /// <summary>The published Carmichael numbers: absolute pseudoprimes, passing a Fermat test to every coprime
    /// base.</summary>
    private static readonly ulong[] CarmichaelNumbers = [561UL, 1_105UL, 1_729UL, 2_465UL, 2_821UL, 6_601UL, 8_911UL,];

    /// <summary>
    /// The <c>montgomery chains</c> section: <see cref="PrimeField64"/>'s Montgomery-form multiplication chains against
    /// oracles that share none of their arithmetic, and its primality verdict swept EXHAUSTIVELY over <c>0..10^6</c>
    /// and over a contiguous 100,000-value band at <c>9·10^11</c>.
    /// </summary>
    /// <returns><see langword="null"/> when every statement holds; the counterexample otherwise.</returns>
    internal static string? MontgomeryChainsSurface() {
        var state = 0x4D6F6E74_676F6D65UL;
        var moduli = new List<ulong> {
            3UL, 5UL, 7UL, 11UL, 13UL, 17UL, 97UL, 65_537UL, 2_147_483_647UL,
            FirstPrimeAtOrAbove(value: (1UL << 20)), FirstPrimeAtOrAbove(value: (1UL << 31)),
            FirstPrimeAtOrAbove(value: (1UL << 32)), FirstPrimeAtOrAbove(value: (1UL << 33)),
            FirstPrimeAtOrAbove(value: (1UL << 47)), FirstPrimeAtOrAbove(value: (1UL << 61)),
            LastPrimeBelow(value: (PrimeField64.MaximumModulus - (1UL << 32))),
            LastPrimeBelow(value: PrimeField64.MaximumModulus),
        };

        // Moduli spanning the whole legal range: the smallest odd primes, the widening multiply's 2^32 seam, and the
        // 2^62 ceiling from both sides, filled out to thirty-two by a deterministic draw from the upper bands.
        while (32 > moduli.Count) {
            moduli.Add(item: FirstPrimeAtOrAbove(value: ((1UL << 40) + (NextRandom(state: ref state) % ((1UL << 62) - (1UL << 41))))));
        }

        var fields = new PrimeField64[moduli.Count];

        for (var index = 0; (index < fields.Length); ++index) {
            var modulus = moduli[index];

            // The moduli are located with an oracle rather than with the subject, so nothing here is a field modulus on
            // the implementation's own word.
            if (!Oracles.ExactPrimality(value: modulus)) {
                return $"the modulus ladder carries {modulus}, which the exact decision says is composite";
            }
            if (modulus >= PrimeField64.MaximumModulus) {
                return $"the modulus ladder carries {modulus}, at or above the field ceiling {PrimeField64.MaximumModulus}";
            }

            fields[index] = PrimeField64.Create(modulus: modulus);
        }

        for (var index = 0; (index < fields.Length); ++index) {
            var failure = MontgomeryChainAtModulus(field: fields[index], state: ref state);

            if (failure is not null) { return failure; }
        }

        // Randomized agreement over the same modulus spread, with full-width exponents so the chain length varies.
        for (var trial = 0; (trial < 20_000); ++trial) {
            var field = fields[((int)(NextRandom(state: ref state) % ((ulong)fields.Length)))];
            var modulus = field.Modulus;
            var value = (NextRandom(state: ref state) % modulus);
            var exponent = NextRandom(state: ref state);
            var expected = ((ulong)BigInteger.ModPow(
                value: new BigInteger(value: value),
                exponent: new BigInteger(value: exponent),
                modulus: new BigInteger(value: modulus)
            ));

            if (field.Pow(value: value, exponent: exponent) != expected) {
                return $"Pow({value}, {exponent}) mod {modulus} = {field.Pow(value: value, exponent: exponent)} where BigInteger.ModPow says {expected}";
            }
        }

        return MontgomeryPrimalitySurface(state: ref state);
    }

    /// <summary>
    /// The <c>baillie-psw</c> section: the composition against the EXACT decision at every one of the 4,294,967,296
    /// values a <see cref="uint"/> holds, then 160,000 sampled and 60,000 contiguous values up to
    /// <see cref="ulong.MaxValue"/>, each half against an oracle sharing none of its code, and both published
    /// pseudoprime populations enumerated rather than trusted.
    /// </summary>
    /// <returns><see langword="null"/> when every statement holds; the counterexample otherwise.</returns>
    internal static string? BailliePswSurface() {
        var failure = BaillieExhaustiveThirtyTwoBit();

        if (failure is not null) { return failure; }

        var state = 0x4261696C_6C696550UL;

        failure = BaillieHalvesSurface(state: ref state);

        if (failure is not null) { return failure; }

        // Past 32 bits the reference is the exact decision outside Puck.Maths entirely. The bands straddle the widening
        // seam, the field's 2^62 ceiling and the top of the carrier, where the reduction's quotient needs its
        // sixty-fifth bit; the contiguous runs are what a verdict wrong for one residue class cannot hide from.
        int[] bandShifts = [32, 33, 40, 52, 61, 62, 63, 64];

        foreach (var shift in bandShifts) {
            var span = ((64 == shift) ? ulong.MaxValue : ((1UL << shift) - 1UL));

            for (var trial = 0; (trial < 20_000); ++trial) {
                var candidate = ((NextRandom(state: ref state) % span) | 1UL);

                failure = BaillieAgreesWithExactDecision(candidate: candidate);

                if (failure is not null) { return failure; }
            }
        }

        const ulong denseLength = 20_000UL;
        ulong[] denseStarts = [
            ((ulong.MaxValue - denseLength) + 1UL),
            (PrimeField64.MaximumModulus - (denseLength >>> 1)),
            ((1UL << 32) - (denseLength >>> 1)),
        ];

        foreach (var start in denseStarts) {
            for (var offset = 0UL; (offset < denseLength); ++offset) {
                failure = BaillieAgreesWithExactDecision(candidate: (start + offset));

                if (failure is not null) { return failure; }
            }
        }

        return BailliePopulationSurface();
    }

    /// <summary>
    /// The <c>jacobi-symbol</c> section: the CROSS-CARRIER agreement — one answer from
    /// <see cref="UnsignedNumberFunctions.JacobiSymbol{T}(T, T)"/> at <see cref="uint"/>, <see cref="ulong"/> and
    /// <see cref="UInt128"/> and from <see cref="NumberTheoryFunctions.JacobiSymbol(BigInteger, BigInteger)"/> — and
    /// the COMPOSITE-MODULUS regime, where the reference is the symbol's definition rather than another reciprocity
    /// descent.
    /// </summary>
    /// <returns><see langword="null"/> when every statement holds; the counterexample otherwise.</returns>
    internal static string? JacobiSymbolSurface() {
        // Exhaustive because a reciprocity sign rule that is wrong for ONE residue class is invisible to a sampled
        // sweep. Every odd modulus through 401 against every numerator through 400, on all four carriers at once.
        for (var modulus = 1U; (401U >= modulus); modulus += 2U) {
            var wideModulus = ((BigInteger)modulus);

            for (var numerator = 0U; (400U >= numerator); ++numerator) {
                var wideNumerator = ((BigInteger)numerator);
                var expected = Oracles.JacobiSymbolReciprocity(numerator: wideNumerator, denominator: wideModulus);
                // The DEFINITION, standing outside the descent both shipped spellings walk: the product of the Legendre
                // symbols of the modulus's prime powers, each by Euler's criterion. Without it the composite-modulus
                // regime would have no reference but the library's own sibling.
                var defined = JacobiSymbolByFactorAndEuler(numerator: wideNumerator, oddModulus: modulus);

                if (defined != expected) {
                    return $"the reciprocity oracle and the factor-and-Euler definition disagree at ({numerator}/{modulus}): {expected} against {defined}";
                }

                // Each carrier is read into its own local so a failure names the width that diverged rather than
                // reporting the narrowest one for every fault.
                var narrow = numerator.JacobiSymbol(modulus: modulus);
                var wide = ((ulong)numerator).JacobiSymbol(modulus: ((ulong)modulus));
                var widest = ((UInt128)numerator).JacobiSymbol(modulus: ((UInt128)modulus));
                var arbitrary = NumberTheoryFunctions.JacobiSymbol(numerator: wideNumerator, denominator: wideModulus);

                if ((narrow != expected) || (wide != expected) || (widest != expected) || (arbitrary != expected)) {
                    return $"({numerator}/{modulus}) is {expected} and the carriers report uint={narrow} ulong={wide} uint128={widest} bigint={arbitrary}";
                }
                // The symbol vanishes exactly on the shared-factor case.
                if ((0 == narrow) != (1U != numerator.GreatestCommonDivisor(other: modulus))) {
                    return $"({numerator}/{modulus}) = {narrow} does not vanish exactly where the two share a factor";
                }
                if ((1U == modulus) && (1 != narrow)) {
                    return $"({numerator}/1) = {narrow} rather than the empty product 1";
                }
                if ((0U == numerator) && (1U != modulus) && (0 != narrow)) {
                    return $"(0/{modulus}) = {narrow} rather than 0";
                }
            }
        }

        return JacobiWideSurface();
    }

    /// <summary>
    /// The full-width FACTORIZATION sweep — the coverage
    /// <c>core.big-integer-prime-factors-vs-word-kernel</c> cites its word-sized reference on. Both shipped word
    /// kernels are swept across the whole <see cref="uint"/> and <see cref="ulong"/> carriers, and every reported
    /// factorization is checked by reassembly, ordering and per-factor primality against an oracle.
    /// </summary>
    /// <returns><see langword="null"/> when every statement holds; the counterexample otherwise.</returns>
    internal static string? FactorizationFullWidthSurface() {
        var state = 0x466163_746F7221UL;
        var trialPrimes = PrimesBelow(exclusiveMaximum: 65_536);
        var destination = new uint[32];

        // The uint carrier, at full width. Trial division by the primes below 65536 decides EVERY uint factor outright,
        // so the per-factor primality statement here carries no probable-prime reasoning at all.
        for (var trial = 0; (trial < 200_000); ++trial) {
            var value = ((uint)NextRandom(state: ref state));
            var failure = NarrowFactorizationHolds(value: value, destination: destination, trialPrimes: trialPrimes);

            if (failure is not null) { return failure; }
        }

        // A contiguous run, where a defect confined to one residue class cannot hide the way it can in a random stream.
        for (var offset = 0U; (200_000U > offset); ++offset) {
            var failure = NarrowFactorizationHolds(value: ((uint.MaxValue - 200_000U) + offset), destination: destination, trialPrimes: trialPrimes);

            if (failure is not null) { return failure; }
        }

        return WideFactorizationSurface(state: ref state);
    }

    // ---- montgomery chains ----

    private static string? MontgomeryChainAtModulus(PrimeField64 field, ref ulong state) {
        var modulus = field.Modulus;
        var wideModulus = new BigInteger(value: modulus);
        // Zero, one, both ends of the range and the halfway split, plus a deterministic sample.
        var residues = new List<ulong> { 0UL, 1UL, 2UL, (modulus >>> 1), (modulus - 2UL), (modulus - 1UL), };
        // Zero and one, the two Fermat/Euler exponents, both carrier-edge exponents, and a sample.
        ulong[] exponents = [
            0UL, 1UL, 2UL, 3UL, ((modulus - 1UL) >>> 1), (modulus - 2UL), (modulus - 1UL),
            modulus, (ulong.MaxValue - 1UL), ulong.MaxValue, NextRandom(state: ref state),
        ];

        for (var trial = 0; (trial < 6); ++trial) {
            residues.Add(item: (NextRandom(state: ref state) % modulus));
        }

        foreach (var residue in residues) {
            var wideResidue = new BigInteger(value: residue);

            foreach (var exponent in exponents) {
                var expected = ((ulong)BigInteger.ModPow(value: wideResidue, exponent: new BigInteger(value: exponent), modulus: wideModulus));
                var actual = field.Pow(value: residue, exponent: exponent);

                if (actual != expected) {
                    return $"Pow({residue}, {exponent}) mod {modulus} = {actual} where BigInteger.ModPow says {expected}";
                }
            }

            // The one-shot product deliberately stayed on the divide, so it needs an oracle of its own rather than the
            // chain's.
            foreach (var other in residues) {
                var expected = ((ulong)((wideResidue * other) % wideModulus));
                var actual = field.Multiply(left: residue, right: other);

                if (actual != expected) {
                    return $"Multiply({residue}, {other}) mod {modulus} = {actual} where the exact product says {expected}";
                }
            }

            // The quadratic character by RECIPROCITY, which never exponentiates, against Euler's criterion, which is
            // nothing but exponentiation. Two derivations, no shared step.
            var character = Oracles.JacobiSymbolReciprocity(numerator: wideResidue, denominator: wideModulus);

            if (field.LegendreCharacter(value: residue) != character) {
                return $"LegendreCharacter({residue}) mod {modulus} = {field.LegendreCharacter(value: residue)} where the reciprocity descent says {character}";
            }

            var square = field.Multiply(left: residue, right: residue);

            if (!field.TrySqrt(value: square, out var squareRoot) || (field.Multiply(left: squareRoot, right: squareRoot) != square)) {
                return $"the square {square} of {residue} mod {modulus} did not root back to itself";
            }
            if (field.TrySqrt(value: residue, out var residueRoot) != (-1 != character)) {
                return $"TrySqrt({residue}) mod {modulus} disagrees with the character {character}";
            }
            if ((-1 != character) && (field.Multiply(left: residueRoot, right: residueRoot) != residue)) {
                return $"TrySqrt({residue}) mod {modulus} returned {residueRoot}, whose square is not {residue}";
            }
            if (0UL != residue) {
                var inverse = field.Inverse(value: residue);
                var expected = ((ulong)BigInteger.ModPow(value: wideResidue, exponent: new BigInteger(value: (modulus - 2UL)), modulus: wideModulus));

                if ((inverse != expected) || (1UL != field.Multiply(left: residue, right: inverse))) {
                    return $"Inverse({residue}) mod {modulus} = {inverse} where the Fermat exponent says {expected}";
                }
            }
        }

        // BatchInverse turns a whole region over through one Inverse, which rides the same chain.
        var batch = new ulong[37];
        var batchExpected = new ulong[batch.Length];

        for (var index = 0; (index < batch.Length); ++index) {
            var element = (1UL + (NextRandom(state: ref state) % (modulus - 1UL)));

            batch[index] = element;
            batchExpected[index] = ((ulong)BigInteger.ModPow(value: new BigInteger(value: element), exponent: new BigInteger(value: (modulus - 2UL)), modulus: wideModulus));
        }

        field.BatchInverse(values: batch);

        for (var index = 0; (index < batch.Length); ++index) {
            if (batch[index] != batchExpected[index]) {
                return $"BatchInverse mod {modulus} wrote {batch[index]} at index {index} where the Fermat exponent says {batchExpected[index]}";
            }
        }

        return null;
    }

    private static string? MontgomeryPrimalitySurface(ref ulong state) {
        // Every value below a million against the dense sieve: the verdicts are mathematical truth, not another test's
        // opinion.
        const int denseLimit = 1_000_000;
        var denseSieve = Oracles.PrimeSieve(inclusiveMaximum: denseLimit);

        for (var value = 0; (value <= denseLimit); ++value) {
            if (PrimeField64.IsPrime(value: ((ulong)value)) != denseSieve[value]) {
                return $"IsPrime({value}) = {PrimeField64.IsPrime(value: ((ulong)value))} where the sieve of Eratosthenes says {denseSieve[value]}";
            }
        }

        // A contiguous band an order of magnitude past where 32-bit primality reaches, SIEVED exactly rather than
        // sampled.
        const ulong windowStart = 900_000_000_001UL;
        const int windowLength = 100_000;
        const int windowTrialLimit = 1_000_000;

        var windowPrimes = PrimesBelow(exclusiveMaximum: windowTrialLimit);
        // Sieving is exact only while the window stays below the square of the smallest prime the list OMITS, so the
        // bound is read off the generated list rather than restated as a literal beside it.
        var omitted = FirstPrimeAtOrAbove(value: ((ulong)windowTrialLimit));

        if ((omitted * omitted) <= (windowStart + windowLength)) {
            return $"the 9e11 window reaches {(windowStart + windowLength)}, at or above {(omitted * omitted)}, so the trial-prime list no longer sieves it exactly";
        }

        var window = SieveWindow(start: windowStart, length: windowLength, trialPrimes: windowPrimes);

        for (var offset = 0; (offset < windowLength); ++offset) {
            var candidate = (windowStart + ((ulong)offset));

            if (PrimeField64.IsPrime(value: candidate) != window[offset]) {
                return $"IsPrime({candidate}) = {PrimeField64.IsPrime(value: candidate)} where the segmented sieve says {window[offset]}";
            }
        }

        // Past 10^12 no sieve reaches, so the reference becomes the exact decision: trial division then twenty
        // BigInteger strong rounds, a strict superset of the subject's twelve Montgomery ones.
        int[] bandShifts = [32, 33, 40, 52, 61, 62, 63, 64];

        foreach (var shift in bandShifts) {
            var span = ((64 == shift) ? ulong.MaxValue : ((1UL << shift) - 1UL));

            for (var trial = 0; (trial < 400); ++trial) {
                var candidate = ((NextRandom(state: ref state) % span) | 1UL);

                if (PrimeField64.IsPrime(value: candidate) != Oracles.ExactPrimality(value: candidate)) {
                    return $"IsPrime({candidate}) = {PrimeField64.IsPrime(value: candidate)} where the exact decision says {Oracles.ExactPrimality(value: candidate)}";
                }
            }
        }

        // A dense run at the very top of the carrier, where every reduction rides the wrapping-quotient path.
        for (var offset = 0; (offset < 4_000); ++offset) {
            var candidate = (ulong.MaxValue - (((ulong)offset) << 1));

            if (PrimeField64.IsPrime(value: candidate) != Oracles.ExactPrimality(value: candidate)) {
                return $"IsPrime({candidate}) = {PrimeField64.IsPrime(value: candidate)} where the exact decision says {Oracles.ExactPrimality(value: candidate)}";
            }
        }

        // Semiprimes of two 32-bit primes: composite by construction. One-sided by nature — it can catch a false accept
        // and never a false reject — so no oracle is needed, because the answer is always no.
        var pool = new ulong[512];

        for (var index = 0; (index < pool.Length); ++index) {
            pool[index] = FirstPrimeAtOrAbove(value: (2_000_000_000UL + (NextRandom(state: ref state) % 100_000_000UL)));
        }

        for (var trial = 0; (trial < 5_000); ++trial) {
            var left = pool[((int)(NextRandom(state: ref state) % ((ulong)pool.Length)))];
            var right = pool[((int)(NextRandom(state: ref state) % ((ulong)pool.Length)))];

            if (PrimeField64.IsPrime(value: (left * right))) {
                return $"IsPrime accepted the semiprime {left} x {right}";
            }
        }

        // The witness set itself, pinned base by base: truncating the twelve to k accepts psi_k and nothing else here
        // notices.
        foreach (var (pseudoprime, baseCount) in WitnessPseudoprimes) {
            if (PrimeField64.IsPrime(value: pseudoprime)) {
                return $"IsPrime accepted {pseudoprime}, a strong probable prime to the first {baseCount} bases: the witness set is short";
            }
            if (Oracles.ExactPrimality(value: pseudoprime)) {
                return $"the exact decision accepted the tabulated pseudoprime {pseudoprime}";
            }
        }

        // The largest prime the carrier holds, located by the implementation and confirmed by the oracle.
        var top = (ulong.MaxValue - 2UL);
        var budget = 800;

        while (!PrimeField64.IsPrime(value: top)) {
            if (0 == --budget) {
                return "the carrier-top prime search exhausted its 800-step budget: IsPrime never returned true below 2^64";
            }

            top -= 2UL;
        }

        return (Oracles.ExactPrimality(value: top)
            ? null
            : $"IsPrime named {top} the carrier's top prime and the exact decision rejects it");
    }

    // ---- baillie-psw ----

    private static string? BaillieExhaustiveThirtyTwoBit() {
        // EVERY 32-bit value against a segmented sieve of Eratosthenes: below 2^32 "no known counterexample" becomes
        // "none exists", computed here rather than cited. The work is embarrassingly parallel; the VERDICT is not,
        // because each block records only its own least disagreement and the blocks are read in ascending order, so a
        // rerun reports the same value.
        const int blockCount = 4_096;
        const uint blockLength = ((uint)(4_294_967_296L / blockCount));

        var trialPrimes = PrimesBelow(exclusiveMaximum: 65_536);
        var compositionFailures = new long[blockCount];
        var lucasFailures = new long[blockCount];

        Array.Fill(array: compositionFailures, value: -1L);
        Array.Fill(array: lucasFailures, value: -1L);
        Parallel.For(fromInclusive: 0, toExclusive: blockCount, body: block => {
            var start = ((ulong)(((uint)block) * blockLength));
            var flags = SieveWindow(start: start, length: ((int)blockLength), trialPrimes: trialPrimes);

            for (var offset = 0; (offset < ((int)blockLength)); ++offset) {
                var value = (start + ((ulong)offset));
                var expected = flags[offset];

                if ((PrimeField64.IsBaillieProbablePrime(value: value) != expected) && (0L > compositionFailures[block])) {
                    compositionFailures[block] = ((long)value);
                }
                // The Lucas half alone must accept every prime. The conjunction would catch a false negative too, but
                // the half is a contract of its own, so it is asserted directly rather than inferred.
                if (expected && !PrimeField64.IsStrongLucasProbablePrime(value: value) && (0L > lucasFailures[block])) {
                    lucasFailures[block] = ((long)value);
                }
            }
        });

        for (var block = 0; (block < blockCount); ++block) {
            if (0L <= compositionFailures[block]) {
                return $"IsBaillieProbablePrime disagrees with the sieve of Eratosthenes at {compositionFailures[block]} — below 2^64 that is either a defect or the first known Baillie-PSW counterexample";
            }
            if (0L <= lucasFailures[block]) {
                return $"IsStrongLucasProbablePrime rejected the prime {lucasFailures[block]}";
            }
        }

        return null;
    }

    private static string? BaillieHalvesSurface(ref ulong state) {
        // The Lucas half against companion-matrix powers in BigInteger — no doubling ladder, no halving, no Montgomery
        // form, and a Selfridge search whose symbol comes from the suite's own reciprocity descent rather than from any
        // shipped Jacobi.
        for (var value = 0UL; (200_000UL > value); ++value) {
            if (PrimeField64.IsStrongLucasProbablePrime(value: value) != Oracles.StrongLucasSelfridge(value: value)) {
                return $"IsStrongLucasProbablePrime({value}) = {PrimeField64.IsStrongLucasProbablePrime(value: value)} where the companion-matrix oracle says {Oracles.StrongLucasSelfridge(value: value)}";
            }
        }

        for (var trial = 0; (trial < 20_000); ++trial) {
            var value = (NextRandom(state: ref state) | 1UL);

            if (PrimeField64.IsStrongLucasProbablePrime(value: value) != Oracles.StrongLucasSelfridge(value: value)) {
                return $"IsStrongLucasProbablePrime({value}) = {PrimeField64.IsStrongLucasProbablePrime(value: value)} where the companion-matrix oracle says {Oracles.StrongLucasSelfridge(value: value)}";
            }
        }

        // The first half needs an oracle of its own, or a witness-round fault and a Lucas fault could cancel inside the
        // conjunction.
        ulong[] witnesses = [0UL, 1UL, 2UL, 3UL, 5UL, 7UL, (ulong.MaxValue - 1UL), ulong.MaxValue,];

        foreach (var witness in witnesses) {
            for (var value = 0UL; (20_000UL > value); ++value) {
                if (PrimeField64.IsStrongProbablePrime(value: value, witness: witness) != Oracles.ModularStrongProbablePrime(value: value, witness: witness)) {
                    return $"IsStrongProbablePrime({value}, {witness}) = {PrimeField64.IsStrongProbablePrime(value: value, witness: witness)} where the plain-residue round says {Oracles.ModularStrongProbablePrime(value: value, witness: witness)}";
                }
            }
        }

        for (var trial = 0; (trial < 2_000); ++trial) {
            var value = (NextRandom(state: ref state) | 1UL);
            var witness = NextRandom(state: ref state);

            if (PrimeField64.IsStrongProbablePrime(value: value, witness: witness) != Oracles.ModularStrongProbablePrime(value: value, witness: witness)) {
                return $"IsStrongProbablePrime({value}, {witness}) = {PrimeField64.IsStrongProbablePrime(value: value, witness: witness)} where the plain-residue round says {Oracles.ModularStrongProbablePrime(value: value, witness: witness)}";
            }
        }

        // The documented degenerate cases, including the base-two FERMAT pseudoprime that is not a STRONG one — the
        // vector that separates the round from the weaker test it is easy to write by accident.
        if (!PrimeField64.IsStrongProbablePrime(value: 7UL, witness: 0UL) ||
            !PrimeField64.IsStrongProbablePrime(value: 7UL, witness: 7UL) ||
            !PrimeField64.IsStrongProbablePrime(value: 7UL, witness: 14UL) ||
            !PrimeField64.IsStrongProbablePrime(value: 2UL, witness: 0UL) ||
            PrimeField64.IsStrongProbablePrime(value: 0UL, witness: 2UL) ||
            PrimeField64.IsStrongProbablePrime(value: 1UL, witness: 2UL) ||
            PrimeField64.IsStrongProbablePrime(value: 4UL, witness: 3UL) ||
            PrimeField64.IsStrongProbablePrime(value: 341UL, witness: 2UL)) {
            return "a degenerate strong-round vector regressed: a zero or self base must pass, and 341 must fail base two";
        }

        return null;
    }

    private static string? BailliePopulationSurface() {
        // Squares are the adversarial shape for the Selfridge search: no D has symbol -1 on one, so the search cannot
        // exit that way. It still exits, on the vanishing symbol.
        for (var root = 2UL; (3_000UL >= root); ++root) {
            var square = (root * root);

            if (PrimeField64.IsStrongLucasProbablePrime(value: square) || PrimeField64.IsBaillieProbablePrime(value: square)) {
                return $"the perfect square {square} was accepted";
            }
        }

        ulong[] squareRoots = [
            FirstPrimeAtOrAbove(value: (1UL << 15)), FirstPrimeAtOrAbove(value: (1UL << 20)),
            FirstPrimeAtOrAbove(value: (1UL << 25)), FirstPrimeAtOrAbove(value: (1UL << 30)),
            FirstPrimeAtOrAbove(value: (1UL << 31)), LastPrimeBelow(value: (1UL << 32)),
            3_037_000_499UL, 4_294_967_295UL,
        ];

        foreach (var root in squareRoots) {
            var square = (root * root);

            if (PrimeField64.IsStrongLucasProbablePrime(value: square) ||
                PrimeField64.IsBaillieProbablePrime(value: square) ||
                PrimeField64.IsPrime(value: square)) {
                return $"the large perfect square {square} (root {root}) was accepted";
            }
        }

        // The two populations below 10^5, ENUMERATED here and compared list against list with the published tables.
        // Each list is exactly what the OTHER half must reject, and their disjointness IS the composition's thesis.
        var sieve = Oracles.PrimeSieve(inclusiveMaximum: 100_000);
        var foundBaseTwo = new List<ulong>();
        var foundLucas = new List<ulong>();

        for (var candidate = 3UL; (100_000UL > candidate); candidate += 2UL) {
            if (sieve[((int)candidate)]) { continue; }
            if (PrimeField64.IsStrongProbablePrime(value: candidate, witness: 2UL)) { foundBaseTwo.Add(item: candidate); }
            if (PrimeField64.IsStrongLucasProbablePrime(value: candidate)) { foundLucas.Add(item: candidate); }
        }

        if (!foundBaseTwo.SequenceEqual(second: BaseTwoPseudoprimes)) {
            return $"the enumerated base-two strong pseudoprimes below 10^5 are {string.Join(separator: ",", values: foundBaseTwo)} and OEIS A001262 says {string.Join(separator: ",", values: BaseTwoPseudoprimes)}";
        }
        if (!foundLucas.SequenceEqual(second: LucasPseudoprimes)) {
            return $"the enumerated strong Lucas pseudoprimes below 10^5 are {string.Join(separator: ",", values: foundLucas)} and OEIS A217255 says {string.Join(separator: ",", values: LucasPseudoprimes)}";
        }
        if (foundBaseTwo.Intersect(second: foundLucas).Any()) {
            return $"the two pseudoprime populations below 10^5 are not disjoint: {string.Join(separator: ",", values: foundBaseTwo.Intersect(second: foundLucas))}";
        }

        // The sole-rejecter controls: each half accepts the other half's pseudoprimes, so both halves are demonstrably
        // load-bearing rather than decorative.
        foreach (var candidate in BaseTwoPseudoprimes) {
            if (!PrimeField64.IsStrongProbablePrime(value: candidate, witness: 2UL) ||
                PrimeField64.IsStrongLucasProbablePrime(value: candidate) ||
                PrimeField64.IsBaillieProbablePrime(value: candidate) ||
                PrimeField64.IsPrime(value: candidate)) {
                return $"the base-two pseudoprime {candidate} is no longer rejected by the Lucas half alone";
            }
        }

        foreach (var candidate in LucasPseudoprimes) {
            if (!PrimeField64.IsStrongLucasProbablePrime(value: candidate) ||
                PrimeField64.IsStrongProbablePrime(value: candidate, witness: 2UL) ||
                PrimeField64.IsBaillieProbablePrime(value: candidate) ||
                PrimeField64.IsPrime(value: candidate)) {
                return $"the strong Lucas pseudoprime {candidate} is no longer rejected by the base-two round alone";
            }
        }

        foreach (var candidate in CarmichaelNumbers) {
            if (PrimeField64.IsBaillieProbablePrime(value: candidate) || PrimeField64.IsPrime(value: candidate)) {
                return $"the Carmichael number {candidate} was accepted";
            }
        }

        ulong[] smallComposites = [0UL, 1UL, 4UL, 6UL, 8UL, 9UL, 15UL, 21UL, 25UL,];

        foreach (var candidate in smallComposites) {
            if (PrimeField64.IsStrongLucasProbablePrime(value: candidate) || PrimeField64.IsBaillieProbablePrime(value: candidate)) {
                return $"the small composite {candidate} was accepted";
            }
        }

        // A value one below a power of two puts d = 1 and the whole test in the squaring loop; a value one above puts
        // s = 1, so the squaring loop never runs and everything rests on U_d and V_d. Both degenerate ends, at every
        // width — and the composite Mersennes here are the Lucas half's only sole-rejecter coverage above 2^32.
        for (var exponent = 2; (64 >= exponent); ++exponent) {
            var low = ((64 == exponent) ? ulong.MaxValue : ((1UL << exponent) - 1UL));
            var high = ((1UL << (exponent - 1)) + 1UL);
            var failure = (BaillieAgreesWithExactDecision(candidate: low) ?? BaillieAgreesWithExactDecision(candidate: high));

            if (failure is not null) { return failure; }
        }

        // The Lucas test's own worst case is the world its D = 5 parameter generates — P = 1, Q = -1, whose sequences
        // are the Fibonacci and Lucas numbers — so every term the carrier holds, and its immediate neighbours, meet the
        // exact decision.
        var fibonacci = (Previous: 0UL, Current: 1UL);
        var lucas = (Previous: 2UL, Current: 1UL);
        var ladder = new List<ulong>();

        while (true) {
            ladder.Add(item: fibonacci.Current);
            ladder.Add(item: lucas.Current);

            // Both sequences run to the last term the carrier holds, and the first one that would leave it ends the
            // walk — so the loop is bounded by the carrier itself.
            if ((fibonacci.Current > (ulong.MaxValue - fibonacci.Previous)) || (lucas.Current > (ulong.MaxValue - lucas.Previous))) {
                break;
            }

            fibonacci = (fibonacci.Current, (fibonacci.Previous + fibonacci.Current));
            lucas = (lucas.Current, (lucas.Previous + lucas.Current));
        }

        foreach (var term in ladder) {
            for (var delta = -2L; (2L >= delta); ++delta) {
                if (((0L > delta) && (term < ((ulong)(-delta)))) || ((0L < delta) && (term > (ulong.MaxValue - ((ulong)delta))))) {
                    continue;
                }

                var failure = BaillieAgreesWithExactDecision(candidate: ((ulong)(((long)term) + delta)));

                if (failure is not null) { return failure; }
            }
        }

        return null;
    }

    private static string? BaillieAgreesWithExactDecision(ulong candidate) {
        var expected = Oracles.ExactPrimality(value: candidate);

        if (PrimeField64.IsBaillieProbablePrime(value: candidate) != expected) {
            return $"IsBaillieProbablePrime({candidate}) = {PrimeField64.IsBaillieProbablePrime(value: candidate)} where the exact decision says {expected} — below 2^64 that is either a defect or the first known Baillie-PSW counterexample";
        }

        return ((expected && !PrimeField64.IsStrongLucasProbablePrime(value: candidate))
            ? $"IsStrongLucasProbablePrime rejected the prime {candidate}"
            : null);
    }

    // ---- jacobi symbol ----

    private static string? JacobiWideSurface() {
        var state = 0x4A61636F_62693A21UL;

        // Euler's criterion on primes, where Jacobi IS Legendre: the leg that stands outside the reciprocity descent
        // altogether. Small primes sweep every residue; large ones sample.
        ulong[] legendrePrimes = [
            3UL, 5UL, 7UL, 11UL, 13UL, 101UL, 65_537UL, 1_000_003UL,
            FirstPrimeAtOrAbove(value: (1UL << 40)), FirstPrimeAtOrAbove(value: (1UL << 61)),
        ];

        foreach (var prime in legendrePrimes) {
            var field = PrimeField64.Create(modulus: prime);

            for (var index = 0; (index < 512); ++index) {
                var residue = ((512UL > prime) ? (((ulong)index) % prime) : (NextRandom(state: ref state) % prime));
                var character = field.LegendreCharacter(value: residue);

                if (residue.JacobiSymbol(modulus: prime) != character) {
                    return $"({residue}/{prime}) = {residue.JacobiSymbol(modulus: prime)} where Euler's criterion says {character}";
                }
            }
        }

        // The defining laws, over operand widths that keep every product inside the carrier.
        for (var trial = 0; (trial < 20_000); ++trial) {
            var left = (NextRandom(state: ref state) >>> 33);
            var right = (NextRandom(state: ref state) >>> 33);
            var oddLeft = ((NextRandom(state: ref state) >>> 33) | 1UL);
            var oddRight = ((NextRandom(state: ref state) >>> 33) | 1UL);
            var leftSymbol = left.JacobiSymbol(modulus: oddLeft);

            if ((left * right).JacobiSymbol(modulus: oddLeft) != (leftSymbol * right.JacobiSymbol(modulus: oddLeft))) {
                return $"the symbol is not multiplicative in the numerator at ({left}·{right}/{oddLeft})";
            }
            if (left.JacobiSymbol(modulus: (oddLeft * oddRight)) != (leftSymbol * left.JacobiSymbol(modulus: oddRight))) {
                return $"the symbol is not multiplicative in the denominator at ({left}/{oddLeft}·{oddRight})";
            }
            if ((leftSymbol != (left + oddLeft).JacobiSymbol(modulus: oddLeft)) ||
                (leftSymbol != (left + (3UL * oddLeft)).JacobiSymbol(modulus: oddLeft))) {
                return $"the symbol is not periodic in the numerator at ({left}/{oddLeft})";
            }
        }

        // Boundary vectors: the degenerate modulus, the vanishing numerator, and both carrier edges.
        ulong[] boundaryValues = [0UL, 1UL, 2UL, 3UL, 6UL, (1UL << 63), (ulong.MaxValue - 1UL), ulong.MaxValue,];
        ulong[] boundaryModuli = [1UL, 3UL, 9UL, 15UL, uint.MaxValue, ((1UL << 63) + 1UL), (ulong.MaxValue - 2UL), ulong.MaxValue,];

        foreach (var modulus in boundaryModuli) {
            foreach (var value in boundaryValues) {
                var expected = Oracles.JacobiSymbolReciprocity(numerator: new BigInteger(value: value), denominator: new BigInteger(value: modulus));
                var actual = value.JacobiSymbol(modulus: modulus);

                if (actual != expected) {
                    return $"the boundary vector ({value}/{modulus}) = {actual} where the reciprocity descent says {expected}";
                }
                if ((1UL == modulus) && (1 != actual)) {
                    return $"({value}/1) = {actual} rather than the empty product 1";
                }
                if ((0UL == value) && (1UL != modulus) && (0 != actual)) {
                    return $"(0/{modulus}) = {actual} rather than 0";
                }
            }
        }

        if ((1 != uint.MaxValue.JacobiSymbol(modulus: 1U)) ||
            (0 != uint.MaxValue.JacobiSymbol(modulus: uint.MaxValue)) ||
            (1 != UInt128.MaxValue.JacobiSymbol(modulus: UInt128.One)) ||
            (0 != UInt128.MaxValue.JacobiSymbol(modulus: UInt128.MaxValue))) {
            return "a carrier-maximum boundary vector regressed";
        }

        // Full-width randomized agreement on all three instantiations, where the odd-modulus edge and the top bits
        // live, plus the arbitrary-width sibling on the same operands.
        for (var trial = 0; (trial < 20_000); ++trial) {
            var wideValue = NextRandom(state: ref state);
            var wideModulus = (NextRandom(state: ref state) | 1UL);
            var hugeValue = ((((UInt128)NextRandom(state: ref state)) << 64) | wideValue);
            var hugeModulus = ((((UInt128)NextRandom(state: ref state)) << 64) | wideModulus);
            var narrowValue = ((uint)wideValue);
            var narrowModulus = (((uint)wideModulus) | 1U);

            if (narrowValue.JacobiSymbol(modulus: narrowModulus) != Oracles.JacobiSymbolReciprocity(numerator: narrowValue, denominator: narrowModulus)) {
                return $"the uint symbol ({narrowValue}/{narrowModulus}) disagrees with the reciprocity descent";
            }
            if (wideValue.JacobiSymbol(modulus: wideModulus) != Oracles.JacobiSymbolReciprocity(numerator: wideValue, denominator: wideModulus)) {
                return $"the ulong symbol ({wideValue}/{wideModulus}) disagrees with the reciprocity descent";
            }
            if (hugeValue.JacobiSymbol(modulus: hugeModulus) != Oracles.JacobiSymbolReciprocity(numerator: ((BigInteger)hugeValue), denominator: ((BigInteger)hugeModulus))) {
                return $"the UInt128 symbol ({hugeValue}/{hugeModulus}) disagrees with the reciprocity descent";
            }
            if (NumberTheoryFunctions.JacobiSymbol(numerator: ((BigInteger)hugeValue), denominator: ((BigInteger)hugeModulus)) != Oracles.JacobiSymbolReciprocity(numerator: ((BigInteger)hugeValue), denominator: ((BigInteger)hugeModulus))) {
                return $"the BigInteger symbol ({hugeValue}/{hugeModulus}) disagrees with the reciprocity descent";
            }
        }

        // The composite-modulus regime at width: odd COMPOSITE moduli below 2^32, where the definition can still be
        // reached by factoring, judged by the definition rather than by another descent.
        for (var trial = 0; (trial < 20_000); ++trial) {
            var modulus = ((((uint)NextRandom(state: ref state)) | 1U) | (1U << 20));
            var numerator = ((uint)NextRandom(state: ref state));
            var expected = JacobiSymbolByFactorAndEuler(numerator: numerator, oddModulus: modulus);
            var actual = numerator.JacobiSymbol(modulus: modulus);

            if (actual != expected) {
                return $"({numerator}/{modulus}) = {actual} where the factor-and-Euler definition says {expected}";
            }
        }

        return JacobiRefusalSurface();
    }

    private static string? JacobiRefusalSurface() {
        (string Label, Action Operation)[] refusals = [
            ("uint zero modulus", () => _ = 5U.JacobiSymbol(modulus: 0U)),
            ("uint even modulus", () => _ = 5U.JacobiSymbol(modulus: 8U)),
            ("ulong zero modulus", () => _ = 5UL.JacobiSymbol(modulus: 0UL)),
            ("ulong even modulus", () => _ = 5UL.JacobiSymbol(modulus: (ulong.MaxValue - 1UL))),
            ("UInt128 zero modulus", () => _ = UInt128.One.JacobiSymbol(modulus: UInt128.Zero)),
            ("UInt128 even modulus", () => _ = UInt128.One.JacobiSymbol(modulus: (UInt128.MaxValue - UInt128.One))),
        ];

        foreach (var (label, operation) in refusals) {
            try {
                operation();

                return $"{label} was accepted rather than refused";
            } catch (ArgumentOutOfRangeException exception) {
                if (!string.Equals(a: "modulus", b: exception.ParamName, comparisonType: StringComparison.Ordinal)) {
                    return $"{label} was refused naming '{exception.ParamName}' rather than 'modulus'";
                }
            }
        }

        return null;
    }

    // ---- factorization ----

    private static string? NarrowFactorizationHolds(uint value, Span<uint> destination, ReadOnlySpan<ulong> trialPrimes) {
        var count = value.Factorize(destination: destination);
        var enumerated = value.EnumeratePrimeFactors().ToArray();

        if (!destination[..count].SequenceEqual(other: enumerated)) {
            return $"Factorize and EnumeratePrimeFactors disagree at {value}: [{string.Join(separator: ",", values: destination[..count].ToArray())}] against [{string.Join(separator: ",", values: enumerated)}]";
        }
        if (2U > value) {
            return ((0 == count) ? null : $"{value} is below two and reported {count} factor(s)");
        }

        var product = 1UL;
        var previous = 0U;

        for (var index = 0; (index < count); ++index) {
            var factor = destination[index];

            if (factor < previous) {
                return $"the factors of {value} are not ascending at index {index}";
            }
            if (!IsPrimeByTrialDivision(value: factor, trialPrimes: trialPrimes)) {
                return $"{factor} is reported as a prime factor of {value} and is not prime";
            }

            product *= factor;
            previous = factor;
        }

        return ((product == value) ? null : $"the factors of {value} reassemble to {product}");
    }

    private static string? WideFactorizationSurface(ref ulong state) {
        // The ulong carrier at full width: the reference core.big-integer-prime-factors-vs-word-kernel leans on, swept
        // where that law's own shared range stops at 2047. Per-factor primality is the exact decision, outside
        // Puck.Maths entirely.
        for (var trial = 0; (trial < 20_000); ++trial) {
            var failure = WideFactorizationHolds(value: NextRandom(state: ref state));

            if (failure is not null) { return failure; }
        }

        // A contiguous run at the top of the carrier, where the splitter's ring runs its widest reduction.
        for (var offset = 0UL; (20_000UL > offset); ++offset) {
            var failure = WideFactorizationHolds(value: (ulong.MaxValue - offset));

            if (failure is not null) { return failure; }
        }

        // The adversarial shape a random stream essentially never draws: a SEMIPRIME of two primes near 2^32, whose
        // least factor is beyond any trial division and whose product fills the carrier. A cycle walk is O(n^(1/4)), so
        // this is where it earns the claim rather than the small factors doing the work.
        var pool = new ulong[256];

        for (var index = 0; (index < pool.Length); ++index) {
            // Bounded below 2^31 + 2^30 so the product of any two stays inside the carrier with room to spare.
            pool[index] = FirstPrimeAtOrAbove(value: ((1UL << 31) + (NextRandom(state: ref state) % (1UL << 30))));
        }

        for (var trial = 0; (trial < 2_000); ++trial) {
            var left = pool[((int)(NextRandom(state: ref state) % ((ulong)pool.Length)))];
            var right = pool[((int)(NextRandom(state: ref state) % ((ulong)pool.Length)))];
            var product = (left * right);
            var expected = ((left <= right) ? new[] { left, right, } : new[] { right, left, });
            var actual = product.EnumeratePrimeFactors().ToArray();

            if (!expected.SequenceEqual(second: actual)) {
                return $"the semiprime {left} x {right} factored as [{string.Join(separator: ",", values: actual)}]";
            }
        }

        // Hand-built shapes of every kind the carrier admits, each with its factorization written out rather than read
        // back from the subject.
        (ulong Value, ulong[] Factors)[] vectors = [
            (2UL, [2UL,]),
            // 2^63 and 10^19 = 2^19 · 5^19: the repeat counts are written as counts rather than as pasted runs, so a
            // miscounted literal cannot make either row pass for the wrong reason.
            (1UL << 63, [.. Enumerable.Repeat(element: 2UL, count: 63),]),
            (10_000_000_000_000_000_000UL, [.. Enumerable.Repeat(element: 2UL, count: 19), .. Enumerable.Repeat(element: 5UL, count: 19),]),
            (2_305_843_009_213_693_951UL, [2_305_843_009_213_693_951UL,]),
            (ulong.MaxValue, [3UL, 5UL, 17UL, 257UL, 641UL, 65_537UL, 6_700_417UL,]),
            (18_446_744_073_709_551_614UL, [2UL, 7UL, 7UL, 73UL, 127UL, 337UL, 92_737UL, 649_657UL,]),
        ];

        foreach (var (value, factors) in vectors) {
            var actual = value.EnumeratePrimeFactors().ToArray();

            if (!factors.SequenceEqual(second: actual)) {
                return $"{value} factored as [{string.Join(separator: ",", values: actual)}] rather than [{string.Join(separator: ",", values: factors)}]";
            }
        }

        return null;
    }

    private static string? WideFactorizationHolds(ulong value) {
        var factors = value.EnumeratePrimeFactors().ToArray();

        if (2UL > value) {
            return ((0 == factors.Length) ? null : $"{value} is below two and reported {factors.Length} factor(s)");
        }

        var product = 1UL;
        var previous = 0UL;

        for (var index = 0; (index < factors.Length); ++index) {
            var factor = factors[index];

            if (factor < previous) {
                return $"the factors of {value} are not ascending at index {index}";
            }
            if (!Oracles.ExactPrimality(value: factor)) {
                return $"{factor} is reported as a prime factor of {value} and is not prime";
            }

            product *= factor;
            previous = factor;
        }

        return ((product == value) ? null : $"the factors of {value} reassemble to {product}");
    }

    // ---- shared-nothing helpers ----

    /// <summary>The Jacobi symbol by its DEFINITION: factor the odd modulus and multiply the Legendre symbols of its
    /// prime powers, each decided by Euler's criterion in <see cref="BigInteger"/>.</summary>
    /// <param name="numerator">The upper argument.</param>
    /// <param name="oddModulus">The lower argument, odd and positive; kept to a <see cref="uint"/>-sized value so the
    /// trial division that factors it stays cheap.</param>
    /// <returns><c>0</c>, <c>1</c> or <c>-1</c>.</returns>
    /// <remarks>Reaches no reciprocity step at all, so it stands outside the descent both shipped spellings walk and
    /// outside <see cref="Oracles.JacobiSymbolReciprocity(BigInteger, BigInteger)"/> too. The trial-division loop
    /// terminates by construction: the trial factor strictly increases and the remaining cofactor never grows.</remarks>
    private static int JacobiSymbolByFactorAndEuler(BigInteger numerator, ulong oddModulus) {
        var remaining = oddModulus;
        var symbol = 1;

        for (var factor = 3UL; ((factor * factor) <= remaining); factor += 2UL) {
            while (0UL == (remaining % factor)) {
                remaining /= factor;
                symbol *= LegendreByEuler(numerator: numerator, prime: factor);

                if (0 == symbol) { return 0; }
            }
        }

        if (1UL != remaining) { symbol *= LegendreByEuler(numerator: numerator, prime: remaining); }

        return symbol;
    }

    private static int LegendreByEuler(BigInteger numerator, ulong prime) {
        var widePrime = new BigInteger(value: prime);
        var residue = (((numerator % widePrime) + widePrime) % widePrime);

        if (residue.IsZero) { return 0; }

        return (BigInteger.ModPow(value: residue, exponent: ((widePrime - BigInteger.One) / 2), modulus: widePrime).IsOne ? 1 : -1);
    }

    private static bool IsPrimeByTrialDivision(uint value, ReadOnlySpan<ulong> trialPrimes) {
        if (2U > value) { return false; }

        foreach (var prime in trialPrimes) {
            if ((prime * prime) > value) { return true; }
            if (0UL == (value % prime)) { return (value == prime); }
        }

        return true;
    }

    /// <summary>The primes below <paramref name="exclusiveMaximum"/>, from the sieve of Eratosthenes.</summary>
    /// <param name="exclusiveMaximum">The exclusive ceiling.</param>
    /// <returns>The ascending prime list.</returns>
    private static ulong[] PrimesBelow(int exclusiveMaximum) {
        var flags = Oracles.PrimeSieve(inclusiveMaximum: (exclusiveMaximum - 1));
        var primes = new List<ulong>();

        for (var value = 2; (value < exclusiveMaximum); ++value) {
            if (flags[value]) { primes.Add(item: ((ulong)value)); }
        }

        return primes.ToArray();
    }

    /// <summary>The primality flags for a contiguous window, by a SEGMENTED sieve of Eratosthenes: multiples crossed
    /// out and survivors read off.</summary>
    /// <param name="start">The window's first value.</param>
    /// <param name="length">The window's length.</param>
    /// <param name="trialPrimes">Every prime at or below the square root of the window's last value; the window is not
    /// decided exactly without them.</param>
    /// <returns>One flag per value in the window.</returns>
    private static bool[] SieveWindow(ulong start, int length, ReadOnlySpan<ulong> trialPrimes) {
        var flags = new bool[length];
        var end = (start + ((ulong)length));

        Array.Fill(array: flags, value: true);

        if (2UL > start) {
            for (var offset = 0; ((offset < length) && (2UL > (start + ((ulong)offset)))); ++offset) { flags[offset] = false; }
        }

        foreach (var prime in trialPrimes) {
            var square = (prime * prime);

            if (square >= end) { break; }

            var first = ((start <= square) ? square : (((start + prime) - 1UL) / prime * prime));

            for (var multiple = first; (multiple < end); multiple += prime) {
                flags[((int)(multiple - start))] = false;
            }
        }

        return flags;
    }

    /// <summary>SplitMix64, written here from its published constants so no sweep borrows a Puck.Maths generator to
    /// produce the operands it then judges.</summary>
    /// <param name="state">The generator state, advanced in place.</param>
    /// <returns>The next draw.</returns>
    private static ulong NextRandom(ref ulong state) {
        unchecked {
            state += 0x9E3779B97F4A7C15UL;

            var mixed = state;

            mixed = ((mixed ^ (mixed >>> 30)) * 0xBF58476D1CE4E5B9UL);
            mixed = ((mixed ^ (mixed >>> 27)) * 0x94D049BB133111EBUL);

            return (mixed ^ (mixed >>> 31));
        }
    }

    /// <summary>The least prime at or above <paramref name="value"/>, decided by
    /// <see cref="Oracles.ExactPrimality(ulong)"/>.</summary>
    /// <param name="value">The lower bound.</param>
    /// <returns>The prime.</returns>
    /// <exception cref="InvalidOperationException">The 4000-step budget ran out. The largest known prime gap below
    /// <c>2^64</c> is 1476, so exhausting it means the search left the region primes exist in.</exception>
    private static ulong FirstPrimeAtOrAbove(ulong value) {
        var candidate = ((2UL > value) ? 2UL : value);

        for (var budget = 4_000; (0 < budget); --budget) {
            if (Oracles.ExactPrimality(value: candidate)) { return candidate; }

            ++candidate;
        }

        throw new InvalidOperationException(message: $"PRIME SEARCH BUDGET EXHAUSTED looking at or above {value}");
    }

    /// <summary>The greatest prime strictly below <paramref name="value"/>, decided by
    /// <see cref="Oracles.ExactPrimality(ulong)"/>.</summary>
    /// <param name="value">The exclusive upper bound.</param>
    /// <returns>The prime.</returns>
    /// <exception cref="InvalidOperationException">The 4000-step budget ran out.</exception>
    private static ulong LastPrimeBelow(ulong value) {
        var candidate = (value - 1UL);

        for (var budget = 4_000; (0 < budget); --budget) {
            if (Oracles.ExactPrimality(value: candidate)) { return candidate; }

            --candidate;
        }

        throw new InvalidOperationException(message: $"PRIME SEARCH BUDGET EXHAUSTED looking below {value}");
    }
}
