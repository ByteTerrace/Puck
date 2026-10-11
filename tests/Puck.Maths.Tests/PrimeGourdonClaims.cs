using System.Numerics;
using System.Runtime.Intrinsics;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private delegate int GourdonHardLeafFilter(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime, Span<uint> divisors,
        Span<short> negations, ref int subtracted);
    private delegate void GourdonQuotientRung(ulong dividend, ReadOnlySpan<uint> divisors, Span<ulong> quotients);
    private delegate void GourdonSegmentPositionRung(ulong dividend, ReadOnlySpan<uint> divisors, ReadOnlySpan<double> reciprocals, uint low,
        Span<ulong> positions);
    private delegate void GourdonLeafSieveCrossing(PrimeLeafKernels.CombinatorialLeafSieve sieve, int index, uint prime);
    private delegate uint GourdonPrefixCount(ref PrimeLeafKernels.CombinatorialPrefixCursor cursor, uint offset);

    // The oracle law's segments hold 256 words, 32 counter blocks of eight words; production segments hold 4096, 512
    // blocks. The far segments start on a production segment boundary, 5569 segments from zero.
    private const uint GourdonLeafSieveWidth = (240U * 256U);
    private const uint GourdonLeafSieveSkipWidth = (240U * 4096U);
    private const ulong GourdonLeafSieveHighLow = (5_569UL * GourdonLeafSieveSkipWidth);
    private const int GourdonLeafSieveFirstIndex = 9;

    private static readonly uint[] GourdonLeafSievePrimes = CreateGourdonLeafSievePrimes();
    private static readonly (string Name, GourdonLeafSieveCrossing CrossOff)[] GourdonLeafSieveRungs = [
        ("Vector512", static (sieve, index, prime) => sieve.CrossOff<VectorLanes512, Vector512<byte>>(index: index, prime: prime)),
        ("Vector256", static (sieve, index, prime) => sieve.CrossOff<VectorLanes256, Vector256<byte>>(index: index, prime: prime)),
        ("Vector128", static (sieve, index, prime) => sieve.CrossOff<VectorLanes128, Vector128<byte>>(index: index, prime: prime)),
    ];
    // The prefix cursor's production dispatch, and each window the host can execute: the vector windows use AVX-512BW
    // and AVX2 instructions directly, which a host without them cannot emulate.
    private static readonly (string Name, GourdonPrefixCount Count)[] GourdonPrefixProduction = [
        ("production", static (ref PrimeLeafKernels.CombinatorialPrefixCursor cursor, uint offset) => cursor.Count(offset: offset)),
    ];
    private static readonly (string Name, GourdonPrefixCount Count)[] GourdonPrefixWindowRungs = CreateGourdonPrefixWindowRungs();
    private static readonly (string Name, GourdonHardLeafFilter Filter)[] GourdonHardLeafFilterRungs = CreateGourdonHardLeafFilterRungs();
    private static readonly (string Name, GourdonQuotientRung Divide)[] GourdonQuotientRungs = [
        ("Vector512", PrimeDivisionKernels.DivideGourdonQuotients512),
        ("Vector256", PrimeDivisionKernels.DivideGourdonQuotients256),
        ("Vector128", PrimeDivisionKernels.DivideGourdonQuotients128),
        ("scalar", static (dividend, divisors, quotients) => PrimeDivisionKernels.DivideGourdonQuotientsScalar(dividend: dividend, divisors: divisors, quotients: quotients, start: 0)),
    ];
    private static readonly (string Name, GourdonSegmentPositionRung Divide)[] GourdonSegmentPositionRungs = CreateGourdonSegmentPositionRungs();
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
        // Ranges: empty, shorter than every vector, straddling each width, long runs starting at every wheel phase, and a
        // long dense run near the array end.
        (int First, int Last)[] ranges = [
            (0, -1), (0, 0), (5, -1), (31, 0), (32, -1), (33, -1), (64, 1), (95, 30), (200, 3), (1000, 517),
            (1001, 517), (1002, 517), (1003, -1), (1004, 300), (1005, 517), (1006, 900), (1007, 517),
            ((factors.Length - 1), -1), ((factors.Length - 1), (factors.Length - 70)),
        ];

        foreach (var prime in GourdonHardLeafThresholds) {
            var failure = PrimeGourdonFilterRanges(capacities: GourdonHardLeafCapacities, factors: factors, prime: prime, ranges: ranges);

            if (failure is not null) { return failure; }
        }
        // Every survivor mask of eight lanes: the aligned group of eight coordinates g survives where g has a bit set,
        // with signs mixed, so an aligned run hands each pack rung every mask in each half; shifted starts mix them.
        var sweep = new ushort[2048];
        const uint SweepPrime = 181;

        for (var index = 0; (index < sweep.Length); ++index) {
            var survives = ((((index >> 3) >> (index & 7)) & 1) != 0);
            var value = (survives ? ((SweepPrime + 1) + ((uint)(index % 5))) : (SweepPrime - ((uint)(index % 3))));

            sweep[index] = ((ushort)(value | (((((index * 5) + (index >> 4)) % 3) == 0) ? 0x8000U : 0U)));
        }
        var sweepFailure = PrimeGourdonFilterRanges(capacities: [33, 128], factors: sweep, prime: SweepPrime,
            ranges: [((sweep.Length - 1), -1), ((sweep.Length - 2), -1), ((sweep.Length - 5), 3), ((sweep.Length - 8), 40)]);

        if (sweepFailure is not null) { return sweepFailure; }
        // A pinned carrier makes the 32 shifted spans visit every two-byte alignment in a 64-byte line.
        // Dense and mixed inputs exercise both sides of the compress rung's sixteen-survivor store boundary.
        var aligned = GC.AllocateArray<ushort>(length: 288, pinned: true);

        foreach (var dense in new[] { false, true }) {
            for (var index = 0; (index < aligned.Length); ++index) {
                aligned[index] = (dense ? ((ushort)(182 | ((index & 1) << 15))) : sweep[index]);
            }
            for (var shift = 0; (shift < 32); ++shift) {
                var failure = PrimeGourdonFilterRanges(capacities: GourdonHardLeafCapacities,
                    factors: aligned.AsSpan(length: 256, start: shift), prime: SweepPrime,
                    ranges: [(255, -1), (254, 0), (127, 30), (64, 31), (32, 0), (31, -1)]);

                if (failure is not null) { return $"factor alignment {shift}, dense={dense}: {failure}"; }
            }
        }
        return null;
    }

    // Runs every filter rung over each range of factors at each capacity and compares the leaves with the definition.
    private static string? PrimeGourdonFilterRanges(ReadOnlySpan<ushort> factors, uint prime, (int First, int Last)[] ranges, int[] capacities) {
        var units = Oracles.UnitsModulo(modulus: 30);
        var expected = new List<(uint Number, short Negation)>();
        var actual = new List<(uint Number, short Negation)>();
        var divisors = new uint[capacities.Max()];
        var negations = new short[capacities.Max()];

        foreach (var (first, last) in ranges) {
            var expectedSubtracted = 0;

            expected.Clear();
            // A survivor's leaf is the coordinate-th unit modulo 30; it subtracts its count unless the sign bit is set.
            for (var coordinate = first; (coordinate > last); --coordinate) {
                if ((factors[coordinate] & 0x7FFF) <= prime) { continue; }
                var subtracts = ((factors[coordinate] & 0x8000) == 0);

                expected.Add(item: (((uint)((30 * (coordinate / 8)) + units[(coordinate % 8)])), ((short)(subtracts ? -1 : 0))));
                expectedSubtracted += (subtracts ? 1 : 0);
            }
            foreach (var capacity in capacities) {
                foreach (var (name, filter) in GourdonHardLeafFilterRungs) {
                    var failure = PrimeGourdonFilterRun(actual: actual, capacity: capacity, divisors: divisors, factors: factors,
                        filter: filter, first: first, last: last, negations: negations, prime: prime, subtracted: out var subtracted);

                    failure ??= (expected.SequenceEqual(second: actual) ? null
                        : $"wrote [{string.Join(separator: ',', values: actual.Take(count: 12))}...] ({actual.Count}), expected [{string.Join(separator: ',', values: expected.Take(count: 12))}...] ({expected.Count})");
                    failure ??= ((subtracted == expectedSubtracted) ? null : $"counted {subtracted} subtracted leaves, expected {expectedSubtracted}");
                    if (failure is not null) { return $"{name} hard-leaf filter, p={prime}, ({last},{first}], capacity {capacity}: {failure}"; }
                }
            }
        }
        return null;
    }

    public static string? PrimeGourdonLeafSieveMatchesOracle() {
        // Thirty-two counter blocks of eight words, crossed off at the host's widest vectors. Primes through 1499
        // cross the periodic and the sparse modes; a final partial segment ends inside a word.
        var sieve = new PrimeLeafKernels.CombinatorialLeafSieve();

        sieve.Reset(primeCount: (GourdonLeafSievePrimes.Length - 1), primes: GourdonLeafSievePrimes, width: GourdonLeafSieveWidth);
        return PrimeGourdonLeafSieveSegments(counts: GourdonPrefixProduction, crossOff: static (sieve, index, prime) => sieve.CrossOff(index: index, prime: prime),
            first: 0, segments: 6, sieve: sieve);
    }
    public static string? PrimeGourdonLeafSieveRungs() {
        // Every crossing width, each called directly whatever the host accelerates, runs the six segments from zero
        // and then three far segments, each queried through every prefix window the host executes. The 512-, 256- and
        // 128-bit widths mask primes through 512, 341 and 204 periodically and count the crossings of the rest; a 512-bit
        // width on a host without AVX-512 VBMI takes the 256-bit kernel and its 341.
        foreach (var (name, crossOff) in GourdonLeafSieveRungs) {
            var low = new PrimeLeafKernels.CombinatorialLeafSieve();

            low.Reset(primeCount: (GourdonLeafSievePrimes.Length - 1), primes: GourdonLeafSievePrimes, width: GourdonLeafSieveWidth);

            var failure = PrimeGourdonLeafSieveSegments(counts: GourdonPrefixWindowRungs, crossOff: crossOff, first: 0, segments: 6, sieve: low);

            if (failure is not null) { return $"{name} crossing: {failure}"; }
            failure = PrimeGourdonPrefixWindowJumps(crossOff: crossOff);
            if (failure is not null) { return $"{name} crossing: {failure}"; }

            var high = new PrimeLeafKernels.CombinatorialLeafSieve();

            // Production-width segments carry every crossing state from zero to the first checked segment.
            high.Reset(primeCount: (GourdonLeafSievePrimes.Length - 1), primes: GourdonLeafSievePrimes, width: GourdonLeafSieveSkipWidth);
            for (var start = 0UL; (start < GourdonLeafSieveHighLow); start += GourdonLeafSieveSkipWidth) {
                high.Initialize(low: start, width: GourdonLeafSieveSkipWidth);
                for (var index = GourdonLeafSieveFirstIndex; (index < GourdonLeafSievePrimes.Length); ++index) {
                    crossOff(index: index, prime: GourdonLeafSievePrimes[index], sieve: high);
                }
            }
            failure = PrimeGourdonLeafSieveSegments(counts: GourdonPrefixWindowRungs, crossOff: crossOff, first: GourdonLeafSieveHighLow, segments: 3, sieve: high);
            if (failure is not null) { return $"{name} crossing: {failure}"; }
        }
        return null;
    }
    public static string? PrimeGourdonQuotientRungs() {
        var limit = PrimeDivisionKernels.GourdonQuotientLimit;
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

                    var failure = PrimeGourdonQuotientMismatch(dividend: dividend, divisors: divisors, quotients: quotients);

                    if (failure is not null) { return $"{name} quotient rung: {failure}"; }
                }
                var windowFailure = PrimeGourdonSegmentPositionWindow(dividend: dividend, divisors: divisors);

                if (windowFailure is not null) { return windowFailure; }
            }
        }
        // Sixteen consecutive divisors from each anchor divisor, about dividends one below, at and d-1 above each
        // multiple of the anchor quotients a segment window admits, put corrected estimates in every vector lane.
        foreach (var divisor in GourdonQuotientDivisors) {
            var run = new uint[((int)Math.Min(val1: 16UL, val2: ((((ulong)uint.MaxValue) - divisor) + 1UL)))];

            for (var index = 0; (index < run.Length); ++index) { run[index] = (divisor + ((uint)index)); }
            foreach (var anchor in ((ReadOnlySpan<ulong>)[1, 2, 29, 30, 239, 240, 268_435_454, 268_435_455, 4_294_967_295, 4_294_967_296, 8_589_934_591])) {
                var product = (((UInt128)anchor) * divisor);

                foreach (var dividend in ((ReadOnlySpan<UInt128>)[(product - 1), product, (product + (divisor - 1U))])) {
                    if (dividend > ulong.MaxValue) { continue; }
                    var failure = PrimeGourdonSegmentPositionWindow(dividend: ((ulong)dividend), divisors: run);

                    if (failure is not null) { return failure; }
                }
            }
        }
        return PrimeGourdonSegmentPositionSplits();
    }

    // Every segment-position rung on the position's edges: dividing by one makes every quotient the dividend, so a
    // dividend low+offset puts that offset in every lane, and runs of 1 to 16 divisors put it in the tail too. Offsets
    // whose a=offset+1 sits at and beside each multiple of 30 and 240, at t=14 and t=15 where the fraction lookup's
    // index skips, at the top of the 2^28 domain, where the fraction's error term is largest, and along a fixed walk,
    // above segment origins from 0 to uint.MaxValue.
    private static string? PrimeGourdonSegmentPositionSplits() {
        var offsets = new List<ulong> {
            0, 1, 12, 13, 14, 15, 28, 29, 30, 238, 239, 240, 241, 479, 480, 57_599, 57_600, 16_777_215,
            268_435_408, 268_435_409, 268_435_438, 268_435_439, 268_435_440, 268_435_452, 268_435_453, 268_435_454,
        };
        var walk = 0x2545_F491_4F6C_DD1DUL;
        var ones = new uint[16];

        Array.Fill(array: ones, value: 1U);
        for (var index = 0; (index < 64); ++index) {
            walk = ((walk * 6_364_136_223_846_793_005UL) + 1_442_695_040_888_963_407UL);
            offsets.Add(item: (((walk >> 36) >> (index % 24)) % GourdonSegmentPositionSpan));
        }
        foreach (var low in ((ReadOnlySpan<uint>)[0, 7, 2_147_483_648, uint.MaxValue])) {
            foreach (var offset in offsets) {
                for (var length = 1; (length <= ones.Length); ++length) {
                    var failure = PrimeGourdonSegmentPositionRun(dividend: (low + offset), divisors: ones.AsSpan(length: length, start: 0), low: low);

                    if (failure is not null) { return failure; }
                }
            }
        }
        return null;
    }
    // Runs every segment-position rung on the longest final part of a run of the divisors below 2^31 whose quotients one
    // 32-bit origin admits, above the lowest and the highest such origin.
    private static string? PrimeGourdonSegmentPositionWindow(ulong dividend, ReadOnlySpan<uint> divisors) {
        var admitted = new List<uint>();

        foreach (var divisor in divisors) {
            if (divisor < (1U << 31)) { admitted.Add(item: divisor); }
        }
        for (var start = 0; (start < admitted.Count); ++start) {
            var run = admitted.ToArray().AsSpan(start: start);

            var (largest, smallest) = (ulong.MinValue, ulong.MaxValue);

            foreach (var divisor in run) {
                largest = Math.Max(val1: largest, val2: (dividend / divisor));
                smallest = Math.Min(val1: smallest, val2: (dividend / divisor));
            }
            var lowest = ((largest >= GourdonSegmentPositionSpan) ? (largest - (GourdonSegmentPositionSpan - 1UL)) : 0UL);
            var highest = Math.Min(val1: smallest, val2: uint.MaxValue);

            if (lowest > highest) { continue; }
            return (PrimeGourdonSegmentPositionRun(dividend: dividend, divisors: run, low: ((uint)lowest))
                ?? PrimeGourdonSegmentPositionRun(dividend: dividend, divisors: run, low: ((uint)highest)));
        }
        return null;
    }
    // Every segment-position rung against the law's own / and %: for a=floor(dividend/d)-low+1, the position must be
    // 2^29*floor(a/30) plus n, the integers from 1 through a-1 coprime to 30 by inclusion and exclusion over 2, 3 and
    // 5, and so carry the word floor(n/64) in its high half and the bit n%64 in its low six bits.
    private static string? PrimeGourdonSegmentPositionRun(ulong dividend, ReadOnlySpan<uint> divisors, uint low) {
        var reciprocals = new double[divisors.Length];
        var positions = new ulong[divisors.Length];

        // The rungs' contract: each reciprocal is 1/d correctly rounded to binary64.
        for (var index = 0; (index < divisors.Length); ++index) { reciprocals[index] = (1D / divisors[index]); }
        foreach (var (name, divide) in GourdonSegmentPositionRungs) {
            Array.Fill(array: positions, value: ulong.MaxValue);
            divide(dividend: dividend, divisors: divisors, low: low, positions: positions, reciprocals: reciprocals);
            for (var index = 0; (index < divisors.Length); ++index) {
                var offset = ((dividend / divisors[index]) - low);
                var coprime = (((((offset - (offset / 2)) - (offset / 3)) - (offset / 5)) + (((offset / 6) + (offset / 10)) + (offset / 15))) - (offset / 30));
                var expected = ((((offset + 1UL) / 30UL) << 29) + coprime);
                var position = positions[index];

                if ((position != expected) || ((position >> 32) != (coprime / 64)) || ((position % 64) != (coprime % 64))) {
                    return $"{name} segment-position rung: {dividend}/{divisors[index]} above {low} gave position 0x{position:X16}, expected 0x{expected:X16} for offset {offset} (lane {index} of {divisors.Length})";
                }
            }
        }
        return null;
    }

    // The segment-position rungs admit quotients from their origin through 2^28-2 above it.
    private const ulong GourdonSegmentPositionSpan = ((1UL << 28) - 1UL);

    private static string? PrimeGourdonQuotientMismatch(ulong dividend, ReadOnlySpan<uint> divisors, ReadOnlySpan<ulong> quotients) {
        for (var index = 0; (index < divisors.Length); ++index) {
            var expected = (dividend / divisors[index]);

            if (quotients[index] != expected) {
                return $"{dividend}/{divisors[index]} gave {quotients[index]}, expected {expected} (lane {index} of {divisors.Length})";
            }
        }
        return null;
    }

    /// <summary>Pins the cutoff rule's admissible range across the ulong Gourdon regime, and that any admissible cutoff
    /// moves work between the phases without changing the count.</summary>
    public static string? PrimeGourdonCutoffMovesWorkOnly() {
        // Gourdon bounds in increasing order: the first above the quotient recurrence's root cap, the powers of ten,
        // 2^63 and the top of ulong.
        ReadOnlySpan<ulong> bounds = [
            17_592_177_655_810, 100_000_000_000_000, 1_000_000_000_000_000, 10_000_000_000_000_000, 100_000_000_000_000_000,
            1_000_000_000_000_000_000, (1UL << 63), 10_000_000_000_000_000_000, ulong.MaxValue,
        ];
        const uint TableCapacityLimit = (1U << 27);
        var previous = 0U;

        foreach (var bound in bounds) {
            var cutoff = PrimeExtensions.ResolveGourdonCutoff(value: bound);
            var root = Oracles.IntegerSquareRoot(value: bound);
            var cube = new BigInteger(value: Math.Cbrt(d: bound));

            while (((cube * cube) * cube) > bound) { --cube; }
            while ((((cube + 1) * (cube + 1)) * (cube + 1)) <= bound) { ++cube; }
            if ((cutoff <= cube) || (cutoff >= root) || (cutoff > TableCapacityLimit)) {
                return $"cutoff {cutoff} at {bound} leaves ({cube}, {root}) or exceeds {TableCapacityLimit}";
            }
            var proposed = Oracles.PrimeGourdonCutoff(root: root);
            var maximum = BigInteger.Min(left: (root - 1), right: TableCapacityLimit);
            var low = BigInteger.Clamp(value: proposed.Low, min: (cube + 1), max: maximum);
            var high = BigInteger.Clamp(value: proposed.High, min: (cube + 1), max: maximum);

            if ((cutoff < (low - 1)) || (cutoff > (high + 1))) { return $"cutoff {cutoff} at {bound} is outside the independent rule enclosure [{low}, {high}] by more than one unit"; }
            if (cutoff < previous) { return $"cutoff {cutoff} at {bound} is below the previous bound's {previous}"; }
            previous = cutoff;
        }
        // The adjacent operands straddle a floor boundary if ln(x) varies inside a square plateau.
        foreach (var bound in new ulong[] { 324_002_622_127_985, 324_002_622_127_986 }) {
            var cutoff = PrimeExtensions.ResolveGourdonCutoff(value: bound);

            if (cutoff != 1_282_701) { return $"square-plateau cutoff at {bound}: expected 1282701, got {cutoff}"; }
        }
        // The divisor floor, the capacity cap, and every logarithm table boundary in the root's carrier.
        var roots = new List<ulong> { 4_194_304, 17_307_779, 17_307_780, 18_000_072, 2_436_820_334, 2_436_820_335, uint.MaxValue };

        for (var exponent = 22; (exponent <= 31); ++exponent) {
            for (var interval = 0; (interval < 128); ++interval) { roots.Add(item: (((ulong)(128 + interval)) << (exponent - 7))); }
        }
        foreach (var root in roots) {
            var square = (root * root);
            var before = PrimeExtensions.ResolveGourdonCutoff(value: (square - 1));
            var at = PrimeExtensions.ResolveGourdonCutoff(value: square);
            var end = ((root == uint.MaxValue) ? ulong.MaxValue : (((root + 1) * (root + 1)) - 1));
            var after = PrimeExtensions.ResolveGourdonCutoff(value: end);

            if ((before > at) || (at != after)) { return $"cutoff decreases or varies within square plateau {root}: {before}, {at}, {after}"; }
        }
        // The whole admissible range at 10^14: just above the cube root 46415, two points between, the resolved
        // cutoff and just below the square root 10^7. Each count records the cutoff it ran at and the hard-leaf
        // frontier floor(x/(y+1)) that cutoff implies, so a counter that substituted its own cutoff would show.
        const ulong Bound = 100_000_000_000_000;
        const ulong Expected = 3_204_941_750_802;
        ReadOnlySpan<uint> cutoffs = [46_416, 200_000, PrimeExtensions.ResolveGourdonCutoff(value: Bound), 2_000_000, 9_999_999];

        foreach (var cutoff in cutoffs) {
            var work = new PrimeCountWorkBuilder(bound: Bound);
            var actual = PrimeExtensions.CountCombinatorial64(cancellationToken: CancellationToken.None, cutoff: cutoff, profile: work, value: Bound);

            if (actual != Expected) { return $"pi(10^14) at cutoff {cutoff}: published {Expected}, got {actual}"; }
            if ((work.Cutoff != cutoff) || (work.LeafFrontier != (Bound / (cutoff + 1UL)))) {
                return $"count at cutoff {cutoff} recorded cutoff {work.Cutoff} and hard-leaf frontier {work.LeafFrontier}";
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

    private static string? PrimeGourdonFilterRun(ReadOnlySpan<ushort> factors, int first, int last, uint prime, int capacity, uint[] divisors,
        short[] negations, GourdonHardLeafFilter filter, List<(uint Number, short Negation)> actual, out int subtracted) {
        var coordinate = first;

        actual.Clear();
        subtracted = 0;
        // Each call examines at least one coordinate; the step budget turns a stall into a named failure.
        for (var calls = 0; (coordinate > last); ++calls) {
            if (calls > (first - last)) { return "the filter stopped advancing"; }
            var before = coordinate;
            var count = filter(coordinate: ref coordinate, divisors: divisors.AsSpan(length: capacity, start: 0), factors: factors,
                last: last, negations: negations.AsSpan(length: capacity, start: 0), prime: prime, subtracted: ref subtracted);

            if ((coordinate >= before) || (coordinate < last) || (count > capacity)) {
                return $"call from {before} moved to {coordinate} with {count} survivors";
            }
            for (var index = 0; (index < count); ++index) { actual.Add(item: (divisors[index], negations[index])); }
        }
        return null;
    }
    // Each segment-position rung, called directly whatever the host accelerates, and the AVX-512 VBMI fraction lookup
    // where the host can execute its permute.
    private static (string Name, GourdonSegmentPositionRung Divide)[] CreateGourdonSegmentPositionRungs() {
        var rungs = new List<(string Name, GourdonSegmentPositionRung Divide)>();

        if (System.Runtime.Intrinsics.X86.Avx512Vbmi.IsSupported) {
            rungs.Add(item: ("Vector512 fraction", PrimeDivisionKernels.DivideGourdonSegmentPositions512<PrimeDivisionKernels.GourdonUnitsByFraction>));
        }
        rungs.Add(item: ("Vector512 halves", PrimeDivisionKernels.DivideGourdonSegmentPositions512<PrimeDivisionKernels.GourdonUnitsByHalves>));
        rungs.Add(item: ("Vector256", PrimeDivisionKernels.DivideGourdonSegmentPositions256));
        rungs.Add(item: ("scalar", static (dividend, divisors, reciprocals, low, positions) => PrimeDivisionKernels.DivideGourdonSegmentPositionsScalar(
            dividend: dividend, divisors: divisors, low: low, positions: positions, reciprocals: reciprocals, start: 0)));
        return [.. rungs];
    }
    // Each hard-leaf filter rung the host can execute: the vector rungs use VBMI2, AVX2 and SSSE3 instructions directly.
    private static (string Name, GourdonHardLeafFilter Filter)[] CreateGourdonHardLeafFilterRungs() {
        var rungs = new List<(string Name, GourdonHardLeafFilter Filter)>();

        if (System.Runtime.Intrinsics.X86.Avx512Vbmi2.IsSupported) { rungs.Add(item: ("VBMI2 compress", PrimeLeafKernels.FilterGourdonHardLeavesCompress)); }
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) { rungs.Add(item: ("AVX2 pack", PrimeLeafKernels.FilterGourdonHardLeavesPack256)); }
        if (System.Runtime.Intrinsics.X86.Ssse3.IsSupported) { rungs.Add(item: ("SSSE3 pack", PrimeLeafKernels.FilterGourdonHardLeavesPack128)); }
        rungs.Add(item: ("scalar", PrimeLeafKernels.FilterGourdonHardLeavesScalar));
        return [.. rungs];
    }
    private static (string Name, GourdonPrefixCount Count)[] CreateGourdonPrefixWindowRungs() {
        var rungs = new List<(string Name, GourdonPrefixCount Count)>();

        if (System.Runtime.Intrinsics.X86.Avx512BW.IsSupported) {
            rungs.Add(item: ("Vector512", static (ref PrimeLeafKernels.CombinatorialPrefixCursor cursor, uint offset) =>
                cursor.Count(offset: offset, window: PrimeLeafKernels.CombinatorialPrefixWindowKind.Avx512BW)));
        }
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) {
            rungs.Add(item: ("Vector256", static (ref PrimeLeafKernels.CombinatorialPrefixCursor cursor, uint offset) =>
                cursor.Count(offset: offset, window: PrimeLeafKernels.CombinatorialPrefixWindowKind.Avx2)));
        }
        rungs.Add(item: ("scalar", static (ref PrimeLeafKernels.CombinatorialPrefixCursor cursor, uint offset) =>
            cursor.Count(offset: offset, window: PrimeLeafKernels.CombinatorialPrefixWindowKind.Scalar)));
        return [.. rungs];
    }
    private static uint[] CreateGourdonLeafSievePrimes() {
        const int LastPrime = 1499;
        var flags = Oracles.PrimeSieve(inclusiveMaximum: LastPrime);
        var primes = new List<uint> { 0 };

        for (var value = 2; (value <= LastPrime); ++value) {
            if (flags[value]) { primes.Add(item: ((uint)value)); }
        }
        return [.. primes];
    }
    // A production-width segment from zero after every crossing, through each prefix window the host executes: a fresh
    // cursor whose first query lands in each of the 512 counter blocks, at a word and offset varying with the block, then
    // jumps up to 40 blocks further; and one cursor walking the segment with jumps of 0 to 40 blocks. First queries walk
    // up to 31 whole sixteen-block windows before the masked one, and jumps up to two.
    private static string? PrimeGourdonPrefixWindowJumps(GourdonLeafSieveCrossing crossOff) {
        const uint BlockIntegers = (240U * 8U);
        var primes = GourdonLeafSievePrimes;
        var width = GourdonLeafSieveSkipWidth;
        var sieve = new PrimeLeafKernels.CombinatorialLeafSieve();
        var alive = new bool[width];
        var prefix = new uint[width];

        sieve.Reset(primeCount: (primes.Length - 1), primes: primes, width: width);
        sieve.Initialize(low: 0, width: width);
        for (var index = GourdonLeafSieveFirstIndex; (index < primes.Length); ++index) { crossOff(index: index, prime: primes[index], sieve: sieve); }
        // Every crossed or tiny prime removes its multiples from zero, the prime itself included.
        Array.Fill(array: alive, value: true);
        for (var index = 1; (index < primes.Length); ++index) {
            for (var multiple = 0U; (multiple < width); multiple += primes[index]) { alive[multiple] = false; }
        }
        var total = 0U;

        for (var offset = 0U; (offset < width); ++offset) {
            total += (alive[offset] ? 1U : 0U);
            prefix[offset] = total;
        }
        foreach (var (name, count) in GourdonPrefixWindowRungs) {
            for (var block = 0U; (block < (width / BlockIntegers)); ++block) {
                var cursor = sieve.StartPrefix();
                var first = (((block * BlockIntegers) + ((block % 8U) * 240U)) + ((block * 37U) % 240U));
                var second = ((first + (((block % 41U) * BlockIntegers))) + 480U);

                foreach (var offset in ((uint[])[first, second])) {
                    if (offset >= width) { continue; }
                    var counted = count(cursor: ref cursor, offset: offset);

                    if (counted != prefix[offset]) { return $"{name} prefix window, fresh cursor in block {block}: prefix({offset}) {counted}, expected {prefix[offset]}"; }
                }
            }
            var walker = sieve.StartPrefix();
            var jump = 0U;

            for (var offset = 0U; (offset < width); offset += (((jump % 41U) * BlockIntegers) + ((jump * 53U) % BlockIntegers))) {
                var counted = count(cursor: ref walker, offset: offset);

                if (counted != prefix[offset]) { return $"{name} prefix window, walk: prefix({offset}) {counted}, expected {prefix[offset]}"; }
                ++jump;
            }
        }
        return null;
    }
    // Runs consecutive oracle segments of GourdonLeafSieveWidth from first, the last ending inside a word: after
    // initialization and after each crossing of every prime 23 through 1499, the sieve's total and prefix-cursor
    // counts through each of counts at word, counter-block and prime-dependent strided offsets, repeats included,
    // must equal the oracle's.
    private static string? PrimeGourdonLeafSieveSegments(PrimeLeafKernels.CombinatorialLeafSieve sieve, GourdonLeafSieveCrossing crossOff,
        (string Name, GourdonPrefixCount Count)[] counts, ulong first, int segments) {
        var primes = GourdonLeafSievePrimes;
        var primeCount = (primes.Length - 1);
        var alive = new bool[GourdonLeafSieveWidth];
        var prefix = new uint[GourdonLeafSieveWidth];

        for (var segment = 0; (segment < segments); ++segment) {
            var low = (first + (((ulong)segment) * GourdonLeafSieveWidth));
            var width = ((segment == (segments - 1)) ? (GourdonLeafSieveWidth - 4321U) : GourdonLeafSieveWidth);

            sieve.Initialize(low: low, width: width);
            for (var offset = 0U; (offset < width); ++offset) {
                var value = (low + offset);
                var coprime = true;

                for (var index = 1; (index < GourdonLeafSieveFirstIndex); ++index) { coprime &= ((value % primes[index]) != 0); }
                alive[offset] = coprime;
            }
            for (var index = GourdonLeafSieveFirstIndex; ; ++index) {
                var total = 0U;

                for (var offset = 0U; (offset < width); ++offset) {
                    total += (alive[offset] ? 1U : 0U);
                    prefix[offset] = total;
                }
                if (sieve.TotalCount != total) { return $"segment {low}, after {(index - GourdonLeafSieveFirstIndex)} crossings: total {sieve.TotalCount}, expected {total}"; }
                var stride = (((((uint)index) * 7919U) % 5003U) + 1U);

                foreach (var (name, count) in counts) {
                    var cursor = sieve.StartPrefix();

                    // Edges of words, counter blocks and periodic strides, then a prime-dependent stride; repeats are
                    // allowed.
                    foreach (var offset in ((uint[])[0, 0, 1, 6, 7, 29, 30, 239, 240, 241, 1919, 1920, 1921, 3839, 3840, 3841])) {
                        var counted = count(cursor: ref cursor, offset: offset);

                        if (counted != prefix[offset]) { return $"{name} cursor, segment {low}, index {index}: prefix({offset}) {counted}, expected {prefix[offset]}"; }
                    }
                    for (var offset = 3841U; (offset < width); offset += stride) {
                        var counted = count(cursor: ref cursor, offset: offset);

                        if (counted != prefix[offset]) { return $"{name} cursor, segment {low}, index {index}: prefix({offset}) {counted}, expected {prefix[offset]}"; }
                    }
                    var final = count(cursor: ref cursor, offset: (width - 1U));

                    if (final != total) { return $"{name} cursor, segment {low}, index {index}: prefix({(width - 1U)}) {final}, expected {total}"; }
                }
                if (index > primeCount) { break; }
                var prime = primes[index];

                crossOff(index: index, prime: prime, sieve: sieve);
                // Unlike a prime sieve, the leaf sieve removes the prime itself.
                for (var multiple = ((((low + prime) - 1UL) / prime) * prime); (multiple < (low + width)); multiple += prime) {
                    alive[((int)(multiple - low))] = false;
                }
            }
        }
        return null;
    }
}
