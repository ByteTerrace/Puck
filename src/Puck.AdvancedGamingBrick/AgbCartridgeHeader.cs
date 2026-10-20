namespace Puck.AdvancedGamingBrick;

/// <summary>The cartridge header every AGB image carries in its first <see cref="Length"/> bytes: the entry branch,
/// the boot logo, the title, the game code, the fixed byte <c>0x96</c> at <c>0xB2</c> and the complement checksum
/// over <c>0xA0</c>–<c>0xBC</c> at <c>0xBD</c>, which the firmware checks before it hands the cartridge control.</summary>
public static class AgbCartridgeHeader {
    private const int ChecksumEnd = 0x0BC;
    private const int ChecksumOffset = 0x0BD;
    private const int ChecksumStart = 0x0A0;
    private const byte FixedValue = 0x96;
    private const int FixedValueOffset = 0x0B2;

    /// <summary>The length of the header region in bytes: an image shorter than this has no header.</summary>
    public const int Length = 0x0C0;

    /// <summary>Computes the complement checksum the header carries at <c>0xBD</c>: the negated sum of
    /// <c>0xA0</c>–<c>0xBC</c>, less <c>0x19</c>, in eight bits.</summary>
    /// <param name="rom">The image's leading bytes, through <c>0xBC</c> at least.</param>
    /// <returns>The checksum byte.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rom"/> ends before <c>0xBD</c>.</exception>
    public static byte ComputeComplementChecksum(ReadOnlySpan<byte> rom) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: rom.Length,
            other: ChecksumOffset,
            paramName: nameof(rom)
        );

        var sum = 0;

        for (var offset = ChecksumStart; (offset <= ChecksumEnd); offset++) {
            sum += rom[offset];
        }

        return ((byte)(((0 - sum) - 0x19) & 0xFF));
    }
    /// <summary>Determines whether an image carries an AGB cartridge header: it is at least <see cref="Length"/>
    /// bytes long, holds <c>0x96</c> at <c>0xB2</c>, and its complement checksum holds.</summary>
    /// <param name="header">The image's leading bytes, at least <see cref="Length"/> of them when the image has that
    /// many.</param>
    /// <param name="imageLength">The whole image's length in bytes.</param>
    /// <returns><see langword="true"/> when the header's fixed byte and complement checksum hold.</returns>
    public static bool IsCartridgeImage(ReadOnlySpan<byte> header, long imageLength) =>
        ((imageLength >= Length) &&
        (header.Length >= Length) &&
        (header[FixedValueOffset] == FixedValue) &&
        (header[ChecksumOffset] == ComputeComplementChecksum(rom: header)));
}
