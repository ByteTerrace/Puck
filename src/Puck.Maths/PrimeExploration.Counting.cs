using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
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

        // The Gourdon caller only counts above 2^44 and chooses cutoff<floor(sqrt(value)).
        Debug.Assert(((cutoff < root) && (value > uint.MaxValue)));
        using var primes = new DescendingPrimeStream(cancellationToken: cancellationToken, high: root, lowExclusive: cutoff, profile: profile);
        var counter = new SemiprimeCounter(primes: primes, root: root, value: value);

        if (!counter.HasQuery) { return counter.Sum; }
        var smallest = (cutoff + 1);

        // A prime in this interval was already found by the reverse stream, so this search cannot
        // cross root or wrap uint. It only establishes the final sieve endpoint, not a prime table.
        while (!smallest.IsPrime()) {
            cancellationToken.ThrowIfCancellationRequested();
            ++smallest;
        }
        if (profile is not null) { profile.SemiprimeForwardFrontier = (value / smallest); }
        _ = Explore(cancellationToken: cancellationToken, high: (value / smallest),
            low: (((ulong)root) + 1), mode: PrimeSieveMode.Eratosthenes, onPrime: null, onSegment: counter.Consume,
            segmentBytes: CacheSegmentBytes, work: profile?.SemiprimeForwardBitmap);
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

        _ = Explore(cancellationToken: cancellationToken, high: high,
            low: Math.Max(val1: low, val2: 7UL), mode: PrimeSieveMode.Automatic, onPrime: null, onSegment: selector.Consume,
            segmentBytes: CacheSegmentBytes, work: profile?.SelectionBitmap);
        return (selector.Selected, (count + selector.Count));
    }

    private static (ulong Selected, ulong Count) SelectPrimeDescending(ulong low, ulong high, ulong ordinal, CancellationToken cancellationToken, PrimeRequestWorkBuilder? profile) {
        var count = 0UL;
        var minimum = Math.Max(val1: low, val2: 7UL);

        if (high >= minimum) {
            var maximumBytes = ((int)Math.Min(val1: (((high / PrimeWheel30.Modulus) - (minimum / PrimeWheel30.Modulus)) + 1), val2: ((SelectionSpanIntegers / PrimeWheel30.Modulus) + 2)));
            using var window = new PrimeBitmapWindow(capacity: maximumBytes);
            var nextHigh = high;

            while (nextHigh >= minimum) {
                cancellationToken.ThrowIfCancellationRequested();
                var windowLow = (nextHigh - Math.Min(val1: (nextHigh - minimum), val2: (SelectionSpanIntegers - 1)));
                var blockLow = (windowLow / PrimeWheel30.Modulus);

                window.Reset(blockLow: blockLow, countPrimes: true, length: ((int)(((nextHigh / PrimeWheel30.Modulus) - blockLow) + 1)));
                _ = Explore(cancellationToken: cancellationToken, high: nextHigh,
                    low: windowLow, mode: PrimeSieveMode.Automatic, onPrime: null, onSegment: window.Consume,
                    segmentBytes: CacheSegmentBytes, work: profile?.SelectionBitmap);
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
                var origin = ((blockLow + ((uint)index)) * PrimeWheel30.Modulus);

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
    // Keep wheel bits rather than a prime array. Forward marking uses the carried kernels;
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
                    var origin = ((blockLow + ((uint)index)) * PrimeWheel30.Modulus);

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

                    if (--ordinal == 0) { return (((m_blockLow + ((uint)index)) * PrimeWheel30.Modulus) + PrimeWheel30.NumericResidues[bit]); }
                    bits &= ~(1U << bit);
                }
            }
            return 0;
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(array: m_bitmap);
    }
    // Streams the P2 queries x/p for descending primes p in (cutoff, sqrt(x)], so the quotients ascend, and answers
    // each from the forward bitmap above sqrt(x). Quotients are divided in batches by the exact Gourdon leaf-quotient
    // rungs: p>cutoff>=cbrt(x) keeps every quotient below x^(2/3)<2^43, inside their 2^48 domain.
    private sealed class SemiprimeCounter {
        private readonly DescendingPrimeStream m_primes;
        private readonly ulong m_value;

        private readonly uint[] m_divisors = new uint[PrimeExtensions.GourdonQuotientBatchSize];
        private readonly ulong[] m_quotients = new ulong[(PrimeExtensions.GourdonQuotientBatchSize + 1)];
        // Holds a final segment whose length is not a whole number of words, zero-padded.
        private ulong[] m_padded = [];

        private int m_next;
        private int m_count;
        private ulong m_ordinal;
        private ulong m_aboveRoot;

        internal bool HasQuery => (m_next < m_count);
        internal ulong Sum { get; private set; }

        internal SemiprimeCounter(ulong value, uint root, DescendingPrimeStream primes) {
            m_value = value;
            m_primes = primes;
            Refill();
            // When floor(x/p)=sqrt(x), the first term precedes the ascending bitmap's lower bound.
            // Its prefix above sqrt(x) is empty, but its diagonal contribution is still one.
            while (HasQuery && (m_quotients[m_next] <= root)) {
                Sum += ++m_ordinal;
                if (++m_next == m_count) { Refill(); }
            }
        }

        // Answers every query inside this segment. Counts above sqrt(x) accumulate whole words, and each query
        // reads one word under a mask of its earlier bytes and the residues through its own. The batch's
        // ordinals ordinal+1..ordinal+n add n*ordinal+n(n+1)/2 after the loop, which keeps them out of it.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal bool Consume(ReadOnlySpan<byte> segment, ulong blockLow, ulong segmentHigh, bool testSurvivors) {
            var words = Words(segment: segment);
            ref var word = ref MemoryMarshal.GetReference(span: words);
            var origin = (blockLow * PrimeWheel30.Modulus);
            var sum = Sum;
            var aboveRoot = m_aboveRoot;
            var counted = 0;

            while (true) {
                if (m_next == m_count) {
                    Refill();
                    if (m_next == m_count) { break; }
                }
                var first = m_next;
                var next = first;
                ref var quotients = ref MemoryMarshal.GetArrayDataReference(array: m_quotients);
                var batch = 0UL;
                var quotient = Unsafe.Add(elementOffset: next, source: ref quotients);

                // Refill ends each batch with a ulong.MaxValue sentinel above every segment.
                while (quotient <= segmentHigh) {
                    // The offset is below one segment, so the byte index divides by thirty with a multiply.
                    var offset = ((uint)(quotient - origin));
                    var byteIndex = (offset / PrimeWheel30.Modulus);
                    var residue = (offset - (byteIndex * PrimeWheel30.Modulus));
                    var target = ((int)(byteIndex / sizeof(ulong)));
                    var shift = ((int)((byteIndex % sizeof(ulong)) * PrimeWheel30.ChannelCount));

                    while (counted < target) { aboveRoot += ((ulong)BitOperations.PopCount(value: Unsafe.Add(elementOffset: counted++, source: ref word))); }
                    var mask = ((1UL << shift) - 1UL)
                        | (((ulong)Unsafe.Add(elementOffset: residue, source: ref MemoryMarshal.GetReference(span: PrimeWheel30.PrefixMasks))) << shift);

                    batch += (aboveRoot + ((ulong)BitOperations.PopCount(value: Unsafe.Add(elementOffset: target, source: ref word) & mask)));
                    quotient = Unsafe.Add(elementOffset: ++next, source: ref quotients);
                }
                var answered = ((ulong)(next - first));

                sum += ((batch + (answered * m_ordinal)) + ((answered * (answered + 1UL)) / 2UL));
                m_ordinal += answered;
                m_next = next;
                if (next < m_count) {
                    for (; (counted < words.Length); ++counted) { aboveRoot += ((ulong)BitOperations.PopCount(value: Unsafe.Add(elementOffset: counted, source: ref word))); }
                    Sum = sum;
                    m_aboveRoot = aboveRoot;
                    return true;
                }
            }
            Sum = sum;
            m_aboveRoot = aboveRoot;
            return false;
        }

        private ReadOnlySpan<ulong> Words(ReadOnlySpan<byte> segment) {
            if ((segment.Length % sizeof(ulong)) == 0) { return MemoryMarshal.Cast<byte, ulong>(span: segment); }
            var length = ((segment.Length + (sizeof(ulong) - 1)) / sizeof(ulong));

            if (m_padded.Length < length) { m_padded = new ulong[length]; }
            var words = m_padded.AsSpan(length: length, start: 0);

            words[^1] = 0;
            segment.CopyTo(destination: MemoryMarshal.AsBytes(span: words));
            return words;
        }
        private void Refill() {
            var count = 0;

            while (count < m_divisors.Length) {
                var prime = m_primes.Next();

                if (prime == 0) { break; }
                m_divisors[count++] = prime;
            }
            PrimeExtensions.DivideGourdonQuotients(dividend: m_value, divisors: m_divisors.AsSpan(length: count, start: 0), quotients: m_quotients);
            m_quotients[count] = ulong.MaxValue;
            m_next = 0;
            m_count = count;
        }
    }
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

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal uint Next() {
            while (true) {
                if (m_bits != 0) {
                    var bit = (63 - BitOperations.LeadingZeroCount(value: m_bits));

                    m_bits &= ~(1UL << bit);
                    return ((uint)(((m_blockLow + ((uint)((m_word * sizeof(ulong)) + (bit >> 3)))) * PrimeWheel30.Modulus) + PrimeWheel30.NumericResidues[bit & (PrimeWheel30.ChannelCount - 1)]));
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
            var lastBlock = (m_nextHigh / PrimeWheel30.Modulus);
            var firstBlock = Math.Max(val1: (minimum / PrimeWheel30.Modulus), val2: ((lastBlock >= BitmapBytes) ? ((lastBlock - BitmapBytes) + 1) : 0));
            var firstInteger = (firstBlock * PrimeWheel30.Modulus);
            var length = ((int)((lastBlock - firstBlock) + 1));

            if (m_profile is not null) { ++m_profile.SemiprimeReverseWindows; }
            m_window.Reset(blockLow: firstBlock, countPrimes: false, length: length);
            _ = Explore(cancellationToken: m_cancellationToken, high: m_nextHigh,
                low: Math.Max(val1: minimum, val2: firstInteger), mode: PrimeSieveMode.Eratosthenes, onPrime: null,
                onSegment: m_consumer, segmentBytes: CacheSegmentBytes,
                work: m_profile?.SemiprimeReverseBitmap);
            m_word = ((length + (sizeof(ulong) - 1)) / sizeof(ulong));
            m_blockLow = firstBlock;
            m_nextHigh = ((firstInteger == 0) ? 0 : (firstInteger - 1));
            return true;
        }

        public void Dispose() => m_window.Dispose();
    }
}
