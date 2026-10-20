namespace Puck.HumbleGamingDeck;

/// <summary>
/// The MMC1 board family (mapper 1: SxROM): a five-bit serial port at $8000-$FFFF loading four internal registers, which
/// select 16 KiB or 32 KiB PRG banking, 4 KiB or 8 KiB CHR banking, one-screen, vertical or horizontal mirroring, and
/// whether PRG-RAM answers at $6000-$7FFF.
/// <para>
/// A write with bit 7 set empties the shift register and sets the control register's PRG mode to 3. Otherwise bit 0
/// shifts in, and the fifth bit loads the register that address bits 13 and 14 select. A write on the CPU cycle right
/// after another write is ignored, whatever its value, so a read-modify-write instruction reaches the port only with the
/// unmodified value it writes first (https://www.nesdev.org/wiki/MMC1#Consecutive-cycle_writes). The board counts CPU
/// cycles from M2's falling edges, never from a host clock.
/// </para>
/// <para>
/// The larger SxROM boards reuse the selected CHR register's upper bits: on PRG-ROM over 256 KiB (SUROM, SXROM) bit 4
/// picks the 256 KiB half, and on 16 KiB (SOROM) or 32 KiB (SXROM) of PRG-RAM bits 3, or 3 and 2, pick its 8 KiB bank.
/// In 4 KiB CHR mode the selected register is the one PPU A12 last chose, as the board's own CHR outputs are. Power-on
/// state is the control register at $0C (the last bank fixed at $C000, so the reset vector is reachable) and every other
/// register zero (https://www.nesdev.org/wiki/MMC1).
/// </para>
/// </summary>
public sealed class HgdMmc1 : IHgdMapper {
    private const byte EmptyShift = 0x10;

    private readonly HgdCartridge m_cartridge;
    private readonly HgdBoardMemory m_memory;
    private readonly int m_prgBanks;

    private byte m_shift = EmptyShift;
    private byte m_control = 0x0C;
    private byte m_chrBank0;
    private byte m_chrBank1;
    private byte m_prgBank;
    private byte m_cyclesSinceWrite = 2;
    private bool m_ppuA12;

    /// <summary>Initializes a new instance of the <see cref="HgdMmc1"/> class at power-on, and installs a trainer at $7000
    /// when present.</summary>
    /// <param name="cartridge">The validated, immutable MMC1 image.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cartridge"/> is <see langword="null"/>.</exception>
    public HgdMmc1(HgdCartridge cartridge) {
        ArgumentNullException.ThrowIfNull(argument: cartridge);
        m_cartridge = cartridge;
        m_memory = new(cartridge: cartridge);
        m_prgBanks = (m_memory.PrgRom.Length >> 14);
    }

    /// <inheritdoc/>
    public HgdCartridgeHeader Header => m_cartridge.Header;
    /// <inheritdoc/>
    public bool Irq => false;
    /// <inheritdoc/>
    /// <remarks>The save contains declared PRG NVRAM followed by CHR NVRAM; volatile RAM is excluded.</remarks>
    public Span<byte> BatteryRam => m_memory.BatteryRam;
    /// <summary>Gets the control register: mirroring in bits 0-1, PRG mode in bits 2-3, CHR mode in bit 4.</summary>
    public byte Control => m_control;
    /// <summary>Gets the shift register, a marker bit above the bits shifted in so far; $10 when empty.</summary>
    public byte Shift => m_shift;

    /// <inheritdoc/>
    public byte CpuRead(ushort address, byte openBus) =>
        CpuPeek(
            address: address,
            openBus: openBus
        );
    /// <inheritdoc/>
    public byte CpuPeek(ushort address, byte openBus) {
        if (address >= 0x8000) {
            return m_memory.PrgRom[PrgOffset(address: address)];
        }

        return ((PrgRamOffset(address: address) is { } offset) ? m_memory.ReadPrgRam(offset: offset) : openBus);
    }
    /// <inheritdoc/>
    public void CpuWrite(ushort address, byte value) {
        if (address < 0x8000) {
            if (PrgRamOffset(address: address) is { } offset) {
                m_memory.WritePrgRam(
                    offset: offset,
                    value: value
                );
            }

            return;
        }

        var consecutive = (m_cyclesSinceWrite < 2);

        m_cyclesSinceWrite = 0;
        if (consecutive) {
            return;
        }
        if ((value & 0x80) != 0) {
            m_shift = EmptyShift;
            m_control |= 0x0C;

            return;
        }

        var full = ((m_shift & 1) != 0);

        m_shift = ((byte)((m_shift >> 1) | ((value & 1) << 4)));
        if (!full) {
            return;
        }
        switch ((address >> 13) & 3) {
            case 0:
                m_control = m_shift;

                break;
            case 1:
                m_chrBank0 = m_shift;

                break;
            case 2:
                m_chrBank1 = m_shift;

                break;
            default:
                m_prgBank = m_shift;

                break;
        }
        m_shift = EmptyShift;
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
            ? m_memory.ReadChr(offset: ChrOffset(address: address))
            : m_memory.ReadNametable(
                address: address,
                nametables: nametables,
                page: Page(address: address)
            )
        );
    /// <inheritdoc/>
    public void PpuWrite(ushort address, byte value, HgdNametableRam nametables) {
        if (address < 0x2000) {
            m_memory.WriteChr(
                offset: ChrOffset(address: address),
                value: value
            );
        } else {
            m_memory.WriteNametable(
                address: address,
                nametables: nametables,
                page: Page(address: address),
                value: value
            );
        }
    }
    /// <inheritdoc/>
    public void ObservePpuAddress(ushort address, ulong masterTick) =>
        m_ppuA12 = ((address & 0x1000) != 0);
    /// <inheritdoc/>
    public void ObserveM2(bool high, ulong masterHalfTick) {
        if (!high && (m_cyclesSinceWrite < 2)) {
            ++m_cyclesSinceWrite;
        }
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain the complete board state.</exception>
    /// <exception cref="InvalidDataException">The serialized shift register or write spacing is out of range.</exception>
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));
        if ((m_shift == 0) || (m_shift > 0x1F) || (m_cyclesSinceWrite > 2)) {
            throw new InvalidDataException(message: "Snapshot MMC1 shift register or write spacing is out of range.");
        }
    }

    // The CHR register whose upper bits reach the PRG and PRG-RAM lines: CHR bank 0 in 8 KiB mode, and in 4 KiB mode the
    // one PPU A12 selects.
    private byte OuterRegister => ((((m_control & 0x10) != 0) && m_ppuA12) ? m_chrBank1 : m_chrBank0);

    private int PrgOffset(ushort address) {
        var bank = (m_prgBank & 0x0F);
        var upper = (address >= 0xC000);

        bank = ((m_control >> 2) & 3) switch {
            0 or 1 => ((bank & 0x0E) | (upper ? 1 : 0)),
            2 => (upper ? bank : 0),
            _ => (upper ? 0x0F : bank),
        };
        if (m_prgBanks > 16) {
            bank |= (OuterRegister & 0x10);
        }

        return (((bank % m_prgBanks) << 14) | (address & 0x3FFF));
    }
    private int? PrgRamOffset(ushort address) {
        var size = m_memory.PrgRamLength;

        if ((address < 0x6000) || (size == 0) || ((m_prgBank & 0x10) != 0)) {
            return null;
        }

        var bank = size switch {
            16384 => ((OuterRegister >> 3) & 1),
            32768 => ((OuterRegister >> 2) & 3),
            _ => 0,
        };

        return (((bank << 13) | (address & 0x1FFF)) & (size - 1));
    }
    private int ChrOffset(ushort address) {
        var offset = (((m_control & 0x10) == 0)
            ? (((m_chrBank0 >> 1) << 13) | address)
            : ((((address < 0x1000) ? m_chrBank0 : m_chrBank1) << 12) | (address & 0x0FFF))
        );

        return (offset % m_memory.ChrLength);
    }
    // Control bits 0-1: one-screen from page 0, one-screen from page 1, vertical (CIRAM A10 = PPU A10), horizontal
    // (CIRAM A10 = PPU A11).
    private int Page(ushort address) =>
        (m_control & 3) switch {
            0 => 0,
            1 => 1,
            2 => ((address >> 10) & 1),
            _ => ((address >> 11) & 1),
        };
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        m_memory.Transfer(transfer: transfer);
        transfer.Byte(value: ref m_shift);
        transfer.Byte(value: ref m_control);
        transfer.Byte(value: ref m_chrBank0);
        transfer.Byte(value: ref m_chrBank1);
        transfer.Byte(value: ref m_prgBank);
        transfer.Byte(value: ref m_cyclesSinceWrite);
        transfer.Boolean(value: ref m_ppuA12);
    }
}
