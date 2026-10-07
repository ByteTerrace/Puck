using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExploration {
    private const int PacketChunkBytes = 16384;

    private static readonly byte[] PacketLifts = CreatePacketLifts();
    private static readonly byte[] PacketCarries = CreatePacketCarries();

    // Packet states cover primes through 98304; larger bases use the sparse bucket scheduler.
    // A relative cursor avoids forming the next full-width multiple near ulong.MaxValue.
    private struct PrimeMarkState {
        // Low word: relative cursor. High word: source phase, prime channel, quotient.
        // A single carrier lets bucket transfers load and store the complete state once.
        internal ulong Data;

        internal uint NextOffset {
            readonly get => ((uint)Data);
            set => Data = (Data & 0xFFFFFFFF00000000UL) | value;
        }
        internal readonly ushort Quotient => ((ushort)(Data >> 48));
        internal readonly byte Source => ((byte)(Data >> 32));
        internal readonly byte PrimeChannel => ((byte)(Data >> 40));
        internal readonly uint Prime => ((30U * Quotient) + PrimeWheel30.Residues[PrimeChannel]);
    }

    private static byte[] CreatePacketLifts() {
        var lifts = new byte[64];

        for (var channel = 0; (channel < 8); ++channel) {
            for (var source = 0; (source < 8); ++source) {
                lifts[((channel * 8) + source)] = ((byte)((PrimeWheel30.Residues[channel] * PrimeWheel30.NumericResidues[source]) / 30));
            }
        }
        return lifts;
    }
    private static byte[] CreatePacketCarries() {
        var carries = new byte[64];

        for (byte channel = 0; (channel < 8); ++channel) {
            for (var source = 0; (source < 8); ++source) {
                var target = PrimeWheel30.Multiply(left: channel, right: PrimeWheel30.NumericChannels[source]);

                carries[((channel * 8) + source)] = ((byte)((PrimeWheel30.Residues[target] + (PrimeWheel30.Residues[channel] * WheelGaps[source])) / 30));
            }
        }
        return carries;
    }
    private static int InitializePacketStates(Span<PrimeMarkState> states, ulong blockLow, ulong low, ulong high, bool usePreSieve) {
        var count = 0;
        var residues = PrimeWheel30.NumericResidues;

        foreach (var basePrime in PrimeKernels.BasePrimes) {
            if (basePrime <= (usePreSieve ? 163U : 5U)) { continue; }
            var prime = ((ulong)basePrime);
            var square = (prime * prime);

            if (square > high) { break; }
            var (quotient, remainder) = Math.DivRem(left: Math.Max(val1: low, val2: square), right: prime);
            var multiplier = (quotient + ((remainder == 0) ? 0UL : 1UL));
            var residue = (multiplier % 30);
            byte source = 0;

            while (residues[source] < residue) { ++source; }
            multiplier += (residues[source] - residue);
            if (multiplier > (high / prime)) { continue; }
            _ = PrimeWheel30.TryChannel(channel: out var primeChannel, residue: ((byte)(prime % 30)));
            // Future squares are at most 65535²/30 bytes away; a currently active prime advances at most p bytes.
            states[count++] = new PrimeMarkState {
                Data = ((uint)(((multiplier * prime) / 30) - blockLow)) |
                (((ulong)(basePrime / 30)) << 48) | (((ulong)source) << 32) | (((ulong)primeChannel) << 40),
            };
        }
        return count;
    }
    private static unsafe void MarkPacketChunks(Span<byte> segment, ulong blockLow, ulong high, Span<PrimeMarkState> states, PrimeByteLayout layout, int chunkBytes,
        ulong stateOrigin, ref int activeCount) {
        var masks = ((layout == PrimeByteLayout.Numeric) ? NumericClearMasks : AlgebraicClearMasks);

        for (var offset = 0; (offset < segment.Length);) {
            var length = Math.Min(val1: chunkBytes, val2: (segment.Length - offset));
            var chunk = segment.Slice(length: length, start: offset);
            var wideEnd = ((((UInt128)((blockLow + ((ulong)offset)) + ((ulong)length))) * 30) - 1);
            var chunkHigh = ((wideEnd > high) ? high : ((ulong)wideEnd));

            while (activeCount < states.Length) {
                ref var next = ref states[activeCount];

                if ((((ulong)next.Prime) * next.Prime) > chunkHigh) { break; }
                // Before activation the cursor is relative to the initial byte origin, not the current chunk.
                // Only a future square can delay activation, and all such squares are below 65535².
                next.NextOffset -= ((uint)((blockLow + ((ulong)offset)) - stateOrigin));
                ++activeCount;
            }
            fixed (byte* output = chunk) {
                var end = (output + length);

                foreach (ref var state in states[..activeCount]) {
                    MarkPacketPrime<DynamicPacketResidue, NumericPacketLayout>(output, end, ref state, masks);
                }
            }
            offset += length;
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte MarkPacketPrime<TResidue, TLayout>(byte* output, byte* end, ref PrimeMarkState state, byte[] masks, bool fullPackets = true, byte startPhase = byte.MaxValue)
        where TResidue : struct, IPacketResidue where TLayout : struct, IPacketLayout {
        var cursor = (output + state.NextOffset);

        if ((TResidue.Residue == 0) && (cursor >= end)) { state.NextOffset = ((uint)(cursor - end)); return (fullPackets ? state.Source : startPhase); }
        var quotient = ((uint)state.Quotient);
        byte phase = 0;
        var prime = ((nint)((30 * quotient) + ((TResidue.Residue == 0) ? PrimeWheel30.Residues[state.PrimeChannel] : TResidue.Residue)));
        var fixedMasks = (TLayout.Numeric ? TResidue.NumericMasks : TResidue.AlgebraicMasks);
        // Constant-residue instantiations never read the runtime tables, including in the full-packet leaf.
        ref var clear = ref Unsafe.NullRef<byte>();
        ref var carries = ref Unsafe.NullRef<byte>();
        ref var lifts = ref Unsafe.NullRef<byte>();

        if (TResidue.Residue == 0) {
            var row = (state.PrimeChannel * 8);

            clear = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array: masks), row);
            carries = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array: PacketCarries), row);
            lifts = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array: PacketLifts), row);
        }

        var distance0 = ((nint)((6 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 0, source: ref carries) : ((((TResidue.Residue * 1) % 30) + (TResidue.Residue * 6)) / 30))));
        var distance1 = ((nint)((4 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 1, source: ref carries) : ((((TResidue.Residue * 7) % 30) + (TResidue.Residue * 4)) / 30))));
        var distance2 = ((nint)((2 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 2, source: ref carries) : ((((TResidue.Residue * 11) % 30) + (TResidue.Residue * 2)) / 30))));
        var distance3 = ((nint)((4 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 3, source: ref carries) : ((((TResidue.Residue * 13) % 30) + (TResidue.Residue * 4)) / 30))));
        var distance7 = ((nint)((2 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 7, source: ref carries) : ((((TResidue.Residue * 29) % 30) + (TResidue.Residue * 2)) / 30))));

        var mask0 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 0, source: ref clear) : ((byte)(fixedMasks >> 0)));
        var mask1 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 1, source: ref clear) : ((byte)(fixedMasks >> 8)));
        var mask2 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 2, source: ref clear) : ((byte)(fixedMasks >> 16)));
        var mask3 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 3, source: ref clear) : ((byte)(fixedMasks >> 24)));
        var mask4 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 4, source: ref clear) : ((byte)(fixedMasks >> 32)));
        var mask5 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 5, source: ref clear) : ((byte)(fixedMasks >> 40)));
        var mask6 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 6, source: ref clear) : ((byte)(fixedMasks >> 48)));
        var mask7 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 7, source: ref clear) : ((byte)(fixedMasks >> 56)));

        // Resume the saved phase once. Each following phase has a fixed gap and mask position.
        switch (fullPackets ? state.Source : startPhase) {
            case 1: goto Phase1;
            case 2: goto Phase2;
            case 3: goto Phase3;
            case 4: goto Phase4;
            case 5: goto Phase5;
            case 6: goto Phase6;
            case 7: goto Phase7;
        }
    Phase0:
        var seven = ((28 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 7, source: ref lifts) : ((TResidue.Residue * 29) / 30)));
        if (fullPackets && ((cursor + seven) < end)) {
            cursor = MarkFullPacketRun<TResidue, TLayout>(clear: ref clear, cursor: cursor, end: end, lifts: ref lifts, prime: ((nint)prime), quotient: quotient);
        }
        if (cursor >= end) { phase = 0; goto Finished; }
        *cursor &= mask0;
        cursor += distance0;

    Phase1:
        if (cursor >= end) { phase = 1; goto Finished; }
        *cursor &= mask1;
        cursor += distance1;

    Phase2:
        if (cursor >= end) { phase = 2; goto Finished; }
        *cursor &= mask2;
        cursor += distance2;

    Phase3:
        if (cursor >= end) { phase = 3; goto Finished; }
        *cursor &= mask3;
        cursor += ((TResidue.Residue is 1 or 11 or 19 or 29) ? distance1 : distance3);

    Phase4:
        if (cursor >= end) { phase = 4; goto Finished; }
        *cursor &= mask4;
        cursor += distance2;

    Phase5:
        if (cursor >= end) { phase = 5; goto Finished; }
        *cursor &= mask5;
        cursor += distance1;

    Phase6:
        if (cursor >= end) { phase = 6; goto Finished; }
        *cursor &= mask6;
        cursor += distance0;

    Phase7:
        if (cursor >= end) { phase = 7; goto Finished; }
        *cursor &= mask7;
        cursor += ((TResidue.Residue is 7 or 13 or 17 or 23) ? distance2 : distance7);
        goto Phase0;

    Finished:
        state.Data = (state.Data & 0xFFFFFF0000000000UL) | (((ulong)phase) << 32) | ((uint)(cursor - end));
        return phase;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte* MarkFullPacketRun<TResidue, TLayout>(byte* cursor, byte* end, nint prime,
        uint quotient, ref byte clear, ref byte lifts)
        where TResidue : struct, IPacketResidue where TLayout : struct, IPacketLayout {
        // At multiplier residue 1, the product remainder is p mod 30. These are the offsets of the next
        // eight ascending multiplier residues. Returning to residue 1 advances the byte cursor by p.
        var one = ((nint)((6 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 1, source: ref lifts) : ((TResidue.Residue * 7) / 30))));
        var two = ((nint)((10 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 2, source: ref lifts) : ((TResidue.Residue * 11) / 30))));
        var three = ((nint)((12 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 3, source: ref lifts) : ((TResidue.Residue * 13) / 30))));
        var four = ((nint)((16 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 4, source: ref lifts) : ((TResidue.Residue * 17) / 30))));
        var five = ((nint)((18 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 5, source: ref lifts) : ((TResidue.Residue * 19) / 30))));
        var six = ((nint)((22 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 6, source: ref lifts) : ((TResidue.Residue * 23) / 30))));
        var seven = ((nint)((28 * quotient) + ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 7, source: ref lifts) : ((TResidue.Residue * 29) / 30))));
        var fixedMasks = (TLayout.Numeric ? TResidue.NumericMasks : TResidue.AlgebraicMasks);
        var mask0 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 0, source: ref clear) : ((byte)fixedMasks));
        var mask1 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 1, source: ref clear) : ((byte)(fixedMasks >> 8)));
        var mask2 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 2, source: ref clear) : ((byte)(fixedMasks >> 16)));
        var mask3 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 3, source: ref clear) : ((byte)(fixedMasks >> 24)));
        var mask4 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 4, source: ref clear) : ((byte)(fixedMasks >> 32)));
        var mask5 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 5, source: ref clear) : ((byte)(fixedMasks >> 40)));
        var mask6 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 6, source: ref clear) : ((byte)(fixedMasks >> 48)));
        var mask7 = ((TResidue.Residue == 0) ? Unsafe.Add(elementOffset: 7, source: ref clear) : ((byte)(fixedMasks >> 56)));

        // Offsets are monotone and below p; the last-byte guard proves all eight stores are in bounds.
        // The native-sized cursor and offsets stay below Array.MaxLength + 98304, without narrowing sums.
        var limit = (end - seven);

        while (cursor < limit) {
            // Explicit read/write pairs let the JIT fold indexed addresses into memory AND operands.
            cursor[0] = ((byte)(cursor[0] & mask0));
            cursor[one] = ((byte)(cursor[one] & mask1));
            cursor[two] = ((byte)(cursor[two] & mask2));
            cursor[three] = ((byte)(cursor[three] & mask3));
            cursor[four] = ((byte)(cursor[four] & mask4));
            cursor[five] = ((byte)(cursor[five] & mask5));
            cursor[six] = ((byte)(cursor[six] & mask6));
            cursor[seven] = ((byte)(cursor[seven] & mask7));
            cursor += prime;
        }
        return cursor;
    }
}
