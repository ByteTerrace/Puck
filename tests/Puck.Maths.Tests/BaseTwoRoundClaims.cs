using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // The arithmetic regions the single round dispatches on, as [low, high) bands of odd values, the last closed at the top
    // of the carrier: the centered seeded walk below the centered ceiling, split at 2^32, 2^58, the lazy ceiling and
    // 2^61, and the exact least-significant-bit-first walk from there.
    private static readonly (ulong Low, ulong High)[] BaseTwoRoundRegions = [
        (3UL, (1UL << 32)), ((1UL << 32), (1UL << 58)), ((1UL << 58), ScaledResidueRing64.LazyModulusCeiling),
        (ScaledResidueRing64.LazyModulusCeiling, (1UL << 61)), ((1UL << 61), ScaledResidueRing64.CenteredModulusCeiling),
        (ScaledResidueRing64.CenteredModulusCeiling, (1UL << 63)), ((1UL << 63), ulong.MaxValue),
    ];

    /// <summary>Compares the single base-two round with plain modular exponentiation at the Default tier's operands.</summary>
    /// <returns>The first counterexample, or <see langword="null"/>.</returns>
    public static string? BaseTwoRoundMatchesModularPower() => BaseTwoRoundMatchesModularPower(full: false);
    /// <summary>Compares the single base-two round with plain modular exponentiation at the Deep tier's stronger operands.</summary>
    /// <returns>The first counterexample, or <see langword="null"/>.</returns>
    public static string? BaseTwoRoundMatchesModularPowerDeep() => BaseTwoRoundMatchesModularPower(full: true);

    private static string? BaseTwoRoundMatchesModularPower(bool full) {
        var generator = Pcg32XshRr.Create(state: 2087, stream: 31);
        var passes = 0;

        ulong NextWord() => (((ulong)generator.NextUInt32()) << 32) | generator.NextUInt32();
        ulong Draw((ulong Low, ulong High) region) => (region.Low + (NextWord() % (region.High - region.Low))) | 1UL;

        // The value's decision against the oracle, which first checks that two and its inverse accept at the same round.
        string? Check(ulong value) {
            var round = Oracles.StrongBaseTwoAcceptanceRound(inverted: false, value: value);
            var inverseRound = Oracles.StrongBaseTwoAcceptanceRound(inverted: true, value: value);

            if (round != inverseRound) { return $"two and its inverse accept {value} at rounds {round} and {inverseRound}"; }

            var expected = (0 <= round);

            if (expected != PrimeKernels.PassesBaseTwoRound(value: value)) {
                return $"PassesBaseTwoRound({value}) = {!expected} where the modular power says {expected}";
            }
            if (expected) { ++passes; }

            return null;
        }

        // Every odd value of a contiguous band, which holds every odd part through the seed's window at every short
        // exponent of two, and the strong base-two pseudoprimes 2047, 3277, 4033, 4681 and 8321.
        for (var value = 3UL; (value < (full ? (1UL << 21) : (1UL << 17))); value += 2UL) {
            if (Check(value: value) is { } failure) { return failure; }
        }

        // Drawn values inside each region.
        var draws = (full ? 48_000 : 4_800);

        foreach (var region in BaseTwoRoundRegions) {
            var before = passes;

            for (var count = 0; (count < draws); ++count) {
                if (Check(value: Draw(region: region)) is { } failure) { return failure; }
            }
            if ((passes - before) < 100) { return $"only {(passes - before)} values in [{region.Low}, {region.High}) passed, so the accepting finish went unexercised there"; }
        }
        // Just above the centered ceiling, where a seeded walk left centered too far doubles its accumulator past the
        // signed word.
        for (var count = 0; (count < (full ? 240_000 : 24_000)); ++count) {
            if (Check(value: Draw(region: (ScaledResidueRing64.CenteredModulusCeiling, (ScaledResidueRing64.CenteredModulusCeiling + (ScaledResidueRing64.CenteredModulusCeiling >>> 1))))) is { } failure) { return failure; }
        }

        // Constructed values d 2^s + 1. Every odd part below 2^10, so the seeded walk takes zero to three steps from every
        // prefix the window reaches, 64 and 128 among them, the reductions by 2^64; and, for each odd-part bit length L
        // from 8 through 63, the parts whose prefixes a = ceil(d / 2^(L - 7)) are 65 (the least, 2^(L - 1) + 1), 66,
        // 128 (all ones, the reduction by 2^64) and a drawn one. Each sits at every exponent of two that keeps the value
        // in the carrier, so the same prefixes run in every region.
        var parts = new List<ulong>();

        for (var oddPart = 1UL; (oddPart < (1UL << 10)); oddPart += 2UL) { parts.Add(item: oddPart); }
        for (var length = 8; (length <= 63); ++length) {
            var top = (1UL << (length - 1));
            var steps = (length - 7);

            parts.Add(item: top | 1UL);
            parts.Add(item: (top | (1UL << steps)) | 1UL);
            parts.Add(item: (ulong.MaxValue >>> (64 - length)));
            parts.Add(item: (top | (NextWord() & (top - 1UL))) | 1UL);
        }
        foreach (var oddPart in parts) {
            for (var twoExponent = 1; (twoExponent <= BitOperations.LeadingZeroCount(value: oddPart)); ++twoExponent) {
                if (Check(value: (oddPart << twoExponent) | 1UL) is { } failure) { return failure; }
            }
        }

        // Strong base-two pseudoprimes (6k + 1)(12k + 1)(18k + 1) with all three factors prime, composite by construction,
        // which the oracle alone decides: those above 2^63, the thirty-six led by 9275096661522257161, those in
        // [2^60, 2^62), where the walk is centered, and those below 2^60.
        var high = new List<ulong>();
        var band = new List<ulong>();
        var low = new List<ulong>();

        if (BaseTwoPseudoprimes(band: band, full: full, high: high, low: low) is { } construction) { return construction; }
        foreach (var pseudoprime in low.Concat(second: band).Concat(second: high)) {
            if (Check(value: pseudoprime) is { } failure) { return failure; }
        }
        // Prime powers inside [2^60, 2^62), the powers of the Wieferich primes 1093 and 3511 the carrier holds, whose
        // squares are base-two pseudoprimes, and products of primes just above 2^30 with primes just below 2^31.
        foreach (var value in BandShapedComposites(full: full)) {
            if (Check(value: value) is { } failure) { return failure; }
        }

        // The odd neighbours of every dispatch boundary on both sides, and the top of the carrier.
        var reach = (full ? 4_096UL : 256UL);

        foreach (var boundary in ((ulong[])[(1UL << 32), (1UL << 58), ScaledResidueRing64.LazyModulusCeiling, (1UL << 61), ScaledResidueRing64.CenteredModulusCeiling, (1UL << 63)])) {
            for (var offset = 1UL; (offset < reach); offset += 2UL) {
                if ((Check(value: (boundary - offset) | 1UL) ?? Check(value: (boundary + offset) | 1UL)) is { } failure) { return failure; }
            }
        }
        for (var offset = 0UL; (offset < reach); offset += 2UL) {
            if (Check(value: (ulong.MaxValue - offset)) is { } failure) { return failure; }
        }

        return BaseTwoRoundRefusals();
    }
    /// <summary>Constructs the strong base-two pseudoprimes <c>(6k + 1)(12k + 1)(18k + 1)</c> with all three factors prime below <c>2^60</c>, in <c>[2^60, 2^62)</c> and above <c>2^63</c>, each decided by the oracle alone.</summary>
    /// <param name="full">Whether to reach the Deep tier's longer run below <c>2^60</c>.</param>
    /// <param name="low">Receives those below <see cref="ScaledResidueRing64.LazyModulusCeiling"/>.</param>
    /// <param name="band">Receives those in <c>[2^60, 2^62)</c>.</param>
    /// <param name="high">Receives those above <c>2^63</c>.</param>
    /// <returns>A failure when the construction does not find the pseudoprimes the laws rely on, or <see langword="null"/>.</returns>
    private static string? BaseTwoPseudoprimes(bool full, List<ulong> low, List<ulong> band, List<ulong> high) {
        foreach (var (first, last, into) in (((ulong First, ulong Last, List<ulong> Into)[])[(1UL, (full ? 96_000UL : 24_000UL), low), (95_000UL, 153_000UL, band), (192_000UL, 243_000UL, high)])) {
            for (var k = first; (k < last); ++k) {
                var product = (((((UInt128)((6UL * k) + 1UL)) * ((12UL * k) + 1UL))) * ((18UL * k) + 1UL));
                var inside = ((into == low)
                    ? (product < ScaledResidueRing64.LazyModulusCeiling)
                    : ((into == band)
                        ? ((product >= ScaledResidueRing64.LazyModulusCeiling) && (product < ScaledResidueRing64.CenteredModulusCeiling))
                        : ((product > (UInt128.One << 63)) && (product <= ulong.MaxValue))));

                if (!inside) { continue; }
                if (!Oracles.ExactPrimality(value: ((6UL * k) + 1UL)) || !Oracles.ExactPrimality(value: ((12UL * k) + 1UL)) || !Oracles.ExactPrimality(value: ((18UL * k) + 1UL))) { continue; }
                if (0 <= Oracles.StrongBaseTwoAcceptanceRound(inverted: false, value: ((ulong)product))) { into.Add(item: ((ulong)product)); }
            }
        }
        if ((high.Count < 36) || (high[0] != 9_275_096_661_522_257_161UL)) {
            return $"the construction found {high.Count} strong base-two pseudoprimes above 2^63, led by {high.FirstOrDefault()}, not the 36 led by 9275096661522257161";
        }
        if (band.Count < 50) { return $"the construction found only {band.Count} strong base-two pseudoprimes in [2^60, 2^62)"; }
        if (low.Count < 20) { return $"the construction found only {low.Count} strong base-two pseudoprimes below the lazy ceiling"; }

        return null;
    }
    /// <summary>Lists composites shaped against the centered region <c>[2^60, 2^62)</c>: prime powers inside it, the powers of the Wieferich primes the carrier holds, and products of primes either side of <c>2^30</c> and <c>2^31</c>.</summary>
    /// <param name="full">Whether to reach the Deep tier's wider prime set.</param>
    /// <returns>The composites.</returns>
    private static List<ulong> BandShapedComposites(bool full) {
        var values = new List<ulong>();
        var smallPrimes = Oracles.SmallPrimes;

        for (var index = 1; (smallPrimes[index] < (full ? 2_000U : 200U)); ++index) {
            var prime = ((ulong)smallPrimes[index]);

            for (var power = (prime * prime); (power <= (ulong.MaxValue / prime)); power *= prime) {
                if ((power >= (ScaledResidueRing64.LazyModulusCeiling / prime)) && (power < (ScaledResidueRing64.CenteredModulusCeiling / prime))) { values.Add(item: (power * prime)); }
            }
        }
        // 1093^2 and 3511^2 are base-two Fermat pseudoprimes; their powers carry the same factor.
        foreach (var wieferich in ((ulong[])[1_093UL, 3_511UL])) {
            for (var power = (wieferich * wieferich); ; power *= wieferich) {
                values.Add(item: power);

                if (power > (ulong.MaxValue / wieferich)) { break; }
            }
        }

        var above = new List<ulong>();
        var below = new List<ulong>();

        for (var candidate = ((1UL << 30) + 1UL); (above.Count < (full ? 64 : 16)); candidate += 2UL) {
            if (Oracles.ExactPrimality(value: candidate)) { above.Add(item: candidate); }
        }
        for (var candidate = ((1UL << 31) - 1UL); (below.Count < (full ? 64 : 16)); candidate -= 2UL) {
            if (Oracles.ExactPrimality(value: candidate)) { below.Add(item: candidate); }
        }
        foreach (var first in above) {
            foreach (var second in below) {
                values.Add(item: (first * second));
            }
        }
        for (var i = 0; (i < below.Count); ++i) {
            for (var j = i; (j < below.Count); ++j) { values.Add(item: (below[i] * below[j])); }
        }

        return values;
    }
    private static string? BaseTwoRoundRefusals() {
        var outOfRange = typeof(ArgumentOutOfRangeException);

        return (Refuses(action: () => PrimeKernels.PassesBaseTwoRound(value: 0UL), parameterName: "value", type: outOfRange, what: "PassesBaseTwoRound(0)")
            ?? (Refuses(action: () => PrimeKernels.PassesBaseTwoRound(value: 1UL), parameterName: "value", type: outOfRange, what: "PassesBaseTwoRound(1)")
            ?? (Refuses(action: () => PrimeKernels.PassesBaseTwoRound(value: 2UL), parameterName: "value", type: outOfRange, what: "PassesBaseTwoRound(2)")
            ?? Refuses(action: () => PrimeKernels.PassesBaseTwoRound(value: (1UL << 40)), parameterName: "value", type: outOfRange, what: "PassesBaseTwoRound(2^40)"))));
    }
}
