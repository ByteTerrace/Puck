using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExploration {
    private interface IPacketLayout { static abstract bool Numeric { get; } }
    private readonly struct NumericPacketLayout : IPacketLayout { public static bool Numeric => true; }
    private readonly struct AlgebraicPacketLayout : IPacketLayout { public static bool Numeric => false; }
    private interface IPacketResidue {
        static abstract ulong AlgebraicMasks { get; }
        static abstract ulong NumericMasks { get; }
        static abstract uint Residue { get; }
    }
    // Residue zero selects the runtime tables in the same marking body, preserving the unspecialized control.
    private readonly struct DynamicPacketResidue : IPacketResidue {
        public static uint Residue => 0;
        public static ulong NumericMasks => 0;
        public static ulong AlgebraicMasks => 0;
    }
    // Byte i clears the lane of (r * s_i) mod 30, for ascending multiplier residues s_i.
    // The two words differ only in the numeric versus algebraic numbering of those lanes.
    private readonly struct PacketResidue1 : IPacketResidue {
        public static uint Residue => 1;
        public static ulong NumericMasks => 0x7FBFDFEFF7FBFDFEUL;
        public static ulong AlgebraicMasks => 0xBF7FFBDFF7EFFDFEUL;
    }
    private readonly struct PacketResidue7 : IPacketResidue {
        public static uint Residue => 7;
        public static ulong NumericMasks => 0xBFFBF77FFEEFDFFDUL;
        public static ulong AlgebraicMasks => 0x7FEFF7BFFEDFFBFDUL;
    }
    private readonly struct PacketResidue19 : IPacketResidue {
        public static uint Residue => 19;
        public static ulong NumericMasks => 0xFBEFFEBFFD7FF7DFUL;
        public static ulong AlgebraicMasks => 0xEFDFFE7FFDBFF7FBUL;
    }
    private readonly struct PacketResidue13 : IPacketResidue {
        public static uint Residue => 13;
        public static ulong NumericMasks => 0xEF7FFDFBDFBFFEF7UL;
        public static ulong AlgebraicMasks => 0xDFBFFDEFFB7FFEF7UL;
    }
    private readonly struct PacketResidue11 : IPacketResidue {
        public static uint Residue => 11;
        public static ulong NumericMasks => 0xDFF77FFDBFFEEFFBUL;
        public static ulong AlgebraicMasks => 0xFBF7BFFD7FFEDFEFUL;
    }
    private readonly struct PacketResidue17 : IPacketResidue {
        public static uint Residue => 17;
        public static ulong NumericMasks => 0xF7FEBFDFFBFD7FEFUL;
        public static ulong AlgebraicMasks => 0xF7FE7FFBEFFDBFDFUL;
    }
    private readonly struct PacketResidue29 : IPacketResidue {
        public static uint Residue => 29;
        public static ulong NumericMasks => 0xFEFDFBF7EFDFBF7FUL;
        public static ulong AlgebraicMasks => 0xFEFDEFF7DFFB7FBFUL;
    }
    private readonly struct PacketResidue23 : IPacketResidue {
        public static uint Residue => 23;
        public static ulong NumericMasks => 0xFDDFEFFE7FF7FBBFUL;
        public static ulong AlgebraicMasks => 0xFDFBDFFEBFF7EF7FUL;
    }

    private static void GroupPacketStates(Span<PrimeMarkState> states, Span<int> starts) {
        if (states.IsEmpty) { starts.Clear(); return; }
        var reorder = ArrayPool<PrimeMarkState>.Shared.Rent(minimumLength: states.Length);

        try {
            // Initialization emits ascending primes; stable partitioning preserves each group's activation order.
            PartitionPacketStates<PacketChannelKey>(reorder: reorder, starts: starts, states: states);
        } finally { ArrayPool<PrimeMarkState>.Shared.Return(reorder); }
    }
    private static void MarkGroupedPacketSegment(Span<byte> segment, ulong blockLow, ulong high, Span<PrimeMarkState> states,
        PrimeByteLayout layout, ulong origin, ReadOnlySpan<int> starts, Span<int> active, Span<PrimeMarkState> reorder) {
        if (layout == PrimeByteLayout.Numeric) {
            MarkGroupedPacketSegment<NumericPacketLayout>(active: active, blockLow: blockLow, high: high, masks: NumericClearMasks, origin: origin, reorder: reorder, segment: segment, starts: starts, states: states);
        } else {
            MarkGroupedPacketSegment<AlgebraicPacketLayout>(active: active, blockLow: blockLow, high: high, masks: AlgebraicClearMasks, origin: origin, reorder: reorder, segment: segment, starts: starts, states: states);
        }
    }
    private static void MarkGroupedPacketSegment<TLayout>(Span<byte> segment, ulong blockLow, ulong high, Span<PrimeMarkState> states,
        ulong origin, ReadOnlySpan<int> starts, Span<int> active, byte[] masks, Span<PrimeMarkState> reorder) where TLayout : struct, IPacketLayout {
        MarkPacketGroup<PacketResidue1, TLayout>(segment, blockLow, high, states[starts[0]..starts[1]], origin, ref active[0], masks, reorder);
        MarkPacketGroup<PacketResidue7, TLayout>(segment, blockLow, high, states[starts[1]..starts[2]], origin, ref active[1], masks, reorder);
        MarkPacketGroup<PacketResidue19, TLayout>(segment, blockLow, high, states[starts[2]..starts[3]], origin, ref active[2], masks, reorder);
        MarkPacketGroup<PacketResidue13, TLayout>(segment, blockLow, high, states[starts[3]..starts[4]], origin, ref active[3], masks, reorder);
        MarkPacketGroup<PacketResidue11, TLayout>(segment, blockLow, high, states[starts[4]..starts[5]], origin, ref active[4], masks, reorder);
        MarkPacketGroup<PacketResidue17, TLayout>(segment, blockLow, high, states[starts[5]..starts[6]], origin, ref active[5], masks, reorder);
        MarkPacketGroup<PacketResidue29, TLayout>(segment, blockLow, high, states[starts[6]..starts[7]], origin, ref active[6], masks, reorder);
        MarkPacketGroup<PacketResidue23, TLayout>(segment, blockLow, high, states[starts[7]..starts[8]], origin, ref active[7], masks, reorder);
    }
    private static unsafe void MarkPacketGroup<TResidue, TLayout>(Span<byte> segment, ulong blockLow, ulong high, Span<PrimeMarkState> states,
        ulong origin, ref int activeCount, byte[] masks, Span<PrimeMarkState> reorder) where TResidue : struct, IPacketResidue where TLayout : struct, IPacketLayout {
        // The dormant suffix stays ordered by prime; phase sorting touches only active states.
        while (activeCount < states.Length) {
            ref var next = ref states[activeCount];

            if ((((ulong)next.Prime) * next.Prime) > high) { break; }
            next.NextOffset -= ((uint)(blockLow - origin));
            ++activeCount;
        }
        var smallCount = 0;

        if (!reorder.IsEmpty) {
            var cutoff = ((uint)(Math.Min(val1: segment.Length, val2: 32768) / 5));

            while ((smallCount < activeCount) && (states[smallCount].Prime <= cutoff)) { ++smallCount; }
            SortPacketPhases(states[smallCount..activeCount], reorder);
        }
        fixed (byte* output = segment) {
            var end = (output + segment.Length);
            ref var current = ref MemoryMarshal.GetReference(span: states);
            ref var inputEnd = ref Unsafe.Add(elementOffset: activeCount, source: ref current);

            while (Unsafe.IsAddressLessThan(left: ref current, right: ref inputEnd)) {
                var state = current;

                MarkPacketPrime<TResidue, TLayout>(output, end, ref state, masks);
                current = state;
                current = ref Unsafe.Add(elementOffset: 1, source: ref current);
            }
        }
    }
    private static void SortPacketPhases(Span<PrimeMarkState> states, Span<PrimeMarkState> reorder) {
        if (states.Length < 2) { return; }
        PartitionPacketStates<PacketPhaseKey>(reorder: reorder, starts: default, states: states);
    }
    private static void PartitionPacketStates<TKey>(Span<PrimeMarkState> states, Span<PrimeMarkState> reorder, Span<int> starts)
        where TKey : struct, IPacketPartitionKey {
        Span<int> counts = stackalloc int[8];

        counts.Clear();
        foreach (var state in states) { ++counts[TKey.Get(state: state)]; }
        var total = 0;

        for (var phase = 0; (phase < 8); ++phase) {
            var count = counts[phase];

            counts[phase] = total;
            total += count;
        }
        if (!starts.IsEmpty) { counts.CopyTo(destination: starts); starts[8] = total; }
        foreach (var state in states) { reorder[counts[TKey.Get(state: state)]++] = state; }
        reorder[..states.Length].CopyTo(destination: states);
    }

    private interface IPacketPartitionKey { static abstract byte Get(PrimeMarkState state); }
    private readonly struct PacketChannelKey : IPacketPartitionKey { public static byte Get(PrimeMarkState state) => state.PrimeChannel; }
    private readonly struct PacketPhaseKey : IPacketPartitionKey { public static byte Get(PrimeMarkState state) => state.Source; }
}
