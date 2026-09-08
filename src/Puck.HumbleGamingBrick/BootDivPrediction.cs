namespace Puck.HumbleGamingBrick;

/// <summary>
/// Predicts the 16-bit divider counter a boot ROM hands the cartridge at <c>0x0100</c>. The counter runs at one step
/// per CPU T-cycle from power-on, so its handoff value is the boot ROM's running time — fixed on the monochrome
/// consoles, a linear function of the forwarded header bits on the companion console, and steered by several header
/// fields on the Color consoles. The tables are that boot-timing data, measurements of the hardware rather than
/// emulator logic.
/// </summary>
public static class BootDivPrediction {
    // The monochrome boot ROMs always hand off at the same counter value.
    /// <summary>The counter the revision-0 monochrome boot ROM hands off with, for every cartridge.</summary>
    public const ushort Dmg0Counter = 0x1830;
    /// <summary>The counter every later monochrome boot ROM hands off with, for every cartridge.</summary>
    public const ushort DmgCounter = 0xABCC;
    // CPU CGB revision 0 runs 0x20C T-cycles longer than the later Color steppings for the same header.
    /// <summary>The T-cycles CPU CGB revision 0 runs beyond the later Color steppings for the same header.</summary>
    public const ushort Cgb0Extra = 0x020C;
    // The Advanced boot ROM's extra `inc b` costs exactly one machine cycle on top of the Color handoff.
    /// <summary>The T-cycles the Advanced boot ROM's extra <c>inc b</c> costs on top of the Color handoff.</summary>
    public const ushort AgbExtra = 0x0004;
    // The companion console forwards 0x0104-0x014F bit by bit, a set bit costing one machine cycle less than a clear
    // one. The base is the all-clear duration; ForwardedSetBitCount subtracts from it.
    /// <summary>The companion console's all-clear forwarding duration, before the per-set-bit subtraction.</summary>
    public const ushort SgbBaseCounter = 0xDC88;
    /// <summary>The T-cycles a set forwarded bit saves against a clear one.</summary>
    public const int SgbTCyclesPerSetBit = 4;
    // A base entry at or below the sentinel is an offset that still needs the title-checksum contribution; anything
    // above it is already the final counter value.
    /// <summary>The boundary between a base entry that still needs the title-checksum contribution (at or below) and
    /// one that is already the final counter (above).</summary>
    public const ushort Sentinel = 0x100;

    // Indexed [isColorGame][licensee bucket (first-party / new-licensee / other)][new0 == '0'][new1 == '1'].
    private static readonly ushort[] ByHeader = [
        0x0000, 0x0000, 0x0000, 0x0000, 0x2678, 0x2678, 0x269C, 0x0020, 0x267C, 0x267C, 0x267C, 0x267C,
        0x2FA8, 0x2FA8, 0x2FA8, 0x2FA8, 0x1E9C, 0x1E9C, 0x1EC0, 0x2FC8, 0x1EA0, 0x1EA0, 0x1EA0, 0x1EA0,
    ];
    // The checksum-dependent contribution, indexed by the 8-bit title checksum.
    private static readonly ushort[] ChecksumDiv = [
        0x28D4, 0x388C, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784,
        0x3634, 0x0000, 0x3784, 0x3784, 0x34CC, 0x3784, 0x3784, 0x3784, 0x2E44, 0x3144, 0x2D94, 0x347C,
        0x0000, 0x29A4, 0x3784, 0x3784, 0x3784, 0x2954, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784,
        0x3784, 0x3784, 0x3784, 0x0000, 0x0000, 0x376C, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784,
        0x3784, 0x3784, 0x3784, 0x3784, 0x2AE4, 0x30F4, 0x2D4C, 0x3784, 0x3784, 0x2DF4, 0x3784, 0x3784,
        0x2AD4, 0x27D4, 0x328C, 0x34A4, 0x3784, 0x3784, 0x3784, 0x34B4, 0x3784, 0x3784, 0x0000, 0x3784,
        0x3784, 0x34DC, 0x3784, 0x2D14, 0x3784, 0x3784, 0x356C, 0x3784, 0x3784, 0x3784, 0x385C, 0x3784,
        0x3784, 0x3784, 0x3784, 0x3784, 0x356C, 0x2F8C, 0x3784, 0x3784, 0x2F6C, 0x397C, 0x3784, 0x3784,
        0x3784, 0x0000, 0x3784, 0x3784, 0x3784, 0x3784, 0x0000, 0x38D4, 0x364C, 0x336C, 0x0000, 0x39DC,
        0x3784, 0x39AC, 0x3784, 0x3394, 0x314C, 0x3664, 0x3784, 0x3784, 0x3784, 0x31B4, 0x3784, 0x3784,
        0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784,
        0x3784, 0x3784, 0x363C, 0x3784, 0x2C24, 0x3784, 0x3784, 0x368C, 0x29A4, 0x3784, 0x3784, 0x3784,
        0x2D44, 0x3784, 0x2EE4, 0x3784, 0x3784, 0x347C, 0x3784, 0x2D04, 0x3784, 0x3214, 0x3104, 0x3784,
        0x3484, 0x387C, 0x3784, 0x3784, 0x3784, 0x3784, 0x35EC, 0x3784, 0x3784, 0x0000, 0x3784, 0x3784,
        0x306C, 0x3784, 0x2FF4, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x0000,
        0x3784, 0x3784, 0x3784, 0x36C4, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3224, 0x3784, 0x0000,
        0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x0000, 0x3784, 0x3784, 0x2FEC, 0x3784, 0x3784,
        0x3784, 0x3784, 0x356C, 0x3784, 0x3784, 0x2D5C, 0x3784, 0x0000, 0x3784, 0x3784, 0x3784, 0x3784,
        0x3784, 0x3784, 0x3784, 0x2CC4, 0x3784, 0x3784, 0x3784, 0x3784, 0x382C, 0x3784, 0x3784, 0x3784,
        0x3784, 0x3784, 0x3784, 0x3784, 0x36B4, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784, 0x3784,
        0x353C, 0x3784, 0x312C, 0x3784, 0x0000, 0x3784, 0x355C, 0x358C, 0x3784, 0x3784, 0x3784, 0x3784,
        0x3784, 0x3784, 0x3784, 0x3154,
    ];

    /// <summary>Gets the header-steered base table, indexed by <see cref="HeaderIndex"/>.</summary>
    public static ReadOnlySpan<ushort> HeaderBases =>
        ByHeader;
    /// <summary>Gets the checksum-dependent contribution, indexed by the 8-bit title checksum.</summary>
    public static ReadOnlySpan<ushort> ChecksumContributions =>
        ChecksumDiv;
    /// <summary>Gets the checksum rows whose contribution depends on the fourth title letter, four bytes per row:
    /// the title checksum, the fourth title letter (<c>0x00</c> matches any letter, and closes the checksum's rows),
    /// then the contribution low and high bytes. A scan takes the first row whose checksum matches and whose letter
    /// matches or is <c>0x00</c>; a checksum absent from the table has no ambiguous contribution.</summary>
    public static ReadOnlySpan<byte> AmbiguousRows =>
        Ambiguous;

    /// <summary>Computes the index into <see cref="HeaderBases"/> a cartridge header selects.</summary>
    /// <param name="header">The parsed cartridge header.</param>
    /// <returns>The table index.</returns>
    public static int HeaderIndex(CartridgeHeader header) {
        ArgumentNullException.ThrowIfNull(argument: header);

        var licenseeBucket = ((header.OldLicenseeCode == 0x01)
            ? 0
            : ((header.OldLicenseeCode == 0x33)
                ? 1
                : 2));
        var new0 = ((header.NewLicenseeCode0 == ((byte)'0'))
            ? 1
            : 0);
        var new1 = ((header.NewLicenseeCode1 == ((byte)'1'))
            ? 1
            : 0);

        return ((((((header.SupportsColor
            ? 1
            : 0) * 3) + licenseeBucket) * 4) + (new0 * 2)) + new1);
    }
    /// <summary>Computes the post-boot divider counter a revision's boot ROM hands the cartridge.</summary>
    /// <param name="model">The revision whose boot ROM runs.</param>
    /// <param name="header">The parsed cartridge header, which steers the companion-console and Color durations.</param>
    /// <returns>The 16-bit counter value the boot ROM hands off with.</returns>
    public static ushort Compute(ConsoleModel model, CartridgeHeader header) {
        if (model.IsSuperGameBoy()) {
            return ((ushort)(SgbBaseCounter - (SgbTCyclesPerSetBit * header.ForwardedSetBitCount)));
        }

        if (!model.SupportsColor()) {
            return ((model == ConsoleModel.Dmg0)
                ? Dmg0Counter
                : DmgCounter);
        }

        var color = ComputeColor(header: header);

        if (model == ConsoleModel.Cgb0) {
            return ((ushort)(color + Cgb0Extra));
        }

        return (model.HasAgbBootHandoff()
            ? (ushort)(color + AgbExtra)
            : color);
    }

    private static ushort ComputeColor(CartridgeHeader header) {
        var baseDiv = ByHeader[HeaderIndex(header: header)];

        if (baseDiv > Sentinel) {
            return baseDiv;
        }

        var checksumDiv = ChecksumDiv[header.TitleChecksum];

        if (checksumDiv > Sentinel) {
            return ((ushort)(baseDiv + checksumDiv));
        }

        // Titles whose checksums collide are told apart by the fourth title letter.
        var ambiguous = AmbiguousDiv(
            checksum: header.TitleChecksum,
            fourthTitleLetter: header.FourthTitleLetter
        );

        return ((ambiguous != 0)
            ? (ushort)(baseDiv + ambiguous)
            : baseDiv);
    }

    // Four bytes per row: title checksum, fourth title letter (0x00 matching any and closing the checksum's rows),
    // contribution low byte, contribution high byte.
    private static readonly byte[] Ambiguous = [
        0x0D, ((byte)'R'), 0xC4, 0x3E, 0x0D, ((byte)'E'), 0xE8, 0x35, 0x0D, 0x00, 0x1C, 0x38,
        0x18, ((byte)'K'), 0xD4, 0x3B, 0x18, ((byte)'I'), 0x28, 0x37, 0x18, 0x00, 0x5C, 0x37,
        0x27, ((byte)'B'), 0xF4, 0x3A, 0x27, ((byte)'N'), 0x10, 0x3C, 0x27, 0x00, 0xFC, 0x36,
        0x28, ((byte)'F'), 0xEC, 0x33, 0x28, ((byte)'A'), 0x88, 0x3A, 0x28, 0x00, 0x3C, 0x36,
        0x46, ((byte)'E'), 0x3C, 0x33, 0x46, ((byte)'R'), 0xE0, 0x3B, 0x46, 0x00, 0x0C, 0x36,
        0x61, ((byte)'E'), 0x4C, 0x37, 0x61, ((byte)'A'), 0x40, 0x3C, 0x61, 0x00, 0x2C, 0x37,
        0x66, ((byte)'E'), 0xFC, 0x33, 0x66, ((byte)'L'), 0x58, 0x37, 0x66, 0x00, 0x8C, 0x37,
        0x6A, ((byte)'K'), 0x34, 0x3C, 0x6A, ((byte)'I'), 0xA8, 0x34, 0x6A, 0x00, 0xBC, 0x37,
        0xA5, ((byte)'A'), 0x5C, 0x3A, 0xA5, ((byte)'R'), 0xF8, 0x34, 0xA5, 0x00, 0x6C, 0x36,
        0xB3, ((byte)'B'), 0xD4, 0x39, 0xB3, ((byte)'U'), 0x28, 0x32, 0xB3, ((byte)'R'), 0xFC, 0x3C, 0xB3, 0x00, 0x38, 0x36,
        0xBF, ((byte)' '), 0x7C, 0x35, 0xBF, ((byte)'C'), 0x80, 0x3B, 0xBF, 0x00, 0xEC, 0x37,
        0xC6, ((byte)'A'), 0x94, 0x39, 0xC6, ((byte)' '), 0x68, 0x36, 0xC6, 0x00, 0x9C, 0x36,
    ];

    private static ushort AmbiguousDiv(byte checksum, byte fourthTitleLetter) {
        for (var offset = 0; (offset < Ambiguous.Length); offset += 4) {
            if (Ambiguous[offset] != checksum) {
                continue;
            }

            var letter = Ambiguous[(offset + 1)];

            if (
                (letter == 0x00) ||
                (letter == fourthTitleLetter)
            ) {
                return ((ushort)(Ambiguous[(offset + 2)] | (Ambiguous[(offset + 3)] << 8)));
            }
        }

        return 0;
    }
}
