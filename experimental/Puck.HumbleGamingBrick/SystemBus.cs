using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The concrete address decoder. It owns no memory of its own beyond the I/O register page that no dedicated component
/// has claimed yet; everything else routes to the cartridge, the internal RAM, or the interrupt controller. As real
/// components come online they take over their register ranges from the fallback page.
/// </summary>
public sealed class SystemBus : ISystemBus, ISnapshotable, IModeSwitchable {
    // The boot overlay's read windows: every model maps the first 256 bytes; Color additionally maps 0x200-0x8FF,
    // leaving the cartridge header visible through the 0x100-0x1FF hole.
    private const ushort BootRomLowEnd = 0x00FF;
    private const ushort CgbBootRomHighEnd = 0x08FF;
    private const ushort CgbBootRomHighStart = 0x0200;
    private const int OamScanMode = 2;
    private const int PixelTransferMode = 3;

    private readonly IApu m_apu;
    private readonly byte[]? m_bootRom;
    private readonly ICartridgeSlot m_cartridgeSlot;
    private readonly IHdma m_hdma;
    private readonly IInterruptController m_interrupts;
    private readonly byte[] m_ioRegisters;
    private readonly IJoypad m_joypad;
    private readonly IKey1 m_key1;
    private readonly SystemMemory m_memory;
    private readonly IOamDma m_oamDma;
    private readonly IPpu m_ppu;
    private readonly ISerial m_serial;
    // Mutable so a LIVE device swap re-gates the Color I/O page: with this false, every color register write (palette
    // RAM, KEY1, HDMA, VRAM/WRAM bank selects, PCM ports) is already dropped by the existing `if (m_supportsColor)`
    // guards and reads return 0xFF — sealing off Color hardware after a demote with no per-register change.
    private bool m_supportsColor;
    private readonly ITimer m_timer;

    // The FF50 latch: the boot ROM overlay is readable until the first nonzero write, which unmaps it for the life of
    // the machine (only a reset — a fresh machine — brings it back). A machine configured without a boot ROM starts
    // with the latch already tripped, so the seeded post-boot path reads FF50 exactly as hardware does after boot.
    private bool m_bootRomMapped;

    /// <summary>Assembles the bus from the devices it routes to.</summary>
    /// <param name="apu">The audio processing unit backing NR10–NR52 and wave RAM.</param>
    /// <param name="cartridgeSlot">The cartridge slot the bus reads the current cartridge through.</param>
    /// <param name="hdma">The Color VRAM DMA unit backing HDMA1–HDMA5.</param>
    /// <param name="interrupts">The interrupt controller backing IF and IE.</param>
    /// <param name="joypad">The joypad backing the P1/JOYP register.</param>
    /// <param name="key1">The Color speed-switch backing the KEY1 register.</param>
    /// <param name="memory">The internal RAM.</param>
    /// <param name="oamDma">The OAM DMA unit backing the DMA register and gating OAM while a transfer runs.</param>
    /// <param name="ppu">The picture processing unit backing the LCDC, STAT, LY, and LYC registers.</param>
    /// <param name="serial">The serial port backing SB and SC.</param>
    /// <param name="timer">The divider/timer block backing DIV, TIMA, TMA, and TAC.</param>
    /// <param name="configuration">The machine configuration, which gates Color-only registers.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SystemBus(IApu apu, ICartridgeSlot cartridgeSlot, IHdma hdma, IInterruptController interrupts, IJoypad joypad, IKey1 key1, SystemMemory memory, IOamDma oamDma, IPpu ppu, ISerial serial, ITimer timer, MachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(argument: apu);
        ArgumentNullException.ThrowIfNull(argument: cartridgeSlot);
        ArgumentNullException.ThrowIfNull(argument: hdma);
        ArgumentNullException.ThrowIfNull(argument: interrupts);
        ArgumentNullException.ThrowIfNull(argument: joypad);
        ArgumentNullException.ThrowIfNull(argument: key1);
        ArgumentNullException.ThrowIfNull(argument: memory);
        ArgumentNullException.ThrowIfNull(argument: oamDma);
        ArgumentNullException.ThrowIfNull(argument: ppu);
        ArgumentNullException.ThrowIfNull(argument: serial);
        ArgumentNullException.ThrowIfNull(argument: timer);
        ArgumentNullException.ThrowIfNull(argument: configuration);

        m_apu = apu;
        m_bootRom = configuration.BootRom;
        m_bootRomMapped = (configuration.BootRom is not null);
        m_cartridgeSlot = cartridgeSlot;
        m_hdma = hdma;
        m_interrupts = interrupts;
        m_ioRegisters = new byte[(MemoryMap.IoRegistersEnd - MemoryMap.IoRegistersStart) + 1];
        m_joypad = joypad;
        m_key1 = key1;
        m_memory = memory;
        m_oamDma = oamDma;
        m_ppu = ppu;
        m_serial = serial;
        m_supportsColor = configuration.Model.SupportsColor();
        m_timer = timer;

        Array.Fill(array: m_ioRegisters, value: (byte)0xFF);
    }

    /// <inheritdoc/>
    public byte ReadByte(ushort address) {
        // On Color, a running OAM DMA occupies its source's bus; a CPU read that collides is hijacked — it sees open
        // bus or the DMA's own bus (read on the DMA's path, so the redirect cannot recurse).
        if (m_oamDma.TryReadConflict(address: address, forceOpenBus: out var forceOpenBus, redirect: out var redirect)) {
            return forceOpenBus ? (byte)0xFF : DmaSource.Read(cartridgeSlot: m_cartridgeSlot, memory: m_memory, address: redirect);
        }

        if (address <= MemoryMap.RomBankNEnd) {
            // The boot overlay is read-only: reads inside its windows come from the image; writes always pass through
            // to the cartridge below, exactly as if the overlay were not there.
            return IsBootRomAddress(address: address)
                ? m_bootRom![address]
                : m_cartridgeSlot.Cartridge.ReadRom(address: address);
        }

        if (address <= MemoryMap.VideoRamEnd) {
            // The PPU owns VRAM during drawing (mode 3), so the CPU reads open bus there.
            return (m_ppu.Mode == PixelTransferMode) ? (byte)0xFF : m_memory.ReadVideoRam(address: address);
        }

        if (address <= MemoryMap.ExternalRamEnd) {
            return m_cartridgeSlot.Cartridge.ReadRam(address: address);
        }

        if (address <= MemoryMap.WorkRamBankNEnd) {
            return m_memory.ReadWorkRam(address: address);
        }

        if (address <= MemoryMap.EchoRamEnd) {
            return m_memory.ReadWorkRam(address: (ushort)(address - MemoryMap.EchoRamMirrorOffset));
        }

        if (address <= MemoryMap.ObjectAttributeMemoryEnd) {
            // OAM is unreadable to the CPU while a DMA copies into it, or while the PPU scans and draws (modes 2 and 3).
            return (m_oamDma.IsActive || IsOamLocked()) ? (byte)0xFF : m_memory.ReadObjectAttributeMemory(address: address);
        }

        if (address <= MemoryMap.UnusableEnd) {
            return 0xFF;
        }

        if (address <= MemoryMap.IoRegistersEnd) {
            if (IsAudioBlock(address: address)) {
                return m_apu.ReadRegister(address: address);
            }

            // The Color-only PCM output registers route to the APU; on a DMG they read as open bus.
            if ((address == MemoryMap.PcmAmplitude12) || (address == MemoryMap.PcmAmplitude34)) {
                return m_supportsColor ? m_apu.ReadPcm(address: address) : (byte)0xFF;
            }

            return ReadIoRegister(address: address);
        }

        if (address <= MemoryMap.HighRamEnd) {
            return m_memory.ReadHighRam(address: address);
        }

        return (byte)m_interrupts.Enabled;
    }
    /// <inheritdoc/>
    public void WriteByte(ushort address, byte value) {
        // On Color, a CPU write that collides with a running OAM DMA's bus is dropped, lands on the DMA's bus instead,
        // or additionally zeroes the OAM byte in flight. The redirected store goes straight to memory — the target is
        // the DMA's own bus, which by construction is not re-classified.
        switch (m_oamDma.ClassifyWriteConflict(address: address, target: out var conflictTarget)) {
            case OamDmaWriteConflict.Drop:
                return;
            case OamDmaWriteConflict.Store:
                WriteConflictTarget(address: conflictTarget, value: value);

                return;
            case OamDmaWriteConflict.StoreAndPoisonOam:
                WriteConflictTarget(address: conflictTarget, value: value);
                m_oamDma.PoisonCurrentOamByte();

                return;
            default:
                break;
        }

        if (address <= MemoryMap.RomBankNEnd) {
            m_cartridgeSlot.Cartridge.WriteControl(address: address, value: value);
        }
        else if (address <= MemoryMap.VideoRamEnd) {
            // Writes are dropped while the PPU owns VRAM during drawing (mode 3).
            if (m_ppu.Mode != PixelTransferMode) {
                m_memory.WriteVideoRam(address: address, value: value);
            }
        }
        else if (address <= MemoryMap.ExternalRamEnd) {
            m_cartridgeSlot.Cartridge.WriteRam(address: address, value: value);
        }
        else if (address <= MemoryMap.WorkRamBankNEnd) {
            m_memory.WriteWorkRam(address: address, value: value);
        }
        else if (address <= MemoryMap.EchoRamEnd) {
            m_memory.WriteWorkRam(address: (ushort)(address - MemoryMap.EchoRamMirrorOffset), value: value);
        }
        else if (address <= MemoryMap.ObjectAttributeMemoryEnd) {
            // The CPU cannot reach OAM while a DMA transfer owns it — including the transfer's warm-up delay — or
            // while the PPU scans and draws (modes 2 and 3).
            if (!m_oamDma.IsActiveOrWarmingUp && !IsOamLocked()) {
                m_memory.WriteObjectAttributeMemory(address: address, value: value);
            }
        }
        else if (address <= MemoryMap.UnusableEnd) {
            // The unusable region drops writes.
        }
        else if (address <= MemoryMap.IoRegistersEnd) {
            if (IsAudioBlock(address: address)) {
                m_apu.WriteRegister(address: address, value: value);
            }
            else {
                WriteIoRegister(address: address, value: value);
            }
        }
        else if (address <= MemoryMap.HighRamEnd) {
            m_memory.WriteHighRam(address: address, value: value);
        }
        else {
            m_interrupts.Enabled = (InterruptKind)value;
        }
    }
    /// <inheritdoc/>
    public void ApplyModel(ConsoleModel model) =>
        m_supportsColor = model.SupportsColor();

    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteBytes(value: m_ioRegisters);
        writer.WriteBoolean(value: m_bootRomMapped);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        reader.ReadBytes(destination: m_ioRegisters);
        m_bootRomMapped = reader.ReadBoolean();
    }

    // Whether an address falls inside the boot overlay's read windows while it is still mapped. The image itself is
    // immutable configuration; only the FF50 latch is machine state.
    private bool IsBootRomAddress(ushort address) {
        if (!m_bootRomMapped || (m_bootRom is null)) {
            return false;
        }

        return ((address <= BootRomLowEnd)
            || (m_supportsColor && (address >= CgbBootRomHighStart) && (address <= CgbBootRomHighEnd)));
    }

    // Land a conflict-redirected store directly on the DMA's bus: the mapper for the ROM region, then VRAM (still
    // subject to the PPU's drawing lock), external RAM, and work RAM with its echo fold.
    private void WriteConflictTarget(ushort address, byte value) {
        if (address <= MemoryMap.RomBankNEnd) {
            m_cartridgeSlot.Cartridge.WriteControl(address: address, value: value);
        }
        else if (address <= MemoryMap.VideoRamEnd) {
            if (m_ppu.Mode != PixelTransferMode) {
                m_memory.WriteVideoRam(address: address, value: value);
            }
        }
        else if (address <= MemoryMap.ExternalRamEnd) {
            m_cartridgeSlot.Cartridge.WriteRam(address: address, value: value);
        }
        else if (address <= MemoryMap.WorkRamBankNEnd) {
            m_memory.WriteWorkRam(address: address, value: value);
        }
        else {
            m_memory.WriteWorkRam(address: (ushort)(address - MemoryMap.EchoRamMirrorOffset), value: value);
        }
    }

    // OAM is locked to the CPU while the PPU is scanning it (mode 2) or drawing (mode 3).
    private bool IsOamLocked() {
        var mode = m_ppu.Mode;

        return (mode == OamScanMode) || (mode == PixelTransferMode);
    }

    // The audio registers and wave RAM form one contiguous block the APU owns end to end.
    private static bool IsAudioBlock(ushort address) =>
        (address >= MemoryMap.AudioStart) && (address <= MemoryMap.WaveRamEnd);

    private byte ReadIoRegister(ushort address) {
        switch (address) {
            case MemoryMap.Joypad:
                return m_joypad.ReadRegister();
            case MemoryMap.SerialData:
            case MemoryMap.SerialControl:
                return m_serial.ReadRegister(address: address);
            case MemoryMap.Divider:
            case MemoryMap.TimerCounter:
            case MemoryMap.TimerModulo:
            case MemoryMap.TimerControl:
                return m_timer.ReadRegister(address: address);
            case MemoryMap.OamDmaSource:
                return m_oamDma.ReadRegister();
            case MemoryMap.LcdControl:
            case MemoryMap.LcdStatus:
            case MemoryMap.ScrollY:
            case MemoryMap.ScrollX:
            case MemoryMap.LcdY:
            case MemoryMap.LcdYCompare:
            case MemoryMap.BackgroundPalette:
            case MemoryMap.ObjectPalette0:
            case MemoryMap.ObjectPalette1:
            case MemoryMap.WindowY:
            case MemoryMap.WindowX:
                return m_ppu.ReadRegister(address: address);
            case MemoryMap.InterruptFlag:
                return (byte)(0xE0 | (byte)m_interrupts.Requested);
            case MemoryMap.BootRomDisable:
                // Bit 0 is the latch (set once the overlay is gone); the undecoded bits read high.
                return (byte)(0xFE | (m_bootRomMapped ? 0x00 : 0x01));
            default:
                return ReadColorIoRegister(address: address);
        }
    }
    // The Color-only register page: everything here reads open bus (0xFF) on a monochrome machine, as does any
    // unmapped I/O address on either model, regardless of any write that landed there.
    private byte ReadColorIoRegister(ushort address) {
        if (!m_supportsColor) {
            return 0xFF;
        }

        switch (address) {
            case MemoryMap.BackgroundColorPaletteIndex:
            case MemoryMap.BackgroundColorPaletteData:
            case MemoryMap.ObjectColorPaletteIndex:
            case MemoryMap.ObjectColorPaletteData:
                return m_ppu.ReadRegister(address: address);
            case MemoryMap.SpeedSwitch:
                return m_key1.ReadRegister();
            case MemoryMap.HdmaSourceHigh:
            case MemoryMap.HdmaSourceLow:
            case MemoryMap.HdmaDestinationHigh:
            case MemoryMap.HdmaDestinationLow:
            case MemoryMap.HdmaControl:
                return m_hdma.ReadRegister(address: address);
            case MemoryMap.VramBankSelect:
                return (byte)(0xFE | m_memory.VideoRamBank);
            case MemoryMap.WorkRamBankSelect:
                return (byte)(0xF8 | m_memory.WorkRamBank);
            case 0xFF72:
            case 0xFF73:
            case 0xFF74:
                // The Color's undocumented fully-readable/writable registers.
                return m_ioRegisters[address - MemoryMap.IoRegistersStart];
            case 0xFF75:
                // Only bits 4-6 are backed; the rest read as ones.
                return (byte)(0x8F | m_ioRegisters[address - MemoryMap.IoRegistersStart]);
            default:
                return 0xFF;
        }
    }
    private void WriteIoRegister(ushort address, byte value) {
        switch (address) {
            case MemoryMap.Joypad:
                m_joypad.WriteRegister(value: value);

                break;
            case MemoryMap.SerialData:
            case MemoryMap.SerialControl:
                m_serial.WriteRegister(address: address, value: value);

                break;
            case MemoryMap.Divider:
            case MemoryMap.TimerCounter:
            case MemoryMap.TimerModulo:
            case MemoryMap.TimerControl:
                m_timer.WriteRegister(address: address, value: value);

                break;
            case MemoryMap.OamDmaSource:
                m_oamDma.WriteRegister(value: value);

                break;
            case MemoryMap.LcdControl:
            case MemoryMap.LcdStatus:
            case MemoryMap.ScrollY:
            case MemoryMap.ScrollX:
            case MemoryMap.LcdY:
            case MemoryMap.LcdYCompare:
            case MemoryMap.BackgroundPalette:
            case MemoryMap.ObjectPalette0:
            case MemoryMap.ObjectPalette1:
            case MemoryMap.WindowY:
            case MemoryMap.WindowX:
                m_ppu.WriteRegister(address: address, value: value);

                break;
            case MemoryMap.InterruptFlag:
                m_interrupts.Requested = (InterruptKind)value;

                break;
            case MemoryMap.BootRomDisable:
                // A one-way latch: any nonzero write unmaps the boot overlay permanently; zero writes are ignored and
                // nothing ever maps it back.
                if (value != 0) {
                    m_bootRomMapped = false;
                }

                break;
            default:
                WriteColorIoRegister(address: address, value: value);

                break;
        }
    }
    // The Color-only register page's write side, mirroring ReadColorIoRegister: the decoded registers are dropped on a
    // monochrome machine, and anything undecoded lands in the fallback byte page on either model.
    private void WriteColorIoRegister(ushort address, byte value) {
        switch (address) {
            case MemoryMap.BackgroundColorPaletteIndex:
            case MemoryMap.BackgroundColorPaletteData:
            case MemoryMap.ObjectColorPaletteIndex:
            case MemoryMap.ObjectColorPaletteData:
                if (m_supportsColor) {
                    m_ppu.WriteRegister(address: address, value: value);
                }

                break;
            case MemoryMap.SpeedSwitch:
                if (m_supportsColor) {
                    m_key1.WriteRegister(value: value);
                }

                break;
            case MemoryMap.HdmaSourceHigh:
            case MemoryMap.HdmaSourceLow:
            case MemoryMap.HdmaDestinationHigh:
            case MemoryMap.HdmaDestinationLow:
            case MemoryMap.HdmaControl:
                if (m_supportsColor) {
                    m_hdma.WriteRegister(address: address, value: value);
                }

                break;
            case MemoryMap.VramBankSelect:
                if (m_supportsColor) {
                    m_memory.VideoRamBank = value;
                }

                break;
            case MemoryMap.WorkRamBankSelect:
                if (m_supportsColor) {
                    m_memory.WorkRamBank = value;
                }

                break;
            default:
                m_ioRegisters[address - MemoryMap.IoRegistersStart] = value;

                break;
        }
    }
}
