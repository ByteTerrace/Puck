namespace Puck.HumbleGamingBrick.Forge.Games;

/// <summary>
/// The arcade-quest cartridge's public face: a minimal framework cart whose whole identity is the walk-to-3 counter
/// game (see <see cref="ArcadeQuestProtocol"/>). The <see cref="Build"/>/<see cref="Verify"/> pair follows the same
/// shape as every other framework game's forge facade (<c>Puck.HumbleGamingBrick.Forge.Tune.TuneRom</c>).
/// </summary>
public static class ArcadeQuestRom {
    /// <summary>Assembles the arcade-quest <c>.gbc</c>.</summary>
    /// <param name="title">The cartridge header title (≤ 15 characters).</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public static byte[] Build(string title = "ARCADE QUEST") => ArcadeQuestGame.Build(title: title);
    /// <summary>Boots the ROM on a real Humble machine and asserts the counter/win-flag protocol end to end. Throws
    /// on any violation (the forge's "verify by running" gate).</summary>
    /// <param name="rom">The ROM image to verify.</param>
    public static void Verify(byte[] rom) => ArcadeQuestVerify.Run(rom: rom);
}
