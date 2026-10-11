using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExploration {
    internal delegate void PrimeCountSegmentVisitor(PrimeCountSegment segment);

    // Traverse [0, highExclusive) once, carrying the marking states and global pi offsets. The consumer reads
    // the current segment synchronously; no prime list is made, and each bitmap is copied once into padded words.
    internal static void VisitPrimeCountSegments(uint highExclusive, int segmentBytes, PrimeCountSegmentVisitor visitor,
        CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfLessThan(highExclusive, 8U);
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentBytes, 8);
        Debug.Assert(((segmentBytes % sizeof(ulong)) == 0));
        var counter = new PrimeCountSegmentBuilder(segmentBytes: segmentBytes, visitor: visitor);

        _ = Explore(cancellationToken: cancellationToken, high: (highExclusive - 1U),
            low: 0, mode: PrimeSieveMode.Eratosthenes, onPrime: null, onSegment: counter.Consume,
            segmentBytes: segmentBytes);
    }

    // One segment's prime counts. Words hold the U(30) bitmap with any partial final word zero-padded, then one zero
    // word. Base counts every prime below the segment, including 2, 3 and 5, and each word's prefix counts the
    // segment's primes in it and the words before it; the zero word's prefix is the segment's count.
    internal readonly ref struct PrimeCountSegment {
        private readonly ref readonly ulong m_words;
        private readonly ref readonly uint m_prefix;
        private readonly int m_wordCount;

        internal uint Low { get; }
        internal uint High { get; }
        internal uint Base { get; }
        internal int BitmapBytes { get; }

        internal PrimeCountSegment(ReadOnlySpan<ulong> words, ReadOnlySpan<uint> prefix, uint low, uint high, uint @base, int bitmapBytes) {
            Debug.Assert(((words.Length >= 1) && (prefix.Length >= words.Length)));
            m_words = ref MemoryMarshal.GetReference(span: words);
            m_prefix = ref MemoryMarshal.GetReference(span: prefix);
            m_wordCount = words.Length;
            Low = low;
            High = high;
            Base = @base;
            BitmapBytes = bitmapBytes;
        }

        // Returns the sum of pi(Low+Q_i) for i<length, over the positions PrimeDivisionKernels.DivideGourdonSegmentPositions
        // writes for quotients Q_i: each names a word in its high half and a bit in its low six bits, and the word counts
        // its prefix less its primes at that bit and above, so a position just past a word's last bit reads the next word
        // whole. Each quotient must satisfy Low<=Q<High and Q>=7: below 7 the omitted primes 2, 3 and 5 would need their
        // own prefix, and every Gourdon easy leaf exceeds x^(1/4)>7. Both in-segment subtotals stay below 2^32 because a
        // batch has at most 2^32/(64*word count) terms.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong SumCounts(ref readonly ulong positions, nint length) {
            Debug.Assert(((length >= 0) && (((((ulong)length) * ((ulong)m_wordCount)) * 64UL) <= uint.MaxValue)));
            // A position's halves as 32-bit values in little-endian order: its bit field, then its word. Reading the word
            // as its own load costs no shift of the position.
            ref var half = ref Unsafe.As<ulong, uint>(source: ref Unsafe.AsRef(source: in positions));
            ref var prefix = ref Unsafe.AsRef(source: in m_prefix);
            ref var bits = ref Unsafe.AsRef(source: in m_words);
            var through = 0U;
            var above = 0U;
            var halves = (2 * length);
            nint index = 0;

            // Pairs halve the loop's compare, branch and index steps per term; the index counts halves, so each address
            // scales it directly. A shift count is taken modulo 64.
            for (; (index < (halves - 2)); index += 4) {
                var first = ((nuint)Unsafe.Add(elementOffset: (index + 1), source: ref half));
                var second = ((nuint)Unsafe.Add(elementOffset: (index + 3), source: ref half));

                Debug.Assert((Math.Max(val1: first, val2: second) < ((uint)m_wordCount)));
                through += (Unsafe.Add(elementOffset: first, source: ref prefix) + Unsafe.Add(elementOffset: second, source: ref prefix));
                above += ((uint)(BitOperations.PopCount(value: (Unsafe.Add(elementOffset: first, source: ref bits)
                    >> ((int)Unsafe.Add(elementOffset: index, source: ref half))))
                    + BitOperations.PopCount(value: (Unsafe.Add(elementOffset: second, source: ref bits)
                    >> ((int)Unsafe.Add(elementOffset: (index + 2), source: ref half))))));
            }
            if (index < halves) {
                var last = ((nuint)Unsafe.Add(elementOffset: (index + 1), source: ref half));

                Debug.Assert((last < ((uint)m_wordCount)));
                through += Unsafe.Add(elementOffset: last, source: ref prefix);
                above += ((uint)BitOperations.PopCount(value: (Unsafe.Add(elementOffset: last, source: ref bits)
                    >> ((int)Unsafe.Add(elementOffset: index, source: ref half)))));
            }
            return (((((ulong)Base) * ((ulong)length)) + through) - above);
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
            var words = m_words.AsSpan(length: (wordCount + 1), start: 0);

            // Copying pads a partial final word with zero bytes, and the word after it is zero, so every query reads
            // one word layout.
            words[(wordCount - 1)..].Clear();
            bitmap.CopyTo(destination: MemoryMarshal.AsBytes(span: words));
            var inside = 0U;

            for (var index = 0; (index < words.Length); ++index) {
                inside += ((uint)BitOperations.PopCount(value: words[index]));
                m_prefix[index] = inside;
            }
            visitor(new(@base: m_count, bitmapBytes: bitmap.Length, high: ((uint)(segmentHigh + 1UL)), low: ((uint)(blockLow * PrimeWheel30.Modulus)),
                prefix: m_prefix, words: words));
            m_count += inside;
            return true;
        }
    }
}
