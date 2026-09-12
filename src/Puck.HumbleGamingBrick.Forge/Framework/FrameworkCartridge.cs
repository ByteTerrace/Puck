using System.Text;

namespace Puck.HumbleGamingBrick.Forge.Framework;

/// <summary>
/// The framework's 32 KiB cartridge assembler: an MBC1 + RAM + BATTERY image (header type 0x03, 8 KiB of save RAM at
/// 0xA000) with the interrupt-driven prologue convention baked into the vectors — 0x0040 jumps to the VBlank handler
/// at <see cref="Hw.VBlankHandlerAddress"/>, the other four vectors are bare <c>reti</c>, and the header trampoline
/// enters the routine at <see cref="Hw.EntryAddress"/> whose first instruction must be <c>jp boot</c>. Both 16 KiB
/// ROM banks are visible without a single bank-switch write (MBC1's primary bank resets to 1), so code lives in
/// 0x0150..0x3FFF and data in 0x4000..0x7FFF. The header/logo/checksum machinery is the framework's own — a
/// deliberate self-contained copy, not a shared dependency.
///
/// <para>The header checksum supports Puck's compatible boot firmware. The default era logo also supports hardware
/// firmware that validates that bitmap; a caller may supply the Puck house logo for its own cartridges.</para>
/// </summary>
public static class FrameworkCartridge {
    private const ushort EntryPoint = 0x0100;
    private const int MaxDataBytes = 0x4000;
    private const int MaxRoutineBytes = (RomDataBuilder.BaseAddress - Hw.EntryAddress);
    private const byte OpcodeJumpAbsolute = 0xC3;
    private const byte OpcodeReturnFromInterrupt = 0xD9;
    /// <summary>One switchable window's size; banks two and up are paged into 0x4000..0x7FFF.</summary>
    public const int BankSize = 0x4000;
    /// <summary>The most banks the header's size field can describe.</summary>
    public const int MaxBankCount = 512;

    /// <summary>Assembles a complete framework cartridge.</summary>
    /// <param name="title">The header title (≤ 15 characters, upper-cased).</param>
    /// <param name="routine">The machine code (emit it with base address <see cref="Hw.EntryAddress"/>; the first
    /// instruction must be a 3-byte <c>jp boot</c> so the VBlank handler sits at <see cref="Hw.VBlankHandlerAddress"/>).</param>
    /// <param name="data">The baked data blob (a <see cref="RomDataBuilder"/> result), placed at 0x4000.</param>
    /// <param name="clock">Whether the header advertises a battery-backed real-time clock.</param>
    /// <param name="statHandlerAddress">The display status handler's address, or zero to leave that vector inert.</param>
    /// <param name="banks">Payloads for banks two and up, each at most <see cref="BankSize"/> bytes. Each is paged
    /// into 0x4000..0x7FFF by writing its number to 0x2000, so a bank's contents are only readable while selected.</param>
    /// <param name="logo">The 48-byte logo the header carries, or null for the era one. Puck's compatible firmware
    /// accepts either logo; strict Era/House boot policies and hardware firmware may require a matching bitmap.</param>
    /// <returns>The ROM image, sized to hold every bank.</returns>
    public static byte[] Build(string title, byte[] routine, byte[] data, IReadOnlyList<byte[]>? banks = null, bool clock = false, ushort statHandlerAddress = 0, byte[]? logo = null) {
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

        var extra = banks ?? [];
        for (var index = 0; index < extra.Count; ++index) {
            if (extra[index] is null || extra[index].Length > BankSize) {
                throw new ArgumentException(message: $"Bank {index + 2} is {extra[index]?.Length ?? 0} bytes, over the {BankSize}-byte window.", paramName: nameof(banks));
            }
        }

        // Two fixed banks plus the switchable ones, rounded up to the power of two the header's size field describes.
        var bankCount = 2;
        while (bankCount < extra.Count + 2) {
            bankCount <<= 1;
        }

        if (bankCount > MaxBankCount) {
            throw new ArgumentException(message: $"{extra.Count} extra banks exceed the {MaxBankCount}-bank ceiling.", paramName: nameof(banks));
        }

        var rom = new byte[bankCount * BankSize];

        WriteInterruptVectors(rom: rom, statHandlerAddress: statHandlerAddress);
        WriteHeader(logo: logo, rom: rom, title: title, bankCount: bankCount, clock: clock);

        routine.CopyTo(array: rom, index: Hw.EntryAddress);
        data.CopyTo(array: rom, index: RomDataBuilder.BaseAddress);
        for (var index = 0; index < extra.Count; ++index) {
            extra[index].CopyTo(array: rom, index: (index + 2) * BankSize);
        }

        Finalize(rom: rom);

        return rom;
    }

    private static void WriteInterruptVectors(byte[] rom, ushort statHandlerAddress) {
        // 0x0040 (VBlank): jp Hw.VBlankHandlerAddress. The handler address is fixed by the prologue convention.
        rom[0x0040] = OpcodeJumpAbsolute;
        rom[0x0041] = ((byte)(Hw.VBlankHandlerAddress & 0xFF));
        rom[0x0042] = ((byte)((Hw.VBlankHandlerAddress >> 8) & 0xFF));

        // STAT: jumps to the raster handler when one exists, otherwise a bare reti like the rest.
        if (statHandlerAddress != 0) {
            rom[0x0048] = OpcodeJumpAbsolute;
            rom[0x0049] = ((byte)(statHandlerAddress & 0xFF));
            rom[0x004A] = ((byte)((statHandlerAddress >> 8) & 0xFF));
        } else {
            rom[0x0048] = OpcodeReturnFromInterrupt;
        }

        // Timer / serial / joypad vectors: bare reti (never enabled, but a stray request stays harmless).
        rom[0x0050] = OpcodeReturnFromInterrupt;
        rom[0x0058] = OpcodeReturnFromInterrupt;
        rom[0x0060] = OpcodeReturnFromInterrupt;
    }
    private static void WriteHeader(byte[] rom, string title, int bankCount, bool clock, byte[]? logo) {
        // Entry point (0x0100): nop; jp EntryAddress.
        rom[EntryPoint] = 0x00;
        rom[(EntryPoint + 1)] = OpcodeJumpAbsolute;
        rom[(EntryPoint + 2)] = ((byte)(Hw.EntryAddress & 0xFF));
        rom[(EntryPoint + 3)] = ((byte)((Hw.EntryAddress >> 8) & 0xFF));

        (logo ?? CartridgeHeader.Logo.ToArray()).CopyTo(destination: rom.AsSpan(start: CartridgeHeader.LogoOffset));

        var titleBytes = Encoding.ASCII.GetBytes(s: title.ToUpperInvariant());

        for (var index = 0; ((index < titleBytes.Length) && (index < 15)); index++) {
            rom[(0x0134 + index)] = titleBytes[index];
        }

        rom[0x0143] = 0xC0; // CGB flag: Color REQUIRED.
        // Cartridge type. MBC5 pages without MBC1's bank-number aliasing, but only MBC3 carries a real-time clock,
        // so a cartridge wanting one takes MBC3's smaller reach in exchange.
        rom[0x0147] = (byte)(clock ? 0x10 : 0x1B);
        // ROM size: the code is log2 of the bank count minus one, so two banks is 0 and 512 banks is 8.
        var sizeCode = 0;
        while ((2 << sizeCode) < bankCount) {
            ++sizeCode;
        }

        rom[0x0148] = (byte)sizeCode;
        rom[0x0149] = 0x02; // RAM size: 8 KiB at 0xA000.
        rom[0x014A] = 0x01; // Destination: non-Japanese.
        rom[0x014B] = 0x33; // Old licensee 0x33 = "see new licensee code".
    }
    private static void Finalize(byte[] rom) {
        byte headerChecksum = 0;

        for (var address = 0x0134; (address <= 0x014C); address++) {
            headerChecksum = ((byte)((headerChecksum - rom[address]) - 1));
        }

        rom[0x014D] = headerChecksum;

        var globalSum = 0;

        for (var address = 0; (address < rom.Length); address++) {
            if ((address == 0x014E) || (address == 0x014F)) {
                continue;
            }

            globalSum += rom[address];
        }

        rom[0x014E] = ((byte)((globalSum >> 8) & 0xFF));
        rom[0x014F] = ((byte)(globalSum & 0xFF));
    }
}
