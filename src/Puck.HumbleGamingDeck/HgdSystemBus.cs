namespace Puck.HumbleGamingDeck;

/// <summary>
/// The CPU address map and its data-bus latch: 2 KiB of work RAM mirrored through $1FFF, the PPU's eight registers
/// mirrored through $3FFF, the APU and I/O registers at $4000-$4017, and the cartridge from $4020. The latch holds the
/// last value driven on the data bus, which a read of anything that drives nothing returns. A $4015 read is internal to
/// the 2A03 and leaves the latch as it was.
/// https://www.nesdev.org/wiki/CPU_memory_map and https://www.nesdev.org/wiki/Open_bus_behavior
/// </summary>
public sealed class HgdSystemBus : ISnapshotable {
    private readonly byte[] m_workRam = new byte[2048];
    private readonly HgdPpu m_ppu;
    private readonly HgdApu m_apu;
    private readonly HgdControllers m_controllers;
    private readonly HgdDma m_dma;

    private byte m_openBus;

    /// <summary>Initializes a new instance of the <see cref="HgdSystemBus"/> class with deterministic work RAM.</summary>
    /// <param name="mapper">The machine's independently owned cartridge board.</param>
    /// <param name="ppu">The PPU behind $2000-$3FFF.</param>
    /// <param name="apu">The APU behind $4000-$4013, $4015 and $4017 writes.</param>
    /// <param name="controllers">The controller ports behind $4016 and $4017.</param>
    /// <param name="dma">The DMA unit a $4014 write starts.</param>
    /// <param name="workRamFill">The power-up byte for all 2 KiB of work RAM.</param>
    /// <exception cref="ArgumentNullException">Any component is <see langword="null"/>.</exception>
    public HgdSystemBus(IHgdMapper mapper, HgdPpu ppu, HgdApu apu, HgdControllers controllers, HgdDma dma, byte workRamFill = 0) {
        ArgumentNullException.ThrowIfNull(argument: mapper);
        ArgumentNullException.ThrowIfNull(argument: ppu);
        ArgumentNullException.ThrowIfNull(argument: apu);
        ArgumentNullException.ThrowIfNull(argument: controllers);
        ArgumentNullException.ThrowIfNull(argument: dma);

        Mapper = mapper;
        m_ppu = ppu;
        m_apu = apu;
        m_controllers = controllers;
        m_dma = dma;
        m_workRam.AsSpan().Fill(value: workRamFill);
    }

    /// <summary>Gets the board that owns $4020-$FFFF.</summary>
    public IHgdMapper Mapper {
        get;
    }
    /// <summary>Gets the last value driven on the CPU data bus.</summary>
    public byte OpenBus => m_openBus;

    /// <summary>Reads one byte from the CPU address map with every side effect the read has.</summary>
    /// <param name="address">The CPU address.</param>
    /// <returns>The data-bus value.</returns>
    public byte Read(ushort address) {
        if (address < 0x2000) {
            m_openBus = m_workRam[address & 0x7FF];
        } else if (address < 0x4000) {
            m_openBus = m_ppu.ReadRegister(register: address);
        } else if (address == 0x4015) {
            return m_apu.ReadStatus(openBus: m_openBus);
        } else if (address == 0x4016) {
            m_openBus = m_controllers.Read(
                openBus: m_openBus,
                port: 0
            );
        } else if (address == 0x4017) {
            m_openBus = m_controllers.Read(
                openBus: m_openBus,
                port: 1
            );
        } else if (address >= 0x4020) {
            m_openBus = Mapper.CpuRead(
                address: address,
                openBus: m_openBus
            );
        }

        return m_openBus;
    }
    /// <summary>Writes one byte; the value stays on the data bus whether or not anything decodes the address.</summary>
    /// <param name="address">The CPU address.</param>
    /// <param name="value">The driven data byte.</param>
    public void Write(ushort address, byte value) {
        m_openBus = value;
        if (address < 0x2000) {
            m_workRam[address & 0x7FF] = value;
        } else if (address < 0x4000) {
            m_ppu.WriteRegister(
                register: address,
                value: value
            );
        } else if (address == 0x4014) {
            m_dma.RequestObjectMemory(page: value);
        } else if (address == 0x4016) {
            m_controllers.Write(value: value);
        } else if (address <= 0x4017) {
            m_apu.WriteRegister(
                address: address,
                value: value
            );
        } else if (address >= 0x4020) {
            Mapper.CpuWrite(
                address: address,
                value: value
            );
        }
    }
    /// <summary>Peeks without changing the data bus or any component's state.</summary>
    /// <param name="address">The CPU address.</param>
    /// <returns>The byte a read would return now.</returns>
    public byte Peek(ushort address) {
        if (address < 0x2000) {
            return m_workRam[address & 0x7FF];
        }
        if (address < 0x4000) {
            return m_ppu.PeekRegister(register: address);
        }

        return address switch {
            0x4015 => m_apu.PeekStatus(openBus: m_openBus),
            0x4016 => m_controllers.Peek(
                openBus: m_openBus,
                port: 0
            ),
            0x4017 => m_controllers.Peek(
                openBus: m_openBus,
                port: 1
            ),
            >= 0x4020 => Mapper.CpuPeek(
                address: address,
                openBus: m_openBus
            ),
            _ => m_openBus,
        };
    }
    /// <summary>Stores a byte in work RAM without driving the data bus, a debugger's write outside replay.</summary>
    /// <param name="address">The CPU address; only $0000-$1FFF reaches work RAM, and any other address is ignored.</param>
    /// <param name="value">The byte to store.</param>
    public void Poke(ushort address, byte value) {
        if (address < 0x2000) {
            m_workRam[address & 0x7FF] = value;
        }
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain complete work RAM and open-bus
    /// state.</exception>
    public void LoadState(StateReader reader) =>
        TransferState(transfer: new StateLoadTransfer(reader: reader));

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
    public byte Read(ushort address) =>
        m_bus.Read(address: address);
    /// <inheritdoc/>
    public void Write(ushort address, byte value) =>
        m_bus.Write(
            address: address,
            value: value
        );
}
