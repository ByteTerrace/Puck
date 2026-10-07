using System.Runtime.CompilerServices;

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
            if (basePrime <= PreSievePrimeLimit) { continue; }
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
        where TResidue : struct, IPacketResidue {
        var cursor = (output + state.NextOffset);
        var quotient = ((uint)state.Quotient);
        byte phase = 0;
        var prime = ((nint)((PrimeWheel30.Modulus * quotient) + TResidue.Residue));
        var masks = TResidue.Masks;

        // Distance k is PrimeWheel30.Gap(k)·q + PrimeWheel30.Carry(r, k): the gaps 6, 4, 2, 4 and 2 and the multipliers
        // 1, 7, 11, 13 and 29 are written out so the JIT folds each constant-residue distance while importing, and
        // prime-exploration.wheel-tables-match-their-derivation proves the written forms equal the derivation.
        var distance0 = ((nint)((6 * quotient) + ((((TResidue.Residue * 1) % PrimeWheel30.Modulus) + (TResidue.Residue * 6)) / PrimeWheel30.Modulus)));
        var distance1 = ((nint)((4 * quotient) + ((((TResidue.Residue * 7) % PrimeWheel30.Modulus) + (TResidue.Residue * 4)) / PrimeWheel30.Modulus)));
        var distance2 = ((nint)((2 * quotient) + ((((TResidue.Residue * 11) % PrimeWheel30.Modulus) + (TResidue.Residue * 2)) / PrimeWheel30.Modulus)));
        var distance3 = ((nint)((4 * quotient) + ((((TResidue.Residue * 13) % PrimeWheel30.Modulus) + (TResidue.Residue * 4)) / PrimeWheel30.Modulus)));
        var distance7 = ((nint)((2 * quotient) + ((((TResidue.Residue * 29) % PrimeWheel30.Modulus) + (TResidue.Residue * 2)) / PrimeWheel30.Modulus)));

        var mask0 = ((byte)(masks >> 0));
        var mask1 = ((byte)(masks >> 8));
        var mask2 = ((byte)(masks >> 16));
        var mask3 = ((byte)(masks >> 24));
        var mask4 = ((byte)(masks >> 32));
        var mask5 = ((byte)(masks >> 40));
        var mask6 = ((byte)(masks >> 48));
        var mask7 = ((byte)(masks >> 56));

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
        var seven = ((28 * quotient) + ((TResidue.Residue * 29) / PrimeWheel30.Modulus));
        if (fullPackets && ((cursor + seven) < end)) {
            cursor = MarkFullPacketRun<TResidue>(cursor: cursor, end: end, prime: ((nint)prime), quotient: quotient);
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
        // Phases one and three share a gap, and their carries agree exactly for the residues that are squares modulo
        // five; those residues reuse distance1.
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
        // Phases two and seven share a gap, and their carries agree exactly for the other four residues; those reuse
        // distance2.
        cursor += ((TResidue.Residue is 7 or 13 or 17 or 23) ? distance2 : distance7);
        goto Phase0;

    Finished:
        state.Data = (state.Data & 0xFFFFFF0000000000UL) | (((ulong)phase) << 32) | ((uint)(cursor - end));
        return phase;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte* MarkFullPacketRun<TResidue>(byte* cursor, byte* end, nint prime, uint quotient)
        where TResidue : struct, IPacketResidue {
        // At multiplier residue 1, the product remainder is p mod 30. These are the offsets of the next
        // eight ascending multiplier residues. Returning to residue 1 advances the byte cursor by p. Offset k is
        // (PrimeWheel30.Multiplier(k) − 1)·q + PrimeWheel30.Lift(r, Multiplier(k)), written out for import-time folding.
        var one = ((nint)((6 * quotient) + ((TResidue.Residue * 7) / PrimeWheel30.Modulus)));
        var two = ((nint)((10 * quotient) + ((TResidue.Residue * 11) / PrimeWheel30.Modulus)));
        var three = ((nint)((12 * quotient) + ((TResidue.Residue * 13) / PrimeWheel30.Modulus)));
        var four = ((nint)((16 * quotient) + ((TResidue.Residue * 17) / PrimeWheel30.Modulus)));
        var five = ((nint)((18 * quotient) + ((TResidue.Residue * 19) / PrimeWheel30.Modulus)));
        var six = ((nint)((22 * quotient) + ((TResidue.Residue * 23) / PrimeWheel30.Modulus)));
        var seven = ((nint)((28 * quotient) + ((TResidue.Residue * 29) / PrimeWheel30.Modulus)));
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
}
