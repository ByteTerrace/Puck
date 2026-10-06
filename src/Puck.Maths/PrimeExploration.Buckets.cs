using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExploration {
    // Each residue owns eight lists. Any one phase can contain its entire medium-prime group.
    // Only occupied entries are touched; pooled spare capacity is never scanned or cleared.
    private sealed class PacketBuckets : IDisposable {
        internal PrimeMarkState[] Current;
        internal PrimeMarkState[] Next;
        internal int[] CurrentEnds = new int[64];
        internal int[] NextEnds = new int[64];

        internal readonly int[] MediumStarts = new int[8];
        internal readonly int[] MediumActive = new int[8];
        internal readonly int[] Capacities = new int[8];
        internal readonly int[] Offsets = new int[8];

        internal PacketBuckets(ReadOnlySpan<PrimeMarkState> states, ReadOnlySpan<int> starts, int cutoff) {
            var capacity = 0;

            for (var group = 0; (group < 8); ++group) {
                var medium = starts[group];

                while ((medium < starts[(group + 1)]) && (states[medium].Prime <= cutoff)) { ++medium; }
                MediumStarts[group] = medium;
                Capacities[group] = (starts[(group + 1)] - medium);
                Offsets[group] = capacity;
                capacity += (8 * Capacities[group]);
            }
            Current = ArrayPool<PrimeMarkState>.Shared.Rent(minimumLength: Math.Max(val1: 1, val2: capacity));
            try { Next = ArrayPool<PrimeMarkState>.Shared.Rent(minimumLength: Math.Max(val1: 1, val2: capacity)); } catch { ArrayPool<PrimeMarkState>.Shared.Return(Current); throw; }
            for (var group = 0; (group < 8); ++group) {
                for (var phase = 0; (phase < 8); ++phase) {
                    CurrentEnds[((group * 8) + phase)] = (Offsets[group] + (phase * Capacities[group]));
                }
            }
        }

        internal void ResetNext() {
            for (var group = 0; (group < 8); ++group) {
                for (var phase = 0; (phase < 8); ++phase) {
                    NextEnds[((group * 8) + phase)] = (Offsets[group] + (phase * Capacities[group]));
                }
            }
        }
        internal void Advance() {
            (Current, Next) = (Next, Current);
            (CurrentEnds, NextEnds) = (NextEnds, CurrentEnds);
        }

        public void Dispose() {
            ArrayPool<PrimeMarkState>.Shared.Return(Current);
            ArrayPool<PrimeMarkState>.Shared.Return(Next);
        }
    }

    private static void MarkBucketSegment(Span<byte> segment, ulong blockLow, ulong high, Span<PrimeMarkState> states,
        PrimeByteLayout layout, ulong origin, ReadOnlySpan<int> starts, Span<int> smallActive, PacketBuckets buckets) {
        if (layout == PrimeByteLayout.Numeric) {
            MarkBucketSegment<NumericPacketLayout>(blockLow: blockLow, buckets: buckets, high: high, masks: NumericClearMasks, origin: origin, segment: segment, smallActive: smallActive, starts: starts, states: states);
        } else {
            MarkBucketSegment<AlgebraicPacketLayout>(blockLow: blockLow, buckets: buckets, high: high, masks: AlgebraicClearMasks, origin: origin, segment: segment, smallActive: smallActive, starts: starts, states: states);
        }
    }
    private static void MarkBucketSegment<TLayout>(Span<byte> segment, ulong blockLow, ulong high, Span<PrimeMarkState> states,
        ulong origin, ReadOnlySpan<int> starts, Span<int> smallActive, PacketBuckets buckets, byte[] masks)
        where TLayout : struct, IPacketLayout {
        var medium = buckets.MediumStarts;

        // All small-prime groups finish a cache chunk before moving to the next chunk.
        for (var offset = 0; (offset < segment.Length); offset += 32768) {
            var chunk = segment.Slice(offset, Math.Min(val1: 32768, val2: (segment.Length - offset)));
            var chunkLow = (blockLow + ((ulong)offset));
            var wideEnd = ((((UInt128)(chunkLow + ((ulong)chunk.Length))) * 30) - 1);
            var chunkHigh = ((wideEnd > high) ? high : ((ulong)wideEnd));

            MarkPacketGroup<PacketResidue1, TLayout>(chunk, chunkLow, chunkHigh, states[starts[0]..medium[0]], origin, ref smallActive[0], masks, default);
            MarkPacketGroup<PacketResidue7, TLayout>(chunk, chunkLow, chunkHigh, states[starts[1]..medium[1]], origin, ref smallActive[1], masks, default);
            MarkPacketGroup<PacketResidue19, TLayout>(chunk, chunkLow, chunkHigh, states[starts[2]..medium[2]], origin, ref smallActive[2], masks, default);
            MarkPacketGroup<PacketResidue13, TLayout>(chunk, chunkLow, chunkHigh, states[starts[3]..medium[3]], origin, ref smallActive[3], masks, default);
            MarkPacketGroup<PacketResidue11, TLayout>(chunk, chunkLow, chunkHigh, states[starts[4]..medium[4]], origin, ref smallActive[4], masks, default);
            MarkPacketGroup<PacketResidue17, TLayout>(chunk, chunkLow, chunkHigh, states[starts[5]..medium[5]], origin, ref smallActive[5], masks, default);
            MarkPacketGroup<PacketResidue29, TLayout>(chunk, chunkLow, chunkHigh, states[starts[6]..medium[6]], origin, ref smallActive[6], masks, default);
            MarkPacketGroup<PacketResidue23, TLayout>(chunk, chunkLow, chunkHigh, states[starts[7]..medium[7]], origin, ref smallActive[7], masks, default);
        }
        buckets.ResetNext();
        MarkMediumBucketGroup<PacketResidue1, TLayout>(segment, blockLow, high, states[medium[0]..starts[1]], origin, buckets, 0, masks);
        MarkMediumBucketGroup<PacketResidue7, TLayout>(segment, blockLow, high, states[medium[1]..starts[2]], origin, buckets, 1, masks);
        MarkMediumBucketGroup<PacketResidue19, TLayout>(segment, blockLow, high, states[medium[2]..starts[3]], origin, buckets, 2, masks);
        MarkMediumBucketGroup<PacketResidue13, TLayout>(segment, blockLow, high, states[medium[3]..starts[4]], origin, buckets, 3, masks);
        MarkMediumBucketGroup<PacketResidue11, TLayout>(segment, blockLow, high, states[medium[4]..starts[5]], origin, buckets, 4, masks);
        MarkMediumBucketGroup<PacketResidue17, TLayout>(segment, blockLow, high, states[medium[5]..starts[6]], origin, buckets, 5, masks);
        MarkMediumBucketGroup<PacketResidue29, TLayout>(segment, blockLow, high, states[medium[6]..starts[7]], origin, buckets, 6, masks);
        MarkMediumBucketGroup<PacketResidue23, TLayout>(segment, blockLow, high, states[medium[7]..starts[8]], origin, buckets, 7, masks);
        buckets.Advance();
    }
    private static unsafe void MarkMediumBucketGroup<TResidue, TLayout>(Span<byte> segment, ulong blockLow, ulong high,
        ReadOnlySpan<PrimeMarkState> dormant, ulong origin, PacketBuckets buckets, int group, byte[] masks)
        where TResidue : struct, IPacketResidue where TLayout : struct, IPacketLayout {
        var capacity = buckets.Capacities[group];
        var offset = buckets.Offsets[group];
        var countOffset = (group * 8);
        var active = buckets.MediumActive[group];

        // Dormant square cursors keep their interval origin; each state enters a bucket exactly once.
        while (active < dormant.Length) {
            var state = dormant[active];
            var prime = ((30U * state.Quotient) + TResidue.Residue);

            if ((((ulong)prime) * prime) > high) { break; }
            state.NextOffset -= ((uint)(blockLow - origin));
            var bucket = (countOffset + state.Source);

            buckets.Current[buckets.CurrentEnds[bucket]++] = state;
            ++active;
        }
        buckets.MediumActive[group] = active;
        ref var input = ref MemoryMarshal.GetArrayDataReference(array: buckets.Current);
        ref var output = ref MemoryMarshal.GetArrayDataReference(array: buckets.Next);
        ref var nextEnds = ref MemoryMarshal.GetArrayDataReference(array: buckets.NextEnds);

        fixed (byte* bitmap = segment) {
            for (byte phase = 0; (phase < 8); ++phase) {
                var count = (buckets.CurrentEnds[(countOffset + phase)] - (offset + (phase * capacity)));
                ref var list = ref Unsafe.Add(elementOffset: (offset + (phase * capacity)), source: ref input);

                if (count != 0) {
                    ref var ends = ref Unsafe.Add(elementOffset: countOffset, source: ref nextEnds);

                    switch (phase) {
                        case 0: MarkMediumPhase<TResidue, TLayout, PacketPhase0>(bitmap, segment.Length, ref list, count, ref output, ref ends, masks); break;
                        case 1: MarkMediumPhase<TResidue, TLayout, PacketPhase1>(bitmap, segment.Length, ref list, count, ref output, ref ends, masks); break;
                        case 2: MarkMediumPhase<TResidue, TLayout, PacketPhase2>(bitmap, segment.Length, ref list, count, ref output, ref ends, masks); break;
                        case 3: MarkMediumPhase<TResidue, TLayout, PacketPhase3>(bitmap, segment.Length, ref list, count, ref output, ref ends, masks); break;
                        case 4: MarkMediumPhase<TResidue, TLayout, PacketPhase4>(bitmap, segment.Length, ref list, count, ref output, ref ends, masks); break;
                        case 5: MarkMediumPhase<TResidue, TLayout, PacketPhase5>(bitmap, segment.Length, ref list, count, ref output, ref ends, masks); break;
                        case 6: MarkMediumPhase<TResidue, TLayout, PacketPhase6>(bitmap, segment.Length, ref list, count, ref output, ref ends, masks); break;
                        case 7: MarkMediumPhase<TResidue, TLayout, PacketPhase7>(bitmap, segment.Length, ref list, count, ref output, ref ends, masks); break;
                    }
                }
            }
        }
    }
    // One call per occupied phase list, matching the reference's bucket boundary; no per-prime call.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void MarkMediumPhase<TResidue, TLayout, TPhase>(byte* bitmap, int length, ref PrimeMarkState input,
        int count, ref PrimeMarkState output, ref int nextEnds, byte[] masks)
        where TResidue : struct, IPacketResidue where TLayout : struct, IPacketLayout where TPhase : struct, IPacketPhase {
        var end = (bitmap + length);
        ref var current = ref input;
        ref var inputEnd = ref Unsafe.Add(elementOffset: count, source: ref input);

        while (Unsafe.IsAddressLessThan(left: ref current, right: ref inputEnd)) {
            var state = current;

            var nextPhase = MarkPacketPrime<TResidue, TLayout>(bitmap, end, ref state, masks, fullPackets: false, startPhase: TPhase.Phase);
            ref var nextEnd = ref Unsafe.Add(elementOffset: nextPhase, source: ref nextEnds);

            // Each state is read once and appended once; a phase list cannot exceed its residue-group capacity.
            Unsafe.Add(ref output, nextEnd++) = state;
            current = ref Unsafe.Add(elementOffset: 1, source: ref current);
        }
    }

    private interface IPacketPhase { static abstract byte Phase { get; } }
    private readonly struct PacketPhase0 : IPacketPhase { public static byte Phase => 0; }
    private readonly struct PacketPhase1 : IPacketPhase { public static byte Phase => 1; }
    private readonly struct PacketPhase2 : IPacketPhase { public static byte Phase => 2; }
    private readonly struct PacketPhase3 : IPacketPhase { public static byte Phase => 3; }
    private readonly struct PacketPhase4 : IPacketPhase { public static byte Phase => 4; }
    private readonly struct PacketPhase5 : IPacketPhase { public static byte Phase => 5; }
    private readonly struct PacketPhase6 : IPacketPhase { public static byte Phase => 6; }
    private readonly struct PacketPhase7 : IPacketPhase { public static byte Phase => 7; }
}
