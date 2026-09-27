namespace Puck.HumbleGamingDeck;

/// <summary>The cartridge's hard-wired nametable layout.</summary>
public enum HgdMirroring {
    /// <summary>Two horizontal pairs of nametables.</summary>
    Horizontal,
    /// <summary>Two vertical pairs of nametables.</summary>
    Vertical,
    /// <summary>Four independent nametables.</summary>
    FourScreen,
}
/// <summary>Timing declared by an image, independent of the selected machine model.</summary>
public enum HgdTiming {
    /// <summary>NTSC timing.</summary>
    Ntsc,
    /// <summary>PAL timing.</summary>
    Pal,
    /// <summary>The image supports both NTSC and PAL.</summary>
    MultiRegion,
    /// <summary>Dendy timing.</summary>
    Dendy,
}
/// <summary>A complete decoded iNES or NES 2.0 header. Parsing does not imply that its board is implemented.</summary>
public sealed record HgdCartridgeHeader {
    /// <summary>Gets whether the header uses NES 2.0.</summary>
    public bool IsNes20 {
        get;
        private init;
    }
    /// <summary>Gets the twelve-bit mapper number.</summary>
    public int Mapper {
        get;
        private init;
    }
    /// <summary>Gets the four-bit submapper number.</summary>
    public int Submapper {
        get;
        private init;
    }
    /// <summary>Gets the PRG-ROM size in bytes.</summary>
    public UInt128 PrgRomSize {
        get;
        private init;
    }
    /// <summary>Gets the CHR-ROM size in bytes.</summary>
    public UInt128 ChrRomSize {
        get;
        private init;
    }
    /// <summary>Gets the volatile PRG-RAM size in bytes.</summary>
    public int PrgRamSize {
        get;
        private init;
    }
    /// <summary>Gets the nonvolatile PRG-RAM size in bytes.</summary>
    public int PrgNvRamSize {
        get;
        private init;
    }
    /// <summary>Gets the volatile CHR-RAM size in bytes.</summary>
    public int ChrRamSize {
        get;
        private init;
    }
    /// <summary>Gets the nonvolatile CHR-RAM size in bytes.</summary>
    public int ChrNvRamSize {
        get;
        private init;
    }
    /// <summary>Gets whether the image contains a 512-byte trainer before PRG-ROM.</summary>
    public bool HasTrainer {
        get;
        private init;
    }
    /// <summary>Gets the header's nonvolatile-memory flag.</summary>
    public bool HasBattery {
        get;
        private init;
    }
    /// <summary>Gets the nametable layout.</summary>
    public HgdMirroring Mirroring {
        get;
        private init;
    }
    /// <summary>Gets the declared timing family.</summary>
    public HgdTiming Timing {
        get;
        private init;
    }
    /// <summary>Gets byte 7's console type: regular, Vs., PlayChoice, or extended.</summary>
    public byte ConsoleType {
        get;
        private init;
    }
    /// <summary>Gets byte 13's console-specific metadata, including both Vs. nibbles.</summary>
    public byte ConsoleDetail {
        get;
        private init;
    }
    /// <summary>Gets the extended console type from byte 13's low nibble.</summary>
    public byte ExtendedConsoleType => ((byte)(ConsoleDetail & 15));
    /// <summary>Gets the Vs. PPU type from byte 13's low nibble.</summary>
    public byte VsPpuType => ((byte)(ConsoleDetail & 15));
    /// <summary>Gets the Vs. hardware type from byte 13's high nibble.</summary>
    public byte VsHardwareType => ((byte)(ConsoleDetail >> 4));
    /// <summary>Gets the number of miscellaneous ROMs following CHR-ROM.</summary>
    public byte MiscellaneousRomCount {
        get;
        private init;
    }
    /// <summary>Gets the seven-bit default expansion device identifier.</summary>
    public byte DefaultExpansionDevice {
        get;
        private init;
    }
    /// <summary>Gets whether the legacy byte 10 requests bus conflicts.</summary>
    public bool HasBusConflicts {
        get;
        private init;
    }
    /// <summary>Gets the diagnostic for legacy fields whose zero value leaves memory unspecified.</summary>
    public string? Diagnostic {
        get;
        private init;
    }

    /// <summary>Parses all sixteen header bytes without consulting a database.</summary>
    /// <param name="header">The header bytes, at least sixteen bytes beginning with the iNES signature.</param>
    /// <returns>The decoded header.</returns>
    /// <exception cref="InvalidDataException">The signature, reserved bits, or size encoding is damaged or ambiguous.</exception>
    public static HgdCartridgeHeader Parse(ReadOnlySpan<byte> header) {
        if ((header.Length < 16) || !header[..4].SequenceEqual(other: "NES\u001a"u8)) {
            throw new InvalidDataException(message: "The cartridge needs a complete iNES or NES 2.0 header and NES signature.");
        }
        var nes20 = ((header[7] & 12) == 8);

        if (!nes20 && (((header[7] & 12) != 0) || ((header[9] & 0xFE) != 0) ||
            ((header[10] & 0xCC) != 0) || header[11..16].ContainsAnyExcept(value: ((byte)0)))) {
            throw new InvalidDataException(message: "Ambiguous or damaged iNES header: reserved bytes/bits in bytes 7-15 are nonzero; supply a corrected header, not a database guess.");
        }
        if (nes20 && (((header[12] & 0xFC) != 0) || ((header[14] & 0xFC) != 0) || ((header[15] & 0x80) != 0) ||
            (((header[7] & 3) != 1) && ((header[13] & 0xF0) != 0)) ||
            (((header[7] & 3) == 0) && (header[13] != 0)))) {
            throw new InvalidDataException(message: "Damaged NES 2.0 header: reserved bits in bytes 12-15 are nonzero.");
        }
        var battery = ((header[6] & 2) != 0);
        var legacyRam = (((header[10] & 0x10) != 0) ? 0 : (Math.Max(val1: 1, val2: ((int)header[8])) * 8192));
        var legacyTiming = (header[10] & 3) switch {
            1 or 3 => HgdTiming.MultiRegion,
            2 => HgdTiming.Pal,
            _ => (((header[9] & 1) != 0) ? HgdTiming.Pal : HgdTiming.Ntsc),
        };

        return new HgdCartridgeHeader {
            IsNes20 = nes20,
            Mapper = (header[6] >> 4) | (header[7] & 0xF0) | (nes20 ? ((header[8] & 15) << 8) : 0),
            Submapper = (nes20 ? (header[8] >> 4) : 0),
            PrgRomSize = RomSize(low: header[4], high: (nes20 ? header[9] & 15 : 0), unit: 16384),
            ChrRomSize = RomSize(low: header[5], high: (nes20 ? (header[9] >> 4) : 0), unit: 8192),
            PrgRamSize = (nes20 ? RamSize(shift: header[10] & 15) : (battery ? 0 : legacyRam)),
            PrgNvRamSize = (nes20 ? RamSize(shift: (header[10] >> 4)) : (battery ? legacyRam : 0)),
            ChrRamSize = (nes20 ? RamSize(shift: header[11] & 15) : ((header[5] == 0) ? 8192 : 0)),
            ChrNvRamSize = (nes20 ? RamSize(shift: (header[11] >> 4)) : 0),
            HasTrainer = ((header[6] & 4) != 0),
            HasBattery = battery,
            Mirroring = (((header[6] & 8) != 0) ? HgdMirroring.FourScreen : (((header[6] & 1) != 0) ? HgdMirroring.Vertical : HgdMirroring.Horizontal)),
            Timing = (nes20 ? (HgdTiming)(header[12] & 3) : legacyTiming),
            ConsoleType = ((byte)(header[7] & 3)),
            ConsoleDetail = (nes20 ? header[13] : (byte)0),
            MiscellaneousRomCount = (nes20 ? (byte)(header[14] & 3) : (byte)0),
            DefaultExpansionDevice = (nes20 ? (byte)(header[15] & 127) : (byte)0),
            HasBusConflicts = (!nes20 && ((header[10] & 0x20) != 0)),
            Diagnostic = ((!nes20 && (header[8] == 0) && (legacyRam != 0))
                ? "iNES leaves PRG-RAM size unspecified; the documented legacy default is 8 KiB. Use NES 2.0 to declare no RAM."
                : null),
        };
    }

    private static int RamSize(int shift) {
        return ((shift == 0) ? 0 : (64 << shift));
    }
    private static UInt128 RomSize(byte low, int high, int unit) {
        if (high != 15) {
            return (((UInt128)((high << 8) | low)) * ((uint)unit));
        }
        var exponent = (low >> 2);
        var multiplier = ((UInt128)(((low & 3) * 2) + 1));

        return (multiplier << exponent);
    }
}
