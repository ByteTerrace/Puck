using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly (ulong Low, ulong High)[] PrimeSelectionIntervals = [
        (0, 1), (0, 2), (2, 3), (0, 31), (24, 30), (65519, 65561),
        (4_294_967_281, 4_294_967_311), (1_000_000_000_000, 1_000_000_000_100),
        (18_446_744_073_709_551_557, ulong.MaxValue), (ulong.MaxValue, ulong.MaxValue),
    ];
    private static readonly (uint Index, uint Prime)[] PrimeSelectionRanks = [
        (6541, 65521), (6542, 65537), (999999, 15485863), (99999999, 2038074743),
        (203280220, 4294967291), (203280221, 0), (uint.MaxValue, 0),
    ];

    public static string? PrimeSelectionRanksMatchSieve() {
        var expected = Oracles.PrimeSieve(inclusiveMaximum: 100_000);
        var index = 0U;

        for (var value = 2; (value < expected.Length); ++value) {
            if (!expected[value]) { continue; }
            var actual = ((uint)index).NthPrime();

            if (actual != value) { return $"rank {index}: expected {value}, got {actual}"; }
            ++index;
        }
        foreach (var row in PrimeSelectionRanks) {
            var actual = row.Index.NthPrime();

            if (actual != row.Prime) { return $"rank {row.Index}: expected {row.Prime}, got {actual}"; }
        }
        return null;
    }
    public static string? PrimeSelectionRandomMapping() {
        foreach (var (low, high) in PrimeSelectionIntervals) {
            var pool = new List<ulong>();

            for (var value = low; ; ++value) {
                if ((high <= 65535) ? Oracles.ExactPrimality(value: value)
                    : ((value is 2 or 3 or 5) || ((value >= 7) && (BigInteger.GreatestCommonDivisor(left: value, right: 30) == 1)))) {
                    pool.Add(item: value);
                }
                if (value == high) { break; }
            }
            if (pool.Count == 0) {
                var generator = new PrimeSelectionWord(word: ulong.MaxValue);

                if (PrimeExploration.TryRandomPrime(generator: ref generator, high: high, low: low, maxAttempts: 3, prime: out var value) || (value != 0) || (generator.Draws != 0)) {
                    return $"empty candidate interval [{low},{high}] produced {value} or consumed draws";
                }
            }
            for (var index = 0; (index < pool.Count); ++index) {
                var word = ((ulong)(((((BigInteger)(index + 1)) << 64) - 1) / pool.Count));
                var generator = new PrimeSelectionWord(word: word);
                var found = PrimeExploration.TryRandomPrime(generator: ref generator, high: high, low: low, maxAttempts: 1, prime: out var value);
                var expected = Oracles.ExactPrimality(value: pool[index]);

                if ((found != expected) || (value != (expected ? pool[index] : 0UL))) {
                    return $"interval [{low},{high}], rank {index}: expected prime decision {expected} at {pool[index]}, got {found}/{value}";
                }
                var expectedDraws = ((pool.Count == 1) ? 0 : 2);

                if (generator.Draws != expectedDraws) { return $"rank {index}: expected {expectedDraws} raw draws, got {generator.Draws}"; }
            }
        }
        // Independently force the three exceptional primes within a full-width wheel sample.
        var wholeCount = (((((((BigInteger)ulong.MaxValue) / 30) * 8) + 4) + 3) - 1);

        for (var index = 0; (index < 3); ++index) {
            var word = ((ulong)(((((BigInteger)(index + 1)) << 64) - 1) / wholeCount));
            var generator = new PrimeSelectionWord(word: word);
            ReadOnlySpan<ulong> exceptional = [2, 3, 5];

            if (!PrimeExploration.TryRandomPrime(generator: ref generator, high: ulong.MaxValue, low: 0, maxAttempts: 1, prime: out var value) || (value != exceptional[index])) {
                return $"full-width exceptional slot {index} selected {value}";
            }
        }
        // A zero word falls in the biased reduction window when the pool has three members.
        var zeros = new PrimeSelectionWord(word: 0);

        if (PrimeExploration.TryRandomPrime(generator: ref zeros, high: 5, low: 2, maxAttempts: 7, prime: out var zeroResult) || (zeroResult != 0) || (zeros.Draws != 14)) {
            return "range-reduction rejection failed to exhaust exactly seven raw 64-bit draws";
        }
        var composite = new PrimeSelectionWord(word: ulong.MaxValue);

        if (PrimeExploration.TryRandomPrime(generator: ref composite, high: (65537UL * 65537), low: (65537UL * 65537), maxAttempts: 7, prime: out var compositeResult) || (compositeResult != 0) || (composite.Draws != 0)) {
            return "singleton composite was not decided without raw draws";
        }
        return (Refuses(() => PrimeExploration.TryRandomPrime(7, 5, ref zeros, out _), typeof(ArgumentOutOfRangeException), "high", "reversed random-prime interval")
            ?? Refuses(() => PrimeExploration.TryRandomPrime(generator: ref zeros, high: 5, low: 2, maxAttempts: 0, prime: out _), typeof(ArgumentOutOfRangeException), "maxAttempts", "empty random-prime budget"));
    }
    public static string? PrimeSelectionRandomReplay() {
        foreach (var (low, high) in new (ulong, ulong)[] { (0, 65535), (0, uint.MaxValue), ((1UL << 63), ulong.MaxValue), (0, ulong.MaxValue) }) {
            var generator = Pcg32XshRr.Create(state: 42, stream: 54);
            var twin = Pcg32XshRr.Create(state: 42, stream: 54);

            for (var draw = 0; (draw < 256); ++draw) {
                var found = PrimeExploration.TryRandomPrime(low, high, ref generator, out var prime);
                var repeated = PrimeExploration.TryRandomPrime(low, high, ref twin, out var repeat);

                if (!found || !repeated || (prime != repeat) || (prime < low) || (prime > high) || !Oracles.ExactPrimality(value: prime) || (generator.State != twin.State)) {
                    return $"random-prime interval [{low},{high}], draw {draw} failed exact primality, bounds or replay: {prime}/{repeat}";
                }
            }
        }
        return null;
    }

    private struct PrimeSelectionWord(ulong word) : IDrawGenerator {
        public int Draws { get; private set; }

        public uint NextUInt32() => (((++Draws & 1) == 1) ? (uint)(word >> 32) : unchecked((uint)word));
        public uint NextUInt32(uint minimum, uint maximum) => throw new InvalidOperationException(message: "The selector must count raw draws.");
        public void Advance(ulong count) => throw new InvalidOperationException(message: "The selector must not skip caller draws.");
    }
}
