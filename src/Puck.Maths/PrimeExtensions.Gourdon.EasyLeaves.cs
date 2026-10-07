using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExtensions {
    // Gourdon A+C formulas, segment bounds and reflected clustered leaves follow
    // primecount src/gourdon/AC_default.hpp and doc/Easy-Special-Leaves.pdf.
    // Copyright (C) 2026 Kim Walisch. Distributed under the BSD 2-Clause license;
    // see THIRD-PARTY-NOTICES.md. The bitmap traversal and unsigned carriers are local.
    private static Int128 CountGourdonEasyLeaves(ulong value, uint cutoff, uint xStar, CombinatorialFactorTables tables,
        CancellationToken cancellationToken, PrimeCountWorkBuilder? profile) {
        cancellationToken.ThrowIfCancellationRequested();
        var counter = new GourdonEasyLeafCounter(cancellationToken: cancellationToken, cutoff: cutoff, profile: profile, tables: tables,
            value: value, xStar: xStar);

        // A 16-KiB U(30) bitmap plus its 8-KiB word prefixes stays within a 32-KiB L1.
        // One persistent sieve serves every A/C query below sqrt(x), including the final partial word.
        PrimeExploration.VisitPrimeCountSegments(highExclusive: ((uint)value.SquareRoot()), segmentBytes: 16384,
            prefixMasks: CombinatorialTables.PrefixMasks, visitor: counter.Consume, cancellationToken: cancellationToken);
        return counter.Sum;
    }

    private sealed class GourdonEasyLeafCounter {
        private readonly ulong m_value;
        private readonly uint m_cutoff;
        private readonly uint m_xStar;
        private readonly uint m_cubeRoot;
        private readonly uint m_sqrtCutoff;
        private readonly uint m_piCutoff;
        private readonly uint m_piSqrtCutoff;
        private readonly uint m_piQuotientCubeRoot;
        private readonly uint m_largestClusteredPrime;
        private readonly ulong m_cutoffQuotient;
        private readonly CombinatorialFactorTables m_tables;
        private readonly uint[] m_primes;
        private readonly CancellationToken m_cancellationToken;
        private readonly PrimeCountWorkBuilder? m_profile;
        private readonly ulong[] m_quotients = new ulong[GourdonQuotientBatchSize];

        internal Int128 Sum { get; private set; }

        internal GourdonEasyLeafCounter(ulong value, uint cutoff, uint xStar, CombinatorialFactorTables tables,
            CancellationToken cancellationToken, PrimeCountWorkBuilder? profile) {
            m_value = value;
            m_cutoff = cutoff;
            m_xStar = xStar;
            m_tables = tables;
            m_primes = tables.Primes;
            m_cubeRoot = CombinatorialCubeRoot(value: value);
            m_sqrtCutoff = cutoff.SquareRoot();
            m_piCutoff = tables.CountPrimes(value: cutoff);
            m_piSqrtCutoff = tables.CountPrimes(value: m_sqrtCutoff);
            m_cutoffQuotient = (value / cutoff);
            m_piQuotientCubeRoot = tables.CountPrimes(value: CombinatorialCubeRoot(value: m_cutoffQuotient));
            m_largestClusteredPrime = m_primes[m_piCutoff];
            m_cancellationToken = cancellationToken;
            m_profile = profile;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void Consume(PrimeExploration.PrimeCountSegment segment) {
            m_cancellationToken.ThrowIfCancellationRequested();
            if (m_profile is not null) {
                ++m_profile.EasyLeafSegments;
                m_profile.EasyLeafBitmapBytes += ((uint)segment.BitmapBytes);
            }
            var low = segment.Low;
            var high = segment.High;
            var piSqrtLow = m_tables.CountPrimes(value: low.SquareRoot());
            var xLow = (m_value / Math.Max(val1: low, val2: 1U));
            var xHigh = (m_value / high);

            // z=y. C1's square-free multiplier is a prime or two distinct primes.
            if (low < m_cutoff) {
                var first = Math.Max(val1: ((uint)CombinatorialTinyPrimeCount), val2: m_piQuotientCubeRoot);
                var minimumPrime = ((uint)Math.Min(val1: (m_cutoffQuotient / high), val2: m_sqrtCutoff));

                first = (Math.Max(val1: first, val2: Math.Max(val1: piSqrtLow, val2: m_tables.CountPrimes(value: minimumPrime))) + 1U);
                for (var index = first; (index <= m_piSqrtCutoff); ++index) {
                    if ((index & 255U) == 0) { m_cancellationToken.ThrowIfCancellationRequested(); }
                    Sum -= CountC1(xLow: xLow, xHigh: xHigh, quotient: (m_value / m_primes[index]), index: index, segment: in segment);
                }
            }

            var firstC2 = Math.Max(val1: ((uint)CombinatorialTinyPrimeCount),
                val2: Math.Max(val1: m_piQuotientCubeRoot, val2: m_piSqrtCutoff));
            var minimumC2Prime = ((uint)Math.Min(val1: (xHigh / m_cutoff), val2: m_xStar));

            firstC2 = (Math.Max(val1: firstC2, val2: Math.Max(val1: piSqrtLow, val2: m_tables.CountPrimes(value: minimumC2Prime))) + 1U);
            var xHighSquaredQuotient = (xHigh / high);
            var minimumAPrime = ((uint)Math.Min(val1: xHighSquaredQuotient, val2: m_cubeRoot));
            var firstA = (m_tables.CountPrimes(value: Math.Max(val1: m_xStar, val2: minimumAPrime)) + 1U);
            var sqrtXLow = xLow.SquareRoot();
            var lastC2 = m_tables.CountPrimes(value: ((uint)Math.Min(val1: sqrtXLow, val2: m_xStar)));
            var maximumC2Prime = (xLow / m_largestClusteredPrime);
            var lastClusteredC2 = m_tables.CountPrimes(value: ((uint)Math.Min(val1: maximumC2Prime,
                val2: Math.Min(val1: sqrtXLow, val2: m_xStar))));
            var firstSparseC2 = (m_tables.CountPrimes(value: ((uint)Math.Min(val1: xHighSquaredQuotient, val2: m_xStar))) + 1U);

            firstSparseC2 = Math.Max(val1: firstC2, val2: Math.Max(val1: firstSparseC2, val2: (lastClusteredC2 + 1U)));
            var lastA = m_tables.CountPrimes(value: ((uint)Math.Min(val1: sqrtXLow, val2: m_cubeRoot)));

            // The reflected cluster and sparse ranges are disjoint. Their bounds also skip
            // primes whose next factor cannot put x/(p*q) inside this half-open segment.
            for (var index = firstC2; (index <= lastClusteredC2); ++index) {
                if ((index & 255U) == 0) { m_cancellationToken.ThrowIfCancellationRequested(); }
                Sum += CountC2(xLow: xLow, xHigh: xHigh, quotient: (m_value / m_primes[index]), index: index, segment: in segment);
            }
            for (var index = firstSparseC2; (index <= lastC2); ++index) {
                if ((index & 255U) == 0) { m_cancellationToken.ThrowIfCancellationRequested(); }
                Sum += CountC2(xLow: xLow, xHigh: xHigh, quotient: (m_value / m_primes[index]), index: index, segment: in segment);
            }
            for (var index = firstA; (index <= lastA); ++index) {
                if ((index & 255U) == 0) { m_cancellationToken.ThrowIfCancellationRequested(); }
                Sum += CountA(xLow: xLow, xHigh: xHigh, quotient: (m_value / m_primes[index]), index: index, segment: in segment);
            }
        }

        private Int128 CountA(ulong xLow, ulong xHigh, ulong quotient, uint index, in PrimeExploration.PrimeCountSegment segment) {
            var prime = m_primes[index];
            var squareRoot = quotient.SquareRoot();
            var minimumSecond = Math.Min(val1: (xHigh / prime), val2: squareRoot);
            var maximumSecond = Math.Min(val1: (xLow / prime), val2: squareRoot);
            var first = (m_tables.CountPrimes(value: ((uint)Math.Max(val1: prime, val2: minimumSecond))) + 1U);
            var lastSingle = m_tables.CountPrimes(value: ((uint)Math.Min(val1: (quotient / m_cutoff), val2: maximumSecond)));
            var lastDouble = m_tables.CountPrimes(value: ((uint)maximumSecond));
            var sum = SumPi(first: first, index: 2, last: lastSingle, multiplier: 1, quotient: quotient, segment: in segment);

            first = Math.Max(val1: first, val2: (lastSingle + 1U));
            return (sum + SumPi(first: first, index: 2, last: lastDouble, multiplier: 2, quotient: quotient, segment: in segment));
        }
        private Int128 CountC1(ulong xLow, ulong xHigh, ulong quotient, uint index, in PrimeExploration.PrimeCountSegment segment) {
            var prime = ((ulong)m_primes[index]);
            var maximum = Math.Min(val1: (xLow / prime), val2: m_cutoff);
            var cubeQuotient = (quotient / (prime * prime));
            var minimum = Math.Max(val1: (xHigh / prime), val2: Math.Max(val1: cubeQuotient, val2: (m_cutoff / prime)));

            if (minimum >= maximum) { return 0; }
            var maximumPrime = Math.Min(val1: m_cutoff, val2: maximum);
            var sum = Int128.Zero;

            if (minimum < maximumPrime) {
                var first = (m_tables.CountPrimes(value: ((uint)minimum)) + 1U);
                var last = m_tables.CountPrimes(value: ((uint)maximumPrime));

                sum -= SumPi(first: first, index: index, last: last, multiplier: 1, quotient: quotient, segment: in segment);
            }
            var maximumFactor = Math.Min(val1: m_cutoff, val2: maximum.SquareRoot());
            var minimumFactor = Math.Max(val1: prime, val2: (minimum / m_cutoff));

            if (minimumFactor < maximumFactor) {
                var first = (m_tables.CountPrimes(value: ((uint)minimumFactor)) + 1U);
                var last = m_tables.CountPrimes(value: ((uint)maximumFactor));

                for (var factorIndex = first; (factorIndex <= last); ++factorIndex) {
                    if ((factorIndex & 255U) == 0) { m_cancellationToken.ThrowIfCancellationRequested(); }
                    var factor = m_primes[factorIndex];
                    var minimumPartner = Math.Max(val1: factor, val2: (minimum / factor));
                    var maximumPartner = Math.Min(val1: m_cutoff, val2: (maximum / factor));

                    if (minimumPartner >= maximumPartner) { continue; }
                    var partnerFirst = (m_tables.CountPrimes(value: ((uint)minimumPartner)) + 1U);
                    var partnerLast = m_tables.CountPrimes(value: ((uint)maximumPartner));

                    sum += SumPi(first: partnerFirst, index: index, last: partnerLast,
                        multiplier: 1, quotient: (quotient / factor), segment: in segment);
                }
            }
            return sum;
        }
        private Int128 CountC2(ulong xLow, ulong xHigh, ulong quotient, uint index, in PrimeExploration.PrimeCountSegment segment) {
            var prime = ((ulong)m_primes[index]);
            var maximum = Math.Min(val1: (xLow / prime), val2: Math.Min(val1: (quotient / prime), val2: m_cutoff));
            var cubeQuotient = (quotient / (prime * prime));
            var minimum = Math.Max(val1: (xHigh / prime), val2: Math.Max(val1: cubeQuotient, val2: prime));

            if (minimum >= maximum) { return 0; }
            var piMinimum = m_tables.CountPrimes(value: ((uint)minimum));
            var squareRoot = quotient.SquareRoot();
            var minimumClustered = Math.Clamp(max: maximum, min: minimum, value: squareRoot);
            var piMinimumClustered = m_tables.CountPrimes(value: ((uint)minimumClustered));
            var globalMinimumClustered = Math.Min(val1: Math.Max(val1: cubeQuotient, val2: Math.Max(val1: squareRoot, val2: prime)), val2: m_cutoff);
            var conjugateLow = piMinimum;
            var conjugateHigh = piMinimum;
            var first = (piMinimum + 1U);
            var sum = Int128.Zero;

            // The full cluster's endpoint lies in exactly one segment. Its integration-by-parts
            // boundary correction is therefore applied once, even when the cluster spans segments.
            if ((maximum >= m_largestClusteredPrime) && (m_piCutoff > piMinimumClustered)) {
                var lowerQuotient = (quotient / m_largestClusteredPrime);
                var upperQuotient = (quotient / (globalMinimumClustered + 1UL));
                var piGlobalMinimum = m_tables.CountPrimes(value: ((uint)globalMinimumClustered));

                sum += ((((Int128)m_tables.CountPrimes(value: ((uint)lowerQuotient))) * m_piCutoff)
                    - (((Int128)m_tables.CountPrimes(value: ((uint)upperQuotient))) * piGlobalMinimum));
                sum -= (((Int128)(index - 2U)) * (m_piCutoff - piGlobalMinimum));
            }
            if ((globalMinimumClustered < m_largestClusteredPrime)
                && (segment.Low < m_largestClusteredPrime) && (segment.High > (globalMinimumClustered + 1UL))) {
                var lowerQuotient = (quotient / m_largestClusteredPrime);
                var upperQuotient = (quotient / (globalMinimumClustered + 1UL));

                conjugateLow = Math.Max(val1: m_tables.CountPrimes(value: ((uint)lowerQuotient)), val2: piMinimum);
                conjugateHigh = Math.Min(val1: m_tables.CountPrimes(value: ((uint)upperQuotient)), val2: piMinimumClustered);
                conjugateHigh = Math.Max(val1: conjugateHigh, val2: conjugateLow);
            }

            // The conjugate interval doubles only pi(q); the -b+2 term is counted once.
            sum += SumPi(first: first, index: index, last: conjugateLow, multiplier: 1, quotient: quotient, segment: in segment);
            first = (conjugateLow + 1U);
            sum += SumPi(first: first, index: index, last: conjugateHigh, multiplier: 2, quotient: quotient, segment: in segment);
            first = (conjugateHigh + 1U);
            return (sum + SumPi(first: first, index: index, last: piMinimumClustered, multiplier: 1, quotient: quotient, segment: in segment));
        }
        // Sums pi(quotient/p_i) for i in [first,last], scaled by multiplier, plus (2-index) per term. Quotients are
        // divided in batches by the exact estimate-and-correct rungs, then looked up from register-held state.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private Int128 SumPi(ulong quotient, uint first, uint last, uint index, uint multiplier, in PrimeExploration.PrimeCountSegment segment) {
            if (first > last) { return 0; }
            var count = ((last - first) + 1U);
            var lookup = segment;
            var primes = m_primes.AsSpan(length: ((int)count), start: ((int)first));
            var quotients = m_quotients.AsSpan();
            ref var quotientsStart = ref MemoryMarshal.GetArrayDataReference(array: m_quotients);
            // The unsigned subtotal fits: fewer than Array.MaxLength terms, each bounded by pi(uint.MaxValue).
            var sum = 0UL;

            while (!primes.IsEmpty) {
                m_cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(val1: primes.Length, val2: quotients.Length);

                DivideGourdonQuotients(dividend: quotient, divisors: primes[..length], quotients: quotients);
                // A chunk-local subtotal stays in a register; the outer sum is live across the division call.
                var chunk = 0UL;

                for (var offset = 0; (offset < length); ++offset) {
                    chunk += lookup.Count(value: Unsafe.Add(elementOffset: offset, source: ref quotientsStart));
                }
                sum += chunk;
                primes = primes[length..];
            }
            return ((((Int128)sum) * multiplier) + (((Int128)count) * (2 - ((Int128)index))));
        }
    }
}
