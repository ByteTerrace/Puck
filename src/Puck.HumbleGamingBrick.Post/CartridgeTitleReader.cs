namespace Puck.HumbleGamingBrick.Post;

// The abbreviated title a stage report reads for a human-readable label — narrower than
// Puck.HumbleGamingBrick.CartridgeHeader's own Title (11 bytes vs. that type's 15, and a stricter 0x80 cutoff vs.
// 0x7E), so this stays its own reader rather than delegating to it.
internal static class CartridgeTitleReader {
    /// <summary>Reads the title bytes at 0x0134-0x013E, stopping at the first NUL or non-ASCII byte.</summary>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <returns>The trimmed title.</returns>
    public static string CartridgeTitle(byte[] rom) {
        var builder = new System.Text.StringBuilder(capacity: 11);

        for (var offset = 0x0134; (offset < 0x013F); ++offset) {
            var character = rom[offset];

            if (
                (character == 0) ||
                (character >= 0x80)
            ) {
                break;
            }

            _ = builder.Append(value: ((char)character));
        }

        return builder.ToString().Trim();
    }
}
