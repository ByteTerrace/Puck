namespace Puck.GameBoy;

/// <summary>
/// The decoded cartridge header (<c>0x0100</c>–<c>0x014F</c>): the fields the loader needs to pick a
/// mapper and size its memory. Only the fields used during construction are surfaced; the rest of the header
/// (logo, checksums) is not interpreted here.
/// </summary>
/// <param name="Title">The ASCII title from <c>0x0134</c>, trimmed of trailing padding.</param>
/// <param name="CartridgeType">The raw cartridge-type byte at <c>0x0147</c> that selects the mapper.</param>
/// <param name="RomByteCount">The ROM size in bytes decoded from <c>0x0148</c>.</param>
/// <param name="RamByteCount">The cartridge-RAM size in bytes decoded from <c>0x0149</c>.</param>
/// <param name="SupportsColor">Whether the CGB flag at <c>0x0143</c> advertises CGB support.</param>
public readonly record struct CartridgeHeader(
    string Title,
    byte CartridgeType,
    int RomByteCount,
    int RamByteCount,
    bool SupportsColor
) {
    /// <summary>Gets whether the cartridge type indicates battery-backed RAM that should be persisted.</summary>
    public bool HasBattery =>
        CartridgeType switch {
            0x03 or 0x06 or 0x09 or 0x0D or 0x0F or 0x10 or 0x13 or 0x1B or 0x1E or 0x22 => true,
            0xFC or 0xFD or 0xFE or 0xFF => true,
            _ => false,
        };

    /// <summary>Decodes the header from a ROM image.</summary>
    /// <param name="rom">The full cartridge ROM image; must be at least <c>0x0150</c> bytes.</param>
    /// <returns>The decoded <see cref="CartridgeHeader"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="rom"/> is too small to contain a header.</exception>
    public static CartridgeHeader Decode(ReadOnlySpan<byte> rom) {
        if (rom.Length < 0x0150) {
            throw new ArgumentException(
                message: $"A ROM image must be at least 0x0150 bytes to contain a header; got {rom.Length}.",
                paramName: nameof(rom)
            );
        }

        var titleBytes = rom.Slice(start: 0x0134, length: 16);
        var titleLength = 0;

        // Newer/CGB carts shrank the title and reused its tail for the manufacturer code (0x013F-0x0142) and the
        // CGB flag (0x0143), so stop at the first NUL or any non-ASCII byte rather than absorbing those into it.
        while ((titleLength < titleBytes.Length) && (titleBytes[titleLength] is > (byte)0x00 and < (byte)0x80)) {
            titleLength += 1;
        }

        return new CartridgeHeader(
            CartridgeType: rom[0x0147],
            RamByteCount: DecodeRamSize(code: rom[0x0149]),
            RomByteCount: DecodeRomSize(code: rom[0x0148]),
            SupportsColor: ((rom[0x0143] & 0x80) != 0),
            Title: System.Text.Encoding.ASCII.GetString(bytes: titleBytes[..titleLength])
        );
    }

    private static int DecodeRomSize(byte code) =>
        // 32 KiB << code for every defined code (0x00..0x08); reject the undefined high codes.
        ((code <= 0x08)
            ? ((32 * 1024) << code)
            : throw new ArgumentOutOfRangeException(
                actualValue: code,
                message: "Unknown ROM-size code in cartridge header.",
                paramName: nameof(code)
            ));

    private static int DecodeRamSize(byte code) =>
        code switch {
            0x00 => 0,
            0x01 => (2 * 1024),
            0x02 => (8 * 1024),
            0x03 => (32 * 1024),
            0x04 => (128 * 1024),
            0x05 => (64 * 1024),
            _ => throw new ArgumentOutOfRangeException(
                actualValue: code,
                message: "Unknown RAM-size code in cartridge header.",
                paramName: nameof(code)
            ),
        };
}
