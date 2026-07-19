using Puck.Forge.Framework;

namespace Puck.Forge.Tune;

/// <summary>
/// The shared constants of the minimal jukebox cartridge: its one state id and its game-owned work-RAM layout (at
/// <see cref="FrameworkMemoryMap.GameRam"/>). The self-verify battery reads the SAME constants it drives the ROM
/// against, so the C# oracle and the SM83 cart can never drift apart.
/// </summary>
internal static class TuneProtocol {
    /// <summary>The (only) play state: the loop is already running, and START toggles play/stop.</summary>
    public const byte StatePlay = 0;

    // Game work RAM (0xC200+).
    /// <summary>Non-zero while the loop is (meant to be) playing — the WRAM mirror <see cref="TuneGame"/>'s START
    /// handler flips, independent of the driver's own idle/playing bookkeeping (so the verifier can observe player
    /// INTENT even on a frame the driver's pointer already unwound to idle).</summary>
    public const ushort PlayingFlag = 0xC200;
}
