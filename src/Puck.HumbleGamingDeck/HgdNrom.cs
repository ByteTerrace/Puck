namespace Puck.HumbleGamingDeck;

/// <summary>NROM's fixed PRG/CHR windows, optional PRG memory, and header-selected nametable wiring.</summary>
public sealed class HgdNrom : IHgdMapper {
    private readonly HgdCartridge m_cartridge;
    private readonly byte[] m_prgRam;
    private readonly byte[] m_chrRam;

    /// <summary>Initializes a new instance of the <see cref="HgdNrom"/> class and installs a trainer at $7000 when present.</summary>
    /// <param name="cartridge">The validated, immutable NROM image.</param>
    /// <exception cref="ArgumentNullException">The cartridge is null.</exception>
    public HgdNrom(HgdCartridge cartridge) {
        ArgumentNullException.ThrowIfNull(argument: cartridge);
        m_cartridge = cartridge;
        m_prgRam = new byte[(Header.PrgRamSize + Header.PrgNvRamSize)];
        m_chrRam = new byte[(Header.ChrRamSize + Header.ChrNvRamSize)];
        if (Header.HasTrainer) {
            cartridge.Trainer.CopyTo(destination: m_prgRam.AsSpan(start: 0x1000));
        }
    }

    /// <inheritdoc/>
    public HgdCartridgeHeader Header => m_cartridge.Header;
    /// <inheritdoc/>
    public bool Irq => false;

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
    public byte PpuRead(ushort address) {
        return PpuPeek(address: address);
    }
    /// <inheritdoc/>
    public byte PpuPeek(ushort address) {
        return ((m_chrRam.Length != 0) ? m_chrRam[address & 0x1FFF] : m_cartridge.ChrRom[address & 0x1FFF]);
    }
    /// <inheritdoc/>
    public void PpuWrite(ushort address, byte value) {
        if (m_chrRam.Length != 0) {
            m_chrRam[address & 0x1FFF] = value;
        }
    }
    /// <inheritdoc/>
    public void ObservePpuAddress(ushort address, ulong masterTick) { }
    /// <inheritdoc/>
    public void ObserveM2(bool high, ulong masterTick) { }
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
    }
}
