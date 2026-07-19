using Puck.Authoring;
namespace Puck.Forge.Tune;

/// <summary>
/// The jukebox cartridge's public face: a minimal framework cart whose ENTIRE identity beyond the standard sound
/// plumbing is an authored <c>puck.audio.v1</c> document (see <see cref="AudioDocument"/>) compiled through
/// <see cref="AudioDocumentCompiler"/> — the music-as-data workstream's first playable proof. The
/// <see cref="Build"/>/<see cref="Verify"/> pair follows the same shape as every other framework game's forge facade.
/// </summary>
public static class TuneRom {
    /// <summary>Assembles the jukebox <c>.gbc</c> from a normalized audio document.</summary>
    /// <param name="document">The normalized document (see <see cref="AudioDocumentStore.Load"/>).</param>
    /// <param name="title">The cartridge header title.</param>
    /// <returns>The 32 KiB ROM image.</returns>
    public static byte[] Build(AudioDocument document, string title = "PUCKTUNE") => TuneGame.Build(document: document, title: title);

    /// <summary>Boots the ROM on a real Humble machine and asserts the state machine runs and START toggles
    /// play/stop. Throws on any violation (the forge's "verify by running" gate).</summary>
    /// <param name="rom">The ROM image to verify.</param>
    public static void Verify(byte[] rom) => TuneVerify.Run(rom: rom);
}
