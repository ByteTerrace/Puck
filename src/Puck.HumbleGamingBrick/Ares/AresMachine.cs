namespace Puck.HumbleGamingBrick.Ares;

/// <summary>
/// A Game Boy assembled from the ares-architecture port: the Sharp SM83 CPU drives a per-dot PPU coroutine through
/// the cycle-addressed bus. Because the CPU and PPU share one 4 MHz clock, ares' cothread <c>synchronize</c> reduces
/// exactly to "advance the PPU until its dot clock reaches the CPU's clock", implemented here as a single-threaded
/// coroutine driver — the same interleaving as ares, without the cost of OS-thread handoffs. DMG only; CGB and audio
/// are deferred.
/// </summary>
public sealed class AresMachine {
    /// <summary>Builds a DMG machine for the given ROM image and seeds its post-boot state.</summary>
    /// <param name="rom">The full cartridge ROM image.</param>
    /// <param name="color">Whether to build a Game Boy Color machine (CGB rendering is deferred).</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public AresMachine(byte[] rom, bool color) {
        ArgumentNullException.ThrowIfNull(argument: rom);

        var cartridge = Cartridge.Load(rom: rom);

        Cpu = new AresCpu(color: color);
        Ppu = new AresPpu(color: color);

        var apu = new AresApu();
        var cartridgeAdapter = new AresCartridgeAdapter(cartridge: cartridge);

        Bus = new AresBus(cpu: Cpu, apu: apu, ppu: Ppu, cartridge: cartridgeAdapter);

        Cpu.Connect(bus: Bus);
        Ppu.Connect(cpu: Cpu, bus: Bus);
        Cpu.PpuLine = () => Ppu.Line;
        Cpu.DrivePpu = Ppu.AdvanceTo;

        Cpu.SeedPostBootDmg();
        Ppu.WriteIo(cycle: 2, address: 0xFF47, data: 0xFC); // BGP
        Ppu.WriteIo(cycle: 2, address: 0xFF48, data: 0xFF); // OBP0
        Ppu.WriteIo(cycle: 2, address: 0xFF49, data: 0xFF); // OBP1
        Ppu.WriteIo(cycle: 4, address: 0xFF40, data: 0x91); // LCDC: display + BG enabled
    }

    /// <summary>The CPU.</summary>
    public AresCpu Cpu { get; }

    /// <summary>The PPU (framebuffer and frame-ready signal).</summary>
    public AresPpu Ppu { get; }

    /// <summary>The bus (for untimed peeks).</summary>
    public AresBus Bus { get; }

    /// <summary>Executes one CPU instruction (advancing the PPU/timer through its cycles).</summary>
    public void Step() =>
        Cpu.Main();

    /// <summary>Runs instructions until the PPU signals a completed frame.</summary>
    public void RunFrame() {
        while (!Ppu.ConsumeFrameReady()) {
            Cpu.Main();
        }
    }

    /// <summary>Reads a byte without advancing time (for terminal-condition detection).</summary>
    /// <param name="address">The address to peek.</param>
    public byte Peek(ushort address) =>
        Bus.Read(address: address, data: 0xFF);
}
