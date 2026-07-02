using System.Text;

namespace Puck.AdvancedGamingBrick;

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
    private static readonly byte[] s_rtc = Encoding.ASCII.GetBytes(s: "SIIRTC_V");

    // Flash software-ID codes returned in identification mode (after a 0x90 command). Pokémon Emerald reads these
    // at boot to detect its backup: Sanyo for the 1&#160;Mbit part, Panasonic for the 512&#160;Kbit part.
    private const int FlashIdSanyo1M = 0x1362;
    private const int FlashIdPanasonic512 = 0x1B32;

    private readonly byte[] m_rom;
    private readonly byte[] m_save;
    private readonly bool m_isFlash;
    private readonly bool m_isEeprom;

    // Serial EEPROM (accessed at 0x0D… over a 1-bit bus, driven by DMA). The bus width — 6 address bits
    // (512 B / 64 blocks) or 14 (8 KiB / 1024 blocks) — is auto-detected from the first command's length, the
    // same way real carts behave. Each block is 64 bits (8 bytes), shifted MSB-first.
    private int m_eepromAddressBits;            // 0 = not yet detected
    private readonly byte[] m_eepromCommand = new byte[128];
    private int m_eepromCommandLength;
    private ulong m_eepromReadData;
    private int m_eepromReadBitsRemaining;       // >0 while shifting a read reply out (68 bits: 4 dummy + 64 data)

    // Flash command state machine: the unlock phase (0 raw / 1 started / 2 continue), the pending command byte
    // (0x80 erase, 0x90 id, 0xA0 program, 0xB0 bank-switch; 0 when idle), and the selected 64&#160;KiB bank.
    private int m_flashPhase;
    private byte m_flashCommand;
    private int m_flashBank;

    // Cartridge GPIO (overlaid on ROM at 0x0C4/0x0C6/0x0C8) and the Seiko S-3511A real-time clock wired to it:
    // pin bit 0 = SCK, 1 = SIO, 2 = CS. Present only when the ROM embeds the SIIRTC_V library (Pokémon Emerald).
    private static readonly int[] s_rtcBytes = { 0, 0, 7, 0, 1, 0, 3, 0 };

    private readonly bool m_hasRtc;
    private readonly byte[] m_rtcTime = new byte[7];

    private int m_gpioPins;
    private int m_gpioDirection;
    private bool m_gpioReadable;

    private Func<long>? m_cycleProvider;

    private bool m_rtcSckEdge;
    private bool m_rtcCommandActive;
    private bool m_rtcSioOutput;
    private int m_rtcCommand;
    private int m_rtcBits;
    private int m_rtcBitsRead;
    private int m_rtcBytesRemaining;
    private byte m_rtcControl;

    /// <summary>Creates a cartridge from a ROM image, detecting and allocating its save backup.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public GbaCartridge(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        m_rom = rom;
        Backup = Detect(rom: rom);
        m_isFlash = Backup is CartridgeBackup.Flash64 or CartridgeBackup.Flash128;
        m_isEeprom = Backup is CartridgeBackup.Eeprom;
        m_save = new byte[BackupSize(backup: Backup)];

        // Flash and (uninitialised) SRAM read as 0xFF on real hardware.
        Array.Fill(array: m_save, value: (byte)0xFF);

        // Diagnostic override (PUCK_GBA_NO_RTC=1): force the GPIO/RTC off to isolate whether an RTC-protocol issue
        // is what stalls a game's boot, vs an engine-timing issue.
        m_hasRtc = Contains(haystack: rom, needle: s_rtc)
            && (Environment.GetEnvironmentVariable(variable: "PUCK_GBA_NO_RTC") != "1");
        m_rtcControl = 0x40; // 24-hour mode
        InitRtcTime();
    }

    /// <summary>Sets the cycle-count provider used by the RTC to advance wall time from emulated cycles.</summary>
    public void SetCycleProvider(Func<long> provider) {
        m_cycleProvider = provider;
    }

    /// <summary>Gets a value indicating whether the cartridge exposes a GPIO real-time clock (e.g. Pokémon
    /// Emerald). When set, the bus routes ROM accesses at 0x0C4–0x0C8 to <see cref="ReadGpio"/>/<see cref="WriteGpio"/>.</summary>
    public bool HasRtc => m_hasRtc;

    /// <summary>Gets the detected save backup type.</summary>
    public CartridgeBackup Backup { get; }

    /// <summary>Gets the ROM image.</summary>
    public ReadOnlySpan<byte> Rom => m_rom;

    /// <summary>Gets the ROM image length in bytes.</summary>
    public int RomLength => m_rom.Length;

    /// <summary>Gets a value indicating whether the cartridge has a writable save backup (SRAM/Flash/EEPROM).</summary>
    public bool HasSave => m_save.Length > 0;

    /// <summary>Gets a value indicating whether the save backup has been written since it was last loaded or
    /// persisted. A host watches this to decide when to flush the save to disk, then calls <see cref="MarkSaveClean"/>.</summary>
    public bool SaveDirty { get; private set; }

    /// <summary>Exports the raw save backup (SRAM/Flash/EEPROM contents) for persistence to a <c>.sav</c> file.</summary>
    public ReadOnlySpan<byte> SaveData => m_save;

    /// <summary>Loads a previously persisted save backup (typically a <c>.sav</c> file's contents). The length must
    /// match the detected backup size; a mismatch is rejected so a stale/wrong save can't corrupt the backup.</summary>
    /// <param name="data">The save bytes to load.</param>
    /// <returns><see langword="true"/> if loaded; <see langword="false"/> if the length did not match the backup.</returns>
    public bool LoadSave(ReadOnlySpan<byte> data) {
        if ((m_save.Length == 0) || (data.Length != m_save.Length)) {
            return false;
        }

        data.CopyTo(destination: m_save);
        SaveDirty = false;

        return true;
    }

    /// <summary>Clears <see cref="SaveDirty"/> after a host has persisted the save to disk.</summary>
    public void MarkSaveClean() => SaveDirty = false;

    // Game-pak burst page counter (ARES gba/cartridge mrom). The 16-bit cartridge bus latches the low address lines
    // on a non-sequential access (startBurst) and auto-increments per sequential half-word — so during a burst the
    // cartridge supplies consecutive half-words regardless of the requested low address. A burst ends at the last
    // half-word of a 128 KiB page (0x1FFFE). Sequential code/data still resolves to the requested address (the
    // counter tracks it); the visible effect is that a fixed/decrement-source DMA reads the auto-incrementing data.
    private uint m_romBurstPage;
    private bool m_romBurst;

    /// <summary>Reads a half-word through the game-pak burst page counter. <paramref name="sequential"/> continues an
    /// in-progress burst; otherwise a new burst is latched at <paramref name="address"/>.</summary>
    public ushort ReadRomBurst(uint address, bool sequential) {
        if (!sequential || !m_romBurst) {
            m_romBurstPage = address >> 1; // startBurst: latch the half-word address
            m_romBurst = true;
        }

        if ((address & 0x1FFFEu) == 0x1FFFEu) {
            m_romBurst = false; // burst ends at the last half-word of the page
        }

        var romAddr = ((address >> 17) << 17) | (m_romBurstPage << 1);

        ++m_romBurstPage;

        return (ushort)(ReadRom(offset: romAddr) | (ReadRom(offset: romAddr + 1u) << 8));
    }

    /// <summary>Reads a ROM byte at <paramref name="offset"/>, or the open-bus pattern beyond the image.</summary>
    /// <param name="offset">The byte offset into the cartridge ROM address space (0–0x01FFFFFF).</param>
    /// <returns>The ROM byte, or the open-bus value for out-of-range offsets.</returns>
    public byte ReadRom(uint offset) {
        // The GPIO/RTC registers overlay the ROM at 0x0C4–0x0C9; reads there return the live pin state.
        if (m_hasRtc && (offset >= 0xC4u) && (offset <= 0xC9u)) {
            var value = ReadGpio(register: offset & ~1u);

            return (byte)(((offset & 1u) == 0u) ? value : (value >> 8));
        }

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
        if (m_save.Length == 0) {
            return 0xFF;
        }

        if (m_isFlash) {
            var offset = address & 0xFFFFu;

            // In identification mode (entered by a 0x90 command) the chip returns its software-ID code at the
            // first two addresses instead of stored data.
            if ((m_flashCommand == 0x90) && (offset < 2u)) {
                var id = (Backup == CartridgeBackup.Flash128) ? FlashIdSanyo1M : FlashIdPanasonic512;

                return (byte)(id >> ((int)offset * 8));
            }

            return m_save[FlashOffset(offset: offset)];
        }

        return m_save[address & (uint)(m_save.Length - 1)];
    }

    /// <summary>Writes a byte to the SRAM/flash save region.</summary>
    /// <param name="address">The address within the save region.</param>
    /// <param name="value">The byte to store.</param>
    public void WriteSave(uint address, byte value) {
        if (m_save.Length == 0) {
            return;
        }

        if (m_isFlash) {
            WriteFlash(address: address & 0xFFFFu, value: value);

            return;
        }

        m_save[address & (uint)(m_save.Length - 1)] = value;
        SaveDirty = true;
    }

    /// <summary>Gets a value indicating whether the cartridge's backup is a serial EEPROM (accessed at 0x0D…).</summary>
    public bool IsEeprom => m_isEeprom;

    /// <summary>Shifts one bit out of the serial EEPROM (the value a DMA read from the EEPROM region returns in
    /// bit 0). Returns 1 when idle/ready. The first read after a command finalises that command.</summary>
    public ushort ReadEeprom() {
        if ((m_eepromReadBitsRemaining == 0) && (m_eepromCommandLength > 0)) {
            FinalizeEepromCommand();
        }

        if (m_eepromReadBitsRemaining > 0) {
            // A read reply is 68 bits: 4 leading zeros then the 64 data bits, MSB first.
            var position = 68 - m_eepromReadBitsRemaining;
            --m_eepromReadBitsRemaining;

            if (position < 4) {
                return 0;
            }

            var dataBit = 63 - (position - 4);

            return (ushort)((m_eepromReadData >> dataBit) & 1u);
        }

        return 1; // ready
    }

    /// <summary>Shifts one command bit into the serial EEPROM (bit 0 of a DMA write to the EEPROM region).</summary>
    public void WriteEeprom(ushort value) {
        // While a read reply is being shifted out, further writes are ignored. Otherwise accumulate the command
        // bit; the command is interpreted on the next read (read setup) or its trailing status read (write).
        if (m_eepromReadBitsRemaining > 0) {
            return;
        }

        if (m_eepromCommandLength < m_eepromCommand.Length) {
            m_eepromCommand[m_eepromCommandLength++] = (byte)(value & 1u);
        }
    }

    private void FinalizeEepromCommand() {
        var length = m_eepromCommandLength;

        m_eepromCommandLength = 0;

        if (length < 3) {
            return;
        }

        var command = (m_eepromCommand[0] << 1) | m_eepromCommand[1];

        // Auto-detect the address bus width from the command length the first time: a read setup is
        // 2 (command) + addr + 1 (stop) bits; a write is 2 + addr + 64 + 1.
        if (m_eepromAddressBits == 0) {
            var detected = (command == 0b11) ? (length - 3) : (length - 67);

            m_eepromAddressBits = detected switch {
                <= 6 => 6,
                _ => 14,
            };
        }

        var addressBits = m_eepromAddressBits;
        var address = 0;

        for (var i = 0; i < addressBits; ++i) {
            address = (address << 1) | m_eepromCommand[2 + i];
        }

        var blockOffset = (address * 8) & (m_save.Length - 1);

        if (command == 0b11) {
            // Read: latch the 64-bit block, then shift it out (with 4 leading dummy bits) on subsequent reads.
            ulong data = 0;

            for (var i = 0; i < 8; ++i) {
                data = (data << 8) | m_save[blockOffset + i];
            }

            m_eepromReadData = data;
            m_eepromReadBitsRemaining = 68;
        }
        else if (command == 0b10) {
            // Write: bits after the address are the 64 data bits (MSB first); store them, then signal ready.
            ulong data = 0;
            var dataStart = 2 + addressBits;

            for (var i = 0; i < 64; ++i) {
                var bit = ((dataStart + i) < length) ? m_eepromCommand[dataStart + i] : 0;

                data = (data << 1) | (uint)bit;
            }

            for (var i = 0; i < 8; ++i) {
                m_save[blockOffset + i] = (byte)(data >> (56 - (i * 8)));
            }

            SaveDirty = true;
        }
    }

    // Drives the flash command/unlock state machine for a byte write. Commands are issued as a three-write
    // sequence — 0xAA to 0x5555, 0x55 to 0x2AAA, then the command byte to 0x5555 — except the byte-program and
    // bank-switch payloads, which land in the raw state immediately after their command is latched.
    private void WriteFlash(uint address, byte value) {
        switch (m_flashPhase) {
            case 0: // Raw: either a deferred command payload or the start of a new unlock sequence.
                if (m_flashCommand == 0xA0) {
                    m_save[FlashOffset(offset: address)] = value;
                    m_flashCommand = 0;
                    SaveDirty = true;

                    return;
                }

                if (m_flashCommand == 0xB0) {
                    if ((address == 0u) && (value < 2)) {
                        m_flashBank = value;
                    }

                    m_flashCommand = 0;

                    return;
                }

                if ((address == 0x5555u) && (value == 0xAA)) {
                    m_flashPhase = 1;
                }

                return;
            case 1: // Started: expect 0x55 to 0x2AAA.
                m_flashPhase = ((address == 0x2AAAu) && (value == 0x55)) ? 2 : 0;

                return;
            default: // Continue: the command byte (or an erase target).
                m_flashPhase = 0;

                if (address == 0x5555u) {
                    if (m_flashCommand == 0) {
                        if (value is 0x80 or 0x90 or 0xA0 or 0xB0) {
                            m_flashCommand = value;
                        }
                    }
                    else if (m_flashCommand == 0x80) {
                        if (value == 0x10) {
                            Array.Fill(array: m_save, value: (byte)0xFF); // chip erase
                            m_flashCommand = 0;
                            SaveDirty = true;
                        }
                    }
                    else if (m_flashCommand == 0x90) {
                        if (value == 0xF0) {
                            m_flashCommand = 0; // leave identification mode
                        }
                    }
                }
                else if ((m_flashCommand == 0x80) && (value == 0x30)) {
                    // Sector erase: clear the 4 KiB page containing the addressed byte in the current bank.
                    var sector = FlashOffset(offset: address) & ~0xFFFu;

                    Array.Fill(array: m_save, value: (byte)0xFF, startIndex: (int)sector, count: 0x1000);
                    m_flashCommand = 0;
                    SaveDirty = true;
                }

                return;
        }
    }

    // Maps a 16-bit flash address into the backing store through the currently selected bank (128 KiB parts only).
    private int FlashOffset(uint offset) => (m_flashBank << 16) | (int)offset;

    /// <summary>Reads a GPIO register (0x0C4 data, 0x0C6 direction, 0x0C8 control). Returns 0 when the control
    /// register has reads disabled, the same as real hardware (the ROM underneath reads back as written zero).</summary>
    /// <param name="register">The aligned register offset (0x0C4, 0x0C6, or 0x0C8).</param>
    /// <returns>The 16-bit register value.</returns>
    public ushort ReadGpio(uint register) {
        if (!m_gpioReadable) {
            return 0;
        }

        return register switch {
            0xC4u => (ushort)(m_gpioPins & 0xF),
            0xC6u => (ushort)(m_gpioDirection & 0xF),
            0xC8u => (ushort)(m_gpioReadable ? 1u : 0u),
            _ => 0,
        };
    }

    /// <summary>Writes a GPIO register, driving the RTC serial protocol when the data register changes.</summary>
    /// <param name="register">The aligned register offset (0x0C4, 0x0C6, or 0x0C8).</param>
    /// <param name="value">The 16-bit value to write.</param>
    public void WriteGpio(uint register, ushort value) {
        switch (register) {
            case 0xC4u:
                // The game drives the pins it has configured as outputs; inputs keep the RTC-driven value.
                m_gpioPins = (value & m_gpioDirection) | (m_gpioPins & ~m_gpioDirection) & 0xF;
                StepRtc();

                break;
            case 0xC6u:
                m_gpioDirection = value & 0xF;

                break;
            case 0xC8u:
                m_gpioReadable = (value & 1) != 0;

                break;
        }
    }

    // Drives the RTC input pins (those the game reads) to the supplied value, leaving the game's output pins.
    private void DriveRtc(int pins) {
        m_gpioPins = (m_gpioPins & m_gpioDirection) | (pins & ~m_gpioDirection) & 0xF;
    }

    // The S-3511A serial clock, evaluated on every data-register write. Ports mGBA's edge model exactly: command
    // bytes shift in on SCK rising edges (LSB first), read replies shift out on SCK falling edges.
    private void StepRtc() {
        var sck = m_gpioPins & 1;
        var sio = (m_gpioPins >> 1) & 1;
        var cs = (m_gpioPins >> 2) & 1;

        DriveRtc(pins: m_gpioPins & 2);

        if (cs == 0) {
            m_rtcBitsRead = 0;
            m_rtcBytesRemaining = 0;
            m_rtcCommandActive = false;
            m_rtcCommand = 0;
            m_rtcSckEdge = true;
            m_rtcSioOutput = true;
            DriveRtc(pins: 2);
            m_rtcSckEdge = sck != 0;

            return;
        }

        if (!m_rtcCommandActive) {
            DriveRtc(pins: 2);

            if (sck == 0) {
                m_rtcBits = (m_rtcBits & ~(1 << m_rtcBitsRead)) | (sio << m_rtcBitsRead);
            }

            if (!m_rtcSckEdge && (sck != 0)) {
                ++m_rtcBitsRead;

                if (m_rtcBitsRead == 8) {
                    BeginRtcCommand();
                }
            }
        }
        else if ((m_rtcCommand & 0x80) == 0) {
            // Write command: clock the parameter byte in.
            DriveRtc(pins: 2);

            if (sck == 0) {
                m_rtcBits = (m_rtcBits & ~(1 << m_rtcBitsRead)) | (sio << m_rtcBitsRead);
            }

            if (!m_rtcSckEdge && (sck != 0)) {
                ++m_rtcBitsRead;

                if (m_rtcBitsRead == 8) {
                    ProcessRtcByte();
                }
            }
        }
        else {
            // Read command: shift the reply out on falling edges.
            if (m_rtcSckEdge && (sck == 0)) {
                m_rtcSioOutput = RtcOutputBit();
                ++m_rtcBitsRead;

                if (m_rtcBitsRead == 8) {
                    --m_rtcBytesRemaining;

                    if (m_rtcBytesRemaining <= 0) {
                        m_rtcBytesRemaining = s_rtcBytes[(m_rtcCommand >> 4) & 0x7];
                    }

                    m_rtcBitsRead = 0;
                }
            }

            DriveRtc(pins: (m_rtcSioOutput ? 1 : 0) << 1);
        }

        m_rtcSckEdge = sck != 0;
    }

    private void BeginRtcCommand() {
        // The command byte is valid only with the 0110 magic in its low nibble.
        if ((m_rtcBits & 0xF) == 0x6) {
            m_rtcCommand = m_rtcBits;
            m_rtcBytesRemaining = s_rtcBytes[(m_rtcBits >> 4) & 0x7];
            m_rtcCommandActive = true;

            switch ((m_rtcBits >> 4) & 0x7) {
                case 0: // reset
                    m_rtcControl = 0;

                    break;
                case 2: // date+time
                case 6: // time
                    InitRtcTime();

                    break;
            }
        }

        m_rtcBits = 0;
        m_rtcBitsRead = 0;
    }

    private void ProcessRtcByte() {
        // The only writable register is control; the rest accept and ignore their parameter bytes.
        if (((m_rtcCommand >> 4) & 0x7) == 4) {
            m_rtcControl = (byte)m_rtcBits;
        }

        m_rtcBits = 0;
        m_rtcBitsRead = 0;
        --m_rtcBytesRemaining;

        if (m_rtcBytesRemaining <= 0) {
            m_rtcBytesRemaining = s_rtcBytes[(m_rtcCommand >> 4) & 0x7];
        }
    }

    private bool RtcOutputBit() {
        var output = ((m_rtcCommand >> 4) & 0x7) switch {
            4 => m_rtcControl,                       // control register
            2 or 6 => m_rtcTime[7 - m_rtcBytesRemaining], // date+time / time bytes
            _ => (byte)0xFF,
        };

        return ((output >> m_rtcBitsRead) & 1) != 0;
    }

    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));

    // Computes the current RTC time from a fixed epoch plus elapsed emulated cycles. The GBA master clock is
    // 16,780,000 Hz, so seconds = cycles / 16_780_000. A fixed epoch keeps the conformance harness reproducible
    // while still advancing the clock at the correct emulated rate.
    private void InitRtcTime() {
        var epoch = new DateTime(year: 2026, month: 6, day: 23, hour: 12, minute: 0, second: 0);

        if (m_cycleProvider is not null) {
            var elapsedSeconds = m_cycleProvider() / 16_780_000L;

            epoch = epoch.AddSeconds(value: elapsedSeconds);
        }

        m_rtcTime[0] = ToBcd(epoch.Year - 2000);
        m_rtcTime[1] = ToBcd(epoch.Month);
        m_rtcTime[2] = ToBcd(epoch.Day);
        m_rtcTime[3] = ToBcd((int)epoch.DayOfWeek);
        m_rtcTime[4] = ToBcd(epoch.Hour);
        m_rtcTime[5] = ToBcd(epoch.Minute);
        m_rtcTime[6] = ToBcd(epoch.Second);
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
