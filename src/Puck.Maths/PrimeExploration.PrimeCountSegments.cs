using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExploration {
    internal delegate void PrimeCountSegmentVisitor(PrimeCountSegment segment);

    // Traverse [0, highExclusive) once, carrying the marking states and global pi offsets. The consumer reads
    // the current segment synchronously; no prime list is made, and each bitmap is copied once into padded words.
    internal static void VisitPrimeCountSegments(uint highExclusive, int segmentBytes,
        PrimeCountSegmentVisitor visitor, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfLessThan(highExclusive, 8U);
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentBytes, 8);
        Debug.Assert(((segmentBytes % sizeof(ulong)) == 0));
        var counter = new PrimeCountSegmentBuilder(segmentBytes: segmentBytes, visitor: visitor);

        _ = Explore(cancellationToken: cancellationToken, high: (highExclusive - 1U),
            low: 0, mode: PrimeSieveMode.Eratosthenes, onPrime: null, onSegment: counter.Consume,
            segmentBytes: segmentBytes);
    }

    // One segment's prime counts. Words hold the U(30) bitmap with any partial final word zero-padded, and each
    // prefix counts every prime below its word, including 2, 3 and 5.
    internal readonly ref struct PrimeCountSegment {
        private readonly ref readonly ulong m_words;
        private readonly ref readonly uint m_prefix;
        private readonly ref readonly ulong m_masks;
        private readonly int m_wordCount;

        internal uint Low { get; }
        internal uint High { get; }
        internal int BitmapBytes { get; }

        internal PrimeCountSegment(ReadOnlySpan<ulong> words, ReadOnlySpan<uint> prefix, ReadOnlySpan<ulong> masks,
            uint low, uint high, int bitmapBytes) {
            Debug.Assert((prefix.Length >= words.Length));
            m_words = ref MemoryMarshal.GetReference(span: words);
            m_prefix = ref MemoryMarshal.GetReference(span: prefix);
            m_masks = ref MemoryMarshal.GetReference(span: masks);
            m_wordCount = words.Length;
            Low = low;
            High = high;
            BitmapBytes = bitmapBytes;
        }

        // Returns pi(value) for Low<=value<High and value>=7. Below 7 the omitted primes 2, 3 and 5 would need
        // their own prefix; every Gourdon easy leaf exceeds x^(1/4)>7.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint Count(ulong value) {
            Debug.Assert(((value >= Low) && (value < High) && (value >= 7)));
            var offset = (((uint)value) - Low);
            var index = (offset / PrimeWheel30.WordIntegers);
            var residue = (offset - (index * PrimeWheel30.WordIntegers));

            Debug.Assert((index < ((uint)m_wordCount)));
            return (Unsafe.Add(source: ref Unsafe.AsRef(source: in m_prefix), elementOffset: index)
                + ((uint)BitOperations.PopCount(value: Unsafe.Add(source: ref Unsafe.AsRef(source: in m_words), elementOffset: index)
                    & Unsafe.Add(source: ref Unsafe.AsRef(source: in m_masks), elementOffset: residue))));
        }
    }

    private sealed class PrimeCountSegmentBuilder(int segmentBytes, PrimeCountSegmentVisitor visitor) {
        private readonly ulong[] m_words = new ulong[((segmentBytes / sizeof(ulong)) + 1)];
        private readonly uint[] m_prefix = new uint[((segmentBytes / sizeof(ulong)) + 1)];
        private uint m_count = 3;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal bool Consume(ReadOnlySpan<byte> bitmap, ulong blockLow, ulong segmentHigh, bool testSurvivors) {
            Debug.Assert(!testSurvivors);
            var wordCount = ((bitmap.Length + (sizeof(ulong) - 1)) / sizeof(ulong));
            var words = m_words.AsSpan(length: wordCount, start: 0);

            // Copying pads a partial final word with zero bytes, so every query reads one word layout.
            words[^1] = 0;
            bitmap.CopyTo(destination: MemoryMarshal.AsBytes(span: words));
            for (var index = 0; (index < words.Length); ++index) {
                m_prefix[index] = m_count;
                m_count += ((uint)BitOperations.PopCount(value: words[index]));
            }
            visitor(new(bitmapBytes: bitmap.Length, high: ((uint)(segmentHigh + 1UL)), low: ((uint)(blockLow * PrimeWheel30.Modulus)),
                masks: PrimeExtensions.CombinatorialTables.PrefixMasks, prefix: m_prefix, words: words));
            return true;
        }
    }
}
