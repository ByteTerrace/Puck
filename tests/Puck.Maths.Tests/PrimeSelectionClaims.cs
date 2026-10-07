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
    public static string? PrimeSelectionCustomDecisionMapping() {
        foreach (var (low, high) in PrimeSelectionIntervals) {
            var ordinary = Pcg32XshRr.Create(state: 42, stream: 54);
            var custom = ordinary;

            for (var request = 0; (request < 8); ++request) {
                var found = PrimeExploration.TryRandomPrime(low: low, high: high, generator: ref ordinary, prime: out var prime);
                var customFound = PrimeExploration.TryRandomPrime<Pcg32XshRr, IndependentSelectionDecision>(
                    low: low, high: high, generator: ref custom, prime: out var selected);

                if ((found != customFound) || (prime != selected) || (ordinary.State != custom.State)) {
                    return $"custom decision changed selection or draws in [{low},{high}] at request {request}";
                }
                if (found && ((prime < low) || (prime > high) || !Oracles.ExactPrimality(value: prime))) {
                    return $"custom decision returned {prime} outside the interval or composite";
                }
                if (!found && (prime != 0)) { return "custom decision failure lost the zero sentinel"; }
            }
        }
        foreach (var factor in new ulong[] { 7, 59, 61, 163 }) {
            var composite = (factor * 4_294_967_311UL);
            var generator = new PrimeSelectionWord(word: ulong.MaxValue);

            if (PrimeExploration.TryRandomPrime<PrimeSelectionWord, IndependentSelectionDecision>(low: composite, high: composite,
                generator: ref generator, prime: out var selected) || (selected != 0) || (generator.Draws != 0)) {
                return $"custom decision filter failed for factor {factor}";
            }
        }
        var invalid = new PrimeSelectionWord(word: 0);

        return (Refuses(() => PrimeExploration.TryRandomPrime<PrimeSelectionWord, IndependentSelectionDecision>(7, 5, ref invalid, out _),
                typeof(ArgumentOutOfRangeException), "high", "custom random-prime reversed interval")
            ?? Refuses(() => PrimeExploration.TryRandomPrime<PrimeSelectionWord, IndependentSelectionDecision>(generator: ref invalid, high: 5, low: 2, maxAttempts: 0, prime: out _),
                typeof(ArgumentOutOfRangeException), "maxAttempts", "custom random-prime empty budget"));
    }
    public static string? PrimeSelectionSpeculativeMatchesSequential() {
        // Wide bands where batches fill, a uint-straddling band whose narrow candidates interrupt batches, the full
        // carrier with its exceptional primes, and a short window; budgets from one draw upward so that the budget
        // runs out before, inside and after a batch.
        (ulong Low, ulong High)[] intervals = [
            ((1UL << 63), ulong.MaxValue), (1_000_000_000_000_000_000, 1_000_000_000_000_999_999),
            (((1UL << 32) + 1UL), (1UL << 33)), (90_000, (1UL << 40)), (0, ulong.MaxValue),
            (18_446_744_073_709_551_557, ulong.MaxValue), ((uint.MaxValue - 4_000UL), (uint.MaxValue + 4_000UL)),
        ];
        int[] budgets = [1, 2, 3, 4, 5, 6, 7, 8, 13, 256];

        foreach (var (low, high) in intervals) {
            foreach (var budget in budgets) {
                var speculative = Pcg32XshRr.Create(state: ((ulong)budget), stream: 54);
                var sequential = speculative;
                var shared = new SharedSelectionState { Generator = speculative };
                var referenced = new ReferencedSelectionGenerator(state: shared);

                for (var request = 0; (request < 48); ++request) {
                    var found = PrimeExploration.TryRandomPrime(generator: ref speculative, high: high, low: low, maxAttempts: budget, prime: out var prime);
                    var sequentialFound = PrimeExploration.TryRandomPrime<Pcg32XshRr, SequentialSelectionDecision>(
                        generator: ref sequential, high: high, low: low, maxAttempts: budget, prime: out var sequentialPrime);
                    var referencedFound = PrimeExploration.TryRandomPrime(generator: ref referenced, high: high, low: low, maxAttempts: budget, prime: out var referencedPrime);

                    if ((found != sequentialFound) || (prime != sequentialPrime) || (speculative.State != sequential.State)) {
                        return $"[{low},{high}] budget {budget} request {request}: interleaved selection gave {found}/{prime}/state {speculative.State}, the sequential order {sequentialFound}/{sequentialPrime}/state {sequential.State}";
                    }
                    if ((found != referencedFound) || (prime != referencedPrime) || (speculative.State != shared.Generator.State)) {
                        return $"[{low},{high}] budget {budget} request {request}: interleaved selection gave {found}/{prime}/state {speculative.State}, a generator holding a reference {referencedFound}/{referencedPrime}/state {shared.Generator.State}";
                    }
                    if (found ? ((prime < low) || (prime > high) || !Oracles.ExactPrimality(value: prime)) : (prime != 0UL)) {
                        return $"[{low},{high}] budget {budget} request {request}: selection returned {found}/{prime}";
                    }
                }
            }
        }

        return null;
    }
    public static string? PrimeSelectionNarrowRouteMatchesReference() {
        // Intervals above the table and at or below uint.MaxValue: exceptional primes at the front, the top of the
        // narrow carrier, and a prime gap whose every request exhausts its budget.
        (ulong Low, ulong High)[] intervals = [
            (0, 70_000), (65_536, 1_000_000), ((1UL << 31), uint.MaxValue), ((uint.MaxValue - 4_000UL), uint.MaxValue),
            (370_262, 370_372),
        ];
        int[] budgets = [1, 2, 3, 5, 8, 256];

        foreach (var (low, high) in intervals) {
            foreach (var budget in budgets) {
                var generator = Pcg32XshRr.Create(state: ((ulong)budget), stream: 54);
                var reference = generator;

                for (var request = 0; (request < 24); ++request) {
                    var found = PrimeExploration.TryRandomPrime(generator: ref generator, high: high, low: low, maxAttempts: budget, prime: out var prime);
                    var expected = NarrowSelectionReference(attempts: budget, generator: ref reference, high: high, low: low, prime: out var expectedPrime);

                    if ((found != expected) || (prime != expectedPrime) || (generator.State != reference.State)) {
                        return $"[{low},{high}] budget {budget} request {request}: selection gave {found}/{prime}/state {generator.State}, the reference {expected}/{expectedPrime}/state {reference.State}";
                    }
                }
            }
        }

        return null;
    }

    // The documented sequential order, written out over BigInteger: candidates are the exceptional primes at or above
    // low followed by every value from max(low, 7) through high coprime to thirty, one index per accepted Lemire
    // product of a raw word built from two draws, high half first, each decided by Oracles.ExactPrimality.
    private static bool NarrowSelectionReference(ulong low, ulong high, ref Pcg32XshRr generator, int attempts, out ulong prime) {
        var units = new List<int>();

        for (var residue = 0; (residue < 30); ++residue) {
            if (BigInteger.GreatestCommonDivisor(left: residue, right: 30) == BigInteger.One) { units.Add(item: residue); }
        }

        ulong UnitsBelow(ulong value) {
            var below = ((value / 30) * ((ulong)units.Count));

            foreach (var unit in units) {
                if (((ulong)unit) < (value % 30)) { ++below; }
            }

            return below;
        }

        var exceptional = new List<ulong>();

        foreach (var candidate in new ulong[] { 2, 3, 5 }) {
            if (candidate >= low) { exceptional.Add(item: candidate); }
        }
        var start = Math.Max(val1: low, val2: 7UL);
        var first = UnitsBelow(value: start);
        var count = ((BigInteger)((UnitsBelow(value: (high + 1UL)) - first) + ((ulong)exceptional.Count)));
        var radix = (BigInteger.One << 64);
        var threshold = (radix % count);

        while (attempts-- > 0) {
            var word = (((BigInteger)generator.NextUInt32()) << 32) | generator.NextUInt32();
            var product = (word * count);

            if ((product % radix) < threshold) { continue; }

            var index = ((ulong)(product >> 64));
            ulong value;

            if (index < ((ulong)exceptional.Count)) {
                value = exceptional[((int)index)];
            } else {
                var rank = ((first + index) - ((ulong)exceptional.Count));

                value = ((30 * (rank / ((ulong)units.Count))) + ((ulong)units[((int)(rank % ((ulong)units.Count)))]));
            }
            if ((value < start) && (index >= ((ulong)exceptional.Count))) { throw new InvalidOperationException(message: $"wheel value {value} below the interval start {start}"); }
            if (Oracles.ExactPrimality(value: value)) {
                prime = value;
                return true;
            }
        }

        prime = 0;
        return false;
    }

    public static string? PrimeSelectionTableRanksMatchSieve() {
        var sieve = Oracles.PrimeSieve(inclusiveMaximum: 65_536);
        var below = 0;

        for (var value = 0; (value <= 65_536); ++value) {
            var rank = PrimeKernels.CountOddPrimesBelow(value: ((uint)value));

            if (rank != below) { return $"CountOddPrimesBelow({value}) = {rank}, where the sieve counts {below} odd primes below it"; }
            if ((value < sieve.Length) && sieve[value] && (2 != value)) { ++below; }
        }

        return null;
    }

    private readonly struct IndependentSelectionDecision : IPrimeCandidateDecision {
        public static bool IsPrimeCandidate(ulong value) {
            if (value <= uint.MaxValue) { throw new InvalidOperationException(message: "A narrow value reached the wide decision."); }
            for (var divisor = 2UL; (divisor <= 163); ++divisor) {
                if ((value % divisor) == 0) { throw new InvalidOperationException(message: $"Unfiltered divisor {divisor} reached the wide decision."); }
            }
            return Oracles.ExactPrimality(value: value);
        }
    }
    // The production decision itself, under a type the sampler does not recognize, so selection takes the sequential order.
    private readonly struct SequentialSelectionDecision : IPrimeCandidateDecision {
        public static bool IsPrimeCandidate(ulong value) => PrimeField64.IsBaillieProbablePrime(value: value);
    }
    private sealed class SharedSelectionState {
        public Pcg32XshRr Generator;
    }
    // A generator whose state lives behind a reference, which is what selects the sequential order for the default decision.
    private readonly struct ReferencedSelectionGenerator(SharedSelectionState state) : IDrawGenerator {
        public uint NextUInt32() => state.Generator.NextUInt32();
        public uint NextUInt32(uint minimum, uint maximum) => throw new InvalidOperationException(message: "The selector must count raw draws.");
        public void Advance(ulong count) => throw new InvalidOperationException(message: "The selector must not skip caller draws.");
    }
    private struct PrimeSelectionWord(ulong word) : IDrawGenerator {
        public int Draws { get; private set; }

        public uint NextUInt32() => (((++Draws & 1) == 1) ? (uint)(word >> 32) : unchecked((uint)word));
        public uint NextUInt32(uint minimum, uint maximum) => throw new InvalidOperationException(message: "The selector must count raw draws.");
        public void Advance(ulong count) => throw new InvalidOperationException(message: "The selector must not skip caller draws.");
    }
}
