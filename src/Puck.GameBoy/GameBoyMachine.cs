namespace Puck.GameBoy;

/// <summary>
/// A fully assembled Game Boy: the CPU bound to the bus, which owns the timer, OAM DMA, and PPU and routes the
/// cartridge. The CPU is the bus master, so advancing the machine is simply stepping the CPU — every other
/// component is clocked through the bus's cycle accessors. When constructed without a boot ROM the machine is
/// initialized to the model's documented post-boot state, so cartridges can run directly from <c>0x0100</c>.
/// </summary>
public sealed class GameBoyMachine {
    private readonly SystemBus m_bus;
    private readonly Sm83 m_cpu;

    /// <summary>Gets the CPU.</summary>
    public Sm83 Cpu =>
        m_cpu;
    /// <summary>Gets the bus.</summary>
    public SystemBus Bus =>
        m_bus;
    /// <summary>Gets the PPU, whose framebuffer is the machine's video output.</summary>
    public Ppu Ppu =>
        m_bus.Ppu;

    /// <summary>Assembles a machine for a model with a cartridge and an optional boot ROM.</summary>
    /// <param name="model">The Game Boy model to emulate.</param>
    /// <param name="cartridge">The cartridge plugged into the bus.</param>
    /// <param name="bootRom">The boot ROM to run from reset, or <see langword="null"/> to start at the post-boot state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cartridge"/> is <see langword="null"/>.</exception>
    public GameBoyMachine(ConsoleModel model, ICartridge cartridge, byte[]? bootRom = null) {
        ArgumentNullException.ThrowIfNull(cartridge);

        m_bus = new SystemBus(
            bootRom: bootRom,
            cartridge: cartridge,
            model: model
        );
        m_cpu = new Sm83(bus: m_bus);

        if (bootRom is null) {
            ApplyPostBootState();
        }
    }

    /// <summary>Advances the machine by one CPU instruction (or one machine cycle while halted/stopped),
    /// clocking every other component through the bus.</summary>
    public void Step() =>
        m_cpu.Step();

    private void ApplyPostBootState() {
        // The DMG boot ROM's handoff state. (The half-carry/carry flags depend on the header checksum, which is
        // non-zero for all real cartridges and test ROMs, giving F = 0xB0.)
        m_cpu.A = 0x01;
        m_cpu.F = 0xB0;
        m_cpu.B = 0x00;
        m_cpu.C = 0x13;
        m_cpu.D = 0x00;
        m_cpu.E = 0xD8;
        m_cpu.H = 0x01;
        m_cpu.L = 0x4D;
        m_cpu.StackPointer = 0xFFFE;
        m_cpu.ProgramCounter = 0x0100;

        // The post-boot I/O state cartridges rely on: the LCD is on with the background enabled, and the palettes
        // are seeded. (DIV and the sound registers are left at reset for now.)
        m_bus.WriteByte(address: MemoryMap.LcdControl, value: 0x91);
        m_bus.WriteByte(address: MemoryMap.BackgroundPalette, value: 0xFC);
        m_bus.WriteByte(address: MemoryMap.ObjectPalette0, value: 0xFF);
        m_bus.WriteByte(address: MemoryMap.ObjectPalette1, value: 0xFF);
    }
}
