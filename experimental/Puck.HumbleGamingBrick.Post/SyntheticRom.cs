namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Builds a tiny, self-contained cartridge image so the core self-tests (Tier A) run anywhere, with no external ROM
/// corpus. It is a 32&#160;KiB ROM-only cartridge (header type <c>0x00</c>, size <c>0x00</c>) whose entry point at
/// <c>0x0100</c> runs a bounded, fully deterministic loop: fill work RAM page <c>0xC000</c>–<c>0xC0FF</c> with a walking
/// value, then restart. No boot ROM is used, so the machine starts at its synthesized post-boot handoff state and begins
/// executing this loop immediately. The loop is not meant to compute anything meaningful — it exists only to advance CPU,
/// memory, timer, serial, PPU, and APU state deterministically so the determinism / snapshot / throughput stages have a
/// real machine to observe.
/// </summary>
internal static class SyntheticRom {
    private const int EntryPoint = 0x0100;
    private const int RomSize = 0x8000;

    // A bounded WRAM-fill loop placed at the post-boot entry point 0x0100:
    //   0x0100  21 00 C0   ld   hl, 0xC000
    //   0x0103  7D         ld   a, l
    //   0x0104  22         ld   (hl+), a
    //   0x0105  7C         ld   a, h
    //   0x0106  FE C1      cp   0xC1
    //   0x0108  20 F9      jr   nz, 0x0103   ; loop until hl reaches 0xC100 (one WRAM page filled)
    //   0x010A  18 F4      jr   0x0100       ; restart forever
    private static readonly byte[] Program = [
        0x21, 0x00, 0xC0,
        0x7D,
        0x22,
        0x7C,
        0xFE, 0xC1,
        0x20, 0xF9,
        0x18, 0xF4,
    ];

    /// <summary>Creates the synthetic ROM image.</summary>
    /// <param name="cartridgeType">The header cartridge-type byte (<c>0x0147</c>); the default <c>0x00</c> is
    /// ROM-only. The battery-save stage passes <c>0x13</c> (MBC3+RAM+BATTERY).</param>
    /// <param name="ramSize">The header RAM-size byte (<c>0x0149</c>); the default <c>0x00</c> is no RAM.</param>
    /// <returns>A 32&#160;KiB cartridge image whose entry point runs a deterministic WRAM-fill loop.</returns>
    public static byte[] Create(byte cartridgeType = 0x00, byte ramSize = 0x00) {
        // A zero-filled image already carries a valid ROM-only header (type 0x00, ROM-size 0x00 = 32 KiB, no RAM, a
        // monochrome color flag), so only the program bytes — and any non-default header bytes — need to be written.
        var rom = new byte[RomSize];

        Program.CopyTo(array: rom, index: EntryPoint);

        rom[0x0147] = cartridgeType;
        rom[0x0149] = ramSize;

        return rom;
    }
}
