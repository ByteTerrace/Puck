namespace Puck.HumbleGamingDeck;

/// <summary>An immutable cartridge image shared by a machine and its forks.</summary>
public sealed class HgdCartridge {
    private readonly byte[] m_image;
    private readonly int m_prgOffset;
    private readonly int m_chrOffset;

    private HgdCartridge(byte[] image, HgdCartridgeHeader header) {
        m_image = image;
        Header = header;
        m_prgOffset = (16 + (header.HasTrainer ? 512 : 0));
        m_chrOffset = (m_prgOffset + ((int)header.PrgRomSize));
    }

    /// <summary>Gets the complete original image, including its header and trainer.</summary>
    public ReadOnlySpan<byte> Image => m_image;
    /// <summary>Gets the decoded header.</summary>
    public HgdCartridgeHeader Header {
        get;
    }
    /// <summary>Gets the immutable PRG bytes.</summary>
    public ReadOnlySpan<byte> PrgRom => m_image.AsSpan(start: m_prgOffset, length: ((int)Header.PrgRomSize));
    /// <summary>Gets the immutable CHR bytes.</summary>
    public ReadOnlySpan<byte> ChrRom => m_image.AsSpan(start: m_chrOffset, length: ((int)Header.ChrRomSize));
    /// <summary>Gets the trainer bytes, or an empty span.</summary>
    public ReadOnlySpan<byte> Trainer => (Header.HasTrainer ? m_image.AsSpan(length: 512, start: 16) : []);

    /// <summary>Copies and resolves an image using only its header.</summary>
    /// <param name="image">The complete iNES or NES 2.0 file.</param>
    /// <returns>An immutable image for an implemented board.</returns>
    /// <exception cref="InvalidDataException">The header or payload is damaged or ambiguous.</exception>
    /// <exception cref="NotSupportedException">The mapper, submapper, console, or board memory layout is unimplemented.</exception>
    public static HgdCartridge Load(ReadOnlySpan<byte> image) {
        var header = HgdCartridgeHeader.Parse(header: image);

        if ((header.Mapper != 0) || (header.Submapper != 0)) {
            throw new NotSupportedException(message: $"Unimplemented mapper {header.Mapper} ({MapperName(mapper: header.Mapper)}), submapper {header.Submapper}.");
        }
        if (header.ConsoleType != 0) {
            throw new NotSupportedException(message: $"Unimplemented console type {header.ConsoleType}, console detail {header.ConsoleDetail:X2}.");
        }
        var prgRam = (header.PrgRamSize + header.PrgNvRamSize);
        var chrRam = (header.ChrRamSize + header.ChrNvRamSize);

        if (((header.PrgRomSize != 16384) && (header.PrgRomSize != 32768)) ||
            ((header.ChrRomSize != 0) && (header.ChrRomSize != 8192)) ||
            ((header.ChrRomSize == 0) ? (chrRam != 8192) : (chrRam != 0)) ||
            (prgRam > 8192) || ((prgRam & (prgRam - 1)) != 0) ||
            (header.HasTrainer && (prgRam < 8192)) || (header.MiscellaneousRomCount != 0) || header.HasBusConflicts) {
            throw new NotSupportedException(message: "Unimplemented NROM (mapper 0, submapper 0) memory layout: needs 16/32 KiB PRG, 8 KiB CHR ROM or RAM, and at most 8 KiB PRG-RAM; a trainer requires 8 KiB PRG-RAM.");
        }
        var expected = (((16UL + (header.HasTrainer ? 512UL : 0UL)) + header.PrgRomSize) + header.ChrRomSize);

        if (((ulong)image.Length) != expected) {
            throw new InvalidDataException(message: $"Damaged NROM payload: header describes {expected} bytes, image has {image.Length}; truncated or unexplained trailing data.");
        }

        return new HgdCartridge(image: image.ToArray(), header: header);
    }
    /// <summary>Creates a board with private writable memory over this image.</summary>
    /// <returns>The independent NROM board.</returns>
    public IHgdMapper CreateMapper() {
        return new HgdNrom(cartridge: this);
    }

    private static string MapperName(int mapper) {
        return mapper switch {
            0 => "NROM",
            1 => "MMC1",
            2 => "UxROM",
            3 => "CNROM",
            4 => "MMC3",
            5 => "MMC5",
            7 => "AxROM",
            9 => "MMC2",
            10 => "MMC4",
            11 => "Color Dreams",
            16 => "Bandai FCG",
            19 => "Namco 163",
            21 or 23 or 25 => "Konami VRC4",
            22 => "Konami VRC2",
            24 or 26 => "Konami VRC6",
            28 => "Action 53",
            31 => "NSF-style",
            34 => "BNROM/NINA-001",
            66 => "GxROM",
            69 => "Sunsoft FME-7",
            71 => "Camerica",
            85 => "Konami VRC7",
            159 => "Bandai LZ93D50",
            206 => "Namco 108",
            _ => "unassigned or unsupported board",
        };
    }
}
