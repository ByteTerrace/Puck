namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Builds a tiny, self-contained Game Boy Advance cartridge image so the core self-tests (Tier A) run anywhere, with no
/// external ROM corpus. Execution begins at the cartridge entry point <c>0x08000000</c> (where
/// <see cref="GameBoyAdvanceMachine.DirectBoot"/> lands) and runs a bounded, fully deterministic ARM loop: clear
/// <c>DISPCNT</c> so the backdrop is displayed (not forced-blank), then walk the BG-palette backdrop colour by writing an
/// ever-incrementing counter to palette entry 0 forever. The loop is not meant to compute anything meaningful — it exists
/// only to advance CPU, bus, timer, and PPU state deterministically <em>and</em> to make the rendered framebuffer evolve,
/// so the determinism / throughput stages have a real, observable machine.
/// </summary>
internal static class SyntheticRom {
    private const int RomSize = 0x8000;

    // A bounded backdrop-walking loop placed at the cartridge entry point 0x08000000:
    //   0x08000000  E3A00404   mov r0, #0x04000000    ; r0 = I/O base
    //   0x08000004  E3A01000   mov r1, #0             ; DISPCNT = 0 (backdrop shown, not forced-blank)
    //   0x08000008  E5801000   str r1, [r0]           ; [0x04000000] = 0
    //   0x0800000C  E3A02405   mov r2, #0x05000000    ; r2 = BG palette base (entry 0 = backdrop colour)
    //   0x08000010  E3A03000   mov r3, #0             ; r3 = counter
    //   0x08000014  E5823000   str r3, [r2]           ; backdrop colour = r3  (loop head)
    //   0x08000018  E2833001   add r3, r3, #1         ; ++counter
    //   0x0800001C  EAFFFFFC   b   0x08000014         ; loop forever
    private static readonly uint[] Program = [
        0xE3A00404u,
        0xE3A01000u,
        0xE5801000u,
        0xE3A02405u,
        0xE3A03000u,
        0xE5823000u,
        0xE2833001u,
        0xEAFFFFFCu,
    ];

    /// <summary>Creates the synthetic ROM image.</summary>
    /// <returns>A 32&#160;KiB cartridge image whose entry point runs a deterministic backdrop-walking loop. It carries no
    /// save-type or RTC identifier string, so the cartridge detects no backup and no clock.</returns>
    public static byte[] Create() {
        var rom = new byte[RomSize];

        for (var index = 0; (index < Program.Length); ++index) {
            _ = BitConverter.TryWriteBytes(destination: rom.AsSpan(start: (index * 4)), value: Program[index]);
        }

        return rom;
    }
}
