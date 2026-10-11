using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExtensions {
    // Gourdon's D decomposition and the filtered batches follow primecount's
    // src/gourdon/D_default.hpp, Copyright (C) 2026 Kim Walisch, BSD 2-Clause.
    // See THIRD-PARTY-NOTICES.md. This specialization fixes z=y and k=8 and uses
    // the local U(30) factor encoding, sequential phi state and unsigned quotients.
    private const uint GourdonHardLeafSegmentWidth = ((32U * 1024U) * PrimeWheel30.Modulus);
    private const int GourdonHardLeafBatchSize = 128;
    private const int GourdonHardLeafScanSpan = 16384;
    private const int GourdonHardLeafNegationWords = ((GourdonHardLeafBatchSize * sizeof(short)) / sizeof(ulong));

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
        Debug.Assert((activeCount > PrimeLeafKernels.CombinatorialTinyPrimeCount));

        var squareRootIndex = Math.Min(val1: activeCount,
            val2: ((int)tables.CountPrimes(value: cutoff.SquareRoot())));

        var (sieve, phi, quotients) = workspace.PrepareLeaves(cancellationToken: cancellationToken,
            primeCount: activeCount, primes: primes, value: value,
            width: GourdonHardLeafSegmentWidth);
        var (lowerBounds, upperBounds) = workspace.PrepareGourdonHardBounds(cancellationToken: cancellationToken,
            cutoff: cutoff, primeCount: activeCount, primes: primes,
            quotients: quotients, squareRootIndex: squareRootIndex);
        workspace.PrepareGourdonHardBatches(divisors: out var divisors, quotients: out var leafQuotients);
        // A square-free batch keeps each leaf's negation mask behind the quotients, one base address for both.
        var negations = MemoryMarshal.Cast<ulong, short>(span: leafQuotients[GourdonHardLeafBatchSize..]);
        var sum = Int128.Zero;

        if (profile is not null) {
            profile.LeafFrontier = (limit - 1UL);
            profile.CachedQuotientDivisions += ((uint)(activeCount - PrimeLeafKernels.CombinatorialTinyPrimeCount));
        }

        // Before crossing off p_b, phi[b] is phi(low-1,b-1), initially zero.
        // The segment bitmap contains exactly the survivors of primes 1..b-1.
        // Descending m makes floor(x/(p*m)) nondecreasing, as the prefix cursor requires.
        for (var low = 0UL; (low < limit); low += GourdonHardLeafSegmentWidth) {
            cancellationToken.ThrowIfCancellationRequested();

            var high = Math.Min(val1: (low + GourdonHardLeafSegmentWidth), val2: limit);
            var lowDivisor = Math.Max(val1: low, val2: 1UL);
            // Both divisors are positive and constant for this segment, and the floor-reciprocal division is exact for
            // every ulong quotient, the 1e19-scale boundary included.
            var highReciprocal = PrimeDivisionKernels.FloorReciprocalCore(divisor: high);
            var lowReciprocal = PrimeDivisionKernels.FloorReciprocalCore(divisor: lowDivisor);

            sieve.InitializeCore(low: low, width: ((uint)(high - low)));
            if (profile is not null) {
                ++profile.LeafSegments;
                profile.LeafBitmapBytes += ((((high - low) + (PrimeWheel30.WordIntegers - 1)) / PrimeWheel30.WordIntegers) * sizeof(ulong));
            }

            var index = (PrimeLeafKernels.CombinatorialTinyPrimeCount + 1);

            for (; (index <= squareRootIndex); ++index) {
                if ((index & 255) == 0) { cancellationToken.ThrowIfCancellationRequested(); }

                var prime = primes[index];
                var quotient = quotients[index];
                var minimum = Math.Max(val1: PrimeDivisionKernels.DivideByFloorReciprocalCore(dividend: quotient, divisor: high, reciprocal: highReciprocal), val2: lowerBounds[index]);
                var maximum = Math.Min(val1: PrimeDivisionKernels.DivideByFloorReciprocalCore(dividend: quotient, divisor: lowDivisor, reciprocal: lowReciprocal), val2: upperBounds[index]);

                // This upper bound decreases with p and with low. Once it is <=p,
                // no later prime can have a leaf in this or any following segment.
                if (prime >= maximum) { break; }

                var last = PrimeLeafKernels.CombinatorialCoordinate(value: ((uint)Math.Min(val1: minimum, val2: maximum)));
                var first = PrimeLeafKernels.CombinatorialCoordinate(value: ((uint)maximum));
                var coordinate = first;

                if (profile is not null) { profile.SpecialFactorCoordinates += ((uint)(first - last)); }

                // With z=y the maximum-prime-factor condition is automatic. A
                // masked factor >p selects exactly square-free m with lpf(m)>p.
                // Separate filtering, division and phi queries into short batches.
                var prefix = sieve.StartPrefix();

                while (coordinate > last) {
                    cancellationToken.ThrowIfCancellationRequested();

                    var subtracted = 0;
                    var count = PrimeLeafKernels.FilterGourdonHardLeaves(coordinate: ref coordinate, divisors: divisors, factors: factors,
                        last: Math.Max(val1: last, val2: (coordinate - GourdonHardLeafScanSpan)), negations: negations, prime: prime,
                        subtracted: ref subtracted);

                    sum += CountGourdonHardBatch(divisors: divisors[..count], low: low, prefix: ref prefix, previous: phi[index],
                        quotient: quotient, quotients: leafQuotients, subtracted: subtracted);
                }
                phi[index] += sieve.TotalCount;
                sieve.CrossOffCore(index: index, prime: prime);
            }
            if (index <= squareRootIndex) { continue; }

            // For p>sqrt(y), an m<=y whose least factor exceeds p must be prime.
            // Its Moebius value is -1, so D contributes +phi(floor(x/(p*m)),b-1).
            for (; (index <= activeCount); ++index) {
                if ((index & 255) == 0) { cancellationToken.ThrowIfCancellationRequested(); }

                var prime = primes[index];
                var quotient = quotients[index];
                var minimum = Math.Max(val1: PrimeDivisionKernels.DivideByFloorReciprocalCore(dividend: quotient, divisor: high, reciprocal: highReciprocal), val2: prime);
                var maximum = Math.Min(val1: PrimeDivisionKernels.DivideByFloorReciprocalCore(dividend: quotient, divisor: lowDivisor, reciprocal: lowReciprocal), val2: upperBounds[index]);

                if (prime >= maximum) { break; }

                if (minimum >= maximum) {
                    phi[index] += sieve.TotalCount;
                    sieve.CrossOffCore(index: index, prime: prime);
                    continue;
                }

                // Leaves are the primes q in (minimum, maximum], read from the largest down so that x/(p*q)
                // and the prefix offsets increase. Each chunk divides ascending primes, then reads it backwards.
                var top = ((int)tables.CountPrimes(value: ((uint)maximum)));
                var stop = ((int)tables.CountPrimes(value: ((uint)minimum)));
                var leaves = ((uint)(top - stop));
                var prefix = sieve.StartPrefix();
                // Each prefix count is below the segment width and there are fewer than y leaves, so the
                // per-prime subtotal fits ulong; phi[b] is added once per prime rather than once per leaf.
                var counted = 0UL;

                while (top > stop) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bottom = Math.Max(val1: (stop + 1), val2: ((top - GourdonHardLeafBatchSize) + 1));
                    var length = ((top - bottom) + 1);

                    PrimeDivisionKernels.DivideGourdonQuotients(dividend: quotient, divisors: primes.AsSpan(length: length, start: bottom), quotients: leafQuotients[..length]);
                    counted += CountGourdonPrimeBatch(low: low, prefix: ref prefix, quotients: leafQuotients[..length]);
                    top = (bottom - 1);
                }
                sum += ((((Int128)phi[index]) * leaves) + counted);
                phi[index] += sieve.TotalCount;
                sieve.CrossOffCore(index: index, prime: prime);
            }
        }
        return sum;
    }
    // Sums one filtered batch of hard leaves: divides quotient by each leaf number and reads the prefix count at
    // floor(quotient/m)-low, negated where the leaf's mask is all ones; subtracted counts those leaves. Leaf i's
    // negation mask is short i of quotients[GourdonHardLeafBatchSize..], where the filter wrote it.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long CountGourdonHardBatch(scoped ReadOnlySpan<uint> divisors, int subtracted, scoped Span<ulong> quotients, ulong quotient,
        ulong low, ulong previous, ref PrimeLeafKernels.CombinatorialPrefixCursor prefix) {
        var length = divisors.Length;

        Debug.Assert(((length <= GourdonHardLeafBatchSize) && (quotients.Length >= (GourdonHardLeafBatchSize + GourdonHardLeafNegationWords))));
        PrimeDivisionKernels.DivideGourdonQuotients(dividend: quotient, divisors: divisors, quotients: quotients[..length]);
        ref var leafQuotient = ref MemoryMarshal.GetReference(span: quotients);
        const nint NegationOffset = ((GourdonHardLeafBatchSize * sizeof(ulong)) / sizeof(short));

        // Each count is <=x/y<2^43 since y>cbrt(x). At most 128 leaves
        // contribute less than 2^50 in magnitude; only the outer sum needs Int128.
        // The Moebius sign selects the count's sign without a branch, as a 50/50 data-dependent
        // branch would mispredict on half of the leaves: (count^negate)-negate is +count or -count,
        // so the loop sums count^negate onto the subtracted leaves' count. The shared phi term is
        // added once per batch as previous times the summed signs, length-2*subtracted.
        var cursor = prefix;
        var sum = (subtracted + (((long)previous) * (length - (2 * subtracted))));

        ref readonly var end = ref Unsafe.Add(elementOffset: length, source: ref leafQuotient);
        ref var negation = ref Unsafe.Add(elementOffset: NegationOffset, source: ref Unsafe.As<ulong, short>(source: ref leafQuotient));

        for (ref var leaf = ref leafQuotient; Unsafe.IsAddressLessThan(left: in leaf, right: in end); leaf = ref Unsafe.Add(elementOffset: 1, source: ref leaf)) {
            sum += ((long)cursor.CountCore(offset: ((nuint)(leaf - low)))) ^ negation;
            negation = ref Unsafe.Add(elementOffset: 1, source: ref negation);
        }
        prefix = cursor;
        return sum;
    }
    // Sums the prefix counts at quotients[i]-low from the last quotient to the first, which the prime leaves of one
    // prime visit in nondecreasing order.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong CountGourdonPrimeBatch(scoped ReadOnlySpan<ulong> quotients, ulong low, ref PrimeLeafKernels.CombinatorialPrefixCursor prefix) {
        ref var leafQuotient = ref MemoryMarshal.GetReference(span: quotients);
        var cursor = prefix;
        var counted = 0UL;

        for (var index = ((nuint)quotients.Length); (index > 0); --index) {
            counted += cursor.CountCore(offset: ((nuint)(Unsafe.Add(elementOffset: (index - 1), source: ref leafQuotient) - low)));
        }
        prefix = cursor;
        return counted;
    }

    private sealed partial class CombinatorialCountingWorkspace {
        private uint[] m_gourdonHardLowerBounds = [];
        private uint[] m_gourdonHardUpperBounds = [];
        // Quotient stores start at vector multiples inside these aligned buffers. Filter stores start at the running
        // survivor count and can cross lines even though the buffer starts are aligned.
        private CacheAlignedArray<uint> m_gourdonHardDivisors;
        private CacheAlignedArray<ulong> m_gourdonHardQuotients;

        internal void PrepareGourdonHardBatches(out Span<uint> divisors, out Span<ulong> quotients) {
            if (m_gourdonHardDivisors.Length == 0) {
                m_gourdonHardDivisors = new(length: GourdonHardLeafBatchSize);
                m_gourdonHardQuotients = new(length: (GourdonHardLeafBatchSize + GourdonHardLeafNegationWords));
            }
            divisors = m_gourdonHardDivisors.Span;
            quotients = m_gourdonHardQuotients.Span;
        }
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
            for (var index = (PrimeLeafKernels.CombinatorialTinyPrimeCount + 1); (index <= primeCount); ++index) {
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
