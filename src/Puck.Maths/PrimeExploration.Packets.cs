using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExploration {
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
        internal readonly uint Prime => ((((uint)PrimeWheel30.Modulus) * Quotient) + PrimeWheel30.Residues[PrimeChannel]);
    }

    private static int InitializePacketStates(Span<PrimeMarkState> states, ulong blockLow, ulong low, ulong high) {
        var count = 0;
        var residues = PrimeWheel30.NumericResidues;

        foreach (var basePrime in PrimeKernels.BasePrimes) {
            if (basePrime <= PrimeSieveKernels.PreSievePrimeLimit) { continue; }
            var prime = ((ulong)basePrime);
            var square = (prime * prime);

            if (square > high) { break; }
            var (quotient, remainder) = Math.DivRem(left: Math.Max(val1: low, val2: square), right: prime);
            var multiplier = (quotient + ((remainder == 0) ? 0UL : 1UL));
            var residue = (multiplier % PrimeWheel30.Modulus);
            byte source = 0;

            while (residues[source] < residue) { ++source; }
            multiplier += (residues[source] - residue);
            if (multiplier > (high / prime)) { continue; }
            _ = PrimeWheel30.TryChannel(channel: out var primeChannel, residue: ((byte)(prime % PrimeWheel30.Modulus)));
            // Future squares are at most 65535²/30 bytes away; a currently active prime advances at most p bytes.
            states[count++] = new PrimeMarkState {
                Data = ((uint)(((multiplier * prime) / PrimeWheel30.Modulus) - blockLow)) |
                (((ulong)(basePrime / PrimeWheel30.Modulus)) << 48) | (((ulong)source) << 32) | (((ulong)primeChannel) << 40),
            };
        }
        return count;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte MarkPacketPrime<TResidue>(byte* output, byte* end, ref PrimeMarkState state, bool fullPackets = true, byte startPhase = byte.MaxValue)
        where TResidue : struct, PrimeSieveKernels.IPacketResidue {
        var quotient = ((uint)state.Quotient);
        var masks = TResidue.Masks;

        // Phase k marks the multiple whose multiplier has the k-th ascending unit residue. Its byte lies a fixed offset
        // past the packet's phase-zero byte, the offsets of MarkFullPacketRun, so every single mark addresses
        // packet+offset and no mark waits on the one before it.
        var one = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 1, quotient: quotient);
        var two = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 2, quotient: quotient);
        var three = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 3, quotient: quotient);
        var four = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 4, quotient: quotient);
        var five = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 5, quotient: quotient);
        var six = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 6, quotient: quotient);
        var seven = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 7, quotient: quotient);
        // The cycle advances p bytes. Seven is 28q+r-1, since floor(29r/30)=r-1 for every unit r, so p is seven+2q+1;
        // spelled from seven, the edge phases' stride stays a value apart from the full-packet run's own p, so the
        // register allocator holds that run's stride in a register instead of spilling one value shared by both loops.
        var advance = (seven + ((nint)((2 * quotient) + 1)));

        var mask0 = ((byte)(masks >> 0));
        var mask1 = ((byte)(masks >> 8));
        var mask2 = ((byte)(masks >> 16));
        var mask3 = ((byte)(masks >> 24));
        var mask4 = ((byte)(masks >> 32));
        var mask5 = ((byte)(masks >> 40));
        var mask6 = ((byte)(masks >> 48));
        var mask7 = ((byte)(masks >> 56));

        // The saved cursor is the byte of the saved phase; the packet starts that phase's offset earlier, possibly
        // before the segment, and is only dereferenced at or after the cursor. remaining is end - packet.
        var cursor = (output + state.NextOffset);
        var packet = cursor;
        var remaining = (end - cursor);
        byte phase;
        nint offset;

        // Resume the saved phase once. Each following phase has a fixed offset and mask position.
        switch (fullPackets ? state.Source : startPhase) {
            case 1: packet -= one; remaining += one; goto Phase1;
            case 2: packet -= two; remaining += two; goto Phase2;
            case 3: packet -= three; remaining += three; goto Phase3;
            case 4: packet -= four; remaining += four; goto Phase4;
            case 5: packet -= five; remaining += five; goto Phase5;
            case 6: packet -= six; remaining += six; goto Phase6;
            case 7: packet -= seven; remaining += seven; goto Phase7;
        }
    Phase0:
        if (fullPackets && (seven < remaining)) {
            packet = MarkFullPacketRun<TResidue>(cursor: packet, end: end, quotient: quotient);
            remaining = (end - packet);
        }
        if (remaining <= 0) { phase = 0; offset = 0; goto Finished; }
        packet[0] = ((byte)(packet[0] & mask0));

    Phase1:
        if (remaining <= one) { phase = 1; offset = one; goto Finished; }
        packet[one] = ((byte)(packet[one] & mask1));

    Phase2:
        if (remaining <= two) { phase = 2; offset = two; goto Finished; }
        packet[two] = ((byte)(packet[two] & mask2));

    Phase3:
        if (remaining <= three) { phase = 3; offset = three; goto Finished; }
        packet[three] = ((byte)(packet[three] & mask3));

    Phase4:
        if (remaining <= four) { phase = 4; offset = four; goto Finished; }
        packet[four] = ((byte)(packet[four] & mask4));

    Phase5:
        if (remaining <= five) { phase = 5; offset = five; goto Finished; }
        packet[five] = ((byte)(packet[five] & mask5));

    Phase6:
        if (remaining <= six) { phase = 6; offset = six; goto Finished; }
        packet[six] = ((byte)(packet[six] & mask6));

    Phase7:
        if (remaining <= seven) { phase = 7; offset = seven; goto Finished; }
        packet[seven] = ((byte)(packet[seven] & mask7));
        // Returning to multiplier residue one advances the packet by p bytes.
        packet += advance;
        remaining -= advance;
        goto Phase0;

    Finished:
        state.Data = (state.Data & 0xFFFFFF0000000000UL) | (((ulong)phase) << 32) | ((uint)(offset - remaining));
        return phase;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte* MarkFullPacketRun<TResidue>(byte* cursor, byte* end, uint quotient)
        where TResidue : struct, PrimeSieveKernels.IPacketResidue {
        var prime = ((nint)((PrimeWheel30.Modulus * quotient) + TResidue.Residue));
        // At multiplier residue 1, the product remainder is p mod 30. These are the offsets of the next
        // eight ascending multiplier residues. Returning to residue 1 advances the byte cursor by p.
        var one = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 1, quotient: quotient);
        var two = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 2, quotient: quotient);
        var three = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 3, quotient: quotient);
        var four = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 4, quotient: quotient);
        var five = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 5, quotient: quotient);
        var six = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 6, quotient: quotient);
        var seven = PrimeSieveKernels.PacketOffsetCore<TResidue>(phase: 7, quotient: quotient);
        var masks = TResidue.Masks;
        var mask0 = ((byte)masks);
        var mask1 = ((byte)(masks >> 8));
        var mask2 = ((byte)(masks >> 16));
        var mask3 = ((byte)(masks >> 24));
        var mask4 = ((byte)(masks >> 32));
        var mask5 = ((byte)(masks >> 40));
        var mask6 = ((byte)(masks >> 48));
        var mask7 = ((byte)(masks >> 56));

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
        ulong origin, ref int activeCount) where TResidue : struct, PrimeSieveKernels.IPacketResidue {
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

            // The state is read and written in place: an enregistered copy would hold its word across the packet run,
            // which keeps the cursor, the limit, the prime and seven offsets in registers.
            while (Unsafe.IsAddressLessThan(left: ref current, right: ref inputEnd)) {
                MarkPacketPrime<TResidue>(output, end, ref current);
                current = ref Unsafe.Add(elementOffset: 1, source: ref current);
            }
        }
    }
}
