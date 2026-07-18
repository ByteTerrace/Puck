namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Builds the self-contained ROMs the Tier-C infrared-exchange stage boots on two Color machines to blink a pattern at
/// the peer over the CGB infrared port (RP, <c>0xFF56</c>) and read the peer's pattern back.
/// <para>
/// Turn-based, not simultaneous: hardware self-sensing (M-02 — a CGB reads its own lit LED, OR-ed with the peer's) makes
/// truly simultaneous bidirectional exchange ambiguous on real hardware — a side driving its own bit while also sampling
/// RP cannot tell "my own light" from "the peer's light" out of an OR. Real IR software (Mystery Gift) resolves this the
/// same way this ROM does: one side transmits (drives its pattern, never arming its own read-enable, so it never
/// self-senses and its own light never pollutes what it can't observe anyway) while the other receives (arms
/// read-enable with its OWN LED held OFF, so its self-sense contribution is always false and every sampled bit is
/// purely the peer's); then the roles swap. <see cref="CreatePrimary"/> transmits first, then receives; <see cref="CreateSecondary"/>
/// receives first, then transmits — pairing the two keeps a transmitter's whole pattern inside its listener's matching
/// receive phase. Because the <see cref="IrLinkSession"/> interleave paces both machines by real elapsed T-cycles (not
/// instruction count), the two sides' differently-shaped code stays phase-aligned to within one instruction regardless.
/// </para>
/// </summary>
internal static class InfraredRom {
    private const int EntryPoint = 0x0100;
    // Transmit at 0x0110, Receive at 0x012D (see the two subroutines' comments for the exact byte layout); the pattern
    // table starts right after Receive ends.
    private const int TransmitAddress = 0x0110;
    private const int ReceiveAddress = 0x012D;
    private const int PatternBase = 0x0153;
    private const int RomSize = 0x8000;
    // The inner settle count between driving a bit and the receiver's matching sample. 0x20 iterations (~7 cycles each)
    // dwarfs the ~1-instruction lock-step skew the furthest-behind interleave can leave, so the transmitted bit is
    // always stable before the receiver samples it.
    private const byte SettleCount = 0x20;
    // CYCLE-MATCHING IS LOAD-BEARING, not cosmetic: the furthest-behind interleave keeps both machines' CLOCKS synced,
    // but it says nothing about where in a routine either one currently stands — that phase relationship is set by each
    // side's OWN per-iteration T-cycle cost. Receive's tail (store, publish progress, compare) costs 44T more per bit
    // than Transmit's bare loop-back, so an untouched Transmit would run faster and drift into the NEXT bit before its
    // slower receiver finishes sampling the current one — corrupting every bit after the first. These 11 NOPs (44T)
    // in Transmit's loop pad it to the IDENTICAL 608T per-bit cost Receive's loop carries (settle=508T + 100T tail vs.
    // settle=508T + 56T tail + 44T padding), so the write-to-sample phase offset established at bit 0 (a few tens of
    // cycles from the two sides' differing one-time setup) stays constant, not accumulating, across every bit.
    private const int TransmitPaddingNopCount = 11;

    /// <summary>The work-RAM address of the first received bit (one <c>0</c>/<c>1</c> byte per pattern bit, ascending).</summary>
    public const ushort ReceiveBufferAddress = 0xC000;
    /// <summary>The work-RAM address of the completion marker (<see cref="CompletionMarker"/> once every expected bit is received).</summary>
    public const ushort CompletionMarkerAddress = 0xC0F0;
    /// <summary>The work-RAM address of the running progress counter — how many bits have been received so far, updated
    /// after each one so a host can pick a mid-exchange churn boundary deterministically.</summary>
    public const ushort ProgressAddress = 0xC0F1;
    /// <summary>The completion-marker value.</summary>
    public const byte CompletionMarker = 0xA5;

    // Transmit (0x0110): drains the 0xFF-terminated pattern table at HL, driving RP = bit (0 or 1) with the
    // data-read-enable bits NEVER set, so this side never self-senses (and correctness never depends on whether it
    // would). The 11-NOP pad after the LED write is cycle-matching (see TransmitPaddingNopCount), not a delay for its
    // own sake. Registers: HL = pattern cursor (caller-seeded), B = settle counter, A = scratch.
    //   tx_loop  (0x0110)  2A         ld   a, (hl+)         ; next pattern bit, 0xFF terminates
    //            (0x0111)  FE FF      cp   0xFF
    //            (0x0113)  28 14      jr   z, tx_done
    //            (0x0115)  E0 56      ldh  (0xFF56), a      ; RP = pattern bit (LED only, no arm bits)
    //            (0x0117)  00 x11     nop * 11              ; cycle-matching pad (44T) — see TransmitPaddingNopCount
    //            (0x0122)  06 20      ld   b, 0x20
    //   tx_settle(0x0124)  05         dec  b
    //            (0x0125)  20 FD      jr   nz, tx_settle
    //            (0x0127)  18 E7      jr   tx_loop
    //   tx_done  (0x0129)  AF         xor  a                ; A = 0
    //            (0x012A)  E0 56      ldh  (0xFF56), a      ; LED off once transmission ends
    //            (0x012C)  C9         ret
    private static readonly byte[] Transmit = [
        0x2A,
        0xFE, 0xFF,
        0x28, 0x14,
        0xE0, 0x56,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // TransmitPaddingNopCount NOPs
        0x06, SettleCount,
        0x05,
        0x20, 0xFD,
        0x18, 0xE7,
        0xAF,
        0xE0, 0x56,
        0xC9,
    ];

    // Receive (0x012D): arms RP's data-read-enable with its own LED bit held off (so self-sensing never contributes —
    // every sampled bit is purely the peer's), then samples once per settle window until the caller-baked bit count is
    // reached. Registers: DE = receive cursor, C = bits received, B = settle counter, A = scratch.
    //            (0x012D)  3E C0      ld   a, 0xC0          ; arm read-enable, LED bit 0 = 0 (not driving)
    //            (0x012F)  E0 56      ldh  (0xFF56), a
    //            (0x0131)  11 00 C0   ld   de, 0xC000       ; receive buffer
    //            (0x0134)  0E 00      ld   c, 0             ; bits received
    //   rx_loop  (0x0136)  06 20      ld   b, 0x20
    //   rx_settle(0x0138)  05         dec  b
    //            (0x0139)  20 FD      jr   nz, rx_settle
    //            (0x013B)  F0 56      ldh  a, (0xFF56)      ; sample RP
    //            (0x013D)  E6 02      and  0x02             ; bit 1: 0 = light detected, 2 = none
    //            (0x013F)  0F         rrca                  ; -> 0 (light) or 1 (none)
    //            (0x0140)  EE 01      xor  0x01             ; -> received bit: 1 (light) or 0 (none)
    //            (0x0142)  12         ld   (de), a          ; store received bit
    //            (0x0143)  13         inc  de
    //            (0x0144)  0C         inc  c                ; progress++
    //            (0x0145)  79         ld   a, c
    //            (0x0146)  EA F1 C0   ld   (0xC0F1), a      ; publish progress
    //            (0x0149)  FE nn      cp   <expected count> ; caller-baked bit count
    //            (0x014B)  20 E9      jr   nz, rx_loop
    //            (0x014D)  3E A5      ld   a, 0xA5
    //            (0x014F)  EA F0 C0   ld   (0xC0F0), a      ; completion marker
    //            (0x0152)  C9         ret
    private static readonly byte[] Receive = [
        0x3E, 0xC0,
        0xE0, 0x56,
        0x11, (byte)(ReceiveBufferAddress & 0xFF), (byte)(ReceiveBufferAddress >> 8),
        0x0E, 0x00,
        0x06, SettleCount,
        0x05,
        0x20, 0xFD,
        0xF0, 0x56,
        0xE6, 0x02,
        0x0F,
        0xEE, 0x01,
        0x12,
        0x13,
        0x0C,
        0x79,
        0xEA, (byte)(ProgressAddress & 0xFF), (byte)(ProgressAddress >> 8),
        0xFE, 0x00, // patched with the expected receive bit count
        0x20, 0xE9,
        0x3E, CompletionMarker,
        0xEA, (byte)(CompletionMarkerAddress & 0xFF), (byte)(CompletionMarkerAddress >> 8),
        0xC9,
    ];
    // Offset of Receive's "cp <expected count>" operand within the Receive array — patched per ROM.
    private const int ReceiveExpectedCountOffset = 29;

    /// <summary>Expands a source byte sequence into the MSB-first bit pattern one side blinks — the deterministic transcript
    /// the peer must receive back exactly.</summary>
    /// <param name="sourceBytes">The bytes to expand (each becomes eight pattern bits, most-significant first).</param>
    /// <returns>One <c>0</c>/<c>1</c> byte per bit.</returns>
    public static byte[] ExpandPattern(ReadOnlySpan<byte> sourceBytes) {
        var bits = new byte[(sourceBytes.Length * 8)];

        for (var index = 0; (index < sourceBytes.Length); ++index) {
            var value = sourceBytes[index];

            for (var bit = 0; (bit < 8); ++bit) {
                bits[((index * 8) + bit)] = (byte)((value >> (7 - bit)) & 0x01);
            }
        }

        return bits;
    }

    /// <summary>Creates the TRANSMIT-then-RECEIVE side's ROM: sends <paramref name="patternBits"/> first, then listens
    /// for <paramref name="expectedReceiveCount"/> bits. Pair with the peer's <see cref="CreateSecondary"/> so the
    /// transmit phase lands inside the peer's matching receive phase.</summary>
    /// <param name="patternBits">This side's pattern to send, one <c>0</c>/<c>1</c> byte per bit (from <see cref="ExpandPattern"/>).</param>
    /// <param name="expectedReceiveCount">The number of bits this side expects to receive afterward.</param>
    /// <returns>A 32&#160;KiB ROM-only cartridge image.</returns>
    public static byte[] CreatePrimary(ReadOnlySpan<byte> patternBits, int expectedReceiveCount) =>
        Assemble(
            // 0x0100  31 FE FF   ld   sp, 0xFFFE
            // 0x0103  21 53 01   ld   hl, PatternBase
            // 0x0106  CD 10 01   call Transmit
            // 0x0109  CD 2D 01   call Receive
            // 0x010C  18 FE      jr   $                    ; spin
            dispatcher: [
                0x31, 0xFE, 0xFF,
                0x21, (byte)(PatternBase & 0xFF), (byte)(PatternBase >> 8),
                0xCD, (byte)(TransmitAddress & 0xFF), (byte)(TransmitAddress >> 8),
                0xCD, (byte)(ReceiveAddress & 0xFF), (byte)(ReceiveAddress >> 8),
                0x18, 0xFE,
            ],
            patternBits: patternBits,
            expectedReceiveCount: expectedReceiveCount
        );
    /// <summary>Creates the RECEIVE-then-TRANSMIT side's ROM: listens for <paramref name="expectedReceiveCount"/> bits
    /// first, then sends <paramref name="patternBits"/>. Pair with the peer's <see cref="CreatePrimary"/>.</summary>
    /// <param name="patternBits">This side's pattern to send afterward, one <c>0</c>/<c>1</c> byte per bit (from <see cref="ExpandPattern"/>).</param>
    /// <param name="expectedReceiveCount">The number of bits this side expects to receive first.</param>
    /// <returns>A 32&#160;KiB ROM-only cartridge image.</returns>
    public static byte[] CreateSecondary(ReadOnlySpan<byte> patternBits, int expectedReceiveCount) =>
        Assemble(
            // 0x0100  31 FE FF   ld   sp, 0xFFFE
            // 0x0103  CD 2D 01   call Receive
            // 0x0106  21 53 01   ld   hl, PatternBase
            // 0x0109  CD 10 01   call Transmit
            // 0x010C  18 FE      jr   $                    ; spin
            dispatcher: [
                0x31, 0xFE, 0xFF,
                0xCD, (byte)(ReceiveAddress & 0xFF), (byte)(ReceiveAddress >> 8),
                0x21, (byte)(PatternBase & 0xFF), (byte)(PatternBase >> 8),
                0xCD, (byte)(TransmitAddress & 0xFF), (byte)(TransmitAddress >> 8),
                0x18, 0xFE,
            ],
            patternBits: patternBits,
            expectedReceiveCount: expectedReceiveCount
        );

    private static byte[] Assemble(byte[] dispatcher, ReadOnlySpan<byte> patternBits, int expectedReceiveCount) {
        // A zero-filled image already carries a valid ROM-only header (see SyntheticRom). Both roles share the same
        // Transmit/Receive subroutine bytes at the same fixed addresses; only the tiny dispatcher (call order) and the
        // baked expected-count/pattern table differ.
        var rom = new byte[RomSize];

        dispatcher.CopyTo(array: rom, index: EntryPoint);
        Transmit.CopyTo(array: rom, index: TransmitAddress);
        Receive.CopyTo(array: rom, index: ReceiveAddress);

        rom[(ReceiveAddress + ReceiveExpectedCountOffset)] = (byte)expectedReceiveCount;

        for (var index = 0; (index < patternBits.Length); ++index) {
            rom[(PatternBase + index)] = (byte)(patternBits[index] & 0x01);
        }

        rom[(PatternBase + patternBits.Length)] = 0xFF;

        return rom;
    }
}
