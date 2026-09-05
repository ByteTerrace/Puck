namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// The Color image's low window past its entry jump. The Color overlay covers <c>0x0000</c>-<c>0x00FF</c> as well as
/// <c>0x0200</c>-<c>0x08FF</c>, and the program itself lives above the header window — so the bytes between the entry
/// jump and the unmap are otherwise wasted address space. The compatibility-palette selection tables sit there, which
/// is what keeps the rest of the program inside the upper window.
/// <para>
/// Nothing here is label-resolved: every table is a fixed size at a fixed address, so the program addresses them with
/// literals and this type is the one place those literals are derived.
/// </para>
/// </summary>
internal static class BootRomLowWindow {
    /// <summary>The first byte past the entry jump.</summary>
    public const ushort Base = 0x0003;
    /// <summary>The first byte the unmap occupies; the data must end before it.</summary>
    public const ushort End = 0x00FE;

    /// <summary>Gets the address of the title-checksum rows the palette selection scans.</summary>
    public static ushort TitleChecksumRows =>
        Base;
    /// <summary>Gets the address of the palette combination each checksum row selects.</summary>
    public static ushort CombinationPerRow =>
        ((ushort)(TitleChecksumRows + CompatibilityPalette.TitleChecksumRows.Length));
    /// <summary>Gets the address of the fourth-title-letter tie-breaks for the duplicated checksum rows.</summary>
    public static ushort DuplicateLetters =>
        ((ushort)(CombinationPerRow + CompatibilityPalette.CombinationPerRow.Length));

    /// <summary>Builds the low window's data block, to be placed at <see cref="Base"/>.</summary>
    /// <returns>The bytes.</returns>
    /// <exception cref="InvalidOperationException">The tables do not fit between the entry jump and the unmap.</exception>
    public static byte[] Build() {
        var data = new byte[((DuplicateLetters + CompatibilityPalette.DuplicateLetters.Length) - Base)];

        if ((Base + data.Length) > End) {
            throw new InvalidOperationException(message: $"The Color boot image's low-window tables are 0x{data.Length:X} bytes and do not fit the 0x{(End - Base):X} bytes between the entry jump and the unmap.");
        }

        CompatibilityPalette.TitleChecksumRows.CopyTo(destination: data.AsSpan(start: (TitleChecksumRows - Base)));
        CompatibilityPalette.CombinationPerRow.CopyTo(destination: data.AsSpan(start: (CombinationPerRow - Base)));
        CompatibilityPalette.DuplicateLetters.CopyTo(destination: data.AsSpan(start: (DuplicateLetters - Base)));

        return data;
    }
}
