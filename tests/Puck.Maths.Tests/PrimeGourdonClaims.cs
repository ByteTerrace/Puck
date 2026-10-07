using System.Runtime.Intrinsics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private delegate int GourdonHardLeafFilter(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime, Span<int> coordinates);
    private delegate void GourdonQuotientRung(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients);

    private static readonly (string Name, GourdonHardLeafFilter Filter)[] GourdonHardLeafFilterRungs = [
        ("Vector512", PrimeExtensions.FilterGourdonHardLeaves<VectorLanes512, Vector512<byte>>),
        ("Vector256", PrimeExtensions.FilterGourdonHardLeaves<VectorLanes256, Vector256<byte>>),
        ("Vector128", PrimeExtensions.FilterGourdonHardLeaves<VectorLanes128, Vector128<byte>>),
        ("scalar", PrimeExtensions.FilterGourdonHardLeavesScalar),
    ];
    private static readonly (string Name, GourdonQuotientRung Divide)[] GourdonQuotientRungs = [
        ("Vector512", PrimeExtensions.DivideGourdonQuotients512),
        ("Vector256", PrimeExtensions.DivideGourdonQuotients256),
        ("Vector128", PrimeExtensions.DivideGourdonQuotients128),
        ("scalar", static (dividend, divisors, quotients) => PrimeExtensions.DivideGourdonQuotientsScalar(dividend: dividend, divisors: divisors, quotients: quotients, start: 0)),
    ];
    // Divisors across the uint carrier: tiny, primes near powers of two, the 1e16 cutoff and the carrier's top.
    private static readonly uint[] GourdonQuotientDivisors = [
        1, 2, 3, 7, 23, 29, 30, 65_521, 65_537, 1_048_573, 4_496_509, 33_554_393, 134_217_689, 2_147_483_647,
        2_147_483_648, 4_294_967_291, 4_294_967_295,
    ];
    // Quotients at the A+C (2^32) and D (2^43) ranges and the rungs' exclusive limit 2^48.
    private static readonly ulong[] GourdonQuotientAnchors = [
        0, 1, 2, 4_294_967_295, 4_294_967_296, 8_796_093_022_207, 8_796_093_022_208, 140_737_488_355_328, 281_474_976_710_655,
    ];
    // Least-factor magnitudes on both sides of every threshold below, the 32767 prime sentinel and zero for a
    // repeated factor. Every value is stored with and without the Moebius sign bit.
    private static readonly ushort[] GourdonHardLeafFactorValues = [
        0, 1, 22, 23, 24, 28, 29, 30, 180, 181, 182, 4092, 4093, 4094, 32748, 32749, 32750, 32766, 32767,
    ];
    private static readonly uint[] GourdonHardLeafThresholds = [23, 29, 181, 4093, 32749];
    // 32 is the widest rung's lane count and 128 the production batch; the others misalign batch ends.
    private static readonly int[] GourdonHardLeafCapacities = [32, 33, 47, 128];

    public static string? PrimeGourdonCheckpointIntervalCount() {
        // Table value pi(2^44); the interval above 2^44 is sieved independently.
        const ulong Checkpoint = (1UL << 44);
        const ulong CheckpointCount = 597_116_381_732;
        // The local sieve budget above 2^44 is 41904370 integers, so this distance needs a global count.
        const ulong Distance = (1UL << 26);
        var interval = Oracles.PrimeWideIntegerWindow(high: (Checkpoint + Distance), low: (Checkpoint + 1), rootBound: 4_194_313);
        var profile = PrimeExtensions.ProfilePrimeCountingFunction(value: (Checkpoint + Distance));
        var expected = (CheckpointCount + ((ulong)interval.Count));

        if ((profile.Counts.Count != 1) || (profile.Counts[0].Route != PrimeCountRoute.Gourdon)) {
            return $"pi({(Checkpoint + Distance)}) did not use one Gourdon count";
        }
        return ((profile.Result == expected) ? null
            : $"Gourdon pi({(Checkpoint + Distance)}): published pi(2^44) plus {interval.Count} sieved primes is {expected}, got {profile.Result}");
    }
    public static string? PrimeGourdonHardLeafFilterRungs() {
        var factors = new ushort[4099];

        // A fixed mixing walk visits every value, signed and unsigned, in many neighbour patterns.
        for (var index = 0; (index < factors.Length); ++index) {
            var value = GourdonHardLeafFactorValues[(((index * 7) + (index / 11)) % GourdonHardLeafFactorValues.Length)];
            var negative = ((((index * 13) + (index / 5)) % 3) == 0);

            factors[index] = ((ushort)(value | (negative ? 0x8000 : 0)));
        }
        // Ranges: empty, shorter than every vector, straddling each width, and a long dense run near the array end.
        (int First, int Last)[] ranges = [
            (0, -1), (0, 0), (5, -1), (31, 0), (32, -1), (33, -1), (64, 1), (95, 30), (200, 3), (1000, 517),
            ((factors.Length - 1), -1), ((factors.Length - 1), (factors.Length - 70)),
        ];
        var expected = new List<int>();
        var actual = new List<int>();
        var buffer = new int[GourdonHardLeafCapacities[^1]];

        foreach (var prime in GourdonHardLeafThresholds) {
            foreach (var (first, last) in ranges) {
                expected.Clear();
                for (var coordinate = first; (coordinate > last); --coordinate) {
                    if ((factors[coordinate] & 0x7FFF) > prime) { expected.Add(item: coordinate); }
                }
                foreach (var capacity in GourdonHardLeafCapacities) {
                    foreach (var (name, filter) in GourdonHardLeafFilterRungs) {
                        var failure = PrimeGourdonFilterRun(actual: actual, capacity: capacity, coordinates: buffer, factors: factors,
                            filter: filter, first: first, last: last, prime: prime);

                        failure ??= (expected.SequenceEqual(second: actual) ? null
                            : $"selected [{string.Join(separator: ',', values: actual.Take(count: 12))}...] ({actual.Count}), expected [{string.Join(separator: ',', values: expected.Take(count: 12))}...] ({expected.Count})");
                        if (failure is not null) { return $"{name} hard-leaf filter, p={prime}, ({last},{first}], capacity {capacity}: {failure}"; }
                    }
                }
            }
        }
        return null;
    }
    public static string? PrimeGourdonLeafSieveMatchesOracle() {
        // Sixteen counter blocks of sixteen words. Primes through 1499 cross both the dense mode, whose
        // wheel cycle fits in two counter blocks, and the sparse mode; a final partial segment ends inside a word.
        const uint Width = (240U * 256U);
        const int Segments = 6;
        const int LastPrime = 1499;
        const int FirstSievedIndex = 9;
        var flags = Oracles.PrimeSieve(inclusiveMaximum: LastPrime);
        var primeList = new List<uint> { 0 };

        for (var value = 2; (value <= LastPrime); ++value) {
            if (flags[value]) { primeList.Add(item: ((uint)value)); }
        }
        var primes = primeList.ToArray();
        var primeCount = (primes.Length - 1);
        var sieve = new PrimeExtensions.CombinatorialLeafSieve();
        var alive = new bool[Width];
        var prefix = new uint[Width];

        sieve.Reset(primeCount: primeCount, primes: primes, width: Width);
        for (var segment = 0; (segment < Segments); ++segment) {
            var low = (((ulong)segment) * Width);
            var width = ((segment == (Segments - 1)) ? (Width - 4321U) : Width);

            sieve.Initialize(low: low, width: width);
            for (var offset = 0U; (offset < width); ++offset) {
                var value = (low + offset);
                var coprime = true;

                for (var index = 1; (index < FirstSievedIndex); ++index) { coprime &= ((value % primes[index]) != 0); }
                alive[offset] = coprime;
            }
            for (var index = FirstSievedIndex; ; ++index) {
                var total = 0U;

                for (var offset = 0U; (offset < width); ++offset) {
                    total += (alive[offset] ? 1U : 0U);
                    prefix[offset] = total;
                }
                if (sieve.TotalCount != total) { return $"segment {low}, after {(index - FirstSievedIndex)} crossings: total {sieve.TotalCount}, expected {total}"; }
                var cursor = sieve.StartPrefix();
                var stride = (((((uint)index) * 7919U) % 5003U) + 1U);

                // Edges of words and counter blocks, then a prime-dependent stride; repeats are allowed.
                foreach (var offset in ((uint[])[0, 0, 1, 6, 7, 29, 30, 239, 240, 241, 3839, 3840, 3841])) {
                    var counted = cursor.Count(offset: offset);

                    if (counted != prefix[offset]) { return $"segment {low}, index {index}: prefix({offset}) {counted}, expected {prefix[offset]}"; }
                }
                for (var offset = 3841U; (offset < width); offset += stride) {
                    var counted = cursor.Count(offset: offset);

                    if (counted != prefix[offset]) { return $"segment {low}, index {index}: prefix({offset}) {counted}, expected {prefix[offset]}"; }
                }
                var final = cursor.Count(offset: (width - 1U));

                if (final != total) { return $"segment {low}, index {index}: prefix({(width - 1U)}) {final}, expected {total}"; }
                if (index > primeCount) { break; }
                var prime = primes[index];

                sieve.CrossOff(index: index, prime: prime);
                // Unlike a prime sieve, the leaf sieve removes the prime itself.
                for (var multiple = ((((low + prime) - 1UL) / prime) * prime); (multiple < (low + width)); multiple += prime) {
                    alive[((int)(multiple - low))] = false;
                }
            }
        }
        return null;
    }
    public static string? PrimeGourdonQuotientRungs() {
        var limit = PrimeExtensions.GourdonQuotientLimit;
        var dividends = new List<ulong> { 0, 1, ulong.MaxValue, (ulong.MaxValue - 1) };

        // Remainders zero and d-1 around each admitted anchor quotient, where an estimate that rounds up
        // must be corrected, and the largest dividend each divisor admits.
        foreach (var divisor in GourdonQuotientDivisors) {
            foreach (var anchor in GourdonQuotientAnchors) {
                var product = (((UInt128)anchor) * divisor);

                if (product > ulong.MaxValue) { continue; }
                if (product > 0) { dividends.Add(item: ((ulong)(product - 1))); }
                dividends.Add(item: ((ulong)product));
                if ((product + (divisor - 1U)) <= ulong.MaxValue) { dividends.Add(item: ((ulong)(product + (divisor - 1U)))); }
            }
            var largest = ((((UInt128)limit) * divisor) - 1);

            dividends.Add(item: ((ulong)UInt128.Min(x: largest, y: ulong.MaxValue)));
        }
        // A fixed multiplicative walk adds interior dividends at every magnitude.
        var walk = 0x9E37_79B9_7F4A_7C15UL;

        for (var index = 0; (index < 512); ++index) {
            walk = ((walk * 6_364_136_223_846_793_005UL) + 1_442_695_040_888_963_407UL);
            dividends.Add(item: (walk >> (index % 40)));
        }
        var admitted = new List<uint>();
        var quotients = new ulong[GourdonQuotientDivisors.Length];

        foreach (var dividend in dividends) {
            admitted.Clear();
            foreach (var divisor in GourdonQuotientDivisors) {
                if ((dividend / divisor) < limit) { admitted.Add(item: divisor); }
            }
            // Each start offset below eight, one Vector512 of quotients, shifts every divisor through the lanes and the tail.
            for (var start = 0; (start < Math.Min(val1: admitted.Count, val2: 8)); ++start) {
                var divisors = admitted.ToArray().AsSpan(start: start);

                foreach (var (name, divide) in GourdonQuotientRungs) {
                    Array.Fill(array: quotients, value: ulong.MaxValue);
                    divide(dividend: dividend, divisors: divisors, quotients: quotients);
                    for (var index = 0; (index < divisors.Length); ++index) {
                        var expected = (dividend / divisors[index]);

                        if (quotients[index] != expected) {
                            return $"{name} quotient rung: {dividend}/{divisors[index]} gave {quotients[index]}, expected {expected} (lane {index} of {divisors.Length})";
                        }
                    }
                }
            }
        }
        return null;
    }
    public static string? PrimeGourdonSemiprimeCounts() {
        // The square of a prime root, whose first quotient equals sqrt(x); a million-wide root interval whose
        // forward bitmap spans several segments and a partial final segment; and the smallest Gourdon regime.
        (ulong Value, uint Cutoff)[] cases = [(4_295_098_369, 1_625), (1_000_000_000_039, 300_000), (17_592_194_433_024, 4_190_000)];

        foreach (var (value, cutoff) in cases) {
            var root = ((uint)Math.Sqrt(d: value));

            while ((((ulong)root) * root) > value) { --root; }
            while ((((ulong)(root + 1)) * (root + 1)) <= value) { ++root; }
            var limit = ((int)(value / (cutoff + 1UL)));
            var flags = Oracles.PrimeSieve(inclusiveMaximum: limit);
            var pi = new int[(limit + 1)];
            var running = 0;

            for (var index = 0; (index <= limit); ++index) {
                running += (flags[index] ? 1 : 0);
                pi[index] = running;
            }
            // P2(x,a) is the sum over primes cutoff<p<=sqrt(x) of pi(x/p)-pi(p)+1.
            var expected = 0UL;

            for (var prime = (cutoff + 1U); (prime <= root); ++prime) {
                if (flags[prime]) { expected += ((ulong)((pi[((int)(value / prime))] - pi[prime]) + 1)); }
            }
            var actual = PrimeExploration.CountSemiprimes(cancellationToken: CancellationToken.None, cutoff: cutoff, value: value);

            if (actual != expected) { return $"P2({value}) above {cutoff}: expected {expected}, got {actual}"; }
        }
        return null;
    }

    private static string? PrimeGourdonFilterRun(ushort[] factors, int first, int last, uint prime, int capacity, int[] coordinates,
        GourdonHardLeafFilter filter, List<int> actual) {
        var coordinate = first;

        actual.Clear();
        // Each call examines at least one coordinate; the step budget turns a stall into a named failure.
        for (var calls = 0; (coordinate > last); ++calls) {
            if (calls > (first - last)) { return "the filter stopped advancing"; }
            var before = coordinate;
            var count = filter(coordinate: ref coordinate, coordinates: coordinates.AsSpan(length: capacity, start: 0),
                factors: factors, last: last, prime: prime);

            if ((coordinate >= before) || (coordinate < last) || (count > capacity)) {
                return $"call from {before} moved to {coordinate} with {count} survivors";
            }
            actual.AddRange(collection: coordinates.Take(count: count));
        }
        return null;
    }
}
