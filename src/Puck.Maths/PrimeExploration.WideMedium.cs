using System.Buffers;

namespace Puck.Maths;

public static partial class PrimeExploration {
    // Match the existing medium-prime kernel's three-times-segment envelope. Upper primes in this
    // band use the same residue-specialized phase buckets as the small-table medium primes.
    private sealed class WideMedium : IDisposable {
        private readonly PrimeMarkState[] m_states;
        private readonly PacketBuckets m_buckets;
        private readonly int[] m_starts = new int[9];
        private readonly int[] m_active = new int[8];
        private readonly int m_count;
        private readonly ulong m_origin;
        private readonly PrimeByteLayout m_layout;

        internal WideMedium(PrimeMarkState[] states, int count, ulong origin, PrimeByteLayout layout) {
            m_states = states;
            m_count = count;
            m_origin = origin;
            m_layout = layout;
            GroupPacketStates(states: states.AsSpan(length: count, start: 0), starts: m_starts);
            m_buckets = new PacketBuckets(states: states.AsSpan(length: count, start: 0), starts: m_starts, cutoff: (WideBucketBytes / 8));
        }

        internal void Mark(Span<byte> segment, ulong blockLow, ulong high) =>
            MarkBucketSegment(segment: segment, blockLow: blockLow, high: high, states: m_states.AsSpan(length: m_count, start: 0),
                layout: m_layout, origin: m_origin, starts: m_starts, smallActive: m_active, buckets: m_buckets);

        public void Dispose() {
            try { m_buckets.Dispose(); } finally { ArrayPool<PrimeMarkState>.Shared.Return(m_states); }
        }
    }
}
