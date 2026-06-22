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
public sealed class SystemBus : ICpuBus {
    private const int CpuDotsPerMachineCycle = 4;
    private const int WorkRamBankSize = 0x1000;
    private const int VideoRamBankSize = 0x2000;

    private readonly List<IClockedComponent> m_clockedComponents = [];
    private readonly ICartridge m_cartridge;
    private readonly byte[] m_highRam = new byte[0x7F];
    private readonly InterruptController m_interrupts = new();
    private readonly Joypad m_joypad;
    private readonly Serial m_serial;
    private readonly byte[]? m_bootRom;
    private readonly ConsoleModel m_model;
    private readonly byte[] m_oam = new byte[0xA0];
    private readonly OamDma m_oamDma;
    private readonly Ppu m_ppu;
    private readonly Timer m_timer;
    private readonly byte[] m_videoRam;
    private readonly byte[] m_workRam;

    private bool m_armedSpeedSwitch;
    private bool m_bootRomMapped;
    private bool m_doubleSpeed;
    private ulong m_elapsedDots;
    private int m_videoRamBank;
    private int m_workRamBank = 1;

    /// <summary>Gets the model this bus emulates.</summary>
    public ConsoleModel Model =>
        m_model;
    /// <summary>Gets the interrupt controller, through which components raise interrupts and the CPU services them.</summary>
    public InterruptController Interrupts =>
        m_interrupts;
    /// <summary>Gets whether the CPU clock is in CGB double-speed mode, which halves the number of LCD-domain
    /// T-cycles per machine cycle. Always <see langword="false"/> on the DMG.</summary>
    public bool DoubleSpeed =>
        m_doubleSpeed;
    /// <summary>Gets the total LCD-domain T-cycles (dots) elapsed since construction — the machine's wall clock.</summary>
    public ulong ElapsedDots =>
        m_elapsedDots;
    /// <summary>Gets the object-attribute memory, for the PPU to scan and OAM DMA to fill.</summary>
    public Span<byte> Oam =>
        m_oam;
    /// <summary>Gets the video RAM, for the PPU to fetch tiles and maps from.</summary>
    public Span<byte> VideoRam =>
        m_videoRam;

    /// <summary>Initializes the bus for a model with a cartridge and an optional boot ROM.</summary>
    /// <param name="model">The Game Boy model to emulate, which sizes the banked memories.</param>
    /// <param name="cartridge">The cartridge plugged into the bus.</param>
    /// <param name="bootRom">The boot ROM mapped over low memory until disabled, or <see langword="null"/> to start post-boot.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cartridge"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="bootRom"/> is shorter than the region the model maps it over.</exception>
    public SystemBus(ConsoleModel model, ICartridge cartridge, byte[]? bootRom = null) {
        ArgumentNullException.ThrowIfNull(cartridge);

        var isColor = (model == ConsoleModel.Cgb);

        // The overlay indexes the boot ROM directly (DMG over 0x000-0x0FF, CGB over 0x000-0x0FF and
        // 0x200-end), so reject an image too short to back that range rather than fault on first fetch.
        var requiredBootRomLength = (isColor ? 0x0900 : 0x0100);

        if ((bootRom is not null) && (bootRom.Length < requiredBootRomLength)) {
            throw new ArgumentException(
                message: $"A {model} boot ROM must be at least 0x{requiredBootRomLength:X} bytes; got 0x{bootRom.Length:X}.",
                paramName: nameof(bootRom)
            );
        }

        m_bootRom = bootRom;
        m_bootRomMapped = (bootRom is not null);
        m_cartridge = cartridge;
        m_model = model;
        m_videoRam = new byte[VideoRamBankSize * (isColor ? 2 : 1)];
        m_workRam = new byte[WorkRamBankSize * (isColor ? 8 : 2)];

        // The timer and OAM DMA are internal SoC components the bus owns and clocks (the cartridge, by contrast,
        // is plugged in). OAM DMA reads its source through the untimed ReadByte path.
        m_timer = new Timer(interrupts: m_interrupts);
        Attach(component: m_timer);

        m_oamDma = new OamDma(oam: m_oam, readSource: ReadDmaSource);
        Attach(component: m_oamDma);

        m_ppu = new Ppu(interrupts: m_interrupts, videoRam: m_videoRam, objectAttributeMemory: m_oam);
        Attach(component: m_ppu);

        m_joypad = new Joypad(interrupts: m_interrupts);
        m_serial = new Serial(interrupts: m_interrupts);
        Attach(component: m_serial);
    }

    /// <summary>Gets the picture processing unit, for the host to present its framebuffer.</summary>
    public Ppu Ppu =>
        m_ppu;
    /// <summary>Gets the timer and divider, for seeding the post-boot divider phase.</summary>
    public Timer Timer =>
        m_timer;
    /// <summary>Gets the joypad, for the host to feed button input.</summary>
    public Joypad Joypad =>
        m_joypad;
    /// <summary>Gets the serial port, for capturing serial output.</summary>
    public Serial Serial =>
        m_serial;

    /// <summary>Registers a component to be advanced each machine cycle in its clock domain.</summary>
    /// <param name="component">The component to clock alongside the CPU.</param>
    /// <exception cref="ArgumentNullException"><paramref name="component"/> is <see langword="null"/>.</exception>
    public void Attach(IClockedComponent component) {
        ArgumentNullException.ThrowIfNull(component);

        m_clockedComponents.Add(item: component);
    }

    /// <summary>Reads a byte over one machine cycle: advances the rest of the machine, then performs the access.</summary>
    /// <param name="address">The CPU address to read.</param>
    /// <returns>The byte the CPU latches at the end of the machine cycle.</returns>
    public byte ReadCycle(ushort address) {
        TickMachineCycle();

        return ReadByte(address: address);
    }
    /// <summary>Writes a byte over one machine cycle: advances the rest of the machine, then performs the access.</summary>
    /// <param name="address">The CPU address to write.</param>
    /// <param name="value">The value to store.</param>
    public void WriteCycle(ushort address, byte value) {
        TickMachineCycle();
        WriteByte(
            address: address,
            value: value
        );
    }
    /// <summary>Advances the machine by one machine cycle of internal CPU work that performs no bus access.</summary>
    public void InternalCycle() =>
        TickMachineCycle();

    /// <summary>Performs a CGB speed switch when one has been armed via <c>KEY1</c>, as the <c>STOP</c> instruction
    /// does. Toggles between normal and double speed and disarms the request.</summary>
    /// <returns><see langword="true"/> when a switch was armed and performed; otherwise <see langword="false"/>.</returns>
    public bool ApplyPreparedSpeedSwitch() {
        if (!m_armedSpeedSwitch) {
            return false;
        }

        m_armedSpeedSwitch = false;
        m_doubleSpeed = !m_doubleSpeed;

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
                // VRAM is locked while the PPU is drawing the scanline.
                if (m_ppu.IsVideoRamAccessible) {
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
                // Object-attribute memory is locked during an OAM DMA transfer and while the PPU scans/draws.
                if (!m_oamDma.IsOamLocked && m_ppu.IsObjectMemoryAccessible) {
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
            >= MemoryMap.Divider and <= MemoryMap.TimerControl => m_timer.ReadRegister(address: address),
            MemoryMap.OamDmaStart => m_oamDma.Page,
            >= MemoryMap.LcdControl and <= MemoryMap.WindowX => m_ppu.ReadRegister(address: address),
            MemoryMap.BootRomDisable => (byte)(0xFE | (m_bootRomMapped ? 0x00 : 0x01)),
            MemoryMap.SpeedSwitch when (m_model == ConsoleModel.Cgb) =>
                (byte)(0x7E | (m_doubleSpeed ? 0x80 : 0x00) | (m_armedSpeedSwitch ? 0x01 : 0x00)),
            MemoryMap.VideoRamBank when (m_model == ConsoleModel.Cgb) =>
                (byte)(0xFE | m_videoRamBank),
            MemoryMap.WorkRamBank when (m_model == ConsoleModel.Cgb) =>
                (byte)(0xF8 | m_workRamBank),
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
        var lcdDots = (m_doubleSpeed ? 2 : 4);

        foreach (var component in m_clockedComponents) {
            component.Step(tCycles: ((component.Domain == ClockDomain.Cpu) ? CpuDotsPerMachineCycle : lcdDots));
        }

        m_elapsedDots += (ulong)lcdDots;
    }
}
