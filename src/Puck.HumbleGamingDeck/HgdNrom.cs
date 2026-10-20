namespace Puck.HumbleGamingDeck;

/// <summary>NROM's fixed PRG/CHR windows, optional PRG memory, and header-selected nametable wiring, including the
/// four-screen variant's own 2 KiB of nametable RAM.</summary>
public sealed class HgdNrom : IHgdMapper {
    private readonly HgdCartridge m_cartridge;
    private readonly HgdBoardMemory m_memory;

    /// <summary>Initializes a new instance of the <see cref="HgdNrom"/> class and installs a trainer at $7000 when present.</summary>
    /// <param name="cartridge">The validated, immutable NROM image.</param>
    /// <exception cref="ArgumentNullException">The cartridge is null.</exception>
    public HgdNrom(HgdCartridge cartridge) {
        ArgumentNullException.ThrowIfNull(argument: cartridge);
        m_cartridge = cartridge;
        m_memory = new(cartridge: cartridge);
    }

    /// <inheritdoc/>
    public HgdCartridgeHeader Header => m_cartridge.Header;
    /// <inheritdoc/>
    public bool Irq => false;
    /// <inheritdoc/>
    /// <remarks>The save contains declared PRG NVRAM followed by CHR NVRAM; volatile RAM is excluded.</remarks>
    public Span<byte> BatteryRam => m_memory.BatteryRam;

    /// <inheritdoc/>
    public byte CpuRead(ushort address, byte openBus) {
        return CpuPeek(address: address, openBus: openBus);
    }
    /// <inheritdoc/>
    public byte CpuPeek(ushort address, byte openBus) {
        if (address >= 0x8000) {
            return m_memory.PrgRom[address & (m_memory.PrgRom.Length - 1)];
        }

        var size = m_memory.PrgRamLength;

        return (((address < 0x6000) || (size == 0)) ? openBus : m_memory.ReadPrgRam(offset: address & (size - 1)));
    }
    /// <inheritdoc/>
    public void CpuWrite(ushort address, byte value) {
        var size = m_memory.PrgRamLength;

        if ((address >= 0x6000) && (address < 0x8000) && (size != 0)) {
            m_memory.WritePrgRam(offset: address & (size - 1), value: value);
        }
    }
    /// <inheritdoc/>
    public byte PpuRead(ushort address, HgdNametableRam nametables) =>
        PpuPeek(
            address: address,
            nametables: nametables
        );
    /// <inheritdoc/>
    public byte PpuPeek(ushort address, HgdNametableRam nametables) =>
        ((address < 0x2000)
            ? m_memory.ReadChr(offset: address)
            : m_memory.ReadNametable(
                address: address,
                nametables: nametables,
                page: HgdBoardMemory.HeaderPage(address: address, mirroring: Header.Mirroring)
            )
        );
    /// <inheritdoc/>
    public void PpuWrite(ushort address, byte value, HgdNametableRam nametables) {
        if (address < 0x2000) {
            m_memory.WriteChr(offset: address, value: value);
        } else {
            m_memory.WriteNametable(
                address: address,
                nametables: nametables,
                page: HgdBoardMemory.HeaderPage(address: address, mirroring: Header.Mirroring),
                value: value
            );
        }
    }
    /// <inheritdoc/>
    public void ObservePpuAddress(ushort address, ulong masterTick) { }
    /// <inheritdoc/>
    public void ObserveM2(bool high, ulong masterHalfTick) { }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        m_memory.Transfer(transfer: new StateSaveTransfer(writer: writer));
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain all declared board RAM.</exception>
    public void LoadState(StateReader reader) =>
        m_memory.Transfer(transfer: new StateLoadTransfer(reader: reader));
}
