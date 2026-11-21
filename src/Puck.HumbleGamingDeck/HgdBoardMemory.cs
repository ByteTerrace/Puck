namespace Puck.HumbleGamingDeck;

/// <summary>
/// The memory a cartridge board decodes, whatever its banking: the image's PRG and CHR ROM, the header's volatile and
/// battery-backed PRG and CHR RAM, and a four-screen board's own 2 KiB of nametable RAM. A board computes offsets (its
/// bank registers); this owns the bytes behind them, the battery save's layout, and their snapshot order.
/// <para>
/// A board maps banks into windows, four 8 KiB PRG windows from $8000 and eight 1 KiB pattern windows, whenever a register
/// changes, so a read is one table lookup and an array index and never recomputes bank arithmetic. The windows are derived
/// from the board's registers, never serialized: a board maps them again after it loads state.
/// </para>
/// <para>
/// PRG-RAM is one window, volatile bytes first and then PRG NVRAM; CHR-RAM likewise, volatile bytes first and then CHR
/// NVRAM. The battery save is PRG NVRAM followed by CHR NVRAM.
/// </para>
/// </summary>
public sealed class HgdBoardMemory {
    private readonly byte[] m_image;
    private readonly int m_prgBase;
    private readonly int m_chrBase;
    private readonly bool m_chrIsRom;
    private readonly int m_prgNvRamSize;
    private readonly int[] m_prgWindows = new int[4];
    private readonly int[] m_chrWindows = new int[8];
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
        m_image = cartridge.ImageBytes;
        m_prgBase = cartridge.PrgRomOffset;
        m_chrBase = cartridge.ChrRomOffset;
        m_chrIsRom = !cartridge.ChrRom.IsEmpty;
        PrgLength = cartridge.PrgRom.Length;

        var header = cartridge.Header;

        m_prgNvRamSize = header.PrgNvRamSize;
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

    /// <summary>Gets the PRG-ROM's length in bytes.</summary>
    public int PrgLength {
        get;
    }
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
    /// <summary>Maps consecutive 8 KiB PRG windows onto consecutive PRG-ROM bytes.</summary>
    /// <param name="firstWindow">The first window, from 0 for $8000 through 3 for $E000.</param>
    /// <param name="count">The number of windows to map.</param>
    /// <param name="offset">The PRG-ROM offset the first window starts at; every window lies inside the ROM.</param>
    public void MapPrg(int firstWindow, int count, int offset) {
        for (var index = 0; (index < count); ++index) {
            m_prgWindows[(firstWindow + index)] = (m_prgBase + offset + (index << 13));
        }
    }
    /// <summary>Maps consecutive 1 KiB pattern windows onto consecutive pattern memory, wrapping at its end.</summary>
    /// <param name="firstWindow">The first window, from 0 for PPU $0000 through 7 for $1C00.</param>
    /// <param name="count">The number of windows to map.</param>
    /// <param name="offset">The offset within pattern memory the first window starts at.</param>
    public void MapChr(int firstWindow, int count, int offset) {
        for (var index = 0; (index < count); ++index) {
            m_chrWindows[(firstWindow + index)] = ((offset + (index << 10)) % ChrLength);
        }
    }
    /// <summary>Reads PRG-ROM through the mapped windows.</summary>
    /// <param name="address">The CPU address, from $8000.</param>
    /// <returns>The ROM byte.</returns>
    public byte ReadPrg(ushort address) =>
        m_image[(m_prgWindows[((address >> 13) & 3)] + (address & 0x1FFF))];
    /// <summary>Reads pattern memory through the mapped windows.</summary>
    /// <param name="address">The PPU address, below $2000.</param>
    /// <returns>The stored byte.</returns>
    public byte ReadChr(ushort address) {
        var offset = (m_chrWindows[(address >> 10)] + (address & 0x3FF));

        if (m_chrIsRom) {
            return m_image[(m_chrBase + offset)];
        }

        return ((offset < m_chrRam.Length) ? m_chrRam[offset] : m_batteryRam[((m_prgNvRamSize + offset) - m_chrRam.Length)]);
    }
    /// <summary>Writes pattern memory through the mapped windows; a write to CHR-ROM changes nothing.</summary>
    /// <param name="address">The PPU address, below $2000.</param>
    /// <param name="value">The byte to store.</param>
    public void WriteChr(ushort address, byte value) {
        if (m_chrIsRom) {
            return;
        }

        var offset = (m_chrWindows[(address >> 10)] + (address & 0x3FF));

        if (offset < m_chrRam.Length) {
            m_chrRam[offset] = value;
        } else {
            m_batteryRam[((m_prgNvRamSize + offset) - m_chrRam.Length)] = value;
        }
    }
    /// <summary>Reads the PPU window below $3F00: pattern memory below $2000, and above it the console's page the wiring
    /// selects or, for the upper two nametables of a four-screen board, the board's own RAM.</summary>
    /// <param name="address">The PPU address, below $3F00.</param>
    /// <param name="nametables">The console's nametable RAM.</param>
    /// <param name="wiring">How the board drives CIRAM A10 now.</param>
    /// <returns>The stored byte.</returns>
    public byte ReadPpu(ushort address, HgdNametableRam nametables, HgdNametableWiring wiring) {
        if (address < 0x2000) {
            return ReadChr(address: address);
        }

        var quadrant = (address >> 10) & 3;

        return (((m_fourScreenRam.Length != 0) && (quadrant >= 2))
            ? m_fourScreenRam[((quadrant & 1) << 10) | (address & 0x3FF)]
            : nametables.Read(
                offset: address,
                page: PageOf(address: address, wiring: wiring)
            )
        );
    }
    /// <summary>Writes the PPU window below $3F00; a write to CHR-ROM changes nothing.</summary>
    /// <param name="address">The PPU address, below $3F00.</param>
    /// <param name="value">The byte to store.</param>
    /// <param name="nametables">The console's nametable RAM.</param>
    /// <param name="wiring">How the board drives CIRAM A10 now.</param>
    public void WritePpu(ushort address, byte value, HgdNametableRam nametables, HgdNametableWiring wiring) {
        if (address < 0x2000) {
            WriteChr(
                address: address,
                value: value
            );

            return;
        }

        var quadrant = (address >> 10) & 3;

        if ((m_fourScreenRam.Length != 0) && (quadrant >= 2)) {
            m_fourScreenRam[((quadrant & 1) << 10) | (address & 0x3FF)] = value;
        } else {
            nametables.Write(
                offset: address,
                page: PageOf(address: address, wiring: wiring),
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
    /// <summary>Returns the wiring a fixed header layout drives: horizontal mirroring, and vertical for every other layout,
    /// since a four-screen board wires its lower two nametables as vertical mirroring does.</summary>
    /// <param name="mirroring">The header's layout.</param>
    /// <returns>The wiring.</returns>
    public static HgdNametableWiring HeaderWiring(HgdMirroring mirroring) =>
        ((mirroring == HgdMirroring.Horizontal) ? HgdNametableWiring.Horizontal : HgdNametableWiring.Vertical);

    // The CIRAM page the wiring drives for a nametable address.
    private static int PageOf(HgdNametableWiring wiring, ushort address) =>
        wiring switch {
            HgdNametableWiring.OneScreenLow => 0,
            HgdNametableWiring.OneScreenHigh => 1,
            HgdNametableWiring.Vertical => ((address >> 10) & 1),
            _ => ((address >> 11) & 1),
        };
}
/// <summary>How a board drives the console's CIRAM A10 line, which picks one of its two nametable pages. The values are
/// the MMC1 control register's mirroring bits.</summary>
public enum HgdNametableWiring : byte {
    /// <summary>Every nametable reads the first page.</summary>
    OneScreenLow = 0,
    /// <summary>Every nametable reads the second page.</summary>
    OneScreenHigh = 1,
    /// <summary>CIRAM A10 follows PPU A10: the left and right nametables differ, and each repeats below itself.</summary>
    Vertical = 2,
    /// <summary>CIRAM A10 follows PPU A11: the top and bottom nametables differ, and each repeats beside itself.</summary>
    Horizontal = 3,
}
