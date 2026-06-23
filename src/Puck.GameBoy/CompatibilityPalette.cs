namespace Puck.GameBoy;

/// <summary>
/// Reproduces the Game Boy Color boot ROM's automatic colorization of original (DMG) games. A real CGB, when it boots
/// a cartridge with no CGB flag, hashes the title and looks the result up in a table the boot ROM carries, assigning a
/// background palette and two object palettes from a fixed set — this is why black-and-white classics show up in color
/// on a CGB. The data tables below are extracted verbatim from SameBoy's open-source <c>cgb_boot.asm</c>.
/// </summary>
public static class CompatibilityPalette {
    // The first 65 title checksums are unique; the remaining 29 share checksums and are disambiguated by the title's
    // fourth letter (Dups4thLetters).
    private const int FirstDuplicateIndex = 65;

    private static readonly byte[] s_titleChecksums = [
        0x00, 0x88, 0x16, 0x36, 0xD1, 0xDB, 0xF2, 0x3C, 0x8C, 0x92, 0x3D, 0x5C, 0x58, 0xC9, 0x3E, 0x70,
        0x1D, 0x59, 0x69, 0x19, 0x35, 0xA8, 0x14, 0xAA, 0x75, 0x95, 0x99, 0x34, 0x6F, 0x15, 0xFF, 0x97,
        0x4B, 0x90, 0x17, 0x10, 0x39, 0xF7, 0xF6, 0xA2, 0x49, 0x4E, 0x43, 0x68, 0xE0, 0x8B, 0xF0, 0xCE,
        0x0C, 0x29, 0xE8, 0xB7, 0x86, 0x9A, 0x52, 0x01, 0x9D, 0x71, 0x9C, 0xBD, 0x5D, 0x6D, 0x67, 0x3F,
        0x6B, 0xB3, 0x46, 0x28, 0xA5, 0xC6, 0xD3, 0x27, 0x61, 0x18, 0x66, 0x6A, 0xBF, 0x0D, 0xF4, 0xB3,
        0x46, 0x28, 0xA5, 0xC6, 0xD3, 0x27, 0x61, 0x18, 0x66, 0x6A, 0xBF, 0x0D, 0xF4, 0xB3,
    ];

    private static readonly byte[] s_palettePerChecksum = [
        0, 4, 5, 35, 34, 3, 31, 15, 10, 5, 19, 36, 7, 37, 30, 44,
        21, 32, 31, 20, 5, 33, 13, 14, 5, 29, 5, 18, 9, 3, 2, 26,
        25, 25, 41, 42, 26, 45, 42, 45, 36, 38, 26, 42, 30, 41, 34, 34,
        5, 42, 6, 5, 33, 25, 42, 42, 40, 2, 16, 25, 42, 42, 5, 0,
        39, 36, 22, 25, 6, 32, 12, 36, 11, 39, 18, 39, 24, 31, 50, 17,
        46, 6, 27, 0, 47, 41, 41, 0, 0, 19, 34, 23, 18, 29,
    ];

    // The fourth title letter that disambiguates each duplicate-checksum entry (a space means none/blank).
    private static readonly byte[] s_dups4thLetters = "BEFAARBEKEK R-URAR INAILICE R"u8.ToArray();

    // Each combination is three byte offsets into s_palettes — object palette 0, object palette 1, background — most
    // landing on a 4-color boundary, a few deliberately mid-palette for a shifted look.
    private static readonly byte[] s_paletteCombinations = [
        32, 32, 232, 144, 144, 144, 160, 160, 160, 192, 192, 192, 72, 72, 72, 0,
        0, 0, 216, 216, 216, 40, 40, 40, 96, 96, 96, 208, 208, 208, 128, 64,
        64, 32, 224, 224, 32, 16, 16, 24, 32, 32, 32, 232, 232, 224, 32, 224,
        16, 136, 16, 128, 128, 64, 32, 32, 56, 32, 32, 144, 32, 32, 160, 152,
        152, 72, 30, 30, 88, 136, 136, 16, 32, 32, 16, 32, 32, 24, 224, 224,
        0, 24, 24, 0, 0, 0, 8, 144, 176, 144, 160, 176, 160, 192, 176, 192,
        128, 176, 64, 136, 32, 104, 222, 0, 112, 222, 32, 120, 152, 182, 72, 128,
        224, 80, 32, 184, 224, 136, 176, 16, 32, 0, 16, 32, 224, 24, 224, 24,
        0, 24, 224, 32, 168, 224, 32, 24, 224, 0, 200, 24, 224, 0, 224, 64,
        32, 24, 224, 224, 24, 48, 32, 224, 232, 240, 240, 240, 248, 248, 248, 224,
        32, 8, 0, 0, 16,
    ];

    // The palette combination chosen for each boot-time key combination (1-based: Right, Left, Up, Down, then the same
    // four with +A, then +B, then +A+B). Holding a direction at boot overrides the title-hash default.
    private static readonly byte[] s_keyCombinationCombos = [
        1, 48, 5, 8, 0, 40, 43, 3, 6, 7, 28, 49, 51, 52, 53, 54,
    ];

    // 32 palettes of four BGR555 colors each (128 colors), addressed by byte offset above (color index = offset / 2).
    private static readonly ushort[] s_palettes = [
        0x7FFF, 0x32BF, 0x00D0, 0x0000, 0x639F, 0x4279, 0x15B0, 0x04CB, 0x7FFF, 0x6E31, 0x454A, 0x0000, 0x7FFF, 0x1BEF, 0x0200, 0x0000,
        0x7FFF, 0x421F, 0x1CF2, 0x0000, 0x7FFF, 0x5294, 0x294A, 0x0000, 0x7FFF, 0x03FF, 0x012F, 0x0000, 0x7FFF, 0x03EF, 0x01D6, 0x0000,
        0x7FFF, 0x42B5, 0x3DC8, 0x0000, 0x7E74, 0x03FF, 0x0180, 0x0000, 0x67FF, 0x77AC, 0x1A13, 0x2D6B, 0x7ED6, 0x4BFF, 0x2175, 0x0000,
        0x53FF, 0x4A5F, 0x7E52, 0x0000, 0x4FFF, 0x7ED2, 0x3A4C, 0x1CE0, 0x03ED, 0x7FFF, 0x255F, 0x0000, 0x036A, 0x021F, 0x03FF, 0x7FFF,
        0x7FFF, 0x01DF, 0x0112, 0x0000, 0x231F, 0x035F, 0x00F2, 0x0009, 0x7FFF, 0x03EA, 0x011F, 0x0000, 0x299F, 0x001A, 0x000C, 0x0000,
        0x7FFF, 0x027F, 0x001F, 0x0000, 0x7FFF, 0x03E0, 0x0206, 0x0120, 0x7FFF, 0x7EEB, 0x001F, 0x7C00, 0x7FFF, 0x3FFF, 0x7E00, 0x001F,
        0x7FFF, 0x03FF, 0x001F, 0x0000, 0x03FF, 0x001F, 0x000C, 0x0000, 0x7FFF, 0x033F, 0x0193, 0x0000, 0x0000, 0x4200, 0x037F, 0x7FFF,
        0x7FFF, 0x7E8C, 0x7C00, 0x0000, 0x7FFF, 0x1BEF, 0x6180, 0x0000, 0x7FFF, 0x7FEA, 0x7D5F, 0x0000, 0x4778, 0x3290, 0x1D87, 0x0861,
    ];

    /// <summary>Resolves the boot ROM's assigned palettes for a cartridge, each as four BGR555 colors.</summary>
    /// <param name="rom">The cartridge ROM image (at least <c>0x150</c> bytes; the header is read for the title).</param>
    /// <param name="input">A button combination held at boot, which overrides the title-hash palette; the default holds nothing.</param>
    /// <returns>The background, object-0, and object-1 palettes the CGB would assign to this DMG game.</returns>
    public static (ushort[] Background, ushort[] Object0, ushort[] Object1) Resolve(ReadOnlySpan<byte> rom, BootPaletteSelection input = default) {
        // A held direction picks one of the boot ROM's key-combination palettes; otherwise the title is hashed.
        var keyIndex = input.KeyCombinationIndex;
        var combination = (((keyIndex > 0)
            ? s_keyCombinationCombos[keyIndex - 1]
            : CombinationIndex(rom: rom)) * 3);

        return (
            Background: ReadPalette(byteOffset: s_paletteCombinations[combination + 2]),
            Object0: ReadPalette(byteOffset: s_paletteCombinations[combination + 0]),
            Object1: ReadPalette(byteOffset: s_paletteCombinations[combination + 1])
        );
    }

    private static int CombinationIndex(ReadOnlySpan<byte> rom) {
        if ((rom.Length < 0x150) || !IsNintendoLicensed(rom: rom)) {
            return (s_palettePerChecksum[0] & 0x7F);
        }

        var checksum = 0;

        for (var i = 0x134; i <= 0x143; i += 1) {
            checksum += rom[i];
        }

        checksum &= 0xFF;

        for (var i = 0; i < s_titleChecksums.Length; i += 1) {
            if (s_titleChecksums[i] != checksum) {
                continue;
            }

            // A unique checksum is an immediate match; a shared one must also match the title's fourth letter.
            if ((i < FirstDuplicateIndex) || (rom[0x137] == s_dups4thLetters[i - FirstDuplicateIndex])) {
                return (s_palettePerChecksum[i] & 0x7F);
            }
        }

        return (s_palettePerChecksum[0] & 0x7F);
    }

    // The boot ROM only colorizes Nintendo-published games: old licensee 0x01, or 0x33 with the new licensee "01".
    private static bool IsNintendoLicensed(ReadOnlySpan<byte> rom) {
        var oldLicensee = rom[0x14B];

        return ((oldLicensee == 0x33)
            ? ((rom[0x144] == (byte)'0') && (rom[0x145] == (byte)'1'))
            : (oldLicensee == 0x01));
    }

    private static ushort[] ReadPalette(int byteOffset) {
        var start = (byteOffset / 2);

        return [s_palettes[start], s_palettes[start + 1], s_palettes[start + 2], s_palettes[start + 3]];
    }
}
