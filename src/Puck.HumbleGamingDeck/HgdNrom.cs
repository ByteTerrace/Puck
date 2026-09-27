namespace Puck.HumbleGamingDeck;

/// <summary>NROM's fixed PRG/CHR windows, optional PRG memory, and header-selected nametable wiring, including the
/// four-screen variant's own 2 KiB of nametable RAM.</summary>
public sealed class HgdNrom : IHgdMapper {
    private readonly HgdCartridge m_cartridge;
    private readonly byte[] m_prgRam;
    private readonly byte[] m_chrRam;
    private readonly byte[] m_fourScreenRam;

    /// <summary>Initializes a new instance of the <see cref="HgdNrom"/> class and installs a trainer at $7000 when present.</summary>
    /// <param name="cartridge">The validated, immutable NROM image.</param>
    /// <exception cref="ArgumentNullException">The cartridge is null.</exception>
    public HgdNrom(HgdCartridge cartridge) {
        ArgumentNullException.ThrowIfNull(argument: cartridge);
        m_cartridge = cartridge;
        m_prgRam = new byte[(Header.PrgRamSize + Header.PrgNvRamSize)];
        m_chrRam = new byte[(Header.ChrRamSize + Header.ChrNvRamSize)];
        m_fourScreenRam = new byte[((Header.Mirroring == HgdMirroring.FourScreen) ? 2048 : 0)];
        if (Header.HasTrainer) {
            cartridge.Trainer.CopyTo(destination: m_prgRam.AsSpan(start: 0x1000));
        }
    }

    /// <inheritdoc/>
    public HgdCartridgeHeader Header => m_cartridge.Header;
    /// <inheritdoc/>
    public bool Irq => false;
    /// <inheritdoc/>
    public Span<byte> BatteryRam => (Header.HasBattery ? m_prgRam.AsSpan() : []);

    /// <inheritdoc/>
    public byte CpuRead(ushort address, byte openBus) {
        return CpuPeek(address: address, openBus: openBus);
    }
    /// <inheritdoc/>
    public byte CpuPeek(ushort address, byte openBus) {
        if (address >= 0x8000) {
            return m_cartridge.PrgRom[address & (m_cartridge.PrgRom.Length - 1)];
        }

        return (((address >= 0x6000) && (m_prgRam.Length != 0)) ? m_prgRam[address & (m_prgRam.Length - 1)] : openBus);
    }
    /// <inheritdoc/>
    public void CpuWrite(ushort address, byte value) {
        if ((address >= 0x6000) && (address < 0x8000) && (m_prgRam.Length != 0)) {
            m_prgRam[address & (m_prgRam.Length - 1)] = value;
        }
    }
    /// <inheritdoc/>
    public byte PpuRead(ushort address, HgdNametableRam nametables) =>
        PpuPeek(
            address: address,
            nametables: nametables
        );
    /// <inheritdoc/>
    public byte PpuPeek(ushort address, HgdNametableRam nametables) {
        if (address < 0x2000) {
            return ((m_chrRam.Length != 0) ? m_chrRam[address & 0x1FFF] : m_cartridge.ChrRom[address & 0x1FFF]);
        }

        var quadrant = (address >> 10) & 3;

        return (((Header.Mirroring == HgdMirroring.FourScreen) && (quadrant >= 2))
            ? m_fourScreenRam[((quadrant & 1) << 10) | (address & 0x3FF)]
            : nametables.Read(
                offset: address,
                page: Page(quadrant: quadrant)
            )
        );
    }
    /// <inheritdoc/>
    public void PpuWrite(ushort address, byte value, HgdNametableRam nametables) {
        if (address < 0x2000) {
            if (m_chrRam.Length != 0) {
                m_chrRam[address & 0x1FFF] = value;
            }

            return;
        }

        var quadrant = (address >> 10) & 3;

        if ((Header.Mirroring == HgdMirroring.FourScreen) && (quadrant >= 2)) {
            m_fourScreenRam[((quadrant & 1) << 10) | (address & 0x3FF)] = value;
        } else {
            nametables.Write(
                offset: address,
                page: Page(quadrant: quadrant),
                value: value
            );
        }
    }
    /// <inheritdoc/>
    public void ObservePpuAddress(ushort address, ulong masterTick) { }
    /// <inheritdoc/>
    public void ObserveM2(bool high, ulong masterHalfTick) { }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    }
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain all declared board RAM.</exception>
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    }

    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Block(values: m_prgRam.AsSpan());
        transfer.Block(values: m_chrRam.AsSpan());
        transfer.Block(values: m_fourScreenRam.AsSpan());
    }
    // CIRAM A10 follows PPU A11 on a horizontally mirrored board and PPU A10 on a vertically mirrored one; a four-screen
    // board answers the upper two quadrants from its own RAM and wires the lower two as vertical mirroring does.
    private int Page(int quadrant) =>
        ((Header.Mirroring == HgdMirroring.Horizontal) ? (quadrant >> 1) : quadrant & 1);
}
