namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Builds the self-contained link-cable exchange ROM the Tier-C serial-link stage boots on BOTH machines. The program
/// performs eight serial transfers of a counting byte sequence and records everything a host needs to judge the link:
/// each received byte lands in work RAM at <c>0xC000</c>+, each completed transfer's serial interrupt request (IF bit
/// 3) is checked, counted into <c>0xC0F1</c>, and acknowledged, and a <c>0xA5</c> completion marker lands at
/// <c>0xC0F0</c> when all eight transfers finish. The one parameterized difference between the two sides is the SC
/// start value — <c>0x81</c> drives the internal clock (the master), <c>0x80</c> waits on the peer's clock (the
/// slave) — plus the first byte of each side's counting sequence, so every exchanged byte is attributable.
/// </summary>
internal static class SerialLinkRom {
    private const int EntryPoint = 0x0100;
    private const int RomSize = 0x8000;

    /// <summary>The number of serial transfers each side performs.</summary>
    public const byte TransferCount = 8;
    /// <summary>The work-RAM address of the first received byte (one per transfer, ascending).</summary>
    public const ushort ReceiveBufferAddress = 0xC000;
    /// <summary>The work-RAM address of the completion marker (<see cref="CompletionMarker"/> once all transfers ran).</summary>
    public const ushort CompletionMarkerAddress = 0xC0F0;
    /// <summary>The completion marker value.</summary>
    public const byte CompletionMarker = 0xA5;
    /// <summary>The work-RAM address of the serial-interrupt observation count — how many completed transfers found IF
    /// bit 3 raised (and acknowledged it), which must equal <see cref="TransferCount"/> on a healthy link.</summary>
    public const ushort InterruptCountAddress = 0xC0F1;

    /// <summary>Creates one side's ROM image.</summary>
    /// <param name="internalClock">Whether this side drives the transfer clock (SC <c>0x81</c>, the master) or waits
    /// on the peer's clock (SC <c>0x80</c>, the slave).</param>
    /// <param name="sendBase">The first byte of this side's counting send sequence (incremented per transfer).</param>
    /// <returns>A 32&#160;KiB ROM-only cartridge image whose entry point runs the exchange protocol.</returns>
    public static byte[] Create(bool internalClock, byte sendBase) {
        var control = (byte)(internalClock ? 0x81 : 0x80);

        // The exchange protocol at the post-boot entry point 0x0100 (registers: B = transfers remaining, C = next
        // byte to send, D = serial-interrupt observations, HL = receive-buffer cursor):
        //   0x0100  31 FE FF   ld   sp, 0xFFFE
        //   0x0103  21 00 C0   ld   hl, 0xC000
        //   0x0106  06 08      ld   b, 8
        //   0x0108  0E nn      ld   c, sendBase
        //   0x010A  16 00      ld   d, 0
        //   0x010C  79         ld   a, c          ; loop: send the next byte
        //   0x010D  E0 01      ldh  (0xFF01), a   ; SB = send byte
        //   0x010F  3E nn      ld   a, control
        //   0x0111  E0 02      ldh  (0xFF02), a   ; SC = start (internal or external clock)
        //   0x0113  F0 02      ldh  a, (0xFF02)   ; wait: poll the transfer bit
        //   0x0115  E6 80      and  0x80
        //   0x0117  20 FA      jr   nz, 0x0113
        //   0x0119  F0 0F      ldh  a, (0xFF0F)   ; the completed transfer must have raised IF bit 3
        //   0x011B  CB 5F      bit  3, a
        //   0x011D  28 07      jr   z, 0x0126     ; not observed: skip the count and acknowledge
        //   0x011F  14         inc  d
        //   0x0120  F0 0F      ldh  a, (0xFF0F)   ; acknowledge (clear ONLY the serial bit), so the next
        //   0x0122  E6 F7      and  0xF7          ; transfer's request is a fresh observation
        //   0x0124  E0 0F      ldh  (0xFF0F), a
        //   0x0126  F0 01      ldh  a, (0xFF01)   ; store the received byte
        //   0x0128  22         ld   (hl+), a
        //   0x0129  0C         inc  c
        //   0x012A  05         dec  b
        //   0x012B  20 DF      jr   nz, 0x010C
        //   0x012D  7A         ld   a, d          ; done: publish the observation count and the marker
        //   0x012E  EA F1 C0   ld   (0xC0F1), a
        //   0x0131  3E A5      ld   a, 0xA5
        //   0x0133  EA F0 C0   ld   (0xC0F0), a
        //   0x0136  18 FE      jr   0x0136        ; spin forever
        byte[] program = [
            0x31, 0xFE, 0xFF,
            0x21, 0x00, 0xC0,
            0x06, TransferCount,
            0x0E, sendBase,
            0x16, 0x00,
            0x79,
            0xE0, 0x01,
            0x3E, control,
            0xE0, 0x02,
            0xF0, 0x02,
            0xE6, 0x80,
            0x20, 0xFA,
            0xF0, 0x0F,
            0xCB, 0x5F,
            0x28, 0x07,
            0x14,
            0xF0, 0x0F,
            0xE6, 0xF7,
            0xE0, 0x0F,
            0xF0, 0x01,
            0x22,
            0x0C,
            0x05,
            0x20, 0xDF,
            0x7A,
            0xEA, 0xF1, 0xC0,
            0x3E, 0xA5,
            0xEA, 0xF0, 0xC0,
            0x18, 0xFE,
        ];

        // A zero-filled image already carries a valid ROM-only header (see SyntheticRom); only the program is written.
        var rom = new byte[RomSize];

        program.CopyTo(array: rom, index: EntryPoint);

        return rom;
    }

    /// <summary>Creates one side's ROM image for the churn stage: the same exchange protocol as <see cref="Create"/> but
    /// with a configurable transfer count and a deliberate idle delay loop between transfers. The idle gap opens a wide
    /// transfer-idle window (SC bit 7 clear on both ports) between every exchange, so the churn stage can reliably land a
    /// budget boundary in a mid-exchange idle instant to sever/snapshot/reconnect at.</summary>
    /// <param name="internalClock">Whether this side drives the transfer clock (SC <c>0x81</c>, the master) or waits on
    /// the peer's clock (SC <c>0x80</c>, the slave).</param>
    /// <param name="sendBase">The first byte of this side's counting send sequence (incremented per transfer).</param>
    /// <param name="transferCount">The number of serial transfers this side performs (1..255).</param>
    /// <param name="idleDelay">The inner-loop iteration count of the between-transfer idle delay (the SM83 `dec e`
    /// loop treats 0 as 256 iterations — pass 1 for the shortest delay).</param>
    /// <returns>A 32&#160;KiB ROM-only cartridge image whose entry point runs the gapped exchange protocol.</returns>
    public static byte[] CreateChurn(bool internalClock, byte sendBase, byte transferCount, byte idleDelay) {
        var control = (byte)(internalClock ? 0x81 : 0x80);

        // The gapped exchange protocol (registers: B = transfers remaining, C = next byte, D = serial-interrupt
        // observations, E = idle-delay counter, HL = receive-buffer cursor). It mirrors Create's loop with an idle
        // delay loop (ld e / dec e / jr nz) inserted after each received byte is stored and before the loop tail:
        //   0x0100  31 FE FF   ld   sp, 0xFFFE
        //   0x0103  21 00 C0   ld   hl, 0xC000
        //   0x0106  06 nn      ld   b, transferCount
        //   0x0108  0E nn      ld   c, sendBase
        //   0x010A  16 00      ld   d, 0
        //   0x010C  79         ld   a, c          ; loop: send the next byte
        //   0x010D  E0 01      ldh  (0xFF01), a
        //   0x010F  3E nn      ld   a, control
        //   0x0111  E0 02      ldh  (0xFF02), a   ; SC = start (internal or external clock)
        //   0x0113  F0 02      ldh  a, (0xFF02)   ; wait: poll the transfer bit
        //   0x0115  E6 80      and  0x80
        //   0x0117  20 FA      jr   nz, 0x0113
        //   0x0119  F0 0F      ldh  a, (0xFF0F)   ; the completed transfer must have raised IF bit 3
        //   0x011B  CB 5F      bit  3, a
        //   0x011D  28 07      jr   z, 0x0126
        //   0x011F  14         inc  d
        //   0x0120  F0 0F      ldh  a, (0xFF0F)   ; acknowledge (clear ONLY the serial bit)
        //   0x0122  E6 F7      and  0xF7
        //   0x0124  E0 0F      ldh  (0xFF0F), a
        //   0x0126  F0 01      ldh  a, (0xFF01)   ; store the received byte
        //   0x0128  22         ld   (hl+), a
        //   0x0129  1E nn      ld   e, idleDelay  ; idle gap: keeps both ports transfer-idle between exchanges
        //   0x012B  1D         dec  e
        //   0x012C  20 FD      jr   nz, 0x012B
        //   0x012E  0C         inc  c
        //   0x012F  05         dec  b
        //   0x0130  20 DA      jr   nz, 0x010C
        //   0x0132  7A         ld   a, d          ; done: publish the observation count and the marker
        //   0x0133  EA F1 C0   ld   (0xC0F1), a
        //   0x0136  3E A5      ld   a, 0xA5
        //   0x0138  EA F0 C0   ld   (0xC0F0), a
        //   0x013B  18 FE      jr   0x013B        ; spin forever
        byte[] program = [
            0x31, 0xFE, 0xFF,
            0x21, 0x00, 0xC0,
            0x06, transferCount,
            0x0E, sendBase,
            0x16, 0x00,
            0x79,
            0xE0, 0x01,
            0x3E, control,
            0xE0, 0x02,
            0xF0, 0x02,
            0xE6, 0x80,
            0x20, 0xFA,
            0xF0, 0x0F,
            0xCB, 0x5F,
            0x28, 0x07,
            0x14,
            0xF0, 0x0F,
            0xE6, 0xF7,
            0xE0, 0x0F,
            0xF0, 0x01,
            0x22,
            0x1E, idleDelay,
            0x1D,
            0x20, 0xFD,
            0x0C,
            0x05,
            0x20, 0xDA,
            0x7A,
            0xEA, 0xF1, 0xC0,
            0x3E, 0xA5,
            0xEA, 0xF0, 0xC0,
            0x18, 0xFE,
        ];

        var rom = new byte[RomSize];

        program.CopyTo(array: rom, index: EntryPoint);

        return rom;
    }
}
