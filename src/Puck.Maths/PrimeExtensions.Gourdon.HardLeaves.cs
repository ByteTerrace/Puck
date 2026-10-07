using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Puck.Maths;

public static partial class PrimeExtensions {
    // Gourdon's D decomposition and the filtered batches follow primecount's
    // src/gourdon/D_default.hpp, Copyright (C) 2026 Kim Walisch, BSD 2-Clause.
    // See THIRD-PARTY-NOTICES.md. This specialization fixes z=y and k=8 and uses
    // the local U(30) factor encoding, sequential phi state and unsigned quotients.
    private const uint GourdonHardLeafSegmentWidth = ((32U * 1024U) * 30U);
    private const int GourdonHardLeafBatchSize = 128;

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

        if (activeCount <= CombinatorialTinyPrimeCount) { return Int128.Zero; }

        var squareRootIndex = Math.Min(val1: activeCount,
            val2: ((int)tables.CountPrimes(value: cutoff.SquareRoot())));

        var (sieve, phi, quotients) = workspace.PrepareLeaves(cancellationToken: cancellationToken,
            primeCount: activeCount, primes: primes, value: value,
            width: GourdonHardLeafSegmentWidth);
        var (lowerBounds, upperBounds) = workspace.PrepareGourdonHardBounds(cancellationToken: cancellationToken,
            cutoff: cutoff, primeCount: activeCount, primes: primes,
            quotients: quotients, squareRootIndex: squareRootIndex);
        Span<int> coordinates = stackalloc int[GourdonHardLeafBatchSize];
        Span<uint> offsets = stackalloc uint[GourdonHardLeafBatchSize];
        var vectorWidth = (Vector512.IsHardwareAccelerated ? 32 : (Vector256.IsHardwareAccelerated ? 16 : 8));
        var sum = Int128.Zero;

        if (profile is not null) {
            profile.LeafFrontier = (limit - 1UL);
            profile.CachedQuotientDivisions += ((uint)(activeCount - CombinatorialTinyPrimeCount));
        }

        // Before crossing off p_b, phi[b] is phi(low-1,b-1), initially zero.
        // The segment bitmap contains exactly the survivors of primes 1..b-1.
        // Descending m makes floor(x/(p*m)) nondecreasing, as CountPrefix requires.
        for (var low = 0UL; (low < limit); low += GourdonHardLeafSegmentWidth) {
            cancellationToken.ThrowIfCancellationRequested();

            var high = Math.Min(val1: (low + GourdonHardLeafSegmentWidth), val2: limit);
            var lowDivisor = Math.Max(val1: low, val2: 1UL);

            sieve.Initialize(low: low, width: ((uint)(high - low)));
            if (profile is not null) {
                ++profile.LeafSegments;
                profile.LeafBitmapBytes += ((((high - low) + 239UL) / 240UL) * 8UL);
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
                var count = 0;

                if (profile is not null) { profile.SpecialFactorCoordinates += ((uint)(first - last)); }

                // With z=y the maximum-prime-factor condition is automatic. A
                // masked factor >p selects exactly square-free m with lpf(m)>p.
                // Separate filtering, division and phi queries into short batches.
                if (Vector128.IsHardwareAccelerated) {
                    for (; (coordinate >= (last + vectorWidth)); coordinate -= vectorWidth) {
                        if ((coordinate & 16383) < vectorWidth) { cancellationToken.ThrowIfCancellationRequested(); }

                        var start = ((coordinate - vectorWidth) + 1);
                        var mask = GourdonFactorSurvivorMask(source: ref factors[start], prime: prime);

                        // Loads ascend in memory; consume the highest set lane first
                        // to preserve descending m and increasing prefix queries.
                        while (mask != 0) {
                            var lane = BitOperations.Log2(value: mask);

                            coordinates[count++] = (start + lane);
                            mask ^= (1U << lane);
                        }
                        if (count > (GourdonHardLeafBatchSize - vectorWidth)) {
                            sum += CountGourdonHardBatch(coordinates: coordinates[..count], offsets: offsets,
                                factors: factors, quotient: quotient, low: low, previous: phi[index], sieve: sieve);
                            count = 0;
                        }
                    }
                }
                for (; (coordinate >= (last + 4)); coordinate -= 4) {
                    if ((coordinate & 16383) <= 3) { cancellationToken.ThrowIfCancellationRequested(); }

                    coordinates[count] = coordinate;
                    count += (((factors[coordinate] & CombinatorialFactorMask) > prime) ? 1 : 0);
                    coordinates[count] = (coordinate - 1);
                    count += (((factors[(coordinate - 1)] & CombinatorialFactorMask) > prime) ? 1 : 0);
                    coordinates[count] = (coordinate - 2);
                    count += (((factors[(coordinate - 2)] & CombinatorialFactorMask) > prime) ? 1 : 0);
                    coordinates[count] = (coordinate - 3);
                    count += (((factors[(coordinate - 3)] & CombinatorialFactorMask) > prime) ? 1 : 0);

                    if (count > (GourdonHardLeafBatchSize - 4)) {
                        sum += CountGourdonHardBatch(coordinates: coordinates[..count], offsets: offsets,
                            factors: factors, quotient: quotient, low: low, previous: phi[index], sieve: sieve);
                        count = 0;
                    }
                }
                for (; (coordinate > last); --coordinate) {
                    coordinates[count] = coordinate;
                    count += (((factors[coordinate] & CombinatorialFactorMask) > prime) ? 1 : 0);
                }
                sum += CountGourdonHardBatch(coordinates: coordinates[..count], offsets: offsets,
                    factors: factors, quotient: quotient, low: low, previous: phi[index], sieve: sieve);
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

                var last = ((int)tables.CountPrimes(value: ((uint)maximum)));

                for (; (primes[last] > minimum); --last) {
                    if ((last & 8191) == 0) { cancellationToken.ThrowIfCancellationRequested(); }

                    var leaf = (quotient / primes[last]);

                    sum += (phi[index] + sieve.CountPrefix(offset: ((uint)(leaf - low))));
                }
                phi[index] += sieve.TotalCount;
                sieve.CrossOff(index: index, prime: prime);
            }
        }
        return sum;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint GourdonFactorSurvivorMask(ref ushort source, uint prime) {
        // The sign bit stores mu, not the factor. After masking, signed compares
        // are exact: factors <=32767 and this loop's p<=sqrt(y)<32767.
        if (Vector512.IsHardwareAccelerated) {
            var factors = (Vector512.LoadUnsafe(source: ref source) & Vector512.Create(value: CombinatorialFactorMask)).AsInt16();

            return ((uint)Vector512.GreaterThan(left: factors, right: Vector512.Create(value: ((short)prime))).ExtractMostSignificantBits());
        }
        if (Vector256.IsHardwareAccelerated) {
            var factors = (Vector256.LoadUnsafe(source: ref source) & Vector256.Create(value: CombinatorialFactorMask)).AsInt16();

            return Vector256.GreaterThan(left: factors, right: Vector256.Create(value: ((short)prime))).ExtractMostSignificantBits();
        }
        var narrowFactors = (Vector128.LoadUnsafe(source: ref source) & Vector128.Create(value: CombinatorialFactorMask)).AsInt16();

        return Vector128.GreaterThan(left: narrowFactors, right: Vector128.Create(value: ((short)prime))).ExtractMostSignificantBits();
    }
    private static Int128 CountGourdonHardBatch(ReadOnlySpan<int> coordinates, Span<uint> offsets,
        ushort[] factors, ulong quotient, ulong low, ulong previous, CombinatorialLeafSieve sieve) {
        for (var index = 0; (index < coordinates.Length); ++index) {
            var number = CombinatorialNumber(coordinate: coordinates[index]);

            offsets[index] = ((uint)((quotient / number) - low));
        }

        // Each count is <=x/y<2^43 since y>cbrt(x). At most 128 leaves
        // contribute less than 2^50 in magnitude; only the outer sum needs Int128.
        var sum = 0L;

        for (var index = 0; (index < coordinates.Length); ++index) {
            var count = (previous + sieve.CountPrefix(offset: offsets[index]));

            sum += (((factors[coordinates[index]] & CombinatorialNegativeMoebius) == 0)
                ? -((long)count) : ((long)count));
        }
        return sum;
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
