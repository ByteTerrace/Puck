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

        internal int[] CurrentEnds = new int[(PrimeWheel30.ChannelCount * PrimeWheel30.ChannelCount)];
        internal int[] NextEnds = new int[(PrimeWheel30.ChannelCount * PrimeWheel30.ChannelCount)];
        internal readonly int[] MediumStarts = new int[PrimeWheel30.ChannelCount];
        internal readonly int[] MediumActive = new int[PrimeWheel30.ChannelCount];
        internal readonly int[] Capacities = new int[PrimeWheel30.ChannelCount];
        internal readonly int[] Offsets = new int[PrimeWheel30.ChannelCount];

        internal PacketBuckets(ReadOnlySpan<PrimeMarkState> states, ReadOnlySpan<int> starts, int cutoff) {
            var capacity = 0;

            for (var group = 0; (group < PrimeWheel30.ChannelCount); ++group) {
                var medium = starts[group];

                while ((medium < starts[(group + 1)]) && (states[medium].Prime <= cutoff)) { ++medium; }
                MediumStarts[group] = medium;
                Capacities[group] = (starts[(group + 1)] - medium);
                Offsets[group] = capacity;
                capacity += (PrimeWheel30.ChannelCount * Capacities[group]);
            }
            Current = ArrayPool<PrimeMarkState>.Shared.Rent(minimumLength: Math.Max(val1: 1, val2: capacity));
            try { Next = ArrayPool<PrimeMarkState>.Shared.Rent(minimumLength: Math.Max(val1: 1, val2: capacity)); } catch { ArrayPool<PrimeMarkState>.Shared.Return(Current); throw; }
            for (var group = 0; (group < PrimeWheel30.ChannelCount); ++group) {
                for (var phase = 0; (phase < PrimeWheel30.ChannelCount); ++phase) {
                    CurrentEnds[((group * PrimeWheel30.ChannelCount) + phase)] = (Offsets[group] + (phase * Capacities[group]));
                }
            }
        }

        internal void ResetNext() {
            for (var group = 0; (group < PrimeWheel30.ChannelCount); ++group) {
                for (var phase = 0; (phase < PrimeWheel30.ChannelCount); ++phase) {
                    NextEnds[((group * PrimeWheel30.ChannelCount) + phase)] = (Offsets[group] + (phase * Capacities[group]));
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
        ulong origin, ReadOnlySpan<int> starts, Span<int> smallActive, PacketBuckets buckets) {
        var medium = buckets.MediumStarts;

        // All small-prime groups finish a cache chunk before moving to the next chunk.
        for (var offset = 0; (offset < segment.Length); offset += CacheSegmentBytes) {
            var chunk = segment.Slice(offset, Math.Min(val1: CacheSegmentBytes, val2: (segment.Length - offset)));
            var chunkLow = (blockLow + ((ulong)offset));
            var wideEnd = ((((UInt128)(chunkLow + ((ulong)chunk.Length))) * PrimeWheel30.Modulus) - 1);
            var chunkHigh = ((wideEnd > high) ? high : ((ulong)wideEnd));

            MarkPacketGroup<PacketResidue1>(chunk, chunkLow, chunkHigh, states[starts[0]..medium[0]], origin, ref smallActive[0]);
            MarkPacketGroup<PacketResidue7>(chunk, chunkLow, chunkHigh, states[starts[1]..medium[1]], origin, ref smallActive[1]);
            MarkPacketGroup<PacketResidue19>(chunk, chunkLow, chunkHigh, states[starts[2]..medium[2]], origin, ref smallActive[2]);
            MarkPacketGroup<PacketResidue13>(chunk, chunkLow, chunkHigh, states[starts[3]..medium[3]], origin, ref smallActive[3]);
            MarkPacketGroup<PacketResidue11>(chunk, chunkLow, chunkHigh, states[starts[4]..medium[4]], origin, ref smallActive[4]);
            MarkPacketGroup<PacketResidue17>(chunk, chunkLow, chunkHigh, states[starts[5]..medium[5]], origin, ref smallActive[5]);
            MarkPacketGroup<PacketResidue29>(chunk, chunkLow, chunkHigh, states[starts[6]..medium[6]], origin, ref smallActive[6]);
            MarkPacketGroup<PacketResidue23>(chunk, chunkLow, chunkHigh, states[starts[7]..medium[7]], origin, ref smallActive[7]);
        }
        buckets.ResetNext();
        MarkMediumBucketGroup<PacketResidue1>(segment, blockLow, high, states[medium[0]..starts[1]], origin, buckets, 0);
        MarkMediumBucketGroup<PacketResidue7>(segment, blockLow, high, states[medium[1]..starts[2]], origin, buckets, 1);
        MarkMediumBucketGroup<PacketResidue19>(segment, blockLow, high, states[medium[2]..starts[3]], origin, buckets, 2);
        MarkMediumBucketGroup<PacketResidue13>(segment, blockLow, high, states[medium[3]..starts[4]], origin, buckets, 3);
        MarkMediumBucketGroup<PacketResidue11>(segment, blockLow, high, states[medium[4]..starts[5]], origin, buckets, 4);
        MarkMediumBucketGroup<PacketResidue17>(segment, blockLow, high, states[medium[5]..starts[6]], origin, buckets, 5);
        MarkMediumBucketGroup<PacketResidue29>(segment, blockLow, high, states[medium[6]..starts[7]], origin, buckets, 6);
        MarkMediumBucketGroup<PacketResidue23>(segment, blockLow, high, states[medium[7]..starts[8]], origin, buckets, 7);
        buckets.Advance();
    }
    private static unsafe void MarkMediumBucketGroup<TResidue>(Span<byte> segment, ulong blockLow, ulong high,
        ReadOnlySpan<PrimeMarkState> dormant, ulong origin, PacketBuckets buckets, int group)
        where TResidue : struct, IPacketResidue {
        var capacity = buckets.Capacities[group];
        var offset = buckets.Offsets[group];
        var countOffset = (group * PrimeWheel30.ChannelCount);
        var active = buckets.MediumActive[group];

        // Dormant square cursors keep their interval origin; each state enters a bucket exactly once.
        while (active < dormant.Length) {
            var state = dormant[active];
            var prime = ((((uint)PrimeWheel30.Modulus) * state.Quotient) + TResidue.Residue);

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
            for (byte phase = 0; (phase < PrimeWheel30.ChannelCount); ++phase) {
                var count = (buckets.CurrentEnds[(countOffset + phase)] - (offset + (phase * capacity)));
                ref var list = ref Unsafe.Add(elementOffset: (offset + (phase * capacity)), source: ref input);

                if (count != 0) {
                    ref var ends = ref Unsafe.Add(elementOffset: countOffset, source: ref nextEnds);

                    switch (phase) {
                        case 0: MarkMediumPhase<TResidue, PacketPhase0>(bitmap, segment.Length, ref list, count, ref output, ref ends); break;
                        case 1: MarkMediumPhase<TResidue, PacketPhase1>(bitmap, segment.Length, ref list, count, ref output, ref ends); break;
                        case 2: MarkMediumPhase<TResidue, PacketPhase2>(bitmap, segment.Length, ref list, count, ref output, ref ends); break;
                        case 3: MarkMediumPhase<TResidue, PacketPhase3>(bitmap, segment.Length, ref list, count, ref output, ref ends); break;
                        case 4: MarkMediumPhase<TResidue, PacketPhase4>(bitmap, segment.Length, ref list, count, ref output, ref ends); break;
                        case 5: MarkMediumPhase<TResidue, PacketPhase5>(bitmap, segment.Length, ref list, count, ref output, ref ends); break;
                        case 6: MarkMediumPhase<TResidue, PacketPhase6>(bitmap, segment.Length, ref list, count, ref output, ref ends); break;
                        case 7: MarkMediumPhase<TResidue, PacketPhase7>(bitmap, segment.Length, ref list, count, ref output, ref ends); break;
                    }
                }
            }
        }
    }
    // One call per occupied phase list, matching the reference's bucket boundary; no per-prime call.
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe void MarkMediumPhase<TResidue, TPhase>(byte* bitmap, int length, ref PrimeMarkState input,
        int count, ref PrimeMarkState output, ref int nextEnds)
        where TResidue : struct, IPacketResidue where TPhase : struct, IPacketPhase {
        var end = (bitmap + length);
        ref var current = ref input;
        ref var inputEnd = ref Unsafe.Add(elementOffset: count, source: ref input);

        while (Unsafe.IsAddressLessThan(left: ref current, right: ref inputEnd)) {
            var state = current;

            var nextPhase = MarkPacketPrime<TResidue>(bitmap, end, ref state, fullPackets: false, startPhase: TPhase.Phase);
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
