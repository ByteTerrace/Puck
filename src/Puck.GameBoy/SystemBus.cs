namespace Puck.GameBoy;

/// <summary>
/// The shared bus and clock that wire the components into a machine. It is the heart of the cycle-stepped
/// model: the CPU never touches memory directly but goes through the <em>cycle accessors</em>
/// (<see cref="ReadCycle"/>, <see cref="WriteCycle"/>, <see cref="InternalCycle"/>), each of which advances
/// every other component by one machine cycle <em>before</em> the access lands — so the PPU, timer, DMA, and
/// serial all observe the access at the cycle it really happens. Memory is decoded here, components claim their
/// I/O registers here, and the two clock domains (<see cref="ClockDomain"/>) are advanced here, which is what
/// keeps the wiring — not any single component — the place the system's timing lives.
/// </summary>
public sealed class SystemBus : ISystemBus {
    private const int CpuDotsPerMachineCycle = 4;
    private const int WorkRamBankSize = 0x1000;
    private const int VideoRamBankSize = 0x2000;

    private readonly IApu m_apu;
    private readonly List<IClockedComponent> m_clockedComponents = [];
    private readonly ICartridge m_cartridge;
    private readonly byte[] m_highRam = new byte[0x7F];
    private readonly IInterruptController m_interrupts;
    private readonly IJoypad m_joypad;
    private readonly ISerial m_serial;
    private readonly byte[]? m_bootRom;
    private readonly ConsoleModel m_model;
    private readonly byte[] m_oam;
    private readonly IOamDma m_oamDma;
    private readonly IPpu m_ppu;
    private readonly ITimer m_timer;
    private readonly byte[] m_videoRam;
    private readonly byte[] m_workRam;

    private bool m_armedSpeedSwitch;
    private bool m_bootRomMapped;
    private readonly ClockState m_clockState;
    private int m_pendingMachineCycles;
    private ulong m_elapsedDots;
    private int m_videoRamBank;
    private int m_workRamBank = 1;
    // CGB miscellany: the boot-ROM CPU-mode latch (KEY0), the infrared port (RP), and the four undocumented registers.
    private byte m_cpuMode;
    private byte m_infraredPort;
    private byte m_undocumented72;
    private byte m_undocumented73;
    private byte m_undocumented74;
    private byte m_undocumented75;
    // CGB VRAM DMA (HDMA/GDMA) state. The transfer copies 0x10-byte blocks from the source into the current VRAM
    // bank; general mode runs every block at once (stalling the CPU), HBlank mode runs one block per horizontal
    // blank. m_hdmaBlocks is the blocks remaining; m_hdmaHBlank distinguishes the modes.
    private ushort m_hdmaSource;
    private ushort m_hdmaDestination;
    private int m_hdmaBlocks;
    private bool m_hdmaActive;
    private bool m_hdmaHBlank;
    private PpuMode m_previousLcdMode = PpuMode.HorizontalBlank;

    /// <summary>Gets the model this bus emulates.</summary>
    public ConsoleModel Model =>
        m_model;
    /// <summary>Gets the interrupt controller, through which components raise interrupts and the CPU services them.</summary>
    public IInterruptController Interrupts =>
        m_interrupts;
    /// <summary>Gets whether the CPU clock is in CGB double-speed mode, which halves the number of LCD-domain
    /// T-cycles per machine cycle. Always <see langword="false"/> on the DMG.</summary>
    public bool DoubleSpeed =>
        m_clockState.DoubleSpeed;
    /// <summary>Gets the total LCD-domain T-cycles (dots) elapsed since construction — the machine's wall clock.</summary>
    public ulong ElapsedDots =>
        m_elapsedDots;
    /// <summary>Gets the object-attribute memory, for the PPU to scan and OAM DMA to fill.</summary>
    public Span<byte> Oam =>
        m_oam;
    /// <summary>Gets the video RAM, for the PPU to fetch tiles and maps from.</summary>
    public Span<byte> VideoRam =>
        m_videoRam;

    /// <summary>Initializes the bus from the per-machine configuration and the injected subsystems and shared
    /// services that make up the machine.</summary>
    /// <param name="configuration">The model, boot ROM, and boot-palette selection.</param>
    /// <param name="cartridge">The cartridge plugged into the bus.</param>
    /// <param name="memory">The shared object/video/work RAM, sized for the model.</param>
    /// <param name="clockState">The shared double-speed clock state.</param>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <param name="timer">The divider/timer.</param>
    /// <param name="oamDma">The OAM DMA engine.</param>
    /// <param name="ppu">The picture processing unit.</param>
    /// <param name="joypad">The joypad.</param>
    /// <param name="serial">The serial link port.</param>
    /// <param name="apu">The audio processing unit.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The configured boot ROM is shorter than the region the model maps it over.</exception>
    public SystemBus(
        MachineConfiguration configuration,
        ICartridge cartridge,
        SystemMemory memory,
        ClockState clockState,
        IInterruptController interrupts,
        ITimer timer,
        IOamDma oamDma,
        IPpu ppu,
        IJoypad joypad,
        ISerial serial,
        IApu apu
    ) {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(cartridge);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(clockState);
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(oamDma);
        ArgumentNullException.ThrowIfNull(ppu);
        ArgumentNullException.ThrowIfNull(joypad);
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(apu);

        var model = configuration.Model;
        var bootRom = configuration.BootRom;
        var isColor = (model == ConsoleModel.Cgb);

        // The overlay indexes the boot ROM directly (DMG over 0x000-0x0FF, CGB over 0x000-0x0FF and
        // 0x200-end), so reject an image too short to back that range rather than fault on first fetch.
        var requiredBootRomLength = (isColor ? 0x0900 : 0x0100);

        if ((bootRom is not null) && (bootRom.Length < requiredBootRomLength)) {
            throw new ArgumentException(
                message: $"A {model} boot ROM must be at least 0x{requiredBootRomLength:X} bytes; got 0x{bootRom.Length:X}.",
                paramName: nameof(configuration)
            );
        }

        m_bootRom = bootRom;
        m_bootRomMapped = (bootRom is not null);
        m_cartridge = cartridge;
        m_model = model;
        m_clockState = clockState;

        // The PPU and OAM DMA share these backing stores with the bus.
        m_oam = memory.ObjectAttributeMemory;
        m_videoRam = memory.VideoRam;
        m_workRam = memory.WorkRam;

        m_interrupts = interrupts;
        m_timer = timer;
        m_oamDma = oamDma;
        m_ppu = ppu;
        m_joypad = joypad;
        m_serial = serial;
        m_apu = apu;

        // OAM DMA reads its source through the untimed path; wiring the reader here (rather than at the engine's
        // construction) keeps it free of a dependency on the bus type, breaking the construction cycle.
        m_oamDma.ReadSource = ReadDmaSource;

        // Clock order matters: the peripherals step in this sequence each machine cycle. A cartridge with an on-board
        // clock (RTC) is clocked in the Lcd domain so it tracks real time in both speed modes; the timer, OAM DMA,
        // PPU, serial, and APU follow. (The joypad is event-driven, not clocked.)
        if (cartridge is IClockedComponent clockedCartridge) {
            Attach(component: clockedCartridge);
        }

        Attach(component: m_timer);
        Attach(component: m_oamDma);
        Attach(component: m_ppu);
        Attach(component: m_serial);
        Attach(component: m_apu);

        ConfigureDmgCompatibilityColorization(model: model, bootPalette: configuration.BootPalette);
    }

    // A CGB console booting a cartridge with no CGB flag colorizes the original game with the boot ROM's assigned
    // palettes (or an alternative chosen by a held button combination); the PPU then renders DMG-style through them.
    // A CGB-aware cartridge (or a DMG console) is left untouched.
    private void ConfigureDmgCompatibilityColorization(ConsoleModel model, BootPaletteSelection bootPalette) {
        if ((model != ConsoleModel.Cgb) || ((m_cartridge.ReadRom(address: 0x0143) & 0x80) != 0)) {
            return;
        }

        var header = new byte[0x0150];

        for (var i = 0; i < header.Length; i += 1) {
            header[i] = m_cartridge.ReadRom(address: (ushort)i);
        }

        var (background, object0, object1) = CompatibilityPalette.Resolve(rom: header, input: bootPalette);

        m_ppu.EnableDmgCompatibilityColorization(background: background, object0: object0, object1: object1);
    }

    /// <summary>Gets the picture processing unit, for the host to present its framebuffer.</summary>
    public IPpu Ppu =>
        m_ppu;
    /// <summary>Gets the timer and divider, for seeding the post-boot divider phase.</summary>
    public ITimer Timer =>
        m_timer;
    /// <summary>Gets the joypad, for the host to feed button input.</summary>
    public IJoypad Joypad =>
        m_joypad;
    /// <summary>Gets the serial port, for capturing serial output.</summary>
    public ISerial Serial =>
        m_serial;
    /// <summary>Gets the audio processing unit, for the host to drain sample output.</summary>
    public IApu Apu =>
        m_apu;

    /// <summary>Registers a component to be advanced each machine cycle in its clock domain.</summary>
    /// <param name="component">The component to clock alongside the CPU.</param>
    /// <exception cref="ArgumentNullException"><paramref name="component"/> is <see langword="null"/>.</exception>
    public void Attach(IClockedComponent component) {
        ArgumentNullException.ThrowIfNull(component);

        m_clockedComponents.Add(item: component);
    }

    /// <summary>Reads a byte over one machine cycle. The deferred-cycle model: the cycle owed by the PREVIOUS
    /// access is discharged first, the read then happens at the start of this machine cycle, and this cycle is
    /// deferred until just before the next access — so peripherals observe the access at the hardware-faithful
    /// point.</summary>
    /// <param name="address">The CPU address to read.</param>
    /// <returns>The byte the CPU latches.</returns>
    public byte ReadCycle(ushort address) {
        FlushPendingCycles();

        if ((address >= MemoryMap.OamBase) && (address <= MemoryMap.UnusableEnd)) {
            TriggerOamBug(
                address: address,
                isWrite: false
            );
        }

        if (IsAudioChannelAccess(address: address)) {
            m_apu.AdvanceChannelsForAccess(tCycles: CpuDotsPerMachineCycle);
        }

        var value = ReadByte(address: address);

        m_pendingMachineCycles = 1;

        return value;
    }
    /// <summary>Writes a byte over one machine cycle, with the same deferred-cycle timing as <see cref="ReadCycle"/>.</summary>
    /// <param name="address">The CPU address to write.</param>
    /// <param name="value">The value to store.</param>
    public void WriteCycle(ushort address, byte value) {
        FlushPendingCycles();

        if ((address >= MemoryMap.OamBase) && (address <= MemoryMap.UnusableEnd)) {
            TriggerOamBug(
                address: address,
                isWrite: true
            );
        }

        if (IsAudioChannelAccess(address: address)) {
            m_apu.AdvanceChannelsForAccess(tCycles: CpuDotsPerMachineCycle);
        }

        WriteByte(
            address: address,
            value: value
        );

        m_pendingMachineCycles = 1;
    }

    // Whether an access at this address observes the APU channel generators (the register/wave block 0xFF10-0xFF3F or
    // the CGB PCM12/PCM34 amplitude registers), so the channels must be brought current to the access point first.
    private static bool IsAudioChannelAccess(ushort address) =>
        (((address >= MemoryMap.AudioBase) && (address <= (MemoryMap.WaveRamBase + 0x0F)))
            || (address == MemoryMap.PcmAmplitude12)
            || (address == MemoryMap.PcmAmplitude34));

    /// <inheritdoc />
    public void TriggerOamBug(ushort address, bool isWrite) {
        // The bug is DMG-only and confined to the OAM region; it only bites while the PPU is scanning OAM (mode 2),
        // which the PPU itself checks.
        if ((m_model == ConsoleModel.Cgb) || (address < MemoryMap.OamBase) || (address > MemoryMap.UnusableEnd)) {
            return;
        }

        if (isWrite) {
            m_ppu.OamBugWrite();
        }
        else {
            m_ppu.OamBugRead();
        }
    }
    /// <summary>Advances the machine by one machine cycle of internal CPU work that performs no bus access, deferred
    /// the same way as an access.</summary>
    public void InternalCycle() {
        FlushPendingCycles();

        m_pendingMachineCycles = 1;
    }
    /// <inheritdoc />
    public void FlushPendingCycles() {
        while (m_pendingMachineCycles > 0) {
            m_pendingMachineCycles -= 1;
            TickMachineCycle();
        }
    }

    // The CPU freezes for this many T-cycles while the clock changes during a speed switch; the peripherals keep
    // running, which is the "display collapse" the hardware exhibits during the switch.
    private const int SpeedSwitchStallTCycles = 0x20008;

    /// <summary>Performs a CGB speed switch when one has been armed via <c>KEY1</c>, as the <c>STOP</c> instruction
    /// does. Toggles between normal and double speed, resets the divider, and stalls the CPU for the ~0x20008-T-cycle
    /// clock-change window during which the peripherals keep advancing. Disarms the request.</summary>
    /// <returns><see langword="true"/> when a switch was armed and performed; otherwise <see langword="false"/>.</returns>
    public bool ApplyPreparedSpeedSwitch() {
        if (!m_armedSpeedSwitch) {
            return false;
        }

        m_armedSpeedSwitch = false;
        m_clockState.DoubleSpeed = !m_clockState.DoubleSpeed;

        // STOP resets DIV, then the clock-change stall runs at the new speed with the CPU frozen.
        FlushPendingCycles();
        m_timer.SetInternalCounter(value: 0);

        for (var cycle = 0; cycle < (SpeedSwitchStallTCycles / CpuDotsPerMachineCycle); cycle += 1) {
            TickMachineCycle();
        }

        return true;
    }

    /// <summary>Reads a byte with no timing effect, for DMA, the PPU, and debugging. The decode matches
    /// <see cref="ReadCycle"/> but does not advance the clock.</summary>
    /// <param name="address">The CPU address to read.</param>
    /// <returns>The decoded byte.</returns>
    public byte ReadByte(ushort address) {
        switch (address) {
            case <= MemoryMap.RomEnd:
                return (IsBootRomAddress(address: address)
                    ? m_bootRom![address]
                    : m_cartridge.ReadRom(address: address));
            case >= MemoryMap.VideoRamBase and <= MemoryMap.VideoRamEnd:
                // VRAM is locked while the PPU is drawing the scanline.
                return (m_ppu.IsVideoRamAccessible
                    ? m_videoRam[(m_videoRamBank * VideoRamBankSize) + (address - MemoryMap.VideoRamBase)]
                    : (byte)0xFF);
            case >= MemoryMap.CartridgeRamBase and <= MemoryMap.CartridgeRamEnd:
                return m_cartridge.ReadRam(address: address);
            case >= MemoryMap.WorkRamBase and <= MemoryMap.WorkRamEnd:
                return m_workRam[WorkRamOffset(address: address)];
            case >= MemoryMap.EchoRamBase and <= MemoryMap.EchoRamEnd:
                return m_workRam[WorkRamOffset(address: (ushort)(address - 0x2000))];
            case >= MemoryMap.OamBase and <= MemoryMap.OamEnd:
                // Object-attribute memory is locked during an OAM DMA transfer and while the PPU scans/draws.
                return ((m_oamDma.IsOamLocked || !m_ppu.IsObjectMemoryAccessible)
                    ? (byte)0xFF
                    : m_oam[address - MemoryMap.OamBase]);
            case >= MemoryMap.UnusableBase and <= MemoryMap.UnusableEnd:
                // DMG reads this prohibited region as 0x00 (outside OAM-block). CGB returns revision- and
                // PPU-mode-dependent nibble patterns; that is refined through the PPU/OAM-access seam later.
                return 0x00;
            case >= MemoryMap.IoBase and <= MemoryMap.IoEnd:
                return ReadIo(address: address);
            case >= MemoryMap.HighRamBase and <= MemoryMap.HighRamEnd:
                return m_highRam[address - MemoryMap.HighRamBase];
            default:
                return m_interrupts.InterruptEnable;
        }
    }
    /// <summary>Writes a byte with no timing effect, for DMA and debugging. The decode matches
    /// <see cref="WriteCycle"/> but does not advance the clock.</summary>
    /// <param name="address">The CPU address to write.</param>
    /// <param name="value">The value to store.</param>
    public void WriteByte(ushort address, byte value) {
        switch (address) {
            case <= MemoryMap.RomEnd:
                m_cartridge.WriteRom(
                    address: address,
                    value: value
                );

                break;
            case >= MemoryMap.VideoRamBase and <= MemoryMap.VideoRamEnd:
                // VRAM is write-locked while the PPU is drawing; the write lock trails the read lock by a cycle.
                if (m_ppu.IsVideoRamWritable) {
                    m_videoRam[(m_videoRamBank * VideoRamBankSize) + (address - MemoryMap.VideoRamBase)] = value;
                }

                break;
            case >= MemoryMap.CartridgeRamBase and <= MemoryMap.CartridgeRamEnd:
                m_cartridge.WriteRam(
                    address: address,
                    value: value
                );

                break;
            case >= MemoryMap.WorkRamBase and <= MemoryMap.WorkRamEnd:
                m_workRam[WorkRamOffset(address: address)] = value;

                break;
            case >= MemoryMap.EchoRamBase and <= MemoryMap.EchoRamEnd:
                m_workRam[WorkRamOffset(address: (ushort)(address - 0x2000))] = value;

                break;
            case >= MemoryMap.OamBase and <= MemoryMap.OamEnd:
                // Object-attribute memory is locked during an OAM DMA transfer and while the PPU scans/draws; the
                // write lock trails the read lock by a cycle and briefly opens at the mode-2/3 boundary.
                if (!m_oamDma.IsOamLocked && m_ppu.IsObjectMemoryWritable) {
                    m_oam[address - MemoryMap.OamBase] = value;
                }

                break;
            case >= MemoryMap.UnusableBase and <= MemoryMap.UnusableEnd:
                break;
            case >= MemoryMap.IoBase and <= MemoryMap.IoEnd:
                WriteIo(
                    address: address,
                    value: value
                );

                break;
            case >= MemoryMap.HighRamBase and <= MemoryMap.HighRamEnd:
                m_highRam[address - MemoryMap.HighRamBase] = value;

                break;
            default:
                m_interrupts.InterruptEnable = value;

                break;
        }
    }

    private byte ReadDmaSource(ushort address) {
        // The DMA controller drives the internal bus directly: it is never subject to the OAM or VRAM access
        // locks it imposes on the CPU/PPU, and source pages 0xE0-0xFF alias the work-RAM echo rather than OAM/IO.
        if (address >= MemoryMap.EchoRamBase) {
            return m_workRam[WorkRamOffset(address: (ushort)(address - 0x2000))];
        }

        if (address is >= MemoryMap.VideoRamBase and <= MemoryMap.VideoRamEnd) {
            return m_videoRam[(m_videoRamBank * VideoRamBankSize) + (address - MemoryMap.VideoRamBase)];
        }

        return ReadByte(address: address);
    }

    private byte ReadIo(ushort address) =>
        // An I/O address with no component claiming it reads as open bus (0xFF). Each component installs its
        // own register handlers as it is built, returning the hardware OR-masked value; only genuinely
        // unmapped addresses fall through to 0xFF.
        address switch {
            MemoryMap.InterruptFlag => m_interrupts.InterruptFlag,
            MemoryMap.Joypad => m_joypad.Read(),
            MemoryMap.SerialData => m_serial.ReadData(),
            MemoryMap.SerialControl => m_serial.ReadControl(),
            >= MemoryMap.AudioBase and <= MemoryMap.WaveRamEnd => m_apu.Read(address: address),
            >= MemoryMap.Divider and <= MemoryMap.TimerControl => m_timer.ReadRegister(address: address),
            MemoryMap.OamDmaStart => m_oamDma.Page,
            >= MemoryMap.LcdControl and <= MemoryMap.WindowX => m_ppu.ReadRegister(address: address),
            MemoryMap.BootRomDisable => (byte)(0xFE | (m_bootRomMapped ? 0x00 : 0x01)),
            MemoryMap.SpeedSwitch when (m_model == ConsoleModel.Cgb) =>
                (byte)(0x7E | (m_clockState.DoubleSpeed ? 0x80 : 0x00) | (m_armedSpeedSwitch ? 0x01 : 0x00)),
            MemoryMap.VideoRamBank when (m_model == ConsoleModel.Cgb) =>
                (byte)(0xFE | m_videoRamBank),
            MemoryMap.WorkRamBank when (m_model == ConsoleModel.Cgb) =>
                (byte)(0xF8 | m_workRamBank),
            MemoryMap.VramDmaControl when (m_model == ConsoleModel.Cgb) =>
                // Bit 7 clear while a transfer is in progress; bits 6-0 are the blocks remaining minus one.
                (byte)((m_hdmaActive ? 0x00 : 0x80) | ((m_hdmaBlocks - 1) & 0x7F)),
            MemoryMap.BackgroundPaletteIndex when (m_model == ConsoleModel.Cgb) =>
                m_ppu.ReadBackgroundPaletteIndex(),
            MemoryMap.BackgroundPaletteData when (m_model == ConsoleModel.Cgb) =>
                m_ppu.ReadBackgroundPaletteData(),
            MemoryMap.ObjectPaletteIndex when (m_model == ConsoleModel.Cgb) =>
                m_ppu.ReadObjectPaletteIndex(),
            MemoryMap.ObjectPaletteData when (m_model == ConsoleModel.Cgb) =>
                m_ppu.ReadObjectPaletteData(),
            MemoryMap.ObjectPriorityMode when (m_model == ConsoleModel.Cgb) =>
                m_ppu.ReadObjectPriorityMode(),
            // The CGB exposes each channel's live digital output through PCM12/PCM34; the DMG has no such registers.
            MemoryMap.PcmAmplitude12 when (m_model == ConsoleModel.Cgb) =>
                m_apu.PcmAmplitude12,
            MemoryMap.PcmAmplitude34 when (m_model == ConsoleModel.Cgb) =>
                m_apu.PcmAmplitude34,
            MemoryMap.CpuMode when (m_model == ConsoleModel.Cgb) =>
                m_cpuMode,
            MemoryMap.InfraredPort when (m_model == ConsoleModel.Cgb) =>
                // Bits 7-6/0 read back as written; bit 1 is the received signal — always 1 (no light) with no IR peer;
                // the unused bits 5-2 read as one.
                (byte)((m_infraredPort & 0xC1) | 0x3E),
            MemoryMap.Undocumented72 when (m_model == ConsoleModel.Cgb) =>
                m_undocumented72,
            MemoryMap.Undocumented73 when (m_model == ConsoleModel.Cgb) =>
                m_undocumented73,
            MemoryMap.Undocumented74 when (m_model == ConsoleModel.Cgb) =>
                m_undocumented74,
            MemoryMap.Undocumented75 when (m_model == ConsoleModel.Cgb) =>
                // Only bits 4-6 are read/write; the rest read as one.
                (byte)(0x8F | (m_undocumented75 & 0x70)),
            _ => 0xFF,
        };
    private void WriteIo(ushort address, byte value) {
        // Writes to unclaimed/unmapped I/O are dropped, matching the open-bus read fallback above.
        switch (address) {
            case MemoryMap.InterruptFlag:
                m_interrupts.InterruptFlag = value;

                break;
            case MemoryMap.Joypad:
                m_joypad.Write(value: value);

                break;
            case MemoryMap.SerialData:
                m_serial.WriteData(value: value);

                break;
            case MemoryMap.SerialControl:
                m_serial.WriteControl(value: value);

                break;
            case >= MemoryMap.AudioBase and <= MemoryMap.WaveRamEnd:
                m_apu.Write(
                    address: address,
                    value: value
                );

                break;
            case >= MemoryMap.Divider and <= MemoryMap.TimerControl:
                m_timer.WriteRegister(
                    address: address,
                    value: value
                );

                break;
            case MemoryMap.OamDmaStart:
                m_oamDma.Start(page: value);

                break;
            case >= MemoryMap.LcdControl and <= MemoryMap.WindowX:
                m_ppu.WriteRegister(
                    address: address,
                    value: value
                );

                break;
            case MemoryMap.BootRomDisable:
                // Any nonzero write unmaps the boot ROM, and it cannot be remapped without a reset.
                if (value != 0) {
                    m_bootRomMapped = false;
                }

                break;
            case MemoryMap.SpeedSwitch when (m_model == ConsoleModel.Cgb):
                m_armedSpeedSwitch = ((value & 0x01) != 0);

                break;
            case MemoryMap.VideoRamBank when (m_model == ConsoleModel.Cgb):
                m_videoRamBank = (value & 0x01);

                break;
            case MemoryMap.WorkRamBank when (m_model == ConsoleModel.Cgb):
                m_workRamBank = (value & 0x07);

                break;
            case MemoryMap.VramDmaSourceHigh when (m_model == ConsoleModel.Cgb):
                m_hdmaSource = (ushort)((value << 8) | (m_hdmaSource & 0x00F0));

                break;
            case MemoryMap.VramDmaSourceLow when (m_model == ConsoleModel.Cgb):
                m_hdmaSource = (ushort)((m_hdmaSource & 0xFF00) | (value & 0xF0));

                break;
            case MemoryMap.VramDmaDestinationHigh when (m_model == ConsoleModel.Cgb):
                // The destination is a VRAM offset: only the low 5 bits of the high byte are used (0x0000-0x1FF0).
                m_hdmaDestination = (ushort)(((value & 0x1F) << 8) | (m_hdmaDestination & 0x00F0));

                break;
            case MemoryMap.VramDmaDestinationLow when (m_model == ConsoleModel.Cgb):
                m_hdmaDestination = (ushort)((m_hdmaDestination & 0x1F00) | (value & 0xF0));

                break;
            case MemoryMap.VramDmaControl when (m_model == ConsoleModel.Cgb):
                StartVramDma(control: value);

                break;
            case MemoryMap.BackgroundPaletteIndex when (m_model == ConsoleModel.Cgb):
                m_ppu.WriteBackgroundPaletteIndex(value: value);

                break;
            case MemoryMap.BackgroundPaletteData when (m_model == ConsoleModel.Cgb):
                m_ppu.WriteBackgroundPaletteData(value: value);

                break;
            case MemoryMap.ObjectPaletteIndex when (m_model == ConsoleModel.Cgb):
                m_ppu.WriteObjectPaletteIndex(value: value);

                break;
            case MemoryMap.ObjectPaletteData when (m_model == ConsoleModel.Cgb):
                m_ppu.WriteObjectPaletteData(value: value);

                break;
            case MemoryMap.ObjectPriorityMode when (m_model == ConsoleModel.Cgb):
                m_ppu.WriteObjectPriorityMode(value: value);

                break;
            case MemoryMap.CpuMode when (m_model == ConsoleModel.Cgb):
                m_cpuMode = value;

                break;
            case MemoryMap.InfraredPort when (m_model == ConsoleModel.Cgb):
                m_infraredPort = value;

                break;
            case MemoryMap.Undocumented72 when (m_model == ConsoleModel.Cgb):
                m_undocumented72 = value;

                break;
            case MemoryMap.Undocumented73 when (m_model == ConsoleModel.Cgb):
                m_undocumented73 = value;

                break;
            case MemoryMap.Undocumented74 when (m_model == ConsoleModel.Cgb):
                m_undocumented74 = value;

                break;
            case MemoryMap.Undocumented75 when (m_model == ConsoleModel.Cgb):
                m_undocumented75 = (byte)(value & 0x70);

                break;
            default:
                break;
        }
    }

    private int WorkRamOffset(ushort address) {
        // SVBK selects banks 1-7 at 0xD000-0xDFFF; a selected bank of 0 maps to bank 1.
        var highBank = ((m_workRamBank == 0) ? 1 : m_workRamBank);

        return ((address < 0xD000)
            ? (address - MemoryMap.WorkRamBase)
            : ((highBank * WorkRamBankSize) + (address - 0xD000)));
    }

    private bool IsBootRomAddress(ushort address) {
        if (!m_bootRomMapped || (m_bootRom is null)) {
            return false;
        }

        // DMG maps a single 256-byte block; CGB maps 0x000-0x0FF and 0x200-0x8FF, leaving the cartridge header
        // visible through the 0x100-0x1FF hole.
        return ((address <= 0x00FF) || ((m_model == ConsoleModel.Cgb) && (address >= 0x0200) && (address < m_bootRom.Length)));
    }

    private void TickMachineCycle() {
        var lcdDots = (m_clockState.DoubleSpeed ? 2 : 4);

        foreach (var component in m_clockedComponents) {
            var dots = ((component.Domain == ClockDomain.Cpu) ? CpuDotsPerMachineCycle : lcdDots);

            if (dots > 0) {
                component.Step(tCycles: dots);
            }
        }

        m_elapsedDots += (ulong)lcdDots;

        // HBlank VRAM DMA copies one block as each visible line enters horizontal blank.
        if (m_hdmaActive && m_hdmaHBlank) {
            var mode = m_ppu.Mode;

            if ((mode == PpuMode.HorizontalBlank) && (m_previousLcdMode != PpuMode.HorizontalBlank) && (m_ppu.Line < Puck.GameBoy.Ppu.ScreenHeight)) {
                TransferVramDmaBlock();
            }

            m_previousLcdMode = mode;
        }
    }

    // Starts a CGB VRAM DMA from an HDMA5 write: bit 7 selects HBlank mode, the low 7 bits give the block count
    // minus one. A general-purpose transfer (bit 7 clear) runs every block immediately and stalls the CPU; clearing
    // bit 7 while an HBlank transfer is in flight cancels it instead of starting a new one.
    private void StartVramDma(byte control) {
        var hblankMode = ((control & 0x80) != 0);

        // The block count is latched first, before the cancel check — so writing bit 7 clear while an HBlank
        // transfer is in flight both stops it and updates the count the HDMA5 readback reports.
        m_hdmaBlocks = ((control & 0x7F) + 1);

        if (!hblankMode && m_hdmaActive && m_hdmaHBlank) {
            m_hdmaActive = false;
            m_hdmaHBlank = false;

            return;
        }

        m_hdmaActive = true;
        m_hdmaHBlank = hblankMode;

        if (!hblankMode) {
            // General-purpose DMA: copy the whole transfer at once, the CPU paused throughout.
            while (m_hdmaActive) {
                TransferVramDmaBlock();

                for (var cycle = 0; cycle < 8; cycle += 1) {
                    TickMachineCycle();
                }
            }
        }
        else if (m_ppu.Mode == PpuMode.HorizontalBlank) {
            // Started during a horizontal blank: the first block transfers right away.
            TransferVramDmaBlock();
        }
    }

    private void TransferVramDmaBlock() {
        var bankBase = (m_videoRamBank * VideoRamBankSize);

        for (var index = 0; index < 0x10; index += 1) {
            var value = ReadByte(address: m_hdmaSource);

            m_videoRam[bankBase + (m_hdmaDestination & 0x1FFF)] = value;
            m_hdmaSource = (ushort)(m_hdmaSource + 1);
            m_hdmaDestination = (ushort)((m_hdmaDestination + 1) & 0x1FFF);
        }

        m_hdmaBlocks -= 1;

        if (m_hdmaBlocks <= 0) {
            m_hdmaActive = false;
            m_hdmaHBlank = false;
        }
    }
}
