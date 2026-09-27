namespace Puck.HumbleGamingDeck;

/// <summary>The CPU address map and data-bus latch. The $2000-$3FFF and $4000-$401F register windows currently
/// return CPU open bus on reads and ignore writes; their PPU, APU, and controller owners are not implemented.</summary>
public sealed class HgdSystemBus : ISnapshotable {
    private readonly byte[] m_workRam = new byte[2048];

    private byte m_openBus;

    /// <summary>Initializes a new instance of the <see cref="HgdSystemBus"/> class with deterministic work RAM.</summary>
    /// <param name="mapper">The machine's independently owned cartridge board.</param>
    /// <param name="workRamFill">The power-up byte for all 2 KiB of RAM.</param>
    /// <exception cref="ArgumentNullException">The mapper is null.</exception>
    public HgdSystemBus(IHgdMapper mapper, byte workRamFill = 0) {
        ArgumentNullException.ThrowIfNull(argument: mapper);
        Mapper = mapper;
        m_workRam.AsSpan().Fill(value: workRamFill);
    }

    /// <summary>Gets the board that owns $4020-$FFFF.</summary>
    public IHgdMapper Mapper {
        get;
    }
    /// <summary>Gets the last value driven on the CPU data bus by a read or write.</summary>
    public byte OpenBus => m_openBus;

    /// <summary>Reads and latches one byte from the CPU address map.</summary>
    /// <param name="address">The CPU address.</param>
    /// <returns>The data-bus value, including open bus for unowned register windows.</returns>
    public byte Read(ushort address) {
        m_openBus = ((address < 0x2000) ? m_workRam[address & 0x7FF]
            : ((address >= 0x4020) ? Mapper.CpuRead(address: address, openBus: m_openBus) : m_openBus));

        return m_openBus;
    }
    /// <summary>Writes and latches one byte. Unowned register windows ignore the write but the bus retains its value.</summary>
    /// <param name="address">The CPU address.</param>
    /// <param name="value">The driven data byte.</param>
    public void Write(ushort address, byte value) {
        m_openBus = value;
        if (address < 0x2000) {
            m_workRam[address & 0x7FF] = value;
        } else if (address >= 0x4020) {
            Mapper.CpuWrite(address: address, value: value);
        }
    }
    /// <summary>Peeks without changing open bus or mapper state.</summary>
    /// <param name="address">The CPU address.</param>
    /// <returns>The byte currently visible at the address.</returns>
    public byte Peek(ushort address) {
        return ((address < 0x2000) ? m_workRam[address & 0x7FF]
            : ((address >= 0x4020) ? Mapper.CpuPeek(address: address, openBus: m_openBus) : m_openBus));
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    }
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain complete work RAM and open-bus state.</exception>
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));
    }

    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Block(values: m_workRam.AsSpan());
        transfer.Byte(value: ref m_openBus);
    }
}
/// <summary>The statically specialized CPU adapter for the NES address map.</summary>
public readonly struct HgdCpuBus : IHgdCpuBus {
    private readonly HgdSystemBus m_bus;

    /// <summary>Initializes a new instance of the <see cref="HgdCpuBus"/> struct over the machine's bus.</summary>
    /// <param name="bus">The bus whose reads and writes it forwards.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bus"/> is <see langword="null"/>.</exception>
    public HgdCpuBus(HgdSystemBus bus) {
        ArgumentNullException.ThrowIfNull(argument: bus);

        m_bus = bus;
    }

    /// <inheritdoc/>
    public byte Read(ushort address) {
        return m_bus.Read(address: address);
    }
    /// <inheritdoc/>
    public void Write(ushort address, byte value) {
        m_bus.Write(address: address, value: value);
    }
}
