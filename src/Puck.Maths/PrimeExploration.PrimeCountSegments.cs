using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExploration {
    internal delegate void PrimeCountSegmentVisitor(PrimeCountSegment segment);

    // Traverse [0, highExclusive) once, carrying the marking states and global pi offsets.
    // The consumer reads the current bitmap synchronously; no prime list or bitmap copy is made.
    internal static void VisitPrimeCountSegments(uint highExclusive, int segmentBytes, ulong[] prefixMasks,
        PrimeCountSegmentVisitor visitor, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfLessThan(highExclusive, 8U);
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentBytes, 8);
        Debug.Assert(((segmentBytes % 8) == 0));
        Debug.Assert((prefixMasks.Length == 240));
        var counter = new PrimeCountSegmentBuilder(prefixMasks: prefixMasks, segmentBytes: segmentBytes, visitor: visitor);

        _ = Explore(cancellationToken: cancellationToken, high: (highExclusive - 1U), layout: PrimeByteLayout.Numeric,
            low: 0, mode: PrimeSieveMode.Eratosthenes, onPrime: null, onSegment: counter.Consume,
            segmentBytes: segmentBytes, strategy: PrimeSieveStrategy.BucketPackets, usePreSieve: true);
    }

    internal readonly ref struct PrimeCountSegment {
        private readonly ReadOnlySpan<ulong> m_words;
        private readonly ReadOnlySpan<uint> m_prefix;
        private readonly ReadOnlySpan<ulong> m_masks;
        private readonly ulong m_tail;

        internal uint Low { get; }
        internal uint High { get; }
        internal int BitmapBytes { get; }

        internal PrimeCountSegment(ReadOnlySpan<ulong> words, ReadOnlySpan<uint> prefix, ReadOnlySpan<ulong> masks,
            ulong tail, uint low, uint high, int bitmapBytes) {
            m_words = words;
            m_prefix = prefix;
            m_masks = masks;
            m_tail = tail;
            Low = low;
            High = high;
            BitmapBytes = bitmapBytes;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint Count(ulong value) {
            Debug.Assert(((value >= Low) && (value < High)));
            // U(30) omits 2, 3 and 5. All later word prefixes include those three primes.
            if (value < 7) { return ((value < 2) ? 0U : ((value < 3) ? 1U : ((value < 5) ? 2U : 3U))); }
            var offset = (((uint)value) - Low);
            var index = ((int)(offset / 240U));
            var bits = ((index < m_words.Length) ? m_words[index] : m_tail);

            return (m_prefix[index] + ((uint)BitOperations.PopCount(value: bits & m_masks[((int)(offset % 240U))])));
        }
    }

    private sealed class PrimeCountSegmentBuilder(int segmentBytes, ulong[] prefixMasks, PrimeCountSegmentVisitor visitor) {
        private readonly uint[] m_prefix = new uint[((segmentBytes / 8) + 1)];
        private uint m_count = 3;

        internal bool Consume(ReadOnlySpan<byte> bitmap, ulong blockLow, ulong segmentHigh, bool testSurvivors) {
            Debug.Assert(!testSurvivors);
            var words = MemoryMarshal.Cast<byte, ulong>(span: bitmap);

            for (var index = 0; (index < words.Length); ++index) {
                m_prefix[index] = m_count;
                m_count += ((uint)BitOperations.PopCount(value: words[index]));
            }
            var tail = 0UL;
            var completeBytes = (words.Length * 8);

            m_prefix[words.Length] = m_count;
            for (var index = completeBytes; (index < bitmap.Length); ++index) {
                tail |= (((ulong)bitmap[index]) << ((index - completeBytes) * 8));
            }
            m_count += ((uint)BitOperations.PopCount(value: tail));
            visitor(new(words: words, prefix: m_prefix, masks: prefixMasks, tail: tail,
                low: ((uint)(blockLow * 30UL)), high: ((uint)(segmentHigh + 1UL)), bitmapBytes: bitmap.Length));
            return true;
        }
    }
}
