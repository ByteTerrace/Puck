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

    private ulong m_runTargetDots;

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
            ApplyPostBootState(model: model);
        }
    }

    /// <summary>Advances the machine by one CPU instruction (or one machine cycle while halted/stopped),
    /// clocking every other component through the bus.</summary>
    public void Step() =>
        m_cpu.Step();

    /// <summary>Advances the machine by a budget of master clock cycles (T-cycles / PPU dots — the units of
    /// <see cref="SystemBus.ElapsedDots"/>), stepping whole instructions until the budget is met. This is the
    /// deterministic pacing seam a host node drives: the host converts its frame's elapsed engine ticks to an
    /// exact integer cycle budget (the rational accumulator <c>ticks·4194304/50400</c>) and hands it here.</summary>
    /// <param name="cycles">The number of master clock cycles to advance this call.</param>
    /// <remarks>An instruction is the smallest step, so a single call overshoots the budget by at most one
    /// instruction's worth of cycles. The overshoot is carried against a cumulative target, so a sequence of
    /// calls stays cycle-exact in aggregate (no drift) — the long-term cycle count tracks the summed budget to
    /// within one in-flight instruction, which is what keeps host pacing free of accumulating error.</remarks>
    public void Run(ulong cycles) {
        m_runTargetDots += cycles;

        // Step() always advances the bus by at least one machine cycle in every path (including halted and
        // stopped, which fall through to an internal cycle), so ElapsedDots strictly increases and this loop
        // always terminates.
        while (m_bus.ElapsedDots < m_runTargetDots) {
            m_cpu.Step();
        }
    }

    private void ApplyPostBootState(ConsoleModel model) {
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
        // are seeded. (The sound registers are left at reset for now.)
        m_bus.WriteByte(address: MemoryMap.LcdControl, value: 0x91);
        m_bus.WriteByte(address: MemoryMap.BackgroundPalette, value: 0xFC);
        m_bus.WriteByte(address: MemoryMap.ObjectPalette0, value: 0xFF);
        m_bus.WriteByte(address: MemoryMap.ObjectPalette1, value: 0xFF);

        // The divider is not zero at handoff: the boot ROM has been running for a model-specific number of cycles,
        // so the 16-bit internal counter has a precise post-boot phase (DIV reads its high byte). Seeding the exact
        // phase is what the boot_div timing test pins down. (A write to DIV would instead clear the counter, which
        // is why this goes through the dedicated seam.)
        //
        // The literature DMG/MGB value is 0xABCC, but that assumes an I/O read latches on the third T-cycle of the
        // access machine cycle. This bus latches at the end of the machine cycle (ReadCycle ticks the full four
        // T-cycles, then reads), so DIV reads land one T-cycle later in phase; seeding one less (0xABCB) cancels
        // that constant offset exactly and reproduces hardware-observed reads for boot_div's before/after-increment
        // probes. (Verified against the mooneye trace — do NOT "correct" this back to 0xABCC.)
        var postBootDivider = model switch {
            ConsoleModel.Dmg => (ushort)0xABCB,
            _ => (ushort)0x0000,
        };

        m_bus.Timer.SetInternalCounter(value: postBootDivider);

        // During boot the LCD is already running, so a VBlank fires and leaves its flag pending in IF; the boot ROM
        // never clears it. Post-boot IF therefore reads 0xE1 (the VBlank request bit set, the unused upper bits read
        // as one). IME is off and IE is zero at handoff, so nothing dispatches — the flag simply sits pending, as on
        // hardware. (Seeding it is more faithful than leaving IF clear: a cartridge that enables VBlank immediately
        // would take the interrupt on real hardware too.)
        m_bus.WriteByte(address: MemoryMap.InterruptFlag, value: 0x01);
    }
}
