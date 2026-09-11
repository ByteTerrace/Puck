using System.Text.Json;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers the authored Tetris cartridge: the document is the game, so these run the real image on the real machine and
/// assert on its state rather than on the document's shape.
/// </summary>
public sealed class TetrisCartridgeTests {
    [Fact]
    public void TheDocumentValidatesAndEveryPrimitiveItUsesIsMeasured() {
        var document = Document();
        Assert.Empty(collection: CartridgeDocuments.Validate(document: document));

        // The estimate is advice, so nothing here compares it against the reservation: a cartridge that overruns a
        // frame now and then still plays, and the original pauses on a line clear anyway. What it must say is that
        // every primitive the document uses was measured, so the advice is about this machine rather than a guess.
        // Whether the game actually keeps up is asked of the machine, in the cadence test.
        var (frame, _) = CartridgeDocuments.Estimate(document: document);
        Assert.True(condition: frame.IsKnown, userMessage: frame.Reason);
    }

    [Fact]
    public void TheCartridgeHoldsFrameCadenceThroughLocksAndLineClears() {
        var result = new HgbCartridgeCompiler().Compile(document: Document());
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "tetris-cadence");

        StartGame(machine: machine);
        var started = machine.Read(address: (ushort)result.Variables["ticks"]);
        const int Frames = 240;
        machine.RunFrames(buttons: JoypadButtons.None, frames: Frames);

        // The counter is a byte, so it wraps; the comparison is against the run's own wrapped expectation.
        var expected = (byte)(started + Frames);
        var ticks = machine.Read(address: (ushort)result.Variables["ticks"]);
        Assert.InRange(actual: ticks, low: (byte)(expected - 4), high: expected);
    }

    [Fact]
    public void APieceSpawnsAndFallsUnderGravity() {
        var result = new HgbCartridgeCompiler().Compile(document: Document());
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "tetris");

        StartGame(machine: machine);
        Assert.Equal(expected: 0, actual: machine.Read(address: (ushort)result.Variables["ph"]));
        var first = machine.Read(address: (ushort)result.Variables["py"]);

        machine.RunFrames(buttons: JoypadButtons.None, frames: 120);
        var later = machine.Read(address: (ushort)result.Variables["py"]);
        Assert.True(condition: later > first, userMessage: $"row {first} then {later}");
    }

    [Fact]
    public void APieceComesToRestOnTheFloorAndAnotherFollowsIt() {
        var result = new HgbCartridgeCompiler().Compile(document: Document());
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "tetris-rest");

        StartGame(machine: machine);

        // Long enough for the first piece to reach the floor, settle, and the next to take the field.
        machine.RunFrames(buttons: JoypadButtons.None, frames: 60 * 20);

        // Something is settled at the foot of the well.
        var settled = 0;
        for (var index = 0; index < 180; ++index) {
            if (machine.Read(address: (ushort)(result.Arrays["field"] + (uint)index)) != 0) { ++settled; }
        }

        Assert.True(condition: settled >= 4, userMessage: $"{settled} cells settled");
        Assert.Equal(expected: 0, actual: machine.Read(address: (ushort)result.Variables["over"]));
    }

    [Fact]
    public void ACompletedRowClearsAndCountsTowardTheLineTotal() {
        // The first piece out of the generator is the bar, spawning across columns three to six. Seeding every other
        // column of the floor once the game is running means it completes that row on landing and nothing else can.
        // The seed goes in after the start sequence because starting a game wipes the well, as it should.
        var result = new HgbCartridgeCompiler().Compile(document: Document());
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "tetris-clear");
        StartGame(machine: machine);
        foreach (var column in new[] { 0, 1, 2, 7, 8, 9 }) {
            machine.Write(address: (ushort)(result.Arrays["field"] + (17 * 10) + (uint)column), value: 1);
        }

        machine.RunFrames(buttons: JoypadButtons.None, frames: 60 * 25);

        Assert.Equal(expected: 1, actual: machine.Read(address: (ushort)result.Variables["lines"]));

        // The completed row is gone rather than merely marked: the floor holds only what fell after it.
        var floor = 0;
        for (var column = 0; column < 10; ++column) {
            if (machine.Read(address: (ushort)(result.Arrays["field"] + (uint)((17 * 10) + column))) != 0) { ++floor; }
        }

        Assert.True(condition: floor < 10, userMessage: $"{floor} cells still on the floor");
    }

    [Fact]
    public void TheCartridgeOpensOnItsTitleAndWalksThroughTheModeMenuIntoAGame() {
        var result = new HgbCartridgeCompiler().Compile(document: Document());
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "tetris-screens");
        var phase = (ushort)result.Variables["ph"];

        // Boot lands on the title, not in a game.
        machine.RunFrames(buttons: JoypadButtons.None, frames: 20);
        Assert.Equal(expected: 8, actual: machine.Read(address: phase));

        // Start leaves it, and the well is wiped on the way to the menu.
        machine.RunFrames(buttons: JoypadButtons.Start, frames: 4);
        machine.RunFrames(buttons: JoypadButtons.None, frames: 40);
        Assert.Equal(expected: 10, actual: machine.Read(address: phase));

        // The chooser moves between the two modes and the second one is remembered.
        machine.RunFrames(buttons: JoypadButtons.Down, frames: 4);
        machine.RunFrames(buttons: JoypadButtons.None, frames: 4);
        machine.RunFrames(buttons: JoypadButtons.Start, frames: 4);
        machine.RunFrames(buttons: JoypadButtons.None, frames: 60);
        Assert.Equal(expected: 1, actual: machine.Read(address: (ushort)result.Variables["gtype"]));
        Assert.Equal(expected: 0, actual: machine.Read(address: phase));

        // That mode starts under rubble, which is the whole of its problem.
        var settled = 0;
        for (var index = 0; index < 180; ++index) {
            if (machine.Read(address: (ushort)(result.Arrays["field"] + (uint)index)) != 0) { ++settled; }
        }

        Assert.InRange(actual: settled, low: 9 * 5, high: 9 * 5 + 4);
    }

    [Fact]
    public void AStackThatReachesTheCeilingEndsTheGame() {
        var result = new HgbCartridgeCompiler().Compile(document: Document());
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "tetris-over");
        StartGame(machine: machine);

        // Fill the ceiling but leave a column short, so no row is complete: a full well would clear instead of
        // topping out, which is the opposite of what this asks.
        for (var row = 0; row < 6; ++row) {
            for (var column = 0; column < 9; ++column) {
                machine.Write(address: (ushort)(result.Arrays["field"] + (uint)((row * 10) + column)), value: 1);
            }
        }

        machine.RunFrames(buttons: JoypadButtons.None, frames: 60 * 6);
        Assert.Equal(expected: 1, actual: machine.Read(address: (ushort)result.Variables["over"]));
        Assert.Equal(expected: 9, actual: machine.Read(address: (ushort)result.Variables["ph"]));
    }

    [Fact]
    public void AFinishedRunIsMeasuredAgainstTheBestAndKeptWhenItWins() {
        var result = new HgbCartridgeCompiler().Compile(document: Document());
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "tetris-best");
        StartGame(machine: machine);

        // A row short of one column, so the first piece completes it and the run is worth something.
        foreach (var column in new[] { 0, 1, 2, 7, 8, 9 }) {
            machine.Write(address: (ushort)(result.Arrays["field"] + (17 * 10) + (uint)column), value: 1);
        }

        machine.RunFrames(buttons: JoypadButtons.None, frames: 60 * 25);
        Assert.Equal(expected: 1, actual: machine.Read(address: (ushort)result.Variables["lines"]));

        var scored = Digits(machine: machine, address: result.Arrays["score"]);
        Assert.NotEqual(expected: "0000", actual: scored);

        // Top out: the run ends, and a run better than the stored best replaces it.
        for (var row = 0; row < 6; ++row) {
            for (var column = 0; column < 9; ++column) {
                machine.Write(address: (ushort)(result.Arrays["field"] + (uint)((row * 10) + column)), value: 1);
            }
        }

        machine.RunFrames(buttons: JoypadButtons.None, frames: 60 * 8);
        Assert.Equal(expected: 9, actual: machine.Read(address: (ushort)result.Variables["ph"]));
        Assert.Equal(expected: scored, actual: Digits(machine: machine, address: result.Arrays["best"]));
    }

    private static string Digits(VerifyMachineDriver machine, uint address) {
        var digits = new System.Text.StringBuilder();
        for (var index = 0u; index < 5u; ++index) {
            digits.Append(value: machine.Read(address: (ushort)(address + index)));
        }

        return digits.ToString();
    }

    [Fact]
    public void WinningTheSecondModeLaunchesTheRocketAndReturnsToTheTitle() {
        var result = new HgbCartridgeCompiler().Compile(document: Document());
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "tetris-ending");

        // Into the second mode: down on the menu picks it.
        machine.RunFrames(buttons: JoypadButtons.None, frames: 20);
        machine.RunFrames(buttons: JoypadButtons.Start, frames: 4);
        machine.RunFrames(buttons: JoypadButtons.None, frames: 40);
        machine.RunFrames(buttons: JoypadButtons.Down, frames: 4);
        machine.RunFrames(buttons: JoypadButtons.Start, frames: 4);
        machine.RunFrames(buttons: JoypadButtons.None, frames: 60);
        Assert.Equal(expected: 1, actual: machine.Read(address: (ushort)result.Variables["gtype"]));

        // Reaching the target is what wins it, so the count is set to the target under the running game.
        machine.Write(address: (ushort)result.Variables["lines"], value: 25);
        machine.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert.Equal(expected: 11, actual: machine.Read(address: (ushort)result.Variables["ph"]));

        // The rocket climbs off the top, and the title comes back behind it.
        machine.RunFrames(buttons: JoypadButtons.None, frames: 60 * 6);
        Assert.Equal(expected: 8, actual: machine.Read(address: (ushort)result.Variables["ph"]));
    }

    // Boot lands on the title, so a test that wants a game presses through the title and the mode menu. The waits
    // cover a screen painting itself and the well being wiped between them, both of which run a band a frame.
    private static void StartGame(VerifyMachineDriver machine) {
        for (var screen = 0; screen < 2; ++screen) {
            machine.RunFrames(buttons: JoypadButtons.None, frames: 20);
            machine.RunFrames(buttons: JoypadButtons.Start, frames: 4);
            machine.RunFrames(buttons: JoypadButtons.None, frames: 40);
        }
    }

    private static CartridgeDocument Document() {
        var json = File.ReadAllText(path: Path.Combine(
            path1: RepositoryRoot(), path2: "src/Puck.World/Assets/cartridges", path3: "tetris.cgb.cartridge.json"));

        return JsonSerializer.Deserialize<CartridgeDocument>(json: json, options: new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true,
        })!;
    }

    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);
        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
}
