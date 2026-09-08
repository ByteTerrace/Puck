using System.Text;

namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// The forge's 64 KiB cartridge assembler: an ARM branch at 0x000 jumps over the header block to the entry stub at
/// <see cref="EntryStubOffset"/>, whose two ARM words hand off into Thumb at <see cref="CodeAddress"/>; the header
/// carries the 12-byte title, 4-byte game code, the fixed 0x96 at 0xB2, and the complement checksum at 0xBD
/// (<c>(0 − sum(0xA0..0xBC) − 0x19) &amp; 0xFF</c>). Code and data occupy fixed windows with explicit size guards.
/// Output is byte-identical across builds — no wall clock and no randomness feed the image.
/// <para>The 156-byte logo field at 0x004 is an optional caller-supplied parameter, zeroed by default: this
/// machine's direct boot never reads it, but a retail BIOS boot on real hardware validates the logo bitmap, so a
/// caller targeting hardware must supply those bytes.</para>
/// </summary>
public static class AgbForgeCartridge {
    /// <summary>The ROM image size (64 KiB).</summary>
    public const int RomSize = 0x10000;
    /// <summary>The logo field's exact length in bytes.</summary>
    public const int LogoLength = 156;
    /// <summary>The ROM offset of the ARM entry stub the header branch targets.</summary>
    public const int EntryStubOffset = 0x0C0;
    /// <summary>The ROM offset the Thumb routine is placed at (right after the two entry-stub ARM words).</summary>
    public const int CodeOffset = 0x0C8;
    /// <summary>The ROM offset the data blob is placed at.</summary>
    public const int DataOffset = 0xC000;
    /// <summary>The bus address of the cartridge base.</summary>
    public const uint RomAddress = 0x08000000u;
    /// <summary>The bus address the Thumb routine executes from — pass it to
    /// <see cref="ThumbEmitter.ToArray"/> as the base address.</summary>
    public const uint CodeAddress = (RomAddress + CodeOffset);
    /// <summary>The bus address of the data window (reference baked data from Thumb via
    /// <see cref="ThumbEmitter.LoadConstant"/>).</summary>
    public const uint DataAddress = (RomAddress + DataOffset);
    /// <summary>The code window's capacity in bytes.</summary>
    public const int MaxRoutineBytes = (DataOffset - CodeOffset);
    /// <summary>The data window's capacity in bytes.</summary>
    public const int MaxDataBytes = (RomSize - DataOffset);

    /// <summary>Assembles a complete direct-boot-valid cartridge.</summary>
    /// <param name="title">The header title (≤ 12 ASCII characters, upper-cased into the 12-byte field).</param>
    /// <param name="gameCode">The 4-character game code.</param>
    /// <param name="routine">The Thumb machine code (emit it with base address <see cref="CodeAddress"/>).</param>
    /// <param name="data">The baked data blob, placed at <see cref="DataAddress"/> (empty is valid).</param>
    /// <param name="logo">The 156-byte logo bitmap for a retail-BIOS boot, or empty to zero the field (direct
    /// boot never reads it).</param>
    /// <returns>The 64 KiB ROM image.</returns>
    public static byte[] Build(string title, string gameCode, byte[] routine, byte[] data, ReadOnlySpan<byte> logo = default) {
        ArgumentException.ThrowIfNullOrEmpty(argument: title);
        ArgumentNullException.ThrowIfNull(argument: routine);
        ArgumentNullException.ThrowIfNull(argument: data);

        if ((title.Length > 12)) {
            throw new ArgumentException(message: $"The title '{title}' is over the 12-byte header field.", paramName: nameof(title));
        }

        if ((gameCode is null) || (gameCode.Length != 4)) {
            throw new ArgumentException(message: "The game code is exactly 4 characters.", paramName: nameof(gameCode));
        }

        if (routine.Length > MaxRoutineBytes) {
            throw new ArgumentException(message: $"The routine is {routine.Length} bytes, over the {MaxRoutineBytes}-byte code window (0x{CodeOffset:X4}..0x{DataOffset:X4}).", paramName: nameof(routine));
        }

        if (data.Length > MaxDataBytes) {
            throw new ArgumentException(message: $"The data blob is {data.Length} bytes, over the {MaxDataBytes}-byte data window.", paramName: nameof(data));
        }

        if ((!logo.IsEmpty) && (logo.Length != LogoLength)) {
            throw new ArgumentException(message: $"A supplied logo must be exactly {LogoLength} bytes.", paramName: nameof(logo));
        }

        var rom = new byte[RomSize];

        WriteWord(rom: rom, offset: 0x000, value: ArmWords.Branch(fromAddress: RomAddress, toAddress: (RomAddress + EntryStubOffset)));

        if (!logo.IsEmpty) {
            logo.CopyTo(destination: rom.AsSpan(length: LogoLength, start: 0x004));
        }

        WriteHeaderText(gameCode: gameCode, rom: rom, title: title);

        rom[0x0B2] = 0x96;
        rom[0x0BD] = ComputeComplementChecksum(rom: rom);

        WriteWord(offset: EntryStubOffset, rom: rom, value: ArmWords.AddR0PcOne);
        WriteWord(offset: (EntryStubOffset + 4), rom: rom, value: ArmWords.BranchExchangeR0);

        routine.CopyTo(array: rom, index: CodeOffset);
        data.CopyTo(array: rom, index: DataOffset);

        return rom;
    }

    private static void WriteHeaderText(byte[] rom, string title, string gameCode) {
        var titleBytes = Encoding.ASCII.GetBytes(s: title.ToUpperInvariant());

        titleBytes.CopyTo(array: rom, index: 0x0A0);
        Encoding.ASCII.GetBytes(s: gameCode).CopyTo(array: rom, index: 0x0AC);
    }
    private static byte ComputeComplementChecksum(byte[] rom) {
        var sum = 0;

        for (var offset = 0x0A0; (offset <= 0x0BC); offset++) {
            sum += rom[offset];
        }

        return ((byte)(((0 - sum) - 0x19) & 0xFF));
    }
    private static void WriteWord(byte[] rom, int offset, uint value) {
        rom[offset] = ((byte)(value & 0xFF));
        rom[(offset + 1)] = ((byte)((value >> 8) & 0xFF));
        rom[(offset + 2)] = ((byte)((value >> 16) & 0xFF));
        rom[(offset + 3)] = ((byte)((value >> 24) & 0xFF));
    }
}
