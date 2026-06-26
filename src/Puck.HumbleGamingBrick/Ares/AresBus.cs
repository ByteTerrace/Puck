namespace Puck.HumbleGamingBrick.Ares;

/// <summary>A component that participates in the cycle-addressed bus. Each access is presented at sub-cycles 0-4; a
/// component reads/writes its registers at the cycle the hardware latches (typically 2, or 4 for LCDC/STAT), and
/// returns <paramref name="data"/> unchanged otherwise. Reads AND their contribution into the open-bus value.</summary>
public interface IAresIo {
    /// <summary>Returns this component's contribution to a read at the given sub-cycle (ANDed into the bus value).</summary>
    byte ReadIo(int cycle, ushort address, byte data);

    /// <summary>Applies a write at the given sub-cycle, if this component owns the address at that cycle.</summary>
    void WriteIo(int cycle, ushort address, byte data);
}

/// <summary>
/// The Game Boy bus, ported from ares (<c>gb/bus</c>). It routes each cycle-addressed access through the CPU, APU,
/// PPU, and cartridge in turn — exactly the order ares uses — so each access lands at its hardware sub-cycle.
/// </summary>
public sealed class AresBus {
    private readonly IAresIo m_cpu;
    private readonly IAresIo m_apu;
    private readonly IAresIo m_ppu;
    private readonly IAresIo m_cartridge;

    /// <summary>Wires the bus to the four register owners, in ares' routing order.</summary>
    /// <param name="cpu">The CPU (work RAM, HRAM, JOYP, timer, serial, interrupts, KEY1, HDMA, …).</param>
    /// <param name="apu">The APU (sound registers and wave RAM).</param>
    /// <param name="ppu">The PPU (VRAM, OAM, LCDC/STAT/SCY/SCX/LY/LYC/DMA/BGP/OBP/WX/WY and CGB palettes).</param>
    /// <param name="cartridge">The cartridge (ROM and cartridge RAM).</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AresBus(IAresIo cpu, IAresIo apu, IAresIo ppu, IAresIo cartridge) {
        ArgumentNullException.ThrowIfNull(argument: cpu);
        ArgumentNullException.ThrowIfNull(argument: apu);
        ArgumentNullException.ThrowIfNull(argument: ppu);
        ArgumentNullException.ThrowIfNull(argument: cartridge);

        m_cpu = cpu;
        m_apu = apu;
        m_ppu = ppu;
        m_cartridge = cartridge;
    }

    /// <summary>Reads a byte at the given bus sub-cycle, ANDing each component's contribution into the open-bus value.</summary>
    public byte Read(int cycle, ushort address, byte data) {
        data &= m_cpu.ReadIo(cycle: cycle, address: address, data: data);
        data &= m_apu.ReadIo(cycle: cycle, address: address, data: data);
        data &= m_ppu.ReadIo(cycle: cycle, address: address, data: data);
        data &= m_cartridge.ReadIo(cycle: cycle, address: address, data: data);

        return data;
    }

    /// <summary>Writes a byte at the given bus sub-cycle to every component.</summary>
    public void Write(int cycle, ushort address, byte data) {
        m_cpu.WriteIo(cycle: cycle, address: address, data: data);
        m_apu.WriteIo(cycle: cycle, address: address, data: data);
        m_ppu.WriteIo(cycle: cycle, address: address, data: data);
        m_cartridge.WriteIo(cycle: cycle, address: address, data: data);
    }

    /// <summary>An untimed read (used by OAM/VRAM DMA), sampling only the cycles that latch.</summary>
    public byte Read(ushort address, byte data) {
        data &= Read(cycle: 2, address: address, data: data);
        data &= Read(cycle: 4, address: address, data: data);

        return data;
    }

    /// <summary>An untimed write (used by VRAM DMA).</summary>
    public void Write(ushort address, byte data) {
        Write(cycle: 2, address: address, data: data);
        Write(cycle: 4, address: address, data: data);
    }
}
