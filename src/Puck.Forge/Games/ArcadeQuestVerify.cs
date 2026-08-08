using Puck.Forge.Framework;
using Puck.HumbleGamingBrick;

namespace Puck.Forge.Games;

/// <summary>
/// The arcade-quest self-verify battery: boots the freshly-forged ROM on a REAL Humble machine (pure CPU, the same
/// <c>Puck.HumbleGamingBrick</c> core the engine hosts) and asserts the counter/win-flag protocol end to end — the
/// forge's "verify by running" discipline, mirroring <c>Puck.Forge.Tune.TuneVerify</c>'s identical shape.
/// </summary>
internal static class ArcadeQuestVerify {
    /// <summary>Runs the whole battery.</summary>
    /// <param name="rom">The ROM image.</param>
    public static void Run(byte[] rom) {
        ArgumentNullException.ThrowIfNull(rom);

        using var driver = new VerifyMachineDriver(rom: rom, label: "arcade-quest");

        // Boot: the machine reaches the (only) play state within a few frames, with the VBlank handler alive and
        // both the counter and the win flag zero-filled (the correctness discipline the mission text calls for —
        // see ArcadeQuestProtocol's remarks on WHERE that zero-fill actually comes from).
        driver.RunFrames(buttons: JoypadButtons.None, frames: 8);
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.GameState) == ArcadeQuestProtocol.StatePlay), message: $"boot did not land on the play state (state {driver.Read(address: FrameworkMemoryMap.GameState)})");
        Assert(condition: (driver.Read(address: FrameworkMemoryMap.PendingState) == 0xFF), message: "the boot state request was never consumed (the frame dispatch is not running)");
        Assert(condition: (driver.ReadWide(address: FrameworkMemoryMap.FrameCounter) > 0), message: "the frame counter never advanced (the VBlank handler is not firing)");
        Assert(condition: (driver.Read(address: ArcadeQuestProtocol.Position) == 0), message: "the position counter did not boot zeroed");
        Assert(condition: (driver.Read(address: ArcadeQuestProtocol.WinFlag) == 0), message: "the win flag did not boot zeroed (a stale WRAM residue would auto-fire the world's memory watch)");

        // WinPosition presses of RIGHT walk the counter up one at a time; the win flag latches only on the last.
        for (var press = 1; (press <= ArcadeQuestProtocol.WinPosition); press++) {
            driver.Press(buttons: JoypadButtons.Right);

            var expectWin = (press == ArcadeQuestProtocol.WinPosition);

            Assert(condition: (driver.Read(address: ArcadeQuestProtocol.Position) == press), message: $"press {press}: position reads {driver.Read(address: ArcadeQuestProtocol.Position)}, expected {press}");
            Assert(condition: (driver.Read(address: ArcadeQuestProtocol.WinFlag) == (expectWin ? 1 : 0)), message: $"press {press}: win flag reads {driver.Read(address: ArcadeQuestProtocol.WinFlag)}, expected {(expectWin ? 1 : 0)}");
        }

        // A further press after the win is a no-op (the tick's early-return guard) — the counter and flag both hold.
        driver.Press(buttons: JoypadButtons.Right);
        Assert(condition: (driver.Read(address: ArcadeQuestProtocol.Position) == ArcadeQuestProtocol.WinPosition), message: "a press after the win moved the counter");
        Assert(condition: (driver.Read(address: ArcadeQuestProtocol.WinFlag) == 1), message: "a press after the win cleared the win flag");

        Console.WriteLine(value: "arcade-quest verify | boot zeroed | 3x RIGHT wins | post-win press is inert");
    }

    private static void Assert(bool condition, string message) =>
        VerifyMachineDriver.Assert(condition: condition, message: message, label: "arcade-quest");
}
