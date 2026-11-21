namespace Puck.HumbleGamingDeck;

/// <summary>
/// What every implemented cartridge board shares: the validated image, its one <see cref="HgdBoardMemory"/>, and the PPU
/// window below $3F00, which reads and writes pattern memory through the board's mapped windows and routes a nametable
/// access by the board's current <see cref="Wiring"/>. A board adds its CPU window, its bank registers and its state.
/// </summary>
public abstract class HgdBoard : IHgdMapper {
    private readonly HgdCartridge m_cartridge;

    /// <summary>Initializes a new instance of the <see cref="HgdBoard"/> class over a fresh <see cref="HgdBoardMemory"/>,
    /// which installs a trainer at $7000 when the image has one.</summary>
    /// <param name="cartridge">The validated, immutable image.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cartridge"/> is <see langword="null"/>.</exception>
    protected HgdBoard(HgdCartridge cartridge) {
        ArgumentNullException.ThrowIfNull(argument: cartridge);
        m_cartridge = cartridge;
        Memory = new(cartridge: cartridge);
        Wiring = HgdBoardMemory.HeaderWiring(mirroring: cartridge.Header.Mirroring);
    }

    /// <inheritdoc/>
    public HgdCartridgeHeader Header => m_cartridge.Header;
    /// <inheritdoc/>
    public abstract bool Irq {
        get;
    }
    /// <inheritdoc/>
    public abstract bool TimesOnEdges {
        get;
    }
    /// <inheritdoc/>
    /// <remarks>The save contains declared PRG NVRAM followed by CHR NVRAM; volatile RAM is excluded.</remarks>
    public Span<byte> BatteryRam => Memory.BatteryRam;

    /// <summary>Gets the board's memory, which owns every RAM byte and the ROM windows.</summary>
    protected HgdBoardMemory Memory {
        get;
    }
    /// <summary>Gets or sets how the board drives CIRAM A10. A board with a register that changes the wiring sets it where
    /// that register changes and after it loads state, so a nametable access never recomputes it.</summary>
    protected HgdNametableWiring Wiring {
        get;
        set;
    }

    /// <inheritdoc/>
    public byte CpuRead(ushort address, byte openBus) =>
        CpuPeek(
            address: address,
            openBus: openBus
        );
    /// <inheritdoc/>
    public abstract byte CpuPeek(ushort address, byte openBus);
    /// <inheritdoc/>
    public abstract void CpuWrite(ushort address, byte value);
    /// <inheritdoc/>
    public byte PpuRead(ushort address, HgdNametableRam nametables) =>
        PpuPeek(
            address: address,
            nametables: nametables
        );
    /// <inheritdoc/>
    public byte PpuPeek(ushort address, HgdNametableRam nametables) =>
        Memory.ReadPpu(
            address: address,
            nametables: nametables,
            wiring: Wiring
        );
    /// <inheritdoc/>
    public void PpuWrite(ushort address, byte value, HgdNametableRam nametables) =>
        Memory.WritePpu(
            address: address,
            nametables: nametables,
            value: value,
            wiring: Wiring
        );
    /// <inheritdoc/>
    public abstract void ObservePpuAddress(ushort address, ulong masterTick);
    /// <inheritdoc/>
    public abstract void ObserveM2(bool high, ulong masterHalfTick);
    /// <inheritdoc/>
    public abstract void SaveState(StateWriter writer);
    /// <inheritdoc/>
    public abstract void LoadState(StateReader reader);
}
