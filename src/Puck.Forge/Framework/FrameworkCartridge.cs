using System.Text;

namespace Puck.Forge.Framework;

/// <summary>
/// The framework's 32 KiB cartridge assembler: an MBC1 + RAM + BATTERY image (header type 0x03, 8 KiB of save RAM at
/// 0xA000) with the interrupt-driven prologue convention baked into the vectors — 0x0040 jumps to the VBlank handler
/// at <see cref="Hw.VBlankHandlerAddress"/>, the other four vectors are bare <c>reti</c>, and the header trampoline
/// enters the routine at <see cref="Hw.EntryAddress"/> whose first instruction must be <c>jp boot</c>. Both 16 KiB
/// ROM banks are visible without a single bank-switch write (MBC1's primary bank resets to 1), so code lives in
/// 0x0150..0x3FFF and data in 0x4000..0x7FFF. The header/logo/checksum machinery is the framework's own — a
/// deliberate self-contained copy, not a shared dependency.
///
/// <para>Puck's <c>--rom</c> path runs no boot ROM (it starts at the seeded post-boot handoff, A = 0x11), so the
/// logo/checksums are not required to boot here; they are written so the <c>.gbc</c> is valid on real hardware.</para>
/// </summary>
internal static class FrameworkCartridge {
    private const ushort EntryPoint = 0x0100;
    private const int MaxDataBytes = 0x4000;
    private const int MaxRoutineBytes = (RomDataBuilder.BaseAddress - Hw.EntryAddress);
    private const byte OpcodeJumpAbsolute = 0xC3;
    private const byte OpcodeReturnFromInterrupt = 0xD9;
    private const int RomSize = 0x8000;

    private static readonly byte[] BootLogo = [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
        0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E, 0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99,
        0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];

    /// <summary>Assembles a complete framework cartridge.</summary>
    /// <param name="title">The header title (≤ 15 characters, upper-cased).</param>
    /// <param name="routine">The machine code (emit it with base address <see cref="Hw.EntryAddress"/>; the first
    /// instruction must be a 3-byte <c>jp boot</c> so the VBlank handler sits at <see cref="Hw.VBlankHandlerAddress"/>).</param>
    /// <param name="data">The baked data blob (a <see cref="RomDataBuilder"/> result), placed at 0x4000.</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public static byte[] Build(string title, byte[] routine, byte[] data) {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(routine);
        ArgumentNullException.ThrowIfNull(data);

        if (routine.Length > MaxRoutineBytes) {
            throw new ArgumentException(message: $"The game routine is {routine.Length} bytes, over the {MaxRoutineBytes}-byte code window (0x{Hw.EntryAddress:X4}..0x{RomDataBuilder.BaseAddress:X4}).", paramName: nameof(routine));
        }

        if (data.Length > MaxDataBytes) {
            throw new ArgumentException(message: $"The data blob is {data.Length} bytes, over the {MaxDataBytes}-byte data window.", paramName: nameof(data));
        }

        if ((routine.Length < 3) || (routine[0] != OpcodeJumpAbsolute)) {
            throw new ArgumentException(message: "The routine must open with the 3-byte 'jp boot' prologue so the VBlank handler lands at 0x0153.", paramName: nameof(routine));
        }

        var rom = new byte[RomSize];

        WriteInterruptVectors(rom: rom);
        WriteHeader(rom: rom, title: title);

        routine.CopyTo(array: rom, index: Hw.EntryAddress);
        data.CopyTo(array: rom, index: RomDataBuilder.BaseAddress);

        Finalize(rom: rom);

        return rom;
    }

    private static void WriteInterruptVectors(byte[] rom) {
        // 0x0040 (VBlank): jp Hw.VBlankHandlerAddress. The handler address is fixed by the prologue convention.
        rom[0x0040] = OpcodeJumpAbsolute;
        rom[0x0041] = (byte)(Hw.VBlankHandlerAddress & 0xFF);
        rom[0x0042] = (byte)((Hw.VBlankHandlerAddress >> 8) & 0xFF);

        // STAT / timer / serial / joypad vectors: bare reti (never enabled, but a stray request stays harmless).
        rom[0x0048] = OpcodeReturnFromInterrupt;
        rom[0x0050] = OpcodeReturnFromInterrupt;
        rom[0x0058] = OpcodeReturnFromInterrupt;
        rom[0x0060] = OpcodeReturnFromInterrupt;
    }
    private static void WriteHeader(byte[] rom, string title) {
        // Entry point (0x0100): nop; jp EntryAddress.
        rom[EntryPoint] = 0x00;
        rom[(EntryPoint + 1)] = OpcodeJumpAbsolute;
        rom[(EntryPoint + 2)] = (byte)(Hw.EntryAddress & 0xFF);
        rom[(EntryPoint + 3)] = (byte)((Hw.EntryAddress >> 8) & 0xFF);

        BootLogo.CopyTo(array: rom, index: 0x0104);

        var titleBytes = Encoding.ASCII.GetBytes(s: title.ToUpperInvariant());

        for (var index = 0; ((index < titleBytes.Length) && (index < 15)); index++) {
            rom[(0x0134 + index)] = titleBytes[index];
        }

        rom[0x0143] = 0xC0; // CGB flag: Color REQUIRED.
        rom[0x0147] = 0x03; // Cartridge type: MBC1 + RAM + BATTERY.
        rom[0x0148] = 0x00; // ROM size: 32 KiB (2 banks, both visible at reset).
        rom[0x0149] = 0x02; // RAM size: 8 KiB at 0xA000.
        rom[0x014A] = 0x01; // Destination: non-Japanese.
        rom[0x014B] = 0x33; // Old licensee 0x33 = "see new licensee code".
    }
    private static void Finalize(byte[] rom) {
        byte headerChecksum = 0;

        for (var address = 0x0134; (address <= 0x014C); address++) {
            headerChecksum = (byte)((headerChecksum - rom[address]) - 1);
        }

        rom[0x014D] = headerChecksum;

        var globalSum = 0;

        for (var address = 0; (address < rom.Length); address++) {
            if ((address == 0x014E) || (address == 0x014F)) {
                continue;
            }

            globalSum += rom[address];
        }

        rom[0x014E] = (byte)((globalSum >> 8) & 0xFF);
        rom[0x014F] = (byte)(globalSum & 0xFF);
    }
}
