namespace Puck.HumbleGamingDeck;

/// <summary>
/// The MMC3 board family (mapper 4: TxROM): eight bank registers behind a bank select at $8000 and bank data at $8001,
/// switchable mirroring at $A000, PRG-RAM enable and write protection at $A001, and a scanline counter that raises the
/// board's IRQ (https://www.nesdev.org/wiki/MMC3).
/// <para>
/// PRG is four 8 KiB windows: R6 and R7 switch two of them, and the second-last and last banks fill the others, bank
/// select bit 6 swapping which of $8000 and $C000 R6 owns. CHR is two 2 KiB windows (R0 and R1, low bit ignored) and four
/// 1 KiB windows (R2-R5), bank select bit 7 swapping the halves of the pattern space they cover. A four-screen image keeps
/// its own nametable RAM and ignores $A000.
/// </para>
/// <para>
/// The counter clocks on a rise of PPU A12 that follows at least three falling M2 edges with A12 low, each counted only
/// when A12 was already low as M2 rose, so A12 must stay low through three whole M2 high phases. The sprite fetches of
/// one line clock it once, and the short low gaps between pattern fetches, the eight dots at the start of a line with the
/// background at $1000 among them, are filtered out. A clock reloads the counter
/// from the latch when it is zero or a $C001 write asked for a reload, and decrements it otherwise; reaching zero with
/// IRQs enabled raises the IRQ, which stays raised until a $E000 write. Submapper 4 is the MMC3A revision, which raises
/// it only when the counter reaches zero from a nonzero value or through a requested reload.
/// </para>
/// <para>
/// The hardware leaves the registers undefined at power-on. This board powers on with R0-R7 at 0, 2, 4, 5, 6, 7, 0, 1
/// (CHR and PRG in order), mirroring from the header, PRG-RAM enabled and writable, and the counter, latch and IRQ
/// clear.
/// </para>
/// </summary>
public sealed class HgdMmc3 : IHgdMapper {
    private const byte FilterFalls = 3;

    private readonly HgdCartridge m_cartridge;
    private readonly HgdBoardMemory m_memory;
    private readonly byte[] m_registers = [0, 2, 4, 5, 6, 7, 0, 1];
    private readonly int m_prgBanks;
    private readonly bool m_revisionA;

    private byte m_bankSelect;
    private byte m_mirroring;
    private byte m_prgRamProtect = 0x80;
    private byte m_irqLatch;
    private byte m_irqCounter;
    private bool m_irqReload;
    private bool m_irqEnabled;
    private bool m_irqPending;
    private bool m_ppuA12;
    private byte m_a12LowFalls;
    private bool m_a12LowAtRise;

    /// <summary>Initializes a new instance of the <see cref="HgdMmc3"/> class at its power-on state, and installs a
    /// trainer at $7000 when present.</summary>
    /// <param name="cartridge">The validated, immutable MMC3 image.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cartridge"/> is <see langword="null"/>.</exception>
    public HgdMmc3(HgdCartridge cartridge) {
        ArgumentNullException.ThrowIfNull(argument: cartridge);
        m_cartridge = cartridge;
        m_memory = new(cartridge: cartridge);
        m_prgBanks = (m_memory.PrgRom.Length >> 13);
        m_revisionA = (cartridge.Header.Submapper == 4);
        m_mirroring = ((cartridge.Header.Mirroring == HgdMirroring.Horizontal) ? (byte)1 : (byte)0);
    }

    /// <inheritdoc/>
    public HgdCartridgeHeader Header => m_cartridge.Header;
    /// <inheritdoc/>
    public bool Irq => m_irqPending;
    /// <inheritdoc/>
    /// <remarks>The save contains declared PRG NVRAM followed by CHR NVRAM; volatile RAM is excluded.</remarks>
    public Span<byte> BatteryRam => m_memory.BatteryRam;
    /// <summary>Gets the scanline counter's current value.</summary>
    public byte IrqCounter => m_irqCounter;

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

        var size = m_memory.PrgRamLength;

        return (((address < 0x6000) || (size == 0) || ((m_prgRamProtect & 0x80) == 0))
            ? openBus
            : m_memory.ReadPrgRam(offset: address & (size - 1))
        );
    }
    /// <inheritdoc/>
    public void CpuWrite(ushort address, byte value) {
        if (address < 0x8000) {
            var size = m_memory.PrgRamLength;

            if ((address >= 0x6000) && (size != 0) && ((m_prgRamProtect & 0xC0) == 0x80)) {
                m_memory.WritePrgRam(
                    offset: address & (size - 1),
                    value: value
                );
            }

            return;
        }
        switch (address & 0xE001) {
            case 0x8000:
                m_bankSelect = value;

                break;
            case 0x8001:
                m_registers[m_bankSelect & 7] = value;

                break;
            case 0xA000:
                m_mirroring = ((byte)(value & 1));

                break;
            case 0xA001:
                m_prgRamProtect = value;

                break;
            case 0xC000:
                m_irqLatch = value;

                break;
            case 0xC001:
                m_irqCounter = 0;
                m_irqReload = true;

                break;
            case 0xE000:
                m_irqEnabled = false;
                m_irqPending = false;

                break;
            default:
                m_irqEnabled = true;

                break;
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
    public void ObservePpuAddress(ushort address, ulong masterTick) {
        var high = ((address & 0x1000) != 0);

        if (!high) {
            m_ppuA12 = false;

            return;
        }
        if (!m_ppuA12 && (m_a12LowFalls >= FilterFalls)) {
            ClockCounter();
        }
        m_ppuA12 = true;
        m_a12LowFalls = 0;
        m_a12LowAtRise = false;
    }
    /// <inheritdoc/>
    public void ObserveM2(bool high, ulong masterHalfTick) {
        if (high) {
            m_a12LowAtRise = !m_ppuA12;
        } else if (m_a12LowAtRise && !m_ppuA12 && (m_a12LowFalls < FilterFalls)) {
            ++m_a12LowFalls;
        }
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain the complete board state.</exception>
    /// <exception cref="InvalidDataException">The serialized A12 filter count is out of range.</exception>
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));
        if (m_a12LowFalls > FilterFalls) {
            throw new InvalidDataException(message: "Snapshot MMC3 A12 filter count is out of range.");
        }
    }

    private void ClockCounter() {
        var before = m_irqCounter;

        if ((m_irqCounter == 0) || m_irqReload) {
            m_irqCounter = m_irqLatch;
        } else {
            --m_irqCounter;
        }
        if ((m_irqCounter == 0) && m_irqEnabled && (!m_revisionA || (before != 0) || m_irqReload)) {
            m_irqPending = true;
        }
        m_irqReload = false;
    }
    private int PrgOffset(ushort address) {
        var swapped = ((m_bankSelect & 0x40) != 0);
        var bank = ((address >> 13) & 3) switch {
            0 => (swapped ? (m_prgBanks - 2) : (m_registers[6] & 0x3F)),
            1 => (m_registers[7] & 0x3F),
            2 => (swapped ? (m_registers[6] & 0x3F) : (m_prgBanks - 2)),
            _ => (m_prgBanks - 1),
        };

        return (((bank % m_prgBanks) << 13) | (address & 0x1FFF));
    }
    private int ChrOffset(ushort address) {
        var window = ((address ^ (((m_bankSelect & 0x80) != 0) ? 0x1000 : 0)) >> 10) & 7;
        var bank = window switch {
            0 => (m_registers[0] & 0xFE),
            1 => (m_registers[0] | 1),
            2 => (m_registers[1] & 0xFE),
            3 => (m_registers[1] | 1),
            _ => m_registers[(window - 2)],
        };

        return (((bank << 10) | (address & 0x3FF)) % m_memory.ChrLength);
    }
    // $A000 bit 0: vertical (CIRAM A10 = PPU A10) or horizontal (CIRAM A10 = PPU A11); a four-screen image's lower
    // nametables are wired vertically.
    private int Page(ushort address) =>
        (((m_mirroring == 0) || (Header.Mirroring == HgdMirroring.FourScreen))
            ? ((address >> 10) & 1)
            : ((address >> 11) & 1)
        );
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        m_memory.Transfer(transfer: transfer);
        transfer.Block(values: m_registers.AsSpan());
        transfer.Byte(value: ref m_bankSelect);
        transfer.Byte(value: ref m_mirroring);
        transfer.Byte(value: ref m_prgRamProtect);
        transfer.Byte(value: ref m_irqLatch);
        transfer.Byte(value: ref m_irqCounter);
        transfer.Boolean(value: ref m_irqReload);
        transfer.Boolean(value: ref m_irqEnabled);
        transfer.Boolean(value: ref m_irqPending);
        transfer.Boolean(value: ref m_ppuA12);
        transfer.Byte(value: ref m_a12LowFalls);
        transfer.Boolean(value: ref m_a12LowAtRise);
    }
}
