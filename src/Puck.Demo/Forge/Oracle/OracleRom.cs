namespace Puck.Demo.Forge;

/// <summary>
/// The ORACLE cartridge's public face — a spare, hand-authored fortune-telling cart on the SM83 game framework. The
/// <see cref="Build"/>/<see cref="Verify"/> pair mirrors the other framework games' facade shape (the forge CLI and
/// the overworld's cart-type table call these), though ORACLE is deliberately small: no battery, no sound, no GPU art
/// — just the word, the blinking prompt, and a typewritten fortune whose only trick is that it is always right.
/// </summary>
internal static class OracleRom {
    /// <summary>Assembles the ORACLE <c>.gbc</c> (a genuine 32 KiB MBC1 Color image; the RAM/battery of the framework
    /// cartridge goes unused).</summary>
    /// <param name="title">The cartridge header title.</param>
    /// <returns>The ROM image.</returns>
    public static byte[] Build(string title = "ORACLE") => OracleGame.Build(title: title);

    /// <summary>Boots the ROM on real Humble machines and asserts the game's observable behaviour — the title renders,
    /// an A press types out a fortune, the SAME press tick yields the SAME fortune across fresh machines (the
    /// determinism joke, proven), and the frame-perfect power-on press reveals the hidden fortune. Throws on any
    /// violation (the forge's "verify by running" gate).</summary>
    /// <param name="rom">The ROM image to verify.</param>
    public static void Verify(byte[] rom) => OracleVerify.Run(rom: rom);
}
