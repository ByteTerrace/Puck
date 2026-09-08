namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// The cartridge headers a boot image is verified against. Each one steers a different row of the
/// boot-timing and register-handoff tables: the licensee buckets, the color flag, the title checksums the Color handoff
/// gives a different H and L, and the checksums whose timing is told apart by the fourth title letter.
/// </summary>
public static class BootRomHandoffCases {
    private const int TitleLength = 15;

    /// <summary>Creates the header cases.</summary>
    /// <returns>The cases, each a name and the ROM image to boot.</returns>
    public static (string Name, byte[] Rom)[] Create() =>
        [
            Case(
                colorFlag: 0x00,
                name: "monochrome first-party",
                newLicensee: "  ",
                oldLicensee: 0x01,
                title: "PUCK"
            ),
            Case(
                colorFlag: 0x00,
                name: "monochrome third-party",
                newLicensee: "  ",
                oldLicensee: 0x79,
                title: "PUCK THIRD"
            ),
            Case(
                colorFlag: 0x00,
                name: "monochrome new-licensee 01",
                newLicensee: "01",
                oldLicensee: 0x33,
                title: "PUCK NEW"
            ),
            Case(
                colorFlag: 0x00,
                name: "monochrome new-licensee 0Z",
                newLicensee: "0Z",
                oldLicensee: 0x33,
                title: "PUCK OTHER"
            ),
            Case(
                colorFlag: 0x00,
                name: "monochrome checksum 0x43",
                newLicensee: "  ",
                oldLicensee: 0x01,
                title: TitleWithChecksum(
                    colorFlag: 0x00,
                    prefix: "PUCK",
                    target: 0x43
                )
            ),
            Case(
                colorFlag: 0x00,
                name: "monochrome checksum 0x58",
                newLicensee: "  ",
                oldLicensee: 0x01,
                title: TitleWithChecksum(
                    colorFlag: 0x00,
                    prefix: "PUCK",
                    target: 0x58
                )
            ),
            Case(
                colorFlag: 0x00,
                name: "monochrome ambiguous 0xB3 U",
                newLicensee: "  ",
                oldLicensee: 0x01,
                title: TitleWithChecksum(
                    colorFlag: 0x00,
                    prefix: "PUCU",
                    target: 0xB3
                )
            ),
            Case(
                colorFlag: 0x00,
                name: "monochrome ambiguous 0xB3 other",
                newLicensee: "  ",
                oldLicensee: 0x01,
                title: TitleWithChecksum(
                    colorFlag: 0x00,
                    prefix: "PUCX",
                    target: 0xB3
                )
            ),
            Case(
                colorFlag: 0x80,
                name: "color first-party",
                newLicensee: "01",
                oldLicensee: 0x33,
                title: "PUCK COLOR"
            ),
            Case(
                colorFlag: 0xC0,
                name: "color-only third-party",
                newLicensee: "AB",
                oldLicensee: 0x33,
                title: "PUCK CGB ONLY"
            ),
        ];

    // A title whose sixteen checksum bytes (the fifteen title bytes plus the color flag) sum to the target, so a case
    // can name the table row it exercises instead of hoping to land on it.
    private static string TitleWithChecksum(string prefix, byte colorFlag, byte target) {
        var padded = prefix.PadRight(
            paddingChar: ' ',
            totalWidth: (TitleLength - 1)
        );
        var sum = colorFlag;

        foreach (var character in padded) {
            sum += ((byte)character);
        }

        return (padded + ((char)((byte)(target - sum))));
    }
    private static (string Name, byte[] Rom) Case(string name, string title, byte colorFlag, byte oldLicensee, string newLicensee) =>
        (name, BootRomProbeCartridge.Create(probe: new BootRomProbe(
            ColorFlag: colorFlag,
            HandoffLine: 0,
            NewLicenseeCode: newLicensee,
            OldLicenseeCode: oldLicensee,
            Title: title
        )));
}
