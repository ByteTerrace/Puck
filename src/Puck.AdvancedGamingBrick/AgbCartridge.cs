using System.Text;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// An Advanced GamingBrick cartridge: the up-to-32&#160;MiB ROM plus its detected non-volatile backup. The backup
/// type is sniffed from the identifier string the developer libraries embed (e.g. <c>SRAM_V</c>,
/// <c>FLASH1M_V</c>, <c>EEPROM_V</c>), the same heuristic real flash carts and emulators use.
/// </summary>
public sealed partial class AgbCartridge {
    private static readonly byte[] s_flash1M = Encoding.ASCII.GetBytes(s: "FLASH1M_V");
    private static readonly byte[] s_flash512 = Encoding.ASCII.GetBytes(s: "FLASH512_V");
    private static readonly byte[] s_flash = Encoding.ASCII.GetBytes(s: "FLASH_V");
    private static readonly byte[] s_eeprom = Encoding.ASCII.GetBytes(s: "EEPROM_V");
    private static readonly byte[] s_sram = Encoding.ASCII.GetBytes(s: "SRAM_V");
    private static readonly byte[] s_rtc = Encoding.ASCII.GetBytes(s: "SIIRTC_V");

    // Flash software-ID codes returned in identification mode (after a 0x90 command). Some commercial games read these
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

    // Cartridge GPIO (overlaid on ROM at 0x0C4/0x0C6/0x0C8) — one shared 4-pin overlay every modeled sensor multiplexes
    // onto: a read fans out to whichever devices are present; each only touches its own pins, so co-resident devices —
    // a solar-sensor cart wires BOTH RTC and the light sensor — never conflict. Present only when the header game code
    // is keyed in AgbGameOverrides for at least one device.
    private readonly bool m_hasGpio;

    // The Seiko S-3511A real-time clock: pin bit 0 = SCK, 1 = SIO, 2 = CS. Present only when the ROM embeds the
    // SIIRTC_V library or the game code is keyed HasRtc.
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

    // The rumble motor: pin bit 3, driven BY the game (direction bit 3 set) as a direct on/off line — no shift
    // protocol, just a raw sample of pin-state bit 3 with no edge detection. Present only when the game code is keyed
    // HasRumble.
    private readonly bool m_hasRumble;
    private bool m_rumbleMotorOn;

    // The light sensor: pin bit 0 = CLK, bit 1 = RESET, bit 2 = chip-select (active LOW — the sensor is silent while
    // high), bit 3 = the device's serial output. RESET samples a fresh light-level reading and zeroes the counter;
    // each CLK rising edge while not reset advances the counter; the output bit is set once the counter reaches the
    // reading (so a brighter reading — a LOWER threshold — trips sooner). Present only when the game code is keyed
    // HasSolar (solar-sensor carts).
    private readonly bool m_hasSolar;
    private int m_lightCounter;
    private bool m_lightEdge;
    // The current reading, 0 (brightest — trips on the very first CLK edge) to 255 (darkest — needs a full 255-edge
    // count). Recorded per-segment host input (AdvancedGamingBrickCore.ApplyInput → SetLightLevel), never a live
    // sample: holding it constant for a whole segment keeps a GPIO poll mid-segment replay-deterministic.
    private byte m_lightThreshold = 255;

    // The address-mapped tilt sensor — a SEPARATE overlay from the 0x0C4-0x0C9 GPIO block, at fixed ROM offsets.
    // 0x8000<-0x55 arms a capture; 0x8100<-0xAA (only while armed) latches the CURRENT recorded tilt into the X/Y
    // registers reads at 0x8200-0x8500 return, mirroring the accelerometer's erase-then-latch shape (Mbc7Cartridge in
    // the Humble core). Present only when the game code is keyed HasTilt.
    private const int TiltCenter = 0x3A0;   // the reading a motionless cartridge latches (the sensor's rest window)
    private const int TiltRange = 0x1A0;    // the raw swing one full-deflection host sample maps to
    private readonly bool m_hasTilt;
    private int m_tiltState;                // 0 idle, 1 armed (0x55 written to 0x8000, awaiting 0xAA at 0x8100)
    private int m_tiltLiveX = TiltCenter;    // the current recorded host sample (SetTilt), not yet latched
    private int m_tiltLiveY = TiltCenter;
    private int m_tiltX = TiltCenter;        // the latched reading the 0x8200-0x8500 registers expose
    private int m_tiltY = TiltCenter;

    /// <summary>Creates a cartridge from a ROM image, detecting and allocating its save backup.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public AgbCartridge(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        m_rom = rom;

        // The per-game override table (keyed by the header game code) corrects the cases the string scan gets wrong,
        // BEFORE the string-scan fallback. A null field defers to the scan.
        var over = AgbGameOverrides.Lookup(rom: rom);

        Backup = (over?.Backup ?? Detect(rom: rom));
        m_isFlash = (Backup is CartridgeBackup.Flash64 or CartridgeBackup.Flash128);
        m_isEeprom = (Backup is CartridgeBackup.Eeprom);
        m_save = new byte[BackupSize(backup: Backup)];

        // Flash and (uninitialised) SRAM read as 0xFF on real hardware.
        Array.Fill(array: m_save, value: (byte)0xFF);

        // RTC presence: the override wins, else the SIIRTC_V string scan. Diagnostic override (PUCK_AGB_NO_RTC=1)
        // forces the GPIO/RTC off, to isolate whether an RTC-protocol issue stalls a boot vs an engine-timing issue.
        m_hasRtc = ((over?.HasRtc ?? Contains(haystack: rom, needle: s_rtc))
            && (Environment.GetEnvironmentVariable(variable: "PUCK_AGB_NO_RTC") != "1"));
        m_rtcControl = 0x40; // 24-hour mode
        InitRtcTime();

        // Rumble/solar/tilt presence: override-table-only (no reliable string signature for any of the three, unlike
        // RTC's SIIRTC_V) — the same reason the override table exists at all for these devices.
        m_hasRumble = (over?.HasRumble ?? false);
        m_hasSolar = (over?.HasSolar ?? false);
        m_hasGpio = (m_hasRtc || m_hasRumble || m_hasSolar);
        m_hasTilt = (over?.HasTilt ?? false);
    }

    /// <summary>Sets the cycle-count provider used by the RTC to advance wall time from emulated cycles.</summary>
    public void SetCycleProvider(Func<long> provider) {
        m_cycleProvider = provider;
    }

    /// <summary>Gets a value indicating whether the cartridge exposes a GPIO real-time clock (as some commercial
    /// games do). When set, the bus routes ROM accesses at 0x0C4–0x0C8 to <see cref="ReadGpio"/>/<see cref="WriteGpio"/>.</summary>
    public bool HasRtc => m_hasRtc;

    /// <summary>Gets a value indicating whether the cartridge overlays ANY GPIO device (RTC, rumble, or solar) on ROM
    /// at 0x0C4–0x0C8 — the bus's single overlay-routing gate, since several devices can share the overlay on one
    /// cartridge (a solar-sensor cart wires both RTC and the light sensor).</summary>
    public bool HasGpio => m_hasGpio;

    /// <summary>Gets a value indicating whether the cartridge exposes the GPIO light sensor.</summary>
    public bool HasSolar => m_hasSolar;

    /// <summary>Gets a value indicating whether the cartridge exposes the address-mapped tilt sensor at ROM offsets
    /// <c>0x8000</c>-<c>0x8500</c>.</summary>
    public bool HasTilt => m_hasTilt;

    /// <summary>Gets the cartridge's current rumble motor level, 0..1 — the neutral host-facing feedback surface's
    /// source. The GPIO rumble line is on/off only (no PWM), so this is always exactly 0 or 1; 0 on a cartridge with
    /// no rumble hardware.</summary>
    public float MotorLevel => (m_hasRumble && m_rumbleMotorOn) ? 1f : 0f;

    /// <summary>Sets the current light-level reading for the solar sensor, 0 (brightest) to 255
    /// (darkest) — recorded per-segment host input (the core adapter's ApplyInput), never a live read from inside the
    /// core: the value stays constant for the whole segment, so a GPIO poll mid-segment replays bit-identically. A
    /// no-op on a cartridge with no solar sensor.</summary>
    /// <param name="level">The reading: 0 trips the sensor's counter on the very first CLK edge (full sun); 255 never
    /// trips it within one 8-bit counter sweep (darkness).</param>
    public void SetLightLevel(byte level) {
        if (m_hasSolar) {
            m_lightThreshold = (byte)(255 - level);
        }
    }

    /// <summary>Sets the current tilt reading for the address-mapped tilt sensor, each axis -1..1 — recorded
    /// per-segment host input (the core adapter's ApplyInput), never a live read from inside the core. Only takes
    /// effect once the game's own arm-then-latch sequence (0x55 at ROM offset 0x8000, then 0xAA at 0x8100) captures
    /// it, mirroring the hardware's own capture protocol. A no-op on a cartridge with no tilt sensor.</summary>
    /// <param name="x">The horizontal tilt, -1..1.</param>
    /// <param name="y">The vertical tilt, -1..1.</param>
    public void SetTilt(float x, float y) {
        if (m_hasTilt) {
            m_tiltLiveX = (TiltCenter - (int)(Math.Clamp(value: x, min: -1f, max: 1f) * TiltRange));
            m_tiltLiveY = (TiltCenter - (int)(Math.Clamp(value: y, min: -1f, max: 1f) * TiltRange));
        }
    }

    /// <summary>Writes the tilt sensor's arm/latch handshake at ROM offset <c>0x8000</c> (arm, expects <c>0x55</c>) or
    /// <c>0x8100</c> (latch, expects <c>0xAA</c> while armed) — the ROM-overlaid write half of the address-mapped
    /// protocol. A no-op for any other offset or on a cartridge with no tilt sensor.</summary>
    /// <param name="offset">The ROM byte offset written.</param>
    /// <param name="value">The byte written.</param>
    public void WriteTilt(uint offset, byte value) {
        if (!m_hasTilt) {
            return;
        }

        if ((offset == 0x8000u) && (value == 0x55)) {
            m_tiltState = 1;
        } else if ((offset == 0x8100u) && (value == 0xAA) && (m_tiltState == 1)) {
            m_tiltState = 0;
            m_tiltX = m_tiltLiveX;
            m_tiltY = m_tiltLiveY;
        }
    }

    // Reads the tilt sensor's latched X/Y at ROM offsets 0x8200-0x8500 (byte-split, high byte OR 0x80 for X), or the
    // underlying ROM byte for any other offset in [0x8000, 0x8600).
    private byte ReadTilt(uint offset) => offset switch {
        0x8200u => (byte)m_tiltX,
        0x8300u => (byte)(((m_tiltX >> 8) & 0xF) | 0x80),
        0x8400u => (byte)m_tiltY,
        0x8500u => (byte)((m_tiltY >> 8) & 0xF),
        _ => (byte)((offset < (uint)m_rom.Length) ? m_rom[offset] : 0xFFu),
    };

    /// <summary>Gets the detected save backup type.</summary>
    public CartridgeBackup Backup { get; }

    /// <summary>Gets the ROM image.</summary>
    public ReadOnlySpan<byte> Rom => m_rom;

    /// <summary>Gets the ROM image length in bytes.</summary>
    public int RomLength => m_rom.Length;

    /// <summary>Gets a value indicating whether the cartridge has a writable save backup (SRAM/Flash/EEPROM).</summary>
    public bool HasSave => (m_save.Length > 0);

    /// <summary>Gets a value indicating whether the save backup has been written since it was last loaded or
    /// persisted. A host watches this to decide when to flush the save to disk, then calls <see cref="MarkSaveClean"/>.</summary>
    public bool SaveDirty { get; private set; }

    /// <summary>Exports the raw save backup (SRAM/Flash/EEPROM contents) for persistence to a <c>.sav</c> file.</summary>
    public ReadOnlySpan<byte> SaveData => m_save;

    /// <summary>Loads a previously persisted save backup (typically a <c>.sav</c> file's contents). The length must
    /// match the detected backup size; a mismatch is rejected so a stale/wrong save can't corrupt the backup.</summary>
    /// <param name="data">The save bytes to load.</param>
    /// <returns><see langword="true"/> if loaded; <see langword="false"/> if the image is empty or larger than the backup.</returns>
    public bool LoadSave(ReadOnlySpan<byte> data) {
        // Accept an undersized image as a prefix (a legacy 32 KiB SRAM .sav loads into the 64 KiB window); reject
        // an oversized one outright — it belongs to a different backup type.
        if ((m_save.Length == 0) || (data.Length == 0) || (data.Length > m_save.Length)) {
            return false;
        }

        data.CopyTo(destination: m_save);
        SaveDirty = false;

        return true;
    }

    /// <summary>Clears <see cref="SaveDirty"/> after a host has persisted the save to disk.</summary>
    public void MarkSaveClean() => SaveDirty = false;

    // Game-pak burst page counter (mask-ROM cartridge behavior). The 16-bit cartridge bus latches the low address lines
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
            m_romBurstPage = (address >> 1); // startBurst: latch the half-word address
            m_romBurst = true;
        }

        if ((address & 0x1FFFEu) == 0x1FFFEu) {
            m_romBurst = false; // burst ends at the last half-word of the page
        }

        var romAddr = ((address >> 17) << 17) | (m_romBurstPage << 1);

        ++m_romBurstPage;

        return (ushort)(ReadRom(offset: romAddr) | (ReadRom(offset: (romAddr + 1u)) << 8));
    }

    /// <summary>Reads a ROM byte at <paramref name="offset"/>, or the open-bus pattern beyond the image.</summary>
    /// <param name="offset">The byte offset into the cartridge ROM address space (0–0x01FFFFFF).</param>
    /// <returns>The ROM byte, or the open-bus value for out-of-range offsets.</returns>
    public byte ReadRom(uint offset) {
        // The GPIO registers overlay the ROM at 0x0C4–0x0C9; reads there return the live pin state.
        if (m_hasGpio && (offset >= 0xC4u) && (offset <= 0xC9u)) {
            var value = ReadGpio(register: offset & ~1u);

            return (byte)(((offset & 1u) == 0u) ? value : (value >> 8));
        }

        // The tilt sensor's latched X/Y overlay ROM at 0x8200-0x8500 (the write half, 0x8000/0x8100, has no read
        // meaning and falls through to ordinary ROM below).
        if (m_hasTilt && (offset >= 0x8200u) && (offset <= 0x8500u)) {
            return ReadTilt(offset: offset);
        }

        if (offset < (uint)m_rom.Length) {
            return m_rom[offset];
        }

        // Out-of-range game-pak reads return the low byte of (address / 2) — the open-bus pattern.
        var halfword = (offset >> 1) & 0xFFFFu;

        return (byte)(((offset & 1u) == 0u) ? halfword : (halfword >> 8));
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
                var id = ((Backup == CartridgeBackup.Flash128) ? FlashIdSanyo1M : FlashIdPanasonic512);

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
            var position = (68 - m_eepromReadBitsRemaining);

            --m_eepromReadBitsRemaining;

            if (position < 4) {
                return 0;
            }

            var dataBit = (63 - (position - 4));

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
            var detected = ((command == 0b11) ? (length - 3) : (length - 67));

            m_eepromAddressBits = detected switch {
                <= 6 => 6,
                _ => 14,
            };
        }

        var addressBits = m_eepromAddressBits;
        var address = 0;

        for (var i = 0; (i < addressBits); ++i) {
            address = (address << 1) | m_eepromCommand[(2 + i)];
        }

        var blockOffset = (address * 8) & (m_save.Length - 1);

        if (command == 0b11) {
            // Read: latch the 64-bit block, then shift it out (with 4 leading dummy bits) on subsequent reads.
            var data = 0UL;

            for (var i = 0; (i < 8); ++i) {
                data = (data << 8) | m_save[(blockOffset + i)];
            }

            m_eepromReadData = data;
            m_eepromReadBitsRemaining = 68;
        } else if (command == 0b10) {
            // Write: bits after the address are the 64 data bits (MSB first); store them, then signal ready.
            var data = 0UL;
            var dataStart = (2 + addressBits);

            for (var i = 0; (i < 64); ++i) {
                var bit = (((dataStart + i) < length) ? m_eepromCommand[(dataStart + i)] : 0);

                data = (data << 1) | (uint)bit;
            }

            for (var i = 0; (i < 8); ++i) {
                m_save[(blockOffset + i)] = (byte)(data >> (56 - (i * 8)));
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
                m_flashPhase = (((address == 0x2AAAu) && (value == 0x55)) ? 2 : 0);

                return;
            default: // Continue: the command byte (or an erase target).
                m_flashPhase = 0;

                if (address == 0x5555u) {
                    if (m_flashCommand == 0) {
                        if (value is 0x80 or 0x90 or 0xA0 or 0xB0) {
                            m_flashCommand = value;
                        }
                    } else if (m_flashCommand == 0x80) {
                        if (value == 0x10) {
                            Array.Fill(array: m_save, value: (byte)0xFF); // chip erase
                            m_flashCommand = 0;
                            SaveDirty = true;
                        }
                    } else if (m_flashCommand == 0x90) {
                        if (value == 0xF0) {
                            m_flashCommand = 0; // leave identification mode
                        }
                    }
                } else if ((m_flashCommand == 0x80) && (value == 0x30)) {
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

    /// <summary>Writes a GPIO register, driving whichever devices are present (RTC, rumble, solar) when the data
    /// register changes.</summary>
    /// <param name="register">The aligned register offset (0x0C4, 0x0C6, or 0x0C8).</param>
    /// <param name="value">The 16-bit value to write.</param>
    public void WriteGpio(uint register, ushort value) {
        switch (register) {
            case 0xC4u:
                // The game drives the pins it has configured as outputs; inputs keep the device-driven value. Every
                // present device gets a chance to react; each only touches its own pins, so co-resident devices
                // (RTC + light on one cart) never conflict.
                m_gpioPins = (value & m_gpioDirection) | m_gpioPins & ~m_gpioDirection & 0xF;

                if (m_hasRtc) {
                    StepRtc();
                }

                if (m_hasSolar) {
                    StepLight();
                }

                if (m_hasRumble) {
                    // No shift protocol: the game drives pin 3 directly, so the current pin state IS the motor state.
                    m_rumbleMotorOn = ((m_gpioPins & 0x08) != 0);
                }

                break;
            case 0xC6u:
                m_gpioDirection = value & 0xF;

                break;
            case 0xC8u:
                m_gpioReadable = ((value & 1) != 0);

                break;
        }
    }

    // Drives a device's input pins (those the game reads) to the supplied value, leaving the game's output pins
    // undisturbed — the shared helper every GPIO device (RTC, light sensor) uses to answer through the overlay.
    private void DriveGpioOutput(int pins) {
        m_gpioPins = (m_gpioPins & m_gpioDirection) | pins & ~m_gpioDirection & 0xF;
    }

    // The light sensor, evaluated on every data-register write while present. Chip-select (bit 2) high silences the
    // sensor; RESET (bit 1) high resamples the reading and zeroes the counter; each CLK (bit 0) rising edge while not
    // held in reset advances the counter; the output bit (bit 3) goes high once the counter reaches the threshold.
    private void StepLight() {
        var clk = (m_gpioPins & 1);
        var reset = ((m_gpioPins >> 1) & 1);
        var chipSelect = ((m_gpioPins >> 2) & 1);

        if (chipSelect != 0) {
            return;
        }

        if (reset != 0) {
            m_lightCounter = 0;
            m_lightEdge = true;
        }

        if ((clk != 0) && m_lightEdge) {
            ++m_lightCounter;
        }

        m_lightEdge = (clk == 0);

        var sendBit = (m_lightCounter >= m_lightThreshold);

        DriveGpioOutput(pins: (sendBit ? 0x08 : 0x00) | (m_gpioPins & 0x07));
    }

    // The S-3511A serial clock, evaluated on every data-register write. Follows the hardware edge model exactly: command
    // bytes shift in on SCK rising edges (LSB first), read replies shift out on SCK falling edges.
    private void StepRtc() {
        var sck = m_gpioPins & 1;
        var sio = (m_gpioPins >> 1) & 1;
        var cs = (m_gpioPins >> 2) & 1;

        DriveGpioOutput(pins: m_gpioPins & 2);

        if (cs == 0) {
            m_rtcBitsRead = 0;
            m_rtcBytesRemaining = 0;
            m_rtcCommandActive = false;
            m_rtcCommand = 0;
            m_rtcSckEdge = true;
            m_rtcSioOutput = true;
            DriveGpioOutput(pins: 2);
            m_rtcSckEdge = (sck != 0);

            return;
        }

        if (!m_rtcCommandActive) {
            DriveGpioOutput(pins: 2);

            if (sck == 0) {
                m_rtcBits = (m_rtcBits & ~(1 << m_rtcBitsRead)) | (sio << m_rtcBitsRead);
            }

            if (!m_rtcSckEdge && (sck != 0)) {
                ++m_rtcBitsRead;

                if (m_rtcBitsRead == 8) {
                    BeginRtcCommand();
                }
            }
        } else if ((m_rtcCommand & 0x80) == 0) {
            // Write command: clock the parameter byte in.
            DriveGpioOutput(pins: 2);

            if (sck == 0) {
                m_rtcBits = (m_rtcBits & ~(1 << m_rtcBitsRead)) | (sio << m_rtcBitsRead);
            }

            if (!m_rtcSckEdge && (sck != 0)) {
                ++m_rtcBitsRead;

                if (m_rtcBitsRead == 8) {
                    ProcessRtcByte();
                }
            }
        } else {
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

            DriveGpioOutput(pins: ((m_rtcSioOutput ? 1 : 0) << 1));
        }

        m_rtcSckEdge = (sck != 0);
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
            2 or 6 => m_rtcTime[(7 - m_rtcBytesRemaining)], // date+time / time bytes
            _ => (byte)0xFF,
        };

        return (((output >> m_rtcBitsRead) & 1) != 0);
    }
    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));

    // Computes the current RTC time from a fixed epoch plus elapsed emulated cycles. The AGB master clock is
    // 16,777,216 Hz (2^24), so seconds = cycles / 16_777_216. A fixed epoch keeps the conformance harness
    // reproducible while still advancing the clock at the correct emulated rate.
    private void InitRtcTime() {
        var epoch = new DateTime(year: 2026, month: 6, day: 23, hour: 12, minute: 0, second: 0);

        if (m_cycleProvider is not null) {
            var elapsedSeconds = (m_cycleProvider() / 16_777_216L);

            epoch = epoch.AddSeconds(value: elapsedSeconds);
        }

        m_rtcTime[0] = ToBcd(value: (epoch.Year - 2000));
        m_rtcTime[1] = ToBcd(value: epoch.Month);
        m_rtcTime[2] = ToBcd(value: epoch.Day);
        m_rtcTime[3] = ToBcd(value: (int)epoch.DayOfWeek);
        m_rtcTime[4] = ToBcd(value: epoch.Hour);
        m_rtcTime[5] = ToBcd(value: epoch.Minute);
        m_rtcTime[6] = ToBcd(value: epoch.Second);
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
        // The battery SRAM backs the FULL 64 KiB bus window (the long-standing emulator .sav convention), so an
        // access above 32 KiB never aliases back onto the low bytes. Most physical carts fit a 32 KiB chip, but a
        // wrap-at-32K model lets a runaway writer (e.g. a test ROM's log cursor crossing 0x8000) corrupt byte 0
        // and destabilise everything that reads the low save bytes afterwards.
        CartridgeBackup.Sram => (64 * 1024),
        CartridgeBackup.Flash64 => (64 * 1024),
        CartridgeBackup.Flash128 => (128 * 1024),
        CartridgeBackup.Eeprom => (8 * 1024),
        _ => 0,
    };
    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        (haystack.IndexOf(value: needle) >= 0);
}
