using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // The odd composites below 2·10^6 at which the single trace C_d is ±2 although neither U_d nor V_d vanishes: a
    // trace ladder that tested that one trace would accept exactly these, and the strong Lucas test rejects them.
    private static readonly ulong[] LucasSingleTraceComposites = [
        27_869UL, 348_611UL, 518_851UL, 852_389UL, 902_339UL, 1_247_159UL, 1_291_289UL, 1_789_199UL, 1_940_951UL,
    ];
    // The boundaries around the ladder, and the carrier's own: 2^32, 2^58, 2^61, 2^62 and ScaledResidueRing64's signed
    // ceiling 2^63, where the ladder leaves signed arithmetic for exact arithmetic.
    private static readonly ulong[] LucasTraceBoundaries = [
        (1UL << 32), (1UL << 58), (1UL << 61), (1UL << 62), ScaledResidueRing64.SignedModulusCeiling,
    ];

    /// <summary>Compares the strong Lucas test with the U and V recurrence oracle at the Default tier's operands.</summary>
    /// <returns>The first counterexample, or <see langword="null"/>.</returns>
    public static string? LucasTraceLadderMatchesRecurrence() => LucasTraceLadderMatchesRecurrence(full: false);
    /// <summary>Compares the strong Lucas test with the U and V recurrence oracle at the Deep tier's stronger operands.</summary>
    /// <returns>The first counterexample, or <see langword="null"/>.</returns>
    public static string? LucasTraceLadderMatchesRecurrenceDeep() => LucasTraceLadderMatchesRecurrence(full: true);

    private static string? LucasTraceLadderMatchesRecurrence(bool full) =>
        (LucasTraceSelfridgeSurface(full: full)
        ?? (LucasTraceParameterSurface(full: full)
        ?? LucasTraceRefusals()));
    // The whole test, Selfridge's search included, against the recurrence oracle with its own search.
    private static string? LucasTraceSelfridgeSurface(bool full) {
        var generator = Pcg32XshRr.Create(state: 2071, stream: 17);
        var acceptedComposites = 0;

        ulong NextWord() => (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();

        string? Check(ulong value) {
            var expected = Oracles.StrongLucasSelfridgeRecurrence(value: value);
            var actual = PrimeField64.IsStrongLucasProbablePrime(value: value);

            if (actual != expected) { return $"IsStrongLucasProbablePrime({value}) = {actual} where the U and V recurrence says {expected}"; }
            if (expected && (value < (1UL << 26)) && !Oracles.ExactPrimality(value: value)) { ++acceptedComposites; }

            return null;
        }

        // Every odd value of a contiguous band, where the composites a single trace would accept live.
        var band = (full ? (1UL << 23) : (1UL << 21));

        for (var value = 3UL; (value < band); value += 2UL) {
            if (Check(value: value) is { } failure) { return failure; }
        }
        foreach (var composite in LucasSingleTraceComposites) {
            if (Oracles.ExactPrimality(value: composite) || Oracles.StrongLucasSelfridgeRecurrence(value: composite)) {
                return $"{composite} is not the composite the strong Lucas test rejects that this law relies on";
            }
            if (Check(value: composite) is { } failure) { return failure; }
        }
        // Every 2^s - 1, where d is one, so the half index is zero, no walk runs and C_d is t itself.
        for (var exponent = 2; (exponent <= 64); ++exponent) {
            if (Check(value: (ulong.MaxValue >> (64 - exponent))) is { } failure) { return failure; }
        }
        // Strong Lucas pseudoprimes, accepted composites, must have appeared: 5459 and 5777 lead them.
        if (acceptedComposites < 2) { return $"the band held {acceptedComposites} accepted composites, so the accepting path at a composite went unexercised"; }

        // Drawn odd words in each band: below 2^58, up to 2^61, up to 2^62, and above.
        var draws = (full ? 40_000 : 4_000);
        var traceCeiling = LucasTraceBoundaries[2];

        for (var count = 0; (count < draws); ++count) {
            ulong[] values = [
                ((NextWord() >> 6) | 1UL),
                ((((NextWord() % (traceCeiling - (1UL << 58))) + (1UL << 58))) | 1UL),
                ((((NextWord() % ((1UL << 62) - traceCeiling)) + traceCeiling)) | 1UL),
                (NextWord() | (1UL << 62) | 1UL),
            ];

            foreach (var value in values) {
                if (Check(value: value) is { } failure) { return failure; }
            }
        }
        // A long run of draws in [2^61, 3·2^60), which holds enough primes for an accepting path that loses a fold to
        // show.
        for (var count = 0; (count < (full ? 120_000 : 24_000)); ++count) {
            if (Check(value: (traceCeiling + (NextWord() % (traceCeiling >> 1))) | 1UL) is { } failure) { return failure; }
        }
        // Just above the signed ceiling, where a ladder left signed too far carries traces past the signed word.
        for (var count = 0; (count < (full ? 120_000 : 24_000)); ++count) {
            if (Check(value: (ScaledResidueRing64.SignedModulusCeiling + (NextWord() % (ScaledResidueRing64.SignedModulusCeiling >> 1))) | 1UL) is { } failure) { return failure; }
        }
        // The strong base-two pseudoprimes of every region, which only this test rejects, and composites shaped against
        // the band [2^60, 2^62): prime powers, Wieferich powers and products of primes either side of 2^30 and 2^31.
        var low = new List<ulong>();
        var bandPseudoprimes = new List<ulong>();
        var high = new List<ulong>();

        if (BaseTwoPseudoprimes(band: bandPseudoprimes, full: false, high: high, low: low) is { } construction) { return construction; }
        foreach (var value in low.Concat(second: bandPseudoprimes).Concat(second: high).Concat(second: BandShapedComposites(full: full))) {
            if (Check(value: value) is { } failure) { return failure; }
        }

        // The odd neighbours of every boundary on both sides, and the top of the carrier.
        var reach = (full ? 8_192UL : 512UL);

        foreach (var boundary in LucasTraceBoundaries) {
            for (var offset = 1UL; (offset < reach); offset += 2UL) {
                var below = (boundary - offset) | 1UL;

                if (Check(value: below) is { } failure) { return failure; }
                if (Check(value: (boundary + offset) | 1UL) is { } above) { return above; }
            }
        }
        for (var offset = 0UL; (offset < reach); offset += 2UL) {
            if (Check(value: (ulong.MaxValue - offset)) is { } failure) { return failure; }
        }

        // Squares and higher powers of every odd prime below 200 the carrier holds, prime squares times a second prime,
        // and the squares and products of the odd primes just below 2^32, where no small factor screens the value.
        var smallPrimes = Oracles.SmallPrimes;

        for (var index = 1; (smallPrimes[index] < 200U); ++index) {
            var prime = ((ulong)smallPrimes[index]);

            for (var power = (prime * prime); ; power *= prime) {
                if (Check(value: power) is { } failure) { return failure; }
                if (power > (ulong.MaxValue / prime)) { break; }
            }
            for (var other = 1; (other < 40); ++other) {
                var cofactor = ((ulong)smallPrimes[other]);

                if (cofactor != prime) {
                    if (Check(value: ((prime * prime) * cofactor)) is { } failure) { return failure; }
                }
            }
        }

        var nearRoot = new List<ulong>();

        for (var candidate = ((ulong)uint.MaxValue); (nearRoot.Count < (full ? 64 : 16)); candidate -= 2UL) {
            if (Oracles.ExactPrimality(value: candidate)) { nearRoot.Add(item: candidate); }
        }
        for (var i = 0; (i < nearRoot.Count); ++i) {
            for (var j = i; (j < nearRoot.Count); ++j) {
                if (Check(value: (nearRoot[i] * nearRoot[j])) is { } failure) { return failure; }
            }
        }

        return null;
    }
    // The ladder at a given parameter, every small Q including the non-units Selfridge's search never selects.
    private static string? LucasTraceParameterSurface(bool full) {
        var generator = Pcg32XshRr.Create(state: 2072, stream: 19);
        var nonUnits = 0;
        var accepted = 0;

        ulong NextWord() => (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();

        string? Check(ulong value, long q) {
            var discriminant = (BigInteger.One - (4 * new BigInteger(value: q)));

            if (!BigInteger.GreatestCommonDivisor(left: discriminant, right: value).IsOne) { return null; }

            var expected = Oracles.StrongLucasRecurrence(q: q, value: value);
            var actual = PrimeKernels.PassesStrongLucasRound(parameter: q, value: value);

            if (actual != expected) { return $"PassesStrongLucasRound({value}, {q}) = {actual} where the U and V recurrence says {expected}"; }
            if (!BigInteger.GreatestCommonDivisor(left: q, right: value).IsOne) {
                ++nonUnits;

                // A parameter sharing a prime with the value makes the recurrence constant modulo that prime.
                if (expected) { return $"the U and V recurrence accepts {value} at the non-unit parameter {q}"; }
            }
            if (expected) { ++accepted; }

            return null;
        }

        var contiguous = (full ? 6_000UL : 2_000UL);
        var drawsPerRegion = (full ? 48 : 8);
        var traceCeiling = LucasTraceBoundaries[2];

        for (var q = -40L; (q <= 40L); ++q) {
            for (var value = 3UL; (value < contiguous); value += 2UL) {
                if (Check(q: q, value: value) is { } failure) { return failure; }
            }
            for (var exponent = 2; (exponent <= 64); ++exponent) {
                if (Check(q: q, value: (ulong.MaxValue >> (64 - exponent))) is { } failure) { return failure; }
            }
            // Values in [2^61, 2^62) sharing an odd prime with the parameter, where it is not a unit.
            var shared = (((ulong)long.Abs(value: q)) >>> BitOperations.TrailingZeroCount(value: Math.Max(val1: long.Abs(value: q), val2: 1L)));

            if (1UL < shared) {
                for (var count = 0; (count < drawsPerRegion); ++count) {
                    var multiple = ((((NextWord() % ((1UL << 62) - traceCeiling)) + traceCeiling) / shared) * shared);

                    if (0UL == (multiple & 1UL)) { multiple -= shared; }
                    if (Check(q: q, value: multiple) is { } failure) { return failure; }
                }
            }
            for (var count = 0; (count < drawsPerRegion); ++count) {
                ulong[] values = [
                    ((NextWord() >> 6) | 1UL),
                    ((((NextWord() % (traceCeiling - (1UL << 58))) + (1UL << 58))) | 1UL),
                    ((((NextWord() % ((1UL << 62) - traceCeiling)) + traceCeiling)) | 1UL),
                    (NextWord() | (1UL << 62) | 1UL),
                    (ulong.MaxValue - ((NextWord() & 255UL) << 1)),
                ];

                foreach (var value in values) {
                    if (Check(q: q, value: value) is { } failure) { return failure; }
                }
            }
        }

        // Wide parameters, whose odd parts are past every tabulated digit, and wide powers of two.
        const long Limit = (1L << 61);
        long[] wide = [(Limit - 1L), -(Limit - 1L), (Limit - 3L), -(Limit - 3L), (1L << 60), -(1L << 60), (3L << 58), -(3L << 58), 65_537L, -65_537L];
        var wideDraws = (full ? 512 : 64);

        for (var count = 0; (count < (wide.Length + wideDraws)); ++count) {
            var q = ((count < wide.Length)
                ? wide[count]
                : (((long)(NextWord() % ((ulong)((2L * Limit) - 1L)))) - (Limit - 1L)));

            for (var region = 0; (region < 6); ++region) {
                var value = (region switch {
                    0 => (((NextWord() >> 44) | 1UL) + 2UL),
                    1 => ((NextWord() >> 6) | 1UL),
                    2 => ((((NextWord() % (traceCeiling - (1UL << 58))) + (1UL << 58))) | 1UL),
                    3 => ((((NextWord() % ((1UL << 62) - traceCeiling)) + traceCeiling)) | 1UL),
                    4 => (NextWord() | (1UL << 62) | 1UL),
                    _ => ulong.MaxValue,
                });

                if (Check(q: q, value: value) is { } failure) { return failure; }
            }
        }

        if (nonUnits < 100) { return $"only {nonUnits} non-unit parameters were reached, so the exact rejection went unexercised"; }
        if (accepted < 100) { return $"only {accepted} parameters were accepted, so the accepting tests went unexercised"; }

        return null;
    }
    private static string? LucasTraceRefusals() {
        var outOfRange = typeof(ArgumentOutOfRangeException);

        return (Refuses(action: () => PrimeKernels.PassesStrongLucasRound(parameter: 1L, value: 0UL), parameterName: "value", type: outOfRange, what: "PassesStrongLucasRound(0, 1)")
            ?? (Refuses(action: () => PrimeKernels.PassesStrongLucasRound(parameter: 1L, value: 1UL), parameterName: "value", type: outOfRange, what: "PassesStrongLucasRound(1, 1)")
            ?? (Refuses(action: () => PrimeKernels.PassesStrongLucasRound(parameter: 1L, value: 2UL), parameterName: "value", type: outOfRange, what: "PassesStrongLucasRound(2, 1)")
            ?? (Refuses(action: () => PrimeKernels.PassesStrongLucasRound(parameter: 1L, value: 1_000_000UL), parameterName: "value", type: outOfRange, what: "PassesStrongLucasRound(10^6, 1)")
            ?? (Refuses(action: () => PrimeKernels.PassesStrongLucasRound(parameter: (1L << 61), value: 101UL), parameterName: "parameter", type: outOfRange, what: "PassesStrongLucasRound(101, 2^61)")
            ?? (Refuses(action: () => PrimeKernels.PassesStrongLucasRound(parameter: -(1L << 61), value: 101UL), parameterName: "parameter", type: outOfRange, what: "PassesStrongLucasRound(101, -2^61)")
            ?? (Refuses(action: () => PrimeKernels.PassesStrongLucasRound(parameter: long.MinValue, value: 101UL), parameterName: "parameter", type: outOfRange, what: "PassesStrongLucasRound(101, long.MinValue)")
            ?? (Refuses(action: () => PrimeKernels.PassesStrongLucasRound(parameter: 2L, value: 7UL), parameterName: "parameter", type: typeof(ArgumentException), what: "PassesStrongLucasRound(7, 2), whose discriminant is -7")
            ?? Refuses(action: () => PrimeKernels.PassesStrongLucasRound(parameter: -1L, value: 15UL), parameterName: "parameter", type: typeof(ArgumentException), what: "PassesStrongLucasRound(15, -1), whose discriminant is 5")))))))));
    }
}
