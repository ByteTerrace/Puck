using System.Text;

namespace Puck.GameBoyAdvance;

/// <summary>
/// A Game Boy Advance cartridge: the up-to-32&#160;MiB ROM plus its detected non-volatile backup. The backup
/// type is sniffed from the identifier string the developer libraries embed (e.g. <c>SRAM_V</c>,
/// <c>FLASH1M_V</c>, <c>EEPROM_V</c>), the same heuristic real flash carts and emulators use.
/// </summary>
public sealed class GbaCartridge {
    private static readonly byte[] s_flash1M = Encoding.ASCII.GetBytes(s: "FLASH1M_V");
    private static readonly byte[] s_flash512 = Encoding.ASCII.GetBytes(s: "FLASH512_V");
    private static readonly byte[] s_flash = Encoding.ASCII.GetBytes(s: "FLASH_V");
    private static readonly byte[] s_eeprom = Encoding.ASCII.GetBytes(s: "EEPROM_V");
    private static readonly byte[] s_sram = Encoding.ASCII.GetBytes(s: "SRAM_V");

    private readonly byte[] m_rom;
    private readonly byte[] m_save;

    /// <summary>Creates a cartridge from a ROM image, detecting and allocating its save backup.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public GbaCartridge(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        m_rom = rom;
        Backup = Detect(rom: rom);
        m_save = new byte[BackupSize(backup: Backup)];

        // Flash and (uninitialised) SRAM read as 0xFF on real hardware.
        Array.Fill(array: m_save, value: (byte)0xFF);
    }

    /// <summary>Gets the detected save backup type.</summary>
    public CartridgeBackup Backup { get; }

    /// <summary>Gets the ROM image.</summary>
    public ReadOnlySpan<byte> Rom => m_rom;

    /// <summary>Gets the ROM image length in bytes.</summary>
    public int RomLength => m_rom.Length;

    /// <summary>Reads a ROM byte at <paramref name="offset"/>, or the open-bus pattern beyond the image.</summary>
    /// <param name="offset">The byte offset into the cartridge ROM address space (0–0x01FFFFFF).</param>
    /// <returns>The ROM byte, or the open-bus value for out-of-range offsets.</returns>
    public byte ReadRom(uint offset) {
        if (offset < (uint)m_rom.Length) {
            return m_rom[offset];
        }

        // Out-of-range game-pak reads return the low byte of (address / 2) — the open-bus pattern.
        var halfword = (offset >> 1) & 0xFFFFu;

        return (byte)((offset & 1u) == 0u ? halfword : (halfword >> 8));
    }

    /// <summary>Reads a byte from the SRAM/flash save region.</summary>
    /// <param name="address">The address within the save region.</param>
    /// <returns>The stored byte, or 0xFF when the cartridge has no save backup.</returns>
    public byte ReadSave(uint address) {
        // TODO: model the flash command/bank state machine and the EEPROM serial protocol; linear access for now.
        return (m_save.Length == 0)
            ? (byte)0xFF
            : m_save[address & (uint)(m_save.Length - 1)];
    }

    /// <summary>Writes a byte to the SRAM/flash save region.</summary>
    /// <param name="address">The address within the save region.</param>
    /// <param name="value">The byte to store.</param>
    public void WriteSave(uint address, byte value) {
        if (m_save.Length == 0) {
            return;
        }

        m_save[address & (uint)(m_save.Length - 1)] = value;
    }

    private static CartridgeBackup Detect(byte[] rom) {
        ReadOnlySpan<byte> span = rom;

        if (Contains(haystack: span, needle: s_flash1M)) {
            return CartridgeBackup.Flash128;
        }

        if (Contains(haystack: span, needle: s_flash512) || Contains(haystack: span, needle: s_flash)) {
            return CartridgeBackup.Flash64;
        }

        if (Contains(haystack: span, needle: s_eeprom)) {
            return CartridgeBackup.Eeprom;
        }

        if (Contains(haystack: span, needle: s_sram)) {
            return CartridgeBackup.Sram;
        }

        return CartridgeBackup.None;
    }

    private static int BackupSize(CartridgeBackup backup) => backup switch {
        CartridgeBackup.Sram => 32 * 1024,
        CartridgeBackup.Flash64 => 64 * 1024,
        CartridgeBackup.Flash128 => 128 * 1024,
        CartridgeBackup.Eeprom => 8 * 1024,
        _ => 0,
    };

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(value: needle) >= 0;
}
