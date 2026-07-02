using System.Text;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The decoded cartridge header — the metadata in the ROM's <c>0x0100</c>–<c>0x014F</c> region that tells the machine
/// which mapper to drive, how much ROM and save RAM the cartridge has, whether it has a battery, and whether it asks
/// for Color features. It is parsed once when the cartridge loads and never changes, so it is configuration rather than
/// snapshot state.
/// </summary>
public sealed class CartridgeHeader {
    private const int CartridgeTypeOffset = 0x0147;
    private const int ColorFlagOffset = 0x0143;
    private const byte FirstPartyOldLicensee = 0x01;
    private const int NewLicenseeOffset = 0x0144;
    private const byte NewLicenseeSentinel = 0x33;
    private const int OldLicenseeOffset = 0x014B;
    private const int RamSizeOffset = 0x0149;
    private const int RomSizeOffset = 0x0148;
    private const int TitleEnd = 0x0143;
    private const int TitleStart = 0x0134;

    private CartridgeHeader(
        string title,
        bool supportsColor,
        bool colorOnly,
        MapperKind mapper,
        bool hasRam,
        bool hasBattery,
        int romBankCount,
        int ramByteCount,
        byte titleChecksum,
        byte fourthTitleLetter,
        byte oldLicenseeCode,
        byte newLicenseeCode0,
        byte newLicenseeCode1
    ) {
        ColorOnly = colorOnly;
        FourthTitleLetter = fourthTitleLetter;
        HasBattery = hasBattery;
        HasRam = hasRam;
        Mapper = mapper;
        NewLicenseeCode0 = newLicenseeCode0;
        NewLicenseeCode1 = newLicenseeCode1;
        OldLicenseeCode = oldLicenseeCode;
        RamByteCount = ramByteCount;
        RomBankCount = romBankCount;
        SupportsColor = supportsColor;
        Title = title;
        TitleChecksum = titleChecksum;
    }

    /// <summary>Gets the cartridge title as printable ASCII.</summary>
    public string Title { get; }
    /// <summary>Gets whether the cartridge advertises Color enhancements (header flag <c>0x80</c> or <c>0xC0</c>).</summary>
    public bool SupportsColor { get; }
    /// <summary>Gets whether the cartridge requires a Color console (header flag <c>0xC0</c>).</summary>
    public bool ColorOnly { get; }
    /// <summary>Gets the mapper the cartridge carries.</summary>
    public MapperKind Mapper { get; }
    /// <summary>Gets whether the cartridge has external RAM.</summary>
    public bool HasRam { get; }
    /// <summary>Gets whether the cartridge has a battery backing its RAM.</summary>
    public bool HasBattery { get; }
    /// <summary>Gets the number of 16&#160;KiB ROM banks.</summary>
    public int RomBankCount { get; }
    /// <summary>Gets the size of external RAM in bytes (zero when the cartridge has none).</summary>
    public int RamByteCount { get; }
    /// <summary>Gets the 8-bit sum of the sixteen title-region bytes (<c>0x0134</c>–<c>0x0143</c>), the hash the Color
    /// boot ROM uses to pick a compatibility palette and that steers its header-dependent timing.</summary>
    public byte TitleChecksum { get; }
    /// <summary>Gets the fourth title byte, the boot ROM's tie-breaker between titles that share a checksum.</summary>
    public byte FourthTitleLetter { get; }
    /// <summary>Gets the legacy licensee code (<c>0x014B</c>).</summary>
    public byte OldLicenseeCode { get; }
    /// <summary>Gets the first character of the new licensee code (<c>0x0144</c>).</summary>
    public byte NewLicenseeCode0 { get; }
    /// <summary>Gets the second character of the new licensee code (<c>0x0145</c>).</summary>
    public byte NewLicenseeCode1 { get; }
    /// <summary>Gets whether the cartridge carries the first-party publisher code (legacy <c>0x01</c>, or legacy
    /// <c>0x33</c> with new code <c>"01"</c>) — the gate for the boot ROM's title-based colorization and timing paths.</summary>
    public bool IsFirstPartyGame =>
        (OldLicenseeCode == FirstPartyOldLicensee)
        || ((OldLicenseeCode == NewLicenseeSentinel) && (NewLicenseeCode0 == (byte)'0') && (NewLicenseeCode1 == (byte)'1'));

    /// <summary>Parses the header out of a full ROM image.</summary>
    /// <param name="rom">The cartridge ROM image; must be at least <c>0x0150</c> bytes.</param>
    /// <returns>The decoded header.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rom"/> is too small to contain a header.</exception>
    public static CartridgeHeader Parse(byte[] rom) {
        ArgumentNullException.ThrowIfNull(argument: rom);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: rom.Length, other: 0x0150, paramName: nameof(rom));

        var colorFlag = rom[ColorFlagOffset];
        var typeCode = rom[CartridgeTypeOffset];

        DecodeType(typeCode: typeCode, mapper: out var mapper, hasRam: out var hasRam, hasBattery: out var hasBattery);

        return new CartridgeHeader(
            title: ReadTitle(rom: rom),
            supportsColor: ((colorFlag == 0x80) || (colorFlag == 0xC0)),
            colorOnly: (colorFlag == 0xC0),
            mapper: mapper,
            hasRam: hasRam,
            hasBattery: hasBattery,
            romBankCount: DecodeRomBankCount(sizeCode: rom[RomSizeOffset]),
            ramByteCount: DecodeRamByteCount(sizeCode: rom[RamSizeOffset]),
            titleChecksum: ComputeTitleChecksum(rom: rom),
            fourthTitleLetter: rom[TitleStart + 3],
            oldLicenseeCode: rom[OldLicenseeOffset],
            newLicenseeCode0: rom[NewLicenseeOffset],
            newLicenseeCode1: rom[NewLicenseeOffset + 1]
        );
    }

    // The boot ROM's title hash: the 8-bit sum of every byte from 0x0134 through 0x0143 (the color flag included).
    private static byte ComputeTitleChecksum(byte[] rom) {
        byte sum = 0;

        for (var offset = TitleStart; (offset <= TitleEnd); ++offset) {
            sum += rom[offset];
        }

        return sum;
    }

    private static int DecodeRamByteCount(byte sizeCode) =>
        sizeCode switch {
            0x01 => 0x0800,
            0x02 => 0x2000,
            0x03 => 0x8000,
            0x04 => 0x20000,
            0x05 => 0x10000,
            _ => 0,
        };
    private static int DecodeRomBankCount(byte sizeCode) =>
        (sizeCode <= 0x08) ? (2 << sizeCode) : 2;
    private static void DecodeType(byte typeCode, out MapperKind mapper, out bool hasRam, out bool hasBattery) {
        (mapper, hasRam, hasBattery) = typeCode switch {
            0x00 => (MapperKind.RomOnly, false, false),
            0x01 => (MapperKind.Mbc1, false, false),
            0x02 => (MapperKind.Mbc1, true, false),
            0x03 => (MapperKind.Mbc1, true, true),
            0x05 => (MapperKind.Mbc2, false, false),
            0x06 => (MapperKind.Mbc2, false, true),
            0x08 => (MapperKind.RomOnly, true, false),
            0x09 => (MapperKind.RomOnly, true, true),
            0x0B => (MapperKind.Mmm01, false, false),
            0x0C => (MapperKind.Mmm01, true, false),
            0x0D => (MapperKind.Mmm01, true, true),
            0x0F => (MapperKind.Mbc3, false, true),
            0x10 => (MapperKind.Mbc3, true, true),
            0x11 => (MapperKind.Mbc3, false, false),
            0x12 => (MapperKind.Mbc3, true, false),
            0x13 => (MapperKind.Mbc3, true, true),
            0x19 => (MapperKind.Mbc5, false, false),
            0x1A => (MapperKind.Mbc5, true, false),
            0x1B => (MapperKind.Mbc5, true, true),
            0x1C => (MapperKind.Mbc5, false, false),
            0x1D => (MapperKind.Mbc5, true, false),
            0x1E => (MapperKind.Mbc5, true, true),
            // The MBC7's save chip is a serial EEPROM behind its register window, not header-described external RAM.
            0x22 => (MapperKind.Mbc7, false, true),
            0xFC => (MapperKind.PocketCamera, true, true),
            0xFE => (MapperKind.HuC3, true, true),
            0xFF => (MapperKind.HuC1, true, true),
            _ => (MapperKind.RomOnly, false, false),
        };
    }
    private static string ReadTitle(byte[] rom) {
        var builder = new StringBuilder(capacity: (TitleEnd - TitleStart));

        for (var offset = TitleStart; (offset < TitleEnd); ++offset) {
            var value = rom[offset];

            if ((value == 0x00) || (value > 0x7E)) {
                break;
            }

            _ = builder.Append(value: (char)value);
        }

        return builder.ToString().TrimEnd();
    }
}
