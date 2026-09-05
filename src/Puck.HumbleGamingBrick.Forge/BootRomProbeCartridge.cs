namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// Builds the throwaway cartridge a boot image is booted against while its timing is solved. It carries a real logo and
/// a real header checksum because the boot program refuses anything else, a header the caller varies to reach a
/// different row of the timing tables, and a two-byte spin at the entry point so the machine stays somewhere harmless
/// after the handoff.
/// </summary>
public static class BootRomProbeCartridge {
    private const int EntryPoint = 0x0100;
    private const int HeaderChecksumEnd = 0x014C;
    private const int HeaderChecksumOffset = 0x014D;
    private const int HeaderChecksumStart = 0x0134;
    private const int RomSize = 0x8000;
    private const int TitleEnd = 0x0142;
    private const int TitleStart = 0x0134;

    /// <summary>Creates the probe cartridge image for a header.</summary>
    /// <param name="probe">The header the probe presents.</param>
    /// <returns>A 32&#160;KiB ROM-only cartridge image.</returns>
    public static byte[] Create(BootRomProbe probe) {
        var rom = new byte[RomSize];

        // jr $ — the cartridge parks at its entry point rather than running off into open ROM.
        rom[EntryPoint] = 0x18;
        rom[(EntryPoint + 1)] = 0xFE;

        CartridgeHeader.Logo.CopyTo(destination: rom.AsSpan(start: CartridgeHeader.LogoOffset));

        var title = (probe.Title ?? string.Empty);

        for (var offset = TitleStart; (offset <= TitleEnd); ++offset) {
            var index = (offset - TitleStart);

            rom[offset] = ((index < title.Length)
                ? ((byte)title[index])
                : ((byte)0x00));
        }

        var newLicensee = (probe.NewLicenseeCode ?? "  ");

        rom[0x0143] = probe.ColorFlag;
        rom[0x0144] = ((byte)newLicensee[0]);
        rom[0x0145] = ((byte)newLicensee[1]);
        rom[0x014B] = probe.OldLicenseeCode;
        rom[HeaderChecksumOffset] = HeaderChecksum(rom: rom);

        return rom;
    }

    // The header checksum the boot program recomputes: x = x - byte - 1 over 0x0134-0x014C.
    private static byte HeaderChecksum(byte[] rom) {
        byte checksum = 0;

        for (var offset = HeaderChecksumStart; (offset <= HeaderChecksumEnd); ++offset) {
            checksum = ((byte)((checksum - rom[offset]) - 1));
        }

        return checksum;
    }
}
