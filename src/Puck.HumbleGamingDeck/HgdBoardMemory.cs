namespace Puck.HumbleGamingDeck;

/// <summary>
/// The memory a cartridge board decodes, whatever its banking: the image's PRG and CHR ROM, the header's volatile and
/// battery-backed PRG and CHR RAM, and a four-screen board's own 2 KiB of nametable RAM. A board computes offsets (its
/// bank registers); this owns the bytes behind them, the battery save's layout, and their snapshot order.
/// <para>
/// PRG-RAM is one window, volatile bytes first and then PRG NVRAM; CHR-RAM likewise, volatile bytes first and then CHR
/// NVRAM. The battery save is PRG NVRAM followed by CHR NVRAM.
/// </para>
/// </summary>
public sealed class HgdBoardMemory {
    private readonly HgdCartridge m_cartridge;
    private readonly byte[] m_prgRam;
    private readonly byte[] m_chrRam;
    private readonly byte[] m_batteryRam;
    private readonly byte[] m_fourScreenRam;

    /// <summary>Initializes a new instance of the <see cref="HgdBoardMemory"/> class with zeroed RAM, and installs a trainer
    /// at PRG-RAM offset $1000 ($7000 on every board that maps its first 8 KiB at $6000) when the image has one.</summary>
    /// <param name="cartridge">The validated, immutable image.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cartridge"/> is <see langword="null"/>.</exception>
    public HgdBoardMemory(HgdCartridge cartridge) {
        ArgumentNullException.ThrowIfNull(argument: cartridge);
        m_cartridge = cartridge;

        var header = cartridge.Header;

        m_prgRam = new byte[header.PrgRamSize];
        m_chrRam = new byte[header.ChrRamSize];
        m_batteryRam = new byte[(header.PrgNvRamSize + header.ChrNvRamSize)];
        m_fourScreenRam = new byte[((header.Mirroring == HgdMirroring.FourScreen) ? 2048 : 0)];
        PrgRamLength = (header.PrgRamSize + header.PrgNvRamSize);
        ChrLength = (cartridge.ChrRom.IsEmpty ? (header.ChrRamSize + header.ChrNvRamSize) : cartridge.ChrRom.Length);
        for (var index = 0; (index < cartridge.Trainer.Length); ++index) {
            WritePrgRam(offset: (0x1000 + index), value: cartridge.Trainer[index]);
        }
    }

    /// <summary>Gets the immutable PRG-ROM.</summary>
    public ReadOnlySpan<byte> PrgRom => m_cartridge.PrgRom;
    /// <summary>Gets the PRG-RAM window's length: volatile and nonvolatile bytes together.</summary>
    public int PrgRamLength {
        get;
    }
    /// <summary>Gets the pattern memory's length: the CHR-ROM, or volatile and nonvolatile CHR-RAM together.</summary>
    public int ChrLength {
        get;
    }
    /// <summary>Gets the battery-backed bytes a save file holds: PRG NVRAM, then CHR NVRAM.</summary>
    public Span<byte> BatteryRam => m_batteryRam;

    /// <summary>Reads PRG-RAM.</summary>
    /// <param name="offset">The offset within the PRG-RAM window, below <see cref="PrgRamLength"/>.</param>
    /// <returns>The stored byte.</returns>
    public byte ReadPrgRam(int offset) =>
        ((offset < m_prgRam.Length) ? m_prgRam[offset] : m_batteryRam[(offset - m_prgRam.Length)]);
    /// <summary>Writes PRG-RAM.</summary>
    /// <param name="offset">The offset within the PRG-RAM window, below <see cref="PrgRamLength"/>.</param>
    /// <param name="value">The byte to store.</param>
    public void WritePrgRam(int offset, byte value) {
        if (offset < m_prgRam.Length) {
            m_prgRam[offset] = value;
        } else {
            m_batteryRam[(offset - m_prgRam.Length)] = value;
        }
    }
    /// <summary>Reads pattern memory.</summary>
    /// <param name="offset">The offset within pattern memory, below <see cref="ChrLength"/>.</param>
    /// <returns>The stored byte.</returns>
    public byte ReadChr(int offset) {
        if (!m_cartridge.ChrRom.IsEmpty) {
            return m_cartridge.ChrRom[offset];
        }

        return ((offset < m_chrRam.Length) ? m_chrRam[offset] : m_batteryRam[((m_cartridge.Header.PrgNvRamSize + offset) - m_chrRam.Length)]);
    }
    /// <summary>Writes pattern memory; a write to CHR-ROM changes nothing.</summary>
    /// <param name="offset">The offset within pattern memory, below <see cref="ChrLength"/>.</param>
    /// <param name="value">The byte to store.</param>
    public void WriteChr(int offset, byte value) {
        if (!m_cartridge.ChrRom.IsEmpty) {
            return;
        }
        if (offset < m_chrRam.Length) {
            m_chrRam[offset] = value;
        } else {
            m_batteryRam[((m_cartridge.Header.PrgNvRamSize + offset) - m_chrRam.Length)] = value;
        }
    }
    /// <summary>Reads a nametable byte: the console's page the board selects, or a four-screen board's own RAM for the
    /// upper two nametables.</summary>
    /// <param name="address">The PPU address, from $2000.</param>
    /// <param name="page">The CIRAM page the board drives through CIRAM A10 for this address.</param>
    /// <param name="nametables">The console's nametable RAM.</param>
    /// <returns>The stored byte.</returns>
    public byte ReadNametable(ushort address, int page, HgdNametableRam nametables) {
        var quadrant = (address >> 10) & 3;

        return (((m_fourScreenRam.Length != 0) && (quadrant >= 2))
            ? m_fourScreenRam[((quadrant & 1) << 10) | (address & 0x3FF)]
            : nametables.Read(
                offset: address,
                page: page
            )
        );
    }
    /// <summary>Writes a nametable byte to the page the board selects, or to a four-screen board's own RAM.</summary>
    /// <param name="address">The PPU address, from $2000.</param>
    /// <param name="page">The CIRAM page the board drives through CIRAM A10 for this address.</param>
    /// <param name="value">The byte to store.</param>
    /// <param name="nametables">The console's nametable RAM.</param>
    public void WriteNametable(ushort address, int page, byte value, HgdNametableRam nametables) {
        var quadrant = (address >> 10) & 3;

        if ((m_fourScreenRam.Length != 0) && (quadrant >= 2)) {
            m_fourScreenRam[((quadrant & 1) << 10) | (address & 0x3FF)] = value;
        } else {
            nametables.Write(
                offset: address,
                page: page,
                value: value
            );
        }
    }
    /// <summary>Transfers every RAM block in a fixed order: PRG-RAM, CHR-RAM, battery RAM, four-screen RAM.</summary>
    /// <typeparam name="TTransfer">The save or load transfer.</typeparam>
    /// <param name="transfer">The transfer.</param>
    public void Transfer<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Block(values: m_prgRam.AsSpan());
        transfer.Block(values: m_chrRam.AsSpan());
        transfer.Block(values: m_batteryRam.AsSpan());
        transfer.Block(values: m_fourScreenRam.AsSpan());
    }
    /// <summary>Returns the CIRAM page a fixed header layout drives: CIRAM A10 follows PPU A11 on a horizontally mirrored
    /// board and PPU A10 otherwise; a four-screen board wires its lower two nametables as vertical mirroring does.</summary>
    /// <param name="mirroring">The header's layout.</param>
    /// <param name="address">The PPU address, from $2000.</param>
    /// <returns>The page, zero or one.</returns>
    public static int HeaderPage(HgdMirroring mirroring, ushort address) =>
        ((mirroring == HgdMirroring.Horizontal) ? ((address >> 11) & 1) : ((address >> 10) & 1));
}
