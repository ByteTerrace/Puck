using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Puck.Maths;

public static partial class PrimeExtensions {
    // Gourdon's D decomposition and the filtered batches follow primecount's
    // src/gourdon/D_default.hpp, Copyright (C) 2026 Kim Walisch, BSD 2-Clause.
    // See THIRD-PARTY-NOTICES.md. This specialization fixes z=y and k=8 and uses
    // the local U(30) factor encoding, sequential phi state and unsigned quotients.
    private const uint GourdonHardLeafSegmentWidth = ((32U * 1024U) * PrimeWheel30.Modulus);
    private const int GourdonHardLeafBatchSize = 128;
    private const int GourdonHardLeafScanSpan = 16384;

    // A count spends seconds in the Gourdon kernels, so they compile fully optimized on first call rather than
    // through tiered compilation's quick first tier and on-stack replacement.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Int128 CountGourdonHardLeaves(ulong value, uint cutoff, uint xStar,
        CombinatorialFactorTables tables, CombinatorialCountingWorkspace workspace,
        CancellationToken cancellationToken, PrimeCountWorkBuilder? profile) {
        cancellationToken.ThrowIfCancellationRequested();

        var primes = tables.Primes;
        var factors = tables.Factors;
        // Every hard leaf has p*m>y and t=floor(x/(p*m))>=p*p. The exclusive
        // quotient limit and the active-prime bound include both equality endpoints.
        var limit = ((value / (cutoff + 1UL)) + 1UL);
        var activeLimit = Math.Min(val1: xStar, val2: (limit - 1UL).SquareRoot());
        var activeCount = ((int)tables.CountPrimes(value: ((uint)activeLimit)));

        // x*>=x^(1/4) and x/(y+1)>sqrt(x), so with x>2^44 the active bound is at least 2^11, far beyond 19.
        Debug.Assert((activeCount > CombinatorialTinyPrimeCount));

        var squareRootIndex = Math.Min(val1: activeCount,
            val2: ((int)tables.CountPrimes(value: cutoff.SquareRoot())));

        var (sieve, phi, quotients) = workspace.PrepareLeaves(cancellationToken: cancellationToken,
            primeCount: activeCount, primes: primes, value: value,
            width: GourdonHardLeafSegmentWidth);
        var (lowerBounds, upperBounds) = workspace.PrepareGourdonHardBounds(cancellationToken: cancellationToken,
            cutoff: cutoff, primeCount: activeCount, primes: primes,
            quotients: quotients, squareRootIndex: squareRootIndex);
        Span<int> coordinates = stackalloc int[GourdonHardLeafBatchSize];
        Span<uint> divisors = stackalloc uint[GourdonHardLeafBatchSize];
        Span<ulong> leafQuotients = stackalloc ulong[GourdonHardLeafBatchSize];
        var sum = Int128.Zero;

        if (profile is not null) {
            profile.LeafFrontier = (limit - 1UL);
            profile.CachedQuotientDivisions += ((uint)(activeCount - CombinatorialTinyPrimeCount));
        }

        // Before crossing off p_b, phi[b] is phi(low-1,b-1), initially zero.
        // The segment bitmap contains exactly the survivors of primes 1..b-1.
        // Descending m makes floor(x/(p*m)) nondecreasing, as the prefix cursor requires.
        for (var low = 0UL; (low < limit); low += GourdonHardLeafSegmentWidth) {
            cancellationToken.ThrowIfCancellationRequested();

            var high = Math.Min(val1: (low + GourdonHardLeafSegmentWidth), val2: limit);
            var lowDivisor = Math.Max(val1: low, val2: 1UL);

            sieve.Initialize(low: low, width: ((uint)(high - low)));
            if (profile is not null) {
                ++profile.LeafSegments;
                profile.LeafBitmapBytes += ((((high - low) + (PrimeWheel30.WordIntegers - 1)) / PrimeWheel30.WordIntegers) * sizeof(ulong));
            }

            var index = (CombinatorialTinyPrimeCount + 1);

            for (; (index <= squareRootIndex); ++index) {
                if ((index & 255) == 0) { cancellationToken.ThrowIfCancellationRequested(); }

                var prime = primes[index];
                var quotient = quotients[index];
                var minimum = Math.Max(val1: (quotient / high), val2: lowerBounds[index]);
                var maximum = Math.Min(val1: (quotient / lowDivisor), val2: upperBounds[index]);

                // This upper bound decreases with p and with low. Once it is <=p,
                // no later prime can have a leaf in this or any following segment.
                if (prime >= maximum) { break; }

                var last = CombinatorialCoordinate(value: ((uint)Math.Min(val1: minimum, val2: maximum)));
                var first = CombinatorialCoordinate(value: ((uint)maximum));
                var coordinate = first;

                if (profile is not null) { profile.SpecialFactorCoordinates += ((uint)(first - last)); }

                // With z=y the maximum-prime-factor condition is automatic. A
                // masked factor >p selects exactly square-free m with lpf(m)>p.
                // Separate filtering, division and phi queries into short batches.
                var prefix = sieve.StartPrefix();

                while (coordinate > last) {
                    cancellationToken.ThrowIfCancellationRequested();

                    var count = FilterGourdonHardLeaves(coordinate: ref coordinate, coordinates: coordinates, factors: factors,
                        last: Math.Max(val1: last, val2: (coordinate - GourdonHardLeafScanSpan)), prime: prime);

                    sum += CountGourdonHardBatch(coordinates: coordinates[..count], divisors: divisors, factors: factors, low: low,
                        prefix: ref prefix, previous: phi[index], quotient: quotient, quotients: leafQuotients);
                }
                phi[index] += sieve.TotalCount;
                sieve.CrossOff(index: index, prime: prime);
            }
            if (index <= squareRootIndex) { continue; }

            // For p>sqrt(y), an m<=y whose least factor exceeds p must be prime.
            // Its Moebius value is -1, so D contributes +phi(floor(x/(p*m)),b-1).
            for (; (index <= activeCount); ++index) {
                if ((index & 255) == 0) { cancellationToken.ThrowIfCancellationRequested(); }

                var prime = primes[index];
                var quotient = quotients[index];
                var minimum = Math.Max(val1: (quotient / high), val2: prime);
                var maximum = Math.Min(val1: (quotient / lowDivisor), val2: upperBounds[index]);

                if (prime >= maximum) { break; }

                if (minimum >= maximum) {
                    phi[index] += sieve.TotalCount;
                    sieve.CrossOff(index: index, prime: prime);
                    continue;
                }

                // Leaves are the primes q in (minimum, maximum], read from the largest down so that x/(p*q)
                // and the prefix offsets increase. Each chunk divides ascending primes, then reads it backwards.
                var top = ((int)tables.CountPrimes(value: ((uint)maximum)));
                var stop = ((int)tables.CountPrimes(value: ((uint)minimum)));
                var leaves = ((uint)(top - stop));
                var prefix = sieve.StartPrefix();
                ref var leafQuotient = ref MemoryMarshal.GetReference(span: leafQuotients);
                // Each prefix count is below the segment width and there are fewer than y leaves, so the
                // per-prime subtotal fits ulong; phi[b] is added once per prime rather than once per leaf.
                var counted = 0UL;

                while (top > stop) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bottom = Math.Max(val1: (stop + 1), val2: ((top - GourdonHardLeafBatchSize) + 1));
                    var length = ((top - bottom) + 1);

                    DivideGourdonQuotients(dividend: quotient, divisors: primes.AsSpan(length: length, start: bottom), quotients: leafQuotients);
                    for (var offset = (length - 1); (offset >= 0); --offset) {
                        counted += prefix.Count(offset: ((uint)(Unsafe.Add(elementOffset: offset, source: ref leafQuotient) - low)));
                    }
                    top = (bottom - 1);
                }
                sum += ((((Int128)phi[index]) * leaves) + counted);
                phi[index] += sieve.TotalCount;
                sieve.CrossOff(index: index, prime: prime);
            }
        }
        return sum;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FilterGourdonHardLeaves(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<int> coordinates) {
        // Instruction-set dispatch folds when the caller is compiled. Every width selects the same
        // coordinates in the same order; the laws run each width on identical inputs.
        if (Vector512.IsHardwareAccelerated) {
            return FilterGourdonHardLeaves<VectorLanes512, Vector512<byte>>(coordinate: ref coordinate,
                coordinates: coordinates, factors: factors, last: last, prime: prime);
        }
        if (Vector256.IsHardwareAccelerated) {
            return FilterGourdonHardLeaves<VectorLanes256, Vector256<byte>>(coordinate: ref coordinate,
                coordinates: coordinates, factors: factors, last: last, prime: prime);
        }
        if (Vector128.IsHardwareAccelerated) {
            return FilterGourdonHardLeaves<VectorLanes128, Vector128<byte>>(coordinate: ref coordinate,
                coordinates: coordinates, factors: factors, last: last, prime: prime);
        }
        return FilterGourdonHardLeavesScalar(coordinate: ref coordinate, coordinates: coordinates, factors: factors,
            last: last, prime: prime);
    }

    // Writes each coordinate c in (last, coordinate] whose least factor exceeds prime, in descending order, and
    // moves coordinate to the next unexamined one. A call stops early only when another full vector might not
    // fit; it then returns at least coordinates.Length-lanes survivors. Requires -1<=last<=coordinate<factors.Length
    // and coordinates.Length>=lanes. The sign bit stores mu, not the factor. After masking, signed compares are
    // exact: factors <=32767 and the caller's p<=sqrt(y)<32767.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FilterGourdonHardLeaves<TLanes, TBytes>(ReadOnlySpan<ushort> factors, ref int coordinate, int last,
        uint prime, Span<int> coordinates)
        where TLanes : struct, IByteVectorLanes<TBytes>
        where TBytes : struct {
        Debug.Assert(((last >= -1) && (last <= coordinate) && (coordinate < factors.Length)));
        var lanes = (TLanes.ByteCount / sizeof(ushort));
        var factorMask = TLanes.Broadcast(value: CombinatorialFactorMask);
        var threshold = TLanes.Broadcast(value: ((ushort)prime));
        ref readonly var source = ref MemoryMarshal.GetReference(span: factors);
        ref var destination = ref MemoryMarshal.GetReference(span: coordinates);
        var limit = (coordinates.Length - lanes);
        var count = 0;
        var current = coordinate;

        while ((current >= (last + lanes)) && (count <= limit)) {
            var start = ((current - lanes) + 1);
            var survivors = TLanes.GreaterThanInt16Mask(left: TLanes.And(left: TLanes.Load(elementOffset: ((nuint)start), source: in source),
                right: factorMask), right: threshold);

            // Loads ascend in memory; consume the highest set lane first to preserve
            // descending m and increasing prefix queries. count<=limit bounds the stores.
            while (survivors != 0) {
                var lane = BitOperations.Log2(value: survivors);

                Unsafe.Add(elementOffset: count++, source: ref destination) = (start + lane);
                survivors ^= (1U << lane);
            }
            current -= lanes;
        }
        return FilterGourdonHardLeafTail(coordinate: ref coordinate, coordinates: coordinates, count: count,
            current: current, factors: factors, lanes: lanes, last: last, prime: prime);
    }
    // The scalar rung of FilterGourdonHardLeaves: four branch-free predicates per step, same contract.
    internal static int FilterGourdonHardLeavesScalar(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<int> coordinates) {
        const int Lanes = 4;
        var limit = (coordinates.Length - Lanes);
        var count = 0;
        var current = coordinate;

        while ((current >= (last + Lanes)) && (count <= limit)) {
            coordinates[count] = current;
            count += (((factors[current] & CombinatorialFactorMask) > prime) ? 1 : 0);
            coordinates[count] = (current - 1);
            count += (((factors[(current - 1)] & CombinatorialFactorMask) > prime) ? 1 : 0);
            coordinates[count] = (current - 2);
            count += (((factors[(current - 2)] & CombinatorialFactorMask) > prime) ? 1 : 0);
            coordinates[count] = (current - 3);
            count += (((factors[(current - 3)] & CombinatorialFactorMask) > prime) ? 1 : 0);
            current -= Lanes;
        }
        return FilterGourdonHardLeafTail(coordinate: ref coordinate, coordinates: coordinates, count: count,
            current: current, factors: factors, lanes: Lanes, last: last, prime: prime);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FilterGourdonHardLeafTail(ReadOnlySpan<ushort> factors, ref int coordinate, int last, uint prime,
        Span<int> coordinates, int count, int current, int lanes) {
        // A batch with room for one more vector stopped because fewer than one vector of
        // coordinates remains; all of them fit. A fuller batch leaves them for the next call.
        if (count <= (coordinates.Length - lanes)) {
            for (; (current > last); --current) {
                coordinates[count] = current;
                count += (((factors[current] & CombinatorialFactorMask) > prime) ? 1 : 0);
            }
        }
        coordinate = current;
        return count;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long CountGourdonHardBatch(scoped ReadOnlySpan<int> coordinates, scoped Span<uint> divisors,
        scoped Span<ulong> quotients, ushort[] factors, ulong quotient, ulong low, ulong previous,
        ref CombinatorialPrefixCursor prefix) {
        Debug.Assert(((divisors.Length >= coordinates.Length) && (quotients.Length >= coordinates.Length)));
        ref var divisor = ref MemoryMarshal.GetReference(span: divisors);
        ref var leafQuotient = ref MemoryMarshal.GetReference(span: quotients);
        ref var factor = ref MemoryMarshal.GetArrayDataReference(array: factors);

        for (var index = 0; (index < coordinates.Length); ++index) {
            Unsafe.Add(elementOffset: index, source: ref divisor) = CombinatorialNumber(coordinate: coordinates[index]);
        }
        DivideGourdonQuotients(dividend: quotient, divisors: divisors[..coordinates.Length], quotients: quotients);

        // Each count is <=x/y<2^43 since y>cbrt(x). At most 128 leaves
        // contribute less than 2^50 in magnitude; only the outer sum needs Int128.
        // The Moebius sign selects the count's sign without a branch, as a 50/50 data-dependent
        // branch would mispredict on half of the leaves, and the shared phi term is added once
        // per batch as previous times the summed signs.
        var cursor = prefix;
        ref var coordinate = ref MemoryMarshal.GetReference(span: coordinates);
        var sum = 0L;
        var signs = 0L;

        for (var index = 0; (index < coordinates.Length); ++index) {
            var count = ((long)cursor.Count(offset: ((uint)(Unsafe.Add(elementOffset: index, source: ref leafQuotient) - low))));
            // Zero when the leaf adds its count (sign bit set), all ones when it subtracts it.
            var negate = (((long)((Unsafe.Add(elementOffset: Unsafe.Add(elementOffset: index, source: ref coordinate), source: ref factor)
                & CombinatorialNegativeMoebius) >> 15)) - 1L);

            sum += ((count ^ negate) - negate);
            signs += negate | 1L;
        }
        prefix = cursor;
        return (sum + (((long)previous) * signs));
    }

    private sealed partial class CombinatorialCountingWorkspace {
        private uint[] m_gourdonHardLowerBounds = [];
        private uint[] m_gourdonHardUpperBounds = [];

        internal (uint[] LowerBounds, uint[] UpperBounds) PrepareGourdonHardBounds(uint cutoff,
            uint[] primes, ulong[] quotients, int primeCount, int squareRootIndex,
            CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            if (m_gourdonHardUpperBounds.Length <= primeCount) {
                m_gourdonHardLowerBounds = new uint[(primeCount + 1)];
                m_gourdonHardUpperBounds = new uint[(primeCount + 1)];
            }

            // Bounds depend on both x and y. Recompute every active slot when a
            // correction count reuses retained arrays, even when the cutoff shrinks.
            // The two arrays retain eight bytes per active prime; only this setup
            // divides by p^2 and, in the square-free region, by p for the y/p bound.
            for (var index = (CombinatorialTinyPrimeCount + 1); (index <= primeCount); ++index) {
                if ((index & 16383) == 0) { cancellationToken.ThrowIfCancellationRequested(); }

                var prime = primes[index];

                m_gourdonHardLowerBounds[index] = ((index <= squareRootIndex) ? (cutoff / prime) : prime);
                m_gourdonHardUpperBounds[index] = ((uint)Math.Min(val1: cutoff,
                    val2: (quotients[index] / (((ulong)prime) * prime))));
            }
            return (m_gourdonHardLowerBounds, m_gourdonHardUpperBounds);
        }
    }
}
