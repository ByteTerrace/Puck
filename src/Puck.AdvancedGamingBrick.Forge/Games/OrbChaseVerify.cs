namespace Puck.AdvancedGamingBrick.Forge.Games;

/// <summary>
/// Orb Chase's self-verify battery: boots the freshly-forged ROM on a real Advanced GamingBrick (zeroed BIOS,
/// direct boot), scripts the keypad through the title→play commit and a PRNG-placed orb chase, and asserts EWRAM
/// state and framebuffer pixels at each stage. The whole script runs twice on fresh machines and the two
/// observation streams must match exactly — the cross-run determinism gate.
/// </summary>
internal static class OrbChaseVerify {
    private const string Label = "orb-chase";

    /// <summary>Runs the whole battery.</summary>
    /// <param name="rom">The ROM image.</param>
    public static void Run(byte[] rom) {
        ArgumentNullException.ThrowIfNull(argument: rom);

        var first = RunScript(rom: rom);
        var second = RunScript(rom: rom);

        VerifyMachineDriverAssert(condition: first.SequenceEqual(second: second), message: "two runs of the same input script diverged (the cart is not deterministic)");
    }

    private static List<uint> RunScript(byte[] rom) {
        var observations = new List<uint>();

        using var driver = new AgbVerifyMachineDriver(label: Label, rom: rom);

        VerifyBoot(driver: driver, observations: observations);
        VerifyPlayCommit(driver: driver, observations: observations);
        VerifyChase(driver: driver, observations: observations);

        return observations;
    }
    // Boot: the machine lands in the title state with the loop alive, the title bar on screen, and the field dark.
    private static void VerifyBoot(AgbVerifyMachineDriver driver, List<uint> observations) {
        driver.RunFrames(frames: 6, keys: AgbKeys.None);

        driver.Require(condition: (driver.ReadHalf(address: AgbForgeMemoryMap.GameState) == OrbChaseProtocol.StateTitle), message: $"boot did not land on the title state (state {driver.ReadHalf(address: AgbForgeMemoryMap.GameState)})");
        driver.Require(condition: (driver.ReadWord(address: AgbForgeMemoryMap.FrameCounter) > 0u), message: "the frame counter never advanced (the polling loop is not running)");
        driver.Require(condition: (driver.ReadPixel(x: 120, y: 4) == ToPixel(colour: OrbChaseProtocol.ColourTitleBar)), message: "the title bar is not on screen");
        driver.Require(condition: (driver.ReadPixel(x: 120, y: 80) == ToPixel(colour: OrbChaseProtocol.ColourBackground)), message: "the play field is not the backdrop at boot");

        Observe(driver: driver, observations: observations);
    }
    // START: seeds the PRNG, commits to play, spawns the player, and places the orb inside the grid off the player.
    private static void VerifyPlayCommit(AgbVerifyMachineDriver driver, List<uint> observations) {
        driver.Press(keys: AgbKeys.Start);
        driver.RunFrames(frames: 2, keys: AgbKeys.None);

        driver.Require(condition: (driver.ReadHalf(address: AgbForgeMemoryMap.GameState) == OrbChaseProtocol.StatePlay), message: "START did not commit to the play state");
        driver.Require(condition: (driver.ReadHalf(address: AgbForgeMemoryMap.PrngState) != 0), message: "START did not seed the PRNG");
        driver.Require(condition: (driver.ReadHalf(address: OrbChaseProtocol.PlayerX) == OrbChaseProtocol.PlayerStartX), message: "the player did not spawn at its start x");
        driver.Require(condition: (driver.ReadHalf(address: OrbChaseProtocol.PlayerY) == OrbChaseProtocol.PlayerStartY), message: "the player did not spawn at its start y");
        driver.Require(condition: (driver.ReadHalf(address: OrbChaseProtocol.Score) == 0), message: "the score did not start at zero");
        RequireTargetValid(driver: driver);
        driver.Require(condition: (driver.ReadPixel(x: (OrbChaseProtocol.PlayerStartX + 4), y: (OrbChaseProtocol.PlayerStartY + 4)) == ToPixel(colour: OrbChaseProtocol.ColourPlayer)), message: "the player square is not on screen");
        driver.Require(condition: (ReadTargetPixel(driver: driver) == ToPixel(colour: OrbChaseProtocol.ColourTarget)), message: "the orb is not on screen");
        driver.Require(condition: (driver.ReadPixel(x: 120, y: 4) == ToPixel(colour: OrbChaseProtocol.ColourBackground)), message: "the title bar survived the play transition");

        Observe(driver: driver, observations: observations);
    }
    // Walk the player onto the orb one 8-pixel pressed edge at a time; landing scores and re-places the orb.
    private static void VerifyChase(AgbVerifyMachineDriver driver, List<uint> observations) {
        var targetX = ((int)driver.ReadHalf(address: OrbChaseProtocol.TargetX));
        var targetY = ((int)driver.ReadHalf(address: OrbChaseProtocol.TargetY));

        PressSteps(delta: (targetX - OrbChaseProtocol.PlayerStartX), driver: driver, negativeKey: AgbKeys.Left, positiveKey: AgbKeys.Right);
        PressSteps(delta: (targetY - OrbChaseProtocol.PlayerStartY), driver: driver, negativeKey: AgbKeys.Up, positiveKey: AgbKeys.Down);
        driver.RunFrames(frames: 2, keys: AgbKeys.None);

        driver.Require(condition: (driver.ReadHalf(address: OrbChaseProtocol.Score) == 1), message: $"reaching the orb did not score (score {driver.ReadHalf(address: OrbChaseProtocol.Score)})");
        driver.Require(condition: (driver.ReadHalf(address: OrbChaseProtocol.PlayerX) == targetX), message: "the walk did not end on the orb's column");
        driver.Require(condition: (driver.ReadHalf(address: OrbChaseProtocol.PlayerY) == targetY), message: "the walk did not end on the orb's row");
        RequireTargetValid(driver: driver);
        driver.Require(condition: (driver.ReadPixel(x: (targetX + 4), y: (targetY + 4)) == ToPixel(colour: OrbChaseProtocol.ColourPlayer)), message: "the player square is not standing on the scored cell");
        driver.Require(condition: (ReadTargetPixel(driver: driver) == ToPixel(colour: OrbChaseProtocol.ColourTarget)), message: "the re-placed orb is not on screen");

        Observe(driver: driver, observations: observations);
    }
    private static void PressSteps(AgbVerifyMachineDriver driver, int delta, AgbKeys positiveKey, AgbKeys negativeKey) {
        var key = ((delta >= 0) ? positiveKey : negativeKey);
        var steps = (Math.Abs(value: delta) / OrbChaseProtocol.SquareSize);

        for (var step = 0; (step < steps); step++) {
            driver.Press(keys: key);
        }
    }
    // The orb sits on the 30×20 grid and never on the player's cell.
    private static void RequireTargetValid(AgbVerifyMachineDriver driver) {
        var targetX = driver.ReadHalf(address: OrbChaseProtocol.TargetX);
        var targetY = driver.ReadHalf(address: OrbChaseProtocol.TargetY);
        var onPlayer = ((targetX == driver.ReadHalf(address: OrbChaseProtocol.PlayerX)) && (targetY == driver.ReadHalf(address: OrbChaseProtocol.PlayerY)));

        driver.Require(condition: ((targetX <= OrbChaseProtocol.MaxX) && ((targetX % OrbChaseProtocol.SquareSize) == 0)), message: $"the orb's x {targetX} is off the grid");
        driver.Require(condition: ((targetY <= OrbChaseProtocol.MaxY) && ((targetY % OrbChaseProtocol.SquareSize) == 0)), message: $"the orb's y {targetY} is off the grid");
        driver.Require(condition: !onPlayer, message: "the orb was placed on the player's cell");
    }
    private static uint ReadTargetPixel(AgbVerifyMachineDriver driver) =>
        driver.ReadPixel(x: (driver.ReadHalf(address: OrbChaseProtocol.TargetX) + 4), y: (driver.ReadHalf(address: OrbChaseProtocol.TargetY) + 4));
    // The determinism stream: every state halfword plus a fixed pixel probe set.
    private static void Observe(AgbVerifyMachineDriver driver, List<uint> observations) {
        observations.Add(item: driver.ReadWord(address: AgbForgeMemoryMap.FrameCounter));
        observations.Add(item: driver.ReadHalf(address: AgbForgeMemoryMap.InputHeld));
        observations.Add(item: driver.ReadHalf(address: AgbForgeMemoryMap.PrngState));
        observations.Add(item: driver.ReadHalf(address: AgbForgeMemoryMap.GameState));
        observations.Add(item: driver.ReadHalf(address: OrbChaseProtocol.PlayerX));
        observations.Add(item: driver.ReadHalf(address: OrbChaseProtocol.PlayerY));
        observations.Add(item: driver.ReadHalf(address: OrbChaseProtocol.TargetX));
        observations.Add(item: driver.ReadHalf(address: OrbChaseProtocol.TargetY));
        observations.Add(item: driver.ReadHalf(address: OrbChaseProtocol.Score));
        observations.Add(item: driver.ReadPixel(x: 0, y: 0));
        observations.Add(item: driver.ReadPixel(x: 239, y: 0));
        observations.Add(item: driver.ReadPixel(x: 0, y: 159));
        observations.Add(item: driver.ReadPixel(x: 239, y: 159));
        observations.Add(item: driver.ReadPixel(x: 120, y: 80));
    }
    // BGR555 → the PPU's packed 0xAABBGGRR (each 5-bit channel expands as (c << 3) | (c >> 2)).
    private static uint ToPixel(ushort colour) {
        var red = Expand(channel: colour & 0x1F);
        var green = Expand(channel: (colour >> 5) & 0x1F);
        var blue = Expand(channel: (colour >> 10) & 0x1F);

        return 0xFF000000u | (blue << 16) | (green << 8) | red;
    }
    private static uint Expand(int channel) => ((uint)((channel << 3) | (channel >> 2)));
    private static void VerifyMachineDriverAssert(bool condition, string message) =>
        AgbVerifyMachineDriver.Assert(condition: condition, label: Label, message: message);
}
