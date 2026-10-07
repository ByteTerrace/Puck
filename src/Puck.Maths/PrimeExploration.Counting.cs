using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExploration {
    // P2(x,a)=sum_{i=a+1..b}(pi(x/p_i)-i+1), b=pi(floor(sqrt(x))). In descending
    // prime order i=b-j, so b cancels and each term is pi(x/p_i)-pi(sqrt(x))+j+1.
    // The ascending side therefore needs only one bitmap traversal above sqrt(x); neither side
    // stores all primes through sqrt(x), and the +1 retains the square p_i*p_i on the diagonal.
    // Formula and streamed quotient ordering: primecount src/P2.cpp, BSD-2-Clause; see the notices.
    // The Gourdon caller supplies cutoff >= floor(cuberoot(value)), making the upper scan O(value^(2/3)).
    internal static ulong CountSemiprimes(ulong value, uint cutoff, CancellationToken cancellationToken, PrimeCountWorkBuilder? profile = null) {
        cancellationToken.ThrowIfCancellationRequested();
        var root = ((uint)value.SquareRoot());

        if (cutoff >= root) { return 0; }
        if (value < 49) {
            var smallCount = 0UL;

            for (var prime = (cutoff + 1); (prime <= root); ++prime) {
                if (prime.IsPrime()) {
                    smallCount += Count(cancellationToken: cancellationToken, high: (value / prime), low: prime);
                }
            }
            return smallCount;
        }
        using var primes = new DescendingPrimeStream(cancellationToken: cancellationToken, high: root, lowExclusive: cutoff, profile: profile);
        var first = primes.Next();

        if (first == 0) { return 0; }
        var counter = new SemiprimeCounter(first: first, primes: primes, root: root, value: value);

        if (!counter.HasQuery) { return counter.Sum; }
        var smallest = (cutoff + 1);

        // A prime in this interval was already found by the reverse stream, so this search cannot
        // cross root or wrap uint. It only establishes the final sieve endpoint, not a prime table.
        while (!smallest.IsPrime()) {
            cancellationToken.ThrowIfCancellationRequested();
            ++smallest;
        }
        if (profile is not null) { profile.SemiprimeForwardFrontier = (value / smallest); }
        _ = Explore(cancellationToken: cancellationToken, high: (value / smallest), layout: PrimeByteLayout.Numeric,
            low: (((ulong)root) + 1), mode: PrimeSieveMode.Eratosthenes, onPrime: null, onSegment: counter.Consume,
            segmentBytes: 32768, strategy: PrimeSieveStrategy.BucketPackets, usePreSieve: true, work: profile?.SemiprimeForwardBitmap);
        return counter.Sum;
    }
    // One-based selection from an inclusive interval. Count is the ordinal consumed in traversal order,
    // or the exact interval count when Selected is zero. Neither direction repeats survivor decisions.
    internal static ulong SelectPrime(ulong low, ulong high, ulong ordinal, CancellationToken cancellationToken, PrimeRequestWorkBuilder? profile = null) =>
        SelectPrimeAndCount(cancellationToken: cancellationToken, forward: true, high: high, low: low, ordinal: ordinal, profile: profile).Selected;
    internal static (ulong Selected, ulong Count) SelectPrimeAndCount(ulong low, ulong high, ulong ordinal, bool forward, CancellationToken cancellationToken, PrimeRequestWorkBuilder? profile = null) {
        cancellationToken.ThrowIfCancellationRequested();
        if ((ordinal == 0) || (high < low)) { return (0, 0); }
        if (profile is not null) { ++profile.SelectionWindows; }
        if (!forward) { return SelectPrimeDescending(cancellationToken: cancellationToken, high: high, low: low, ordinal: ordinal, profile: profile); }
        ReadOnlySpan<uint> exceptional = [2, 3, 5];
        var count = 0UL;

        foreach (var prime in exceptional) {
            if ((prime >= low) && (prime <= high)) {
                ++count;
                if (--ordinal == 0) { return (prime, count); }
            }
        }
        if (high < 7) { return (0, count); }
        var selector = new PrimeSelector(ordinal: ordinal);

        _ = Explore(cancellationToken: cancellationToken, high: high, layout: PrimeByteLayout.Numeric,
            low: Math.Max(val1: low, val2: 7UL), mode: PrimeSieveMode.Automatic, onPrime: null, onSegment: selector.Consume,
            segmentBytes: 32768, strategy: PrimeSieveStrategy.BucketPackets, usePreSieve: true, work: profile?.SelectionBitmap);
        return (selector.Selected, (count + selector.Count));
    }

    private static (ulong Selected, ulong Count) SelectPrimeDescending(ulong low, ulong high, ulong ordinal, CancellationToken cancellationToken, PrimeRequestWorkBuilder? profile) {
        const ulong MaximumWindowIntegers = 8_388_608;
        var count = 0UL;
        var minimum = Math.Max(val1: low, val2: 7UL);

        if (high >= minimum) {
            var maximumBytes = ((int)Math.Min(val1: (((high / 30) - (minimum / 30)) + 1), val2: ((MaximumWindowIntegers / 30) + 2)));
            using var window = new PrimeBitmapWindow(capacity: maximumBytes);
            var nextHigh = high;

            while (nextHigh >= minimum) {
                cancellationToken.ThrowIfCancellationRequested();
                var windowLow = (nextHigh - Math.Min(val1: (nextHigh - minimum), val2: (MaximumWindowIntegers - 1)));
                var blockLow = (windowLow / 30);

                window.Reset(blockLow: blockLow, countPrimes: true, length: ((int)(((nextHigh / 30) - blockLow) + 1)));
                _ = Explore(cancellationToken: cancellationToken, high: nextHigh, layout: PrimeByteLayout.Numeric,
                    low: windowLow, mode: PrimeSieveMode.Automatic, onPrime: null, onSegment: window.Consume,
                    segmentBytes: 32768, strategy: PrimeSieveStrategy.BucketPackets, usePreSieve: true, work: profile?.SelectionBitmap);
                if (window.Count >= ordinal) {
                    var selected = window.SelectDescending(ordinal: ordinal);

                    cancellationToken.ThrowIfCancellationRequested();
                    return (selected, (count + ordinal));
                }
                ordinal -= window.Count;
                count += window.Count;
                nextHigh = (windowLow - 1);
            }
        }
        ReadOnlySpan<uint> exceptional = [5, 3, 2];

        foreach (var prime in exceptional) {
            if ((prime >= low) && (prime <= high)) {
                ++count;
                if (--ordinal == 0) { return (prime, count); }
            }
        }
        return (0, count);
    }

    private sealed class PrimeSelector(ulong ordinal) {
        private readonly ulong m_ordinal = ordinal;
        private ulong m_remaining = ordinal;

        internal ulong Count => (m_ordinal - m_remaining);
        internal ulong Selected { get; private set; }

        internal bool Consume(ReadOnlySpan<byte> segment, ulong blockLow, ulong segmentHigh, bool testSurvivors) {
            if (!testSurvivors) {
                var count = CountBits(segment: segment);

                if (count < m_remaining) {
                    m_remaining -= count;
                    return true;
                }
            }
            for (var index = 0; (index < segment.Length); ++index) {
                var bits = ((uint)segment[index]);
                var origin = ((blockLow + ((uint)index)) * 30);

                while (bits != 0) {
                    var bit = BitOperations.TrailingZeroCount(value: bits);
                    var value = (origin + PrimeWheel30.NumericResidues[bit]);

                    bits &= (bits - 1);
                    // Endpoint masks clear the unrepresentable residues in the final ulong block.
                    if (testSurvivors && (value >= ProvenPresieveLimit) && !PrimeKernels.IsPrimeCandidateWord(value: value)) { continue; }
                    if (--m_remaining == 0) {
                        Selected = value;
                        return false;
                    }
                }
            }
            return true;
        }
    }
    // Keep wheel bits rather than a prime array. Forward marking retains the existing carried kernels;
    // reverse readers then visit bytes in descending order. Failed survivor decisions clear their bits once.
    private sealed class PrimeBitmapWindow(int capacity) : IDisposable {
        private readonly byte[] m_bitmap = ArrayPool<byte>.Shared.Rent(minimumLength: (capacity + 7) & ~7);
        private readonly int m_capacity = capacity;

        private ulong m_blockLow;
        private int m_length;
        private bool m_countPrimes;

        internal ulong Count { get; private set; }
        internal ReadOnlySpan<byte> PaddedBitmap => m_bitmap.AsSpan(length: (m_length + 7) & ~7, start: 0);

        internal void Reset(ulong blockLow, int length, bool countPrimes) {
            ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, m_capacity);
            m_blockLow = blockLow;
            m_length = length;
            m_countPrimes = countPrimes;
            Count = 0;
            m_bitmap.AsSpan(length: (((length + 7) & ~7) - length), start: length).Clear();
        }
        internal bool Consume(ReadOnlySpan<byte> segment, ulong blockLow, ulong segmentHigh, bool testSurvivors) {
            var destination = m_bitmap.AsSpan(length: m_length, start: 0).Slice(start: ((int)(blockLow - m_blockLow)), length: segment.Length);

            segment.CopyTo(destination: destination);
            if (testSurvivors) {
                for (var index = 0; (index < destination.Length); ++index) {
                    var bits = ((uint)destination[index]);
                    var origin = ((blockLow + ((uint)index)) * 30);

                    while (bits != 0) {
                        var bit = BitOperations.TrailingZeroCount(value: bits);
                        var value = (origin + PrimeWheel30.NumericResidues[bit]);

                        bits &= (bits - 1);
                        if ((value >= ProvenPresieveLimit) && !PrimeKernels.IsPrimeCandidateWord(value: value)) {
                            destination[index] &= unchecked((byte)~(1U << bit));
                        }
                    }
                }
            }
            if (m_countPrimes) { Count += CountBits(segment: destination); }
            return true;
        }
        internal ulong SelectDescending(ulong ordinal) {
            for (var index = (m_length - 1); (index >= 0); --index) {
                var bits = ((uint)m_bitmap[index]);
                var count = ((uint)BitOperations.PopCount(value: bits));

                if (ordinal > count) { ordinal -= count; continue; }
                while (bits != 0) {
                    var bit = (31 - BitOperations.LeadingZeroCount(value: bits));

                    if (--ordinal == 0) { return (((m_blockLow + ((uint)index)) * 30) + PrimeWheel30.NumericResidues[bit]); }
                    bits &= ~(1U << bit);
                }
            }
            return 0;
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(array: m_bitmap);
    }
    private sealed class SemiprimeCounter {
        private readonly DescendingPrimeStream m_primes;
        private readonly ulong m_value;

        private ulong m_quotient;
        private ulong m_ordinal;
        private ulong m_aboveRoot;

        internal bool HasQuery => (m_quotient != 0);
        internal ulong Sum { get; private set; }

        internal SemiprimeCounter(ulong value, uint root, uint first, DescendingPrimeStream primes) {
            m_value = value;
            m_primes = primes;
            m_quotient = (value / first);
            // When floor(x/p)=sqrt(x), the first term precedes the ascending bitmap's lower bound.
            // Its prefix above sqrt(x) is empty, but its diagonal contribution is still one.
            while (HasQuery && (m_quotient <= root)) {
                Sum += ++m_ordinal;
                Advance();
            }
        }

        internal bool Consume(ReadOnlySpan<byte> segment, ulong blockLow, ulong segmentHigh, bool testSurvivors) {
            var consumed = 0;

            while (m_quotient <= segmentHigh) {
                var (block, residue) = Math.DivRem(left: m_quotient, right: 30UL);
                var index = ((int)(block - blockLow));

                // Complete bytes are counted once, even when several consecutive quotient queries
                // share a byte. Its partial prefix is read independently for each inclusive query.
                if (index != consumed) {
                    m_aboveRoot += CountBits(segment: segment.Slice(length: (index - consumed), start: consumed));
                }
                consumed = index;
                var mask = NumericPrefixMasks[((int)residue)];
                var prefix = ((ulong)BitOperations.PopCount(value: ((uint)segment[index]) & mask));

                Sum += ((m_aboveRoot + prefix) + (++m_ordinal));
                Advance();
                if (!HasQuery) { return false; }
            }
            m_aboveRoot += CountBits(segment: segment[consumed..]);
            return true;
        }

        private void Advance() {
            var prime = m_primes.Next();

            m_quotient = ((prime == 0) ? 0 : (m_value / prime));
        }
    }

    // Bit k is set exactly when numeric residue k is no greater than the indexed remainder.
    private static ReadOnlySpan<byte> NumericPrefixMasks => [
        0, 1, 1, 1, 1, 1, 1, 3, 3, 3, 3, 7, 7, 15, 15,
        15, 15, 31, 31, 63, 63, 63, 63, 127, 127, 127, 127, 127, 127, 255,
    ];

    // A 1-MiB reverse bitmap covers 31,457,280 integers. One Explore setup marks thirty-two 32-KiB
    // physical segments with carried state, then the stream reads their concatenated bits backwards.
    // Regeneration needs only the shared uint base-prime table; no sqrt(value)-sized prime array is retained.
    private sealed class DescendingPrimeStream : IDisposable {
        private const int BitmapBytes = 1_048_576;

        private readonly PrimeBitmapWindow m_window;
        private readonly SegmentConsumer m_consumer;
        private readonly CancellationToken m_cancellationToken;
        private readonly uint m_lowExclusive;
        private readonly uint m_high;
        private readonly PrimeCountWorkBuilder? m_profile;

        private ulong m_nextHigh;
        private ulong m_blockLow;
        private ulong m_bits;
        private int m_word;
        private int m_exceptional;

        internal DescendingPrimeStream(uint lowExclusive, uint high, CancellationToken cancellationToken, PrimeCountWorkBuilder? profile) {
            m_lowExclusive = lowExclusive;
            m_high = high;
            m_nextHigh = high;
            m_cancellationToken = cancellationToken;
            m_profile = profile;
            m_window = new PrimeBitmapWindow(capacity: BitmapBytes);
            m_consumer = m_window.Consume;
        }

        internal uint Next() {
            while (true) {
                if (m_bits != 0) {
                    var bit = (63 - BitOperations.LeadingZeroCount(value: m_bits));

                    m_bits &= ~(1UL << bit);
                    return ((uint)(((m_blockLow + ((uint)((m_word * 8) + (bit >> 3)))) * 30) + PrimeWheel30.NumericResidues[bit & 7]));
                }
                if (m_word != 0) {
                    m_bits = MemoryMarshal.Cast<byte, ulong>(span: m_window.PaddedBitmap)[--m_word];
                    continue;
                }
                if (Fill()) { continue; }
                ReadOnlySpan<uint> exceptional = [5, 3, 2];

                while (m_exceptional < exceptional.Length) {
                    var prime = exceptional[m_exceptional++];

                    if ((prime > m_lowExclusive) && (prime <= m_high)) { return prime; }
                }
                return 0;
            }
        }

        private bool Fill() {
            m_cancellationToken.ThrowIfCancellationRequested();
            var minimum = Math.Max(val1: (((ulong)m_lowExclusive) + 1), val2: 7UL);

            if (m_nextHigh < minimum) { return false; }
            var lastBlock = (m_nextHigh / 30);
            var firstBlock = Math.Max(val1: (minimum / 30), val2: ((lastBlock >= BitmapBytes) ? ((lastBlock - BitmapBytes) + 1) : 0));
            var firstInteger = (firstBlock * 30);
            var length = ((int)((lastBlock - firstBlock) + 1));

            if (m_profile is not null) { ++m_profile.SemiprimeReverseWindows; }
            m_window.Reset(blockLow: firstBlock, countPrimes: false, length: length);
            _ = Explore(cancellationToken: m_cancellationToken, high: m_nextHigh, layout: PrimeByteLayout.Numeric,
                low: Math.Max(val1: minimum, val2: firstInteger), mode: PrimeSieveMode.Eratosthenes, onPrime: null,
                onSegment: m_consumer, segmentBytes: 32768, strategy: PrimeSieveStrategy.BucketPackets, usePreSieve: true,
                work: m_profile?.SemiprimeReverseBitmap);
            m_word = ((length + 7) / 8);
            m_blockLow = firstBlock;
            m_nextHigh = ((firstInteger == 0) ? 0 : (firstInteger - 1));
            return true;
        }

        public void Dispose() => m_window.Dispose();
    }
}
