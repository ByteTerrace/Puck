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

        if (Unimplemented(header: header) is { } unimplemented) {
            throw new NotSupportedException(message: unimplemented);
        }
        if (Damaged(header: header, imageLength: image.Length) is { } damaged) {
            throw new InvalidDataException(message: damaged);
        }

        return new HgdCartridge(image: image.ToArray(), header: header);
    }
    /// <summary>Returns why <see cref="Load"/> refuses an image with this header and length, judged without the
    /// payload: a board, console or memory layout the Deck does not implement, or a length the header does not
    /// describe.</summary>
    /// <param name="header">The image's decoded header.</param>
    /// <param name="imageLength">The whole image's length in bytes.</param>
    /// <returns>The reason, or <see langword="null"/> when <see cref="Load"/> accepts the image.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="header"/> is <see langword="null"/>.</exception>
    public static string? Refusal(HgdCartridgeHeader header, long imageLength) {
        ArgumentNullException.ThrowIfNull(argument: header);

        return (Unimplemented(header: header) ?? Damaged(header: header, imageLength: imageLength));
    }
    /// <summary>Creates the header's board with private writable memory over this image, at its power-on state.</summary>
    /// <returns>The independent NROM, MMC1 or MMC3 board.</returns>
    public IHgdMapper CreateMapper() =>
        Header.Mapper switch {
            1 => new HgdMmc1(cartridge: this),
            4 => new HgdMmc3(cartridge: this),
            _ => new HgdNrom(cartridge: this),
        };

    private static string? Board(HgdCartridgeHeader header) =>
        (header.Mapper, header.Submapper) switch {
            (0, 0) => "NROM",
            (1, 0) => "MMC1",
            (4, 0) or (4, 4) => "MMC3",
            _ => null,
        };
    private static string? Damaged(HgdCartridgeHeader header, long imageLength) {
        var expected = (((16UL + (header.HasTrainer ? 512UL : 0UL)) + header.PrgRomSize) + header.ChrRomSize);

        return ((((ulong)imageLength) != expected)
            ? $"Damaged {Board(header: header)} payload: header describes {expected} bytes, image has {imageLength}; truncated or unexplained trailing data."
            : null
        );
    }
    private static string? Unimplemented(HgdCartridgeHeader header) {
        if (Board(header: header) is not { } board) {
            return $"Unimplemented mapper {header.Mapper} ({MapperName(mapper: header.Mapper)}), submapper {header.Submapper}.";
        }
        if (header.ConsoleType != 0) {
            return $"Unimplemented console type {header.ConsoleType}, console detail {header.ConsoleDetail:X2}.";
        }
        var prgRam = (header.PrgRamSize + header.PrgNvRamSize);
        var chrRam = (header.ChrRamSize + header.ChrNvRamSize);
        var layout = header.Mapper switch {
            0 => (Fits: (
                ((header.PrgRomSize == 16384) || (header.PrgRomSize == 32768)) &&
                ((header.ChrRomSize == 0) ? (chrRam == 8192) : ((header.ChrRomSize == 8192) && (chrRam == 0))) &&
                (prgRam <= 8192)
            ), Needs: "16/32 KiB PRG, 8 KiB CHR ROM or RAM, and at most 8 KiB PRG-RAM"),
            1 => (Fits: (
                RomFits(size: header.PrgRomSize, unit: 16384, minimum: 32768, maximum: 524288) &&
                ((header.ChrRomSize == 0) ? RamFits(size: chrRam, minimum: 8192, maximum: 131072) : (RomFits(size: header.ChrRomSize, unit: 8192, minimum: 8192, maximum: 131072) && (chrRam == 0))) &&
                (prgRam <= 32768) && (header.Mirroring != HgdMirroring.FourScreen)
            ), Needs: "32-512 KiB PRG in 16 KiB banks, up to 128 KiB CHR ROM or 8-128 KiB CHR-RAM, at most 32 KiB PRG-RAM, and no four-screen RAM"),
            _ => (Fits: (
                RomFits(size: header.PrgRomSize, unit: 8192, minimum: 32768, maximum: 524288) &&
                ((header.ChrRomSize == 0) ? RamFits(size: chrRam, minimum: 8192, maximum: 262144) : (RomFits(size: header.ChrRomSize, unit: 8192, minimum: 8192, maximum: 262144) && (chrRam == 0))) &&
                (prgRam <= 8192)
            ), Needs: "32-512 KiB PRG in 8 KiB banks, up to 256 KiB CHR ROM or 8-256 KiB CHR-RAM, and at most 8 KiB PRG-RAM"),
        };

        return ((!layout.Fits || ((prgRam & (prgRam - 1)) != 0) || (header.HasTrainer && (prgRam < 8192)) ||
            (header.MiscellaneousRomCount != 0) || header.HasBusConflicts)
            ? $"Unimplemented {board} (mapper {header.Mapper}, submapper {header.Submapper}) memory layout: needs {layout.Needs}; a trainer requires 8 KiB PRG-RAM."
            : null
        );
    }
    private static bool RomFits(UInt128 size, int unit, int minimum, int maximum) =>
        ((size >= ((UInt128)minimum)) && (size <= ((UInt128)maximum)) && ((size % ((UInt128)unit)) == 0));
    private static bool RamFits(int size, int minimum, int maximum) =>
        ((size >= minimum) && (size <= maximum) && ((size & (size - 1)) == 0));
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
