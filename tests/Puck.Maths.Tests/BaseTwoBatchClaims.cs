using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // The bounds the batch dispatches on, 2^32 and ScaledResidueRing64's lazy and signed ceilings, the last also the
    // bound above which every value seeds by a shift, and 2^62, inside the signed region.
    private static readonly ulong[] BaseTwoBatchBoundaries = [
        (1UL << 32), ScaledResidueRing64.LazyModulusCeiling, (1UL << 62), ScaledResidueRing64.SignedModulusCeiling,
    ];
    // The arithmetic regions a batch's largest value selects, as [low, high) bands of odd values, the last closed at
    // the top of the carrier.
    private static readonly (ulong Low, ulong High)[] BaseTwoBatchRegions = [
        (3UL, (1UL << 32)), ((1UL << 32), (1UL << 58)), ((1UL << 58), ScaledResidueRing64.LazyModulusCeiling),
        (ScaledResidueRing64.LazyModulusCeiling, (1UL << 61)), ((1UL << 61), (1UL << 62)),
        ((1UL << 62), ScaledResidueRing64.SignedModulusCeiling), (ScaledResidueRing64.SignedModulusCeiling, ulong.MaxValue),
    ];

    /// <summary>Compares the batched base-two round with plain modular exponentiation at the Default tier's operands.</summary>
    /// <returns>The first counterexample, or <see langword="null"/>.</returns>
    public static string? BaseTwoBatchMatchesModularPower() => BaseTwoBatchMatchesModularPower(full: false);
    /// <summary>Compares the batched base-two round with plain modular exponentiation at the Deep tier's stronger operands.</summary>
    /// <returns>The first counterexample, or <see langword="null"/>.</returns>
    public static string? BaseTwoBatchMatchesModularPowerDeep() => BaseTwoBatchMatchesModularPower(full: true);

    private static string? BaseTwoBatchMatchesModularPower(bool full) {
        var generator = Pcg32XshRr.Create(state: 2083, stream: 29);
        var lanes = new ulong[PrimeKernels.BaseTwoBatchWidth];
        var passes = 0;

        ulong NextWord() => (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();
        ulong Draw((ulong Low, ulong High) region) => (region.Low + (NextWord() % (region.High - region.Low))) | 1UL;

        // Every lane's bit against the oracle, which first checks that two and its inverse accept at the same round.
        string? Check(ulong first, ulong second, ulong third, ulong fourth) {
            lanes[0] = first;
            lanes[1] = second;
            lanes[2] = third;
            lanes[3] = fourth;

            var mask = PrimeKernels.PassesBaseTwoBatch(values: lanes);

            if (0 != (mask >>> PrimeKernels.BaseTwoBatchWidth)) { return $"PassesBaseTwoBatch([{first}, {second}, {third}, {fourth}]) = {mask}, a mask past the batch"; }

            for (var lane = 0; (lane < PrimeKernels.BaseTwoBatchWidth); ++lane) {
                var value = lanes[lane];
                var round = Oracles.StrongBaseTwoAcceptanceRound(inverted: false, value: value);
                var inverseRound = Oracles.StrongBaseTwoAcceptanceRound(inverted: true, value: value);

                if (round != inverseRound) { return $"two and its inverse accept {value} at rounds {round} and {inverseRound}"; }

                var expected = (0 <= round);

                if (expected != (0 != ((mask >>> lane) & 1))) {
                    return $"PassesBaseTwoBatch([{first}, {second}, {third}, {fourth}]) lane {lane} = {!expected} where the modular power says {expected}";
                }
                if (expected) { ++passes; }
            }

            return null;
        }
        // Every rotation of the lanes, so each value is decided in every position beside the same companions.
        string? CheckRotations(ulong first, ulong second, ulong third, ulong fourth) =>
            (Check(first: first, fourth: fourth, second: second, third: third)
            ?? (Check(first: second, fourth: first, second: third, third: fourth)
            ?? (Check(first: third, fourth: second, second: fourth, third: first)
            ?? Check(first: fourth, fourth: third, second: first, third: second))));

        // Every odd value of a contiguous band, four consecutive values to a batch, and the band's start beside three
        // values above 2^63, so short odd parts walk beside the longest ones.
        var band = (full ? (1UL << 21) : (1UL << 17));

        for (var value = 3UL; (value < band); value += 8UL) {
            if (Check(first: value, fourth: (value + 6UL), second: (value + 2UL), third: (value + 4UL)) is { } failure) { return failure; }
        }
        for (var value = 3UL; (value < (band >>> 5)); value += 2UL) {
            var top = BaseTwoBatchRegions[^1];

            if (CheckRotations(first: value, fourth: Draw(region: top), second: Draw(region: top), third: Draw(region: top)) is { } failure) { return failure; }
        }

        // Drawn batches inside each region, then batches whose lanes come from any region.
        var draws = (full ? 12_000 : 1_200);

        foreach (var region in BaseTwoBatchRegions) {
            var before = passes;

            for (var count = 0; (count < draws); ++count) {
                if (Check(first: Draw(region: region), fourth: Draw(region: region), second: Draw(region: region), third: Draw(region: region)) is { } failure) { return failure; }
            }
            if ((passes - before) < 100) { return $"only {(passes - before)} values in [{region.Low}, {region.High}) passed, so the accepting finish went unexercised there"; }
        }
        for (var count = 0; (count < (draws << 1)); ++count) {
            var regions = BaseTwoBatchRegions.Length;

            if (Check(
                first: Draw(region: BaseTwoBatchRegions[((int)(NextWord() % ((ulong)regions)))]),
                fourth: Draw(region: BaseTwoBatchRegions[((int)(NextWord() % ((ulong)regions)))]),
                second: Draw(region: BaseTwoBatchRegions[((int)(NextWord() % ((ulong)regions)))]),
                third: Draw(region: BaseTwoBatchRegions[((int)(NextWord() % ((ulong)regions)))])
            ) is { } failure) { return failure; }
        }
        // Above the lazy ceiling up to 2^61, where a batch left lazy too far carries its accumulators past their
        // operand bound; a lazy accumulator there leaves 4n only now and then, so the sweep is long.
        for (var count = 0; (count < (full ? 240_000 : 24_000)); ++count) {
            var region = (ScaledResidueRing64.LazyModulusCeiling, (ScaledResidueRing64.LazyModulusCeiling << 1));

            if (Check(first: Draw(region: region), fourth: Draw(region: region), second: Draw(region: region), third: Draw(region: region)) is { } failure) { return failure; }
        }
        // Just above the signed ceiling, where a batch left signed too far carries its accumulators past the signed word.
        for (var count = 0; (count < (full ? 60_000 : 6_000)); ++count) {
            var region = (ScaledResidueRing64.SignedModulusCeiling, (ScaledResidueRing64.SignedModulusCeiling + (ScaledResidueRing64.SignedModulusCeiling >>> 1)));

            if (Check(first: Draw(region: region), fourth: Draw(region: region), second: Draw(region: region), third: Draw(region: region)) is { } failure) { return failure; }
        }

        // Constructed odd parts: with k = L - w steps over a longest odd part of L bits, the prefixes a = ceil(d / 2^k)
        // reach 1, 2, 33, 64, 65 and 128 (65 through 128 needing m = a - 64, 64 and 128 the reduction by 2^64), and the
        // common lengths reach 1, 6, 7 and 8, where k is zero or one, and 63, the longest an odd part of a word has.
        // Each value d 2^s + 1 is placed at bit lengths in every region, so the same prefixes run in every arithmetic.
        int[] lengths = (full ? [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 13, 16, 31, 32, 33, 47, 57, 58, 59, 60, 61, 62, 63] : [1, 2, 5, 6, 7, 8, 9, 32, 57, 62, 63]);
        int[] widths = (full ? [33, 34, 40, 50, 58, 59, 60, 61, 62, 63, 64] : [33, 58, 60, 61, 62, 63, 64]);

        static ulong Place(ulong oddPart, int width) {
            var shift = (width - (64 - BitOperations.LeadingZeroCount(value: oddPart)));

            return ((0 < shift) ? (oddPart << shift) | 1UL : 0UL);
        }

        foreach (var length in lengths) {
            var top = (1UL << (length - 1));
            var all = (ulong.MaxValue >>> (64 - length));
            var low = Math.Max(val1: (length - 7), val2: 0);
            ulong[] leads = [all, (top | 1UL), ((low > 0) ? (top | (1UL << low)) | 1UL : all), ((length > 6) ? (top | (1UL << (length - 6))) | 1UL : all)];
            ulong[] companions = [1UL, ((length > 1) ? (all >>> 1) : 1UL), ((low > 1) ? ((1UL << low) - 1UL) : 1UL), ((low > 1) ? ((1UL << low) + 1UL) : 1UL), (((NextWord() & all) >>> 1) | 1UL)];

            foreach (var lead in leads) {
                foreach (var width in widths) {
                    var leadValue = Place(oddPart: lead, width: width);

                    if (0UL == leadValue) { continue; }
                    if (Check(first: leadValue, fourth: leadValue, second: leadValue, third: leadValue) is { } same) { return same; }

                    for (var i = 0; (i < companions.Length); ++i) {
                        var second = Place(oddPart: companions[i], width: width);
                        var third = Place(oddPart: companions[((i + 1) % companions.Length)], width: widths[((i + width) % widths.Length)]);
                        var fourth = Place(oddPart: companions[((i + 2) % companions.Length)], width: width);

                        if ((0UL == second) || (0UL == third) || (0UL == fourth)) { continue; }
                        if (CheckRotations(first: leadValue, fourth: fourth, second: second, third: third) is { } failure) { return failure; }
                    }
                }
            }
        }

        // Strong base-two pseudoprimes: (6k + 1)(12k + 1)(18k + 1) with all three factors prime is composite by
        // construction, and the oracle says which pass. Those above 2^63 are each decided beside three of their kind,
        // where the batch seeds by a shift, and beside values below 2^63 and below 2^32, where it seeds by a product;
        // those in [2^60, 2^62), where the batch is signed, beside three of their kind, beside values below the lazy
        // ceiling and beside values above 2^61, 2^62 and the signed ceiling, the last of which take the batch exact.
        var pseudoprimes = new List<ulong>();
        var bandPseudoprimes = new List<ulong>();

        if (BaseTwoPseudoprimes(band: bandPseudoprimes, full: false, high: pseudoprimes, low: new List<ulong>()) is { } construction) { return construction; }
        for (var i = 0; (i < bandPseudoprimes.Count); ++i) {
            var pseudoprime = bandPseudoprimes[i];

            if (CheckRotations(first: pseudoprime, fourth: bandPseudoprimes[((i + 3) % bandPseudoprimes.Count)], second: bandPseudoprimes[((i + 1) % bandPseudoprimes.Count)], third: bandPseudoprimes[((i + 2) % bandPseudoprimes.Count)]) is { } same) { return same; }
            if (CheckRotations(first: pseudoprime, fourth: Draw(region: BaseTwoBatchRegions[3]), second: Draw(region: BaseTwoBatchRegions[2]), third: Draw(region: BaseTwoBatchRegions[0])) is { } lower) { return lower; }
            if (CheckRotations(first: pseudoprime, fourth: Draw(region: BaseTwoBatchRegions[4]), second: Draw(region: BaseTwoBatchRegions[5]), third: Draw(region: BaseTwoBatchRegions[6])) is { } upper) { return upper; }
        }
        foreach (var value in BandShapedComposites(full: full)) {
            if (Check(first: value, fourth: Draw(region: BaseTwoBatchRegions[4]), second: Draw(region: BaseTwoBatchRegions[3]), third: Draw(region: BaseTwoBatchRegions[1])) is { } failure) { return failure; }
        }
        for (var i = 0; (i < pseudoprimes.Count); ++i) {
            var pseudoprime = pseudoprimes[i];

            if (CheckRotations(first: pseudoprime, fourth: pseudoprimes[((i + 3) % pseudoprimes.Count)], second: pseudoprimes[((i + 1) % pseudoprimes.Count)], third: pseudoprimes[((i + 2) % pseudoprimes.Count)]) is { } failure) { return failure; }
            if (CheckRotations(first: pseudoprime, fourth: Draw(region: BaseTwoBatchRegions[5]), second: Draw(region: BaseTwoBatchRegions[6]), third: Draw(region: BaseTwoBatchRegions[6])) is { } wide) { return wide; }
            if (CheckRotations(first: pseudoprime, fourth: Draw(region: BaseTwoBatchRegions[0]), second: Draw(region: BaseTwoBatchRegions[1]), third: Draw(region: BaseTwoBatchRegions[3])) is { } narrow) { return narrow; }
        }

        // The odd neighbours of every dispatch boundary on both sides, alone and beside drawn values of every region,
        // and the top of the carrier.
        var reach = (full ? 4_096UL : 256UL);

        foreach (var boundary in BaseTwoBatchBoundaries) {
            for (var offset = 1UL; (offset < reach); offset += 2UL) {
                var below = (boundary - offset) | 1UL;
                var above = (boundary + offset) | 1UL;

                if (Check(first: below, fourth: above, second: (below - 2UL), third: (above + 2UL)) is { } failure) { return failure; }
                if (Check(first: below, fourth: (below - 6UL), second: (below - 2UL), third: (below - 4UL)) is { } under) { return under; }
                if (Check(first: above, fourth: (above + 6UL), second: (above + 2UL), third: (above + 4UL)) is { } over) { return over; }
                if (Check(first: below, fourth: Draw(region: BaseTwoBatchRegions[6]), second: Draw(region: BaseTwoBatchRegions[6]), third: Draw(region: BaseTwoBatchRegions[6])) is { } mixed) { return mixed; }
            }
        }
        for (var offset = 0UL; (offset < reach); offset += 8UL) {
            if (Check(first: (ulong.MaxValue - offset), fourth: ((ulong.MaxValue - offset) - 6UL), second: ((ulong.MaxValue - offset) - 2UL), third: ((ulong.MaxValue - offset) - 4UL)) is { } failure) { return failure; }
        }

        return BaseTwoBatchRefusals();
    }
    private static string? BaseTwoBatchRefusals() {
        var outOfRange = typeof(ArgumentOutOfRangeException);
        var wrongLength = typeof(ArgumentException);

        return (Refuses(action: () => PrimeKernels.PassesBaseTwoBatch(values: [3UL, 5UL, 7UL]), parameterName: "values", type: wrongLength, what: "PassesBaseTwoBatch of three values")
            ?? (Refuses(action: () => PrimeKernels.PassesBaseTwoBatch(values: [3UL, 5UL, 7UL, 9UL, 11UL]), parameterName: "values", type: wrongLength, what: "PassesBaseTwoBatch of five values")
            ?? (Refuses(action: () => PrimeKernels.PassesBaseTwoBatch(values: [3UL, 5UL, 8UL, 9UL]), parameterName: "values", type: outOfRange, what: "PassesBaseTwoBatch over an even value")
            ?? (Refuses(action: () => PrimeKernels.PassesBaseTwoBatch(values: [3UL, 5UL, 7UL, 1UL]), parameterName: "values", type: outOfRange, what: "PassesBaseTwoBatch over one")
            ?? Refuses(action: () => PrimeKernels.PassesBaseTwoBatch(values: [0UL, 5UL, 7UL, 9UL]), parameterName: "values", type: outOfRange, what: "PassesBaseTwoBatch over zero")))));
    }
}
