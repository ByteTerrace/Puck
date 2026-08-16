using Puck.Forge.Framework;

namespace Puck.Forge.Games;

/// <summary>
/// The shared constants of the arcade-quest cartridge: its one state id and its game-owned work-RAM layout (at
/// <see cref="FrameworkMemoryMap.GameRam"/>). The self-verify battery reads the SAME constants it drives the ROM
/// against, so the C# oracle and the SM83 cart can never drift apart. The world side detects the win with a
/// <c>Puck.World.WorldRule</c>'s <c>$machine:&lt;screen&gt;:&lt;address&gt;</c> reserved <c>compareState</c> channel,
/// authored against <see cref="WinFlag"/>'s numeric address directly in the world document JSON — see the ROM's
/// README for that address spelled out for a document author.
/// </summary>
internal static class ArcadeQuestProtocol {
    /// <summary>The (only) state: walk the counter up to <see cref="WinPosition"/> via the RIGHT button.</summary>
    public const byte StatePlay = 0;
    // Game work RAM (0xC200+). FrameworkKernel.EmitBootPrologue's boot block-fill zero-fills this whole span every
    // boot (split only around the reserved victory-share slot at 0xC0F0..0xC0FF, well below GameRam) — a stale
    // WRAM residue can never leave WinFlag set at boot, the Demo win-detection correctness discipline this cart
    // inherits for free from the framework's own boot sequence. EmitPlayEnter (see ArcadeQuestGame) restates the
    // clear explicitly anyway, defense in depth, rather than relying on it silently.
    /// <summary>The walk counter, 0..<see cref="WinPosition"/>, incremented one per RIGHT press.</summary>
    public const ushort Position = 0xC200;
    /// <summary>The win flag: 0 until the counter reaches <see cref="WinPosition"/>, then 1 forever (this cart never
    /// clears it again — a single-shot latch, matching the Demo's WRAM win-flag convention). This is the address a
    /// world document's <c>WorldAddonMemoryWatch</c> row watches.</summary>
    public const ushort WinFlag = 0xC201;
    /// <summary>The counter value that wins the game — three RIGHT presses, deliberately short so a scripted smoke
    /// test can drive it in a handful of lines.</summary>
    public const byte WinPosition = 3;
}
