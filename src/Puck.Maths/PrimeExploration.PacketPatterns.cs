using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExploration {
    /// <summary>Names one prime residue channel of the thirty-wheel as a type, so the marking loop specializes per channel.</summary>
    /// <remarks>Public so a law can compare each channel's packed masks with the wheel derivation; only the sieve's own loops use them.</remarks>
    public interface IPacketResidue {
        /// <summary>Gets the eight clear masks of the channel, one per ascending multiplier phase, packed from the lowest byte.</summary>
        static abstract ulong Masks { get; }
        /// <summary>Gets the channel's residue modulo thirty.</summary>
        static abstract uint Residue { get; }
    }
    // Byte i clears the bit PrimeWheel30.TargetBit(r, i) of (r * s_i) mod 30, for ascending multiplier residues s_i. The
    // words are written out so a constant-residue instantiation folds them while importing;
    // prime-exploration.wheel-tables-match-their-derivation proves they equal the derivation.
    public readonly struct PacketResidue1 : IPacketResidue {
        public static uint Residue => 1;
        public static ulong Masks => 0x7FBFDFEFF7FBFDFEUL;
    }
    public readonly struct PacketResidue7 : IPacketResidue {
        public static uint Residue => 7;
        public static ulong Masks => 0xBFFBF77FFEEFDFFDUL;
    }
    public readonly struct PacketResidue19 : IPacketResidue {
        public static uint Residue => 19;
        public static ulong Masks => 0xFBEFFEBFFD7FF7DFUL;
    }
    public readonly struct PacketResidue13 : IPacketResidue {
        public static uint Residue => 13;
        public static ulong Masks => 0xEF7FFDFBDFBFFEF7UL;
    }
    public readonly struct PacketResidue11 : IPacketResidue {
        public static uint Residue => 11;
        public static ulong Masks => 0xDFF77FFDBFFEEFFBUL;
    }
    public readonly struct PacketResidue17 : IPacketResidue {
        public static uint Residue => 17;
        public static ulong Masks => 0xF7FEBFDFFBFD7FEFUL;
    }
    public readonly struct PacketResidue29 : IPacketResidue {
        public static uint Residue => 29;
        public static ulong Masks => 0xFEFDFBF7EFDFBF7FUL;
    }
    public readonly struct PacketResidue23 : IPacketResidue {
        public static uint Residue => 23;
        public static ulong Masks => 0xFDDFEFFE7FF7FBBFUL;
    }

    // Initialization emits ascending primes; a stable counting partition by residue channel preserves each group's
    // activation order and records where each group starts.
    private static void GroupPacketStates(Span<PrimeMarkState> states, Span<int> starts) {
        if (states.IsEmpty) { starts.Clear(); return; }
        var reorder = ArrayPool<PrimeMarkState>.Shared.Rent(minimumLength: states.Length);

        try {
            Span<int> counts = stackalloc int[PrimeWheel30.ChannelCount];

            counts.Clear();
            foreach (var state in states) { ++counts[state.PrimeChannel]; }
            var total = 0;

            for (var channel = 0; (channel < PrimeWheel30.ChannelCount); ++channel) {
                var count = counts[channel];

                counts[channel] = total;
                total += count;
            }
            counts.CopyTo(destination: starts);
            starts[PrimeWheel30.ChannelCount] = total;
            foreach (var state in states) { reorder[counts[state.PrimeChannel]++] = state; }
            reorder.AsSpan(length: states.Length, start: 0).CopyTo(destination: states);
        } finally { ArrayPool<PrimeMarkState>.Shared.Return(reorder); }
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void MarkPacketGroup<TResidue>(Span<byte> segment, ulong blockLow, ulong high, Span<PrimeMarkState> states,
        ulong origin, ref int activeCount) where TResidue : struct, IPacketResidue {
        while (activeCount < states.Length) {
            ref var next = ref states[activeCount];

            if ((((ulong)next.Prime) * next.Prime) > high) { break; }
            next.NextOffset -= ((uint)(blockLow - origin));
            ++activeCount;
        }
        fixed (byte* output = segment) {
            var end = (output + segment.Length);
            ref var current = ref MemoryMarshal.GetReference(span: states);
            ref var inputEnd = ref Unsafe.Add(elementOffset: activeCount, source: ref current);

            while (Unsafe.IsAddressLessThan(left: ref current, right: ref inputEnd)) {
                var state = current;

                MarkPacketPrime<TResidue>(output, end, ref state);
                current = state;
                current = ref Unsafe.Add(elementOffset: 1, source: ref current);
            }
        }
    }
}
