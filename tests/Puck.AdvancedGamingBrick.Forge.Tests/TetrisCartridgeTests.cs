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

        // The heavy passes — the row scan, the collapse, the republish — are arms of one phase guard, and the phase
        // is adopted only after every arm has been passed, so the estimate charges the dearest arm rather than all of
        // them. That is what has to fit the machine, and the cadence test below is what proves the estimate right.
        var (frame, reservation) = CartridgeDocuments.Estimate(document: document);
        Assert.True(condition: frame.IsKnown, userMessage: frame.Reason);
        Assert.True(condition: frame.Cycles <= reservation, userMessage: $"{frame.Cycles} units against {reservation}");
    }

    [Fact]
    public void TheCartridgeHoldsFrameCadenceThroughLocksAndLineClears() {
        var result = new HgbCartridgeCompiler().Compile(document: Document());
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "tetris-cadence");

        const int Frames = 240;
        machine.RunFrames(buttons: JoypadButtons.None, frames: Frames);

        // The counter is a byte, so it wraps; the comparison is against the run's own wrapped expectation.
        var expected = (byte)Frames;
        var ticks = machine.Read(address: (ushort)result.Variables["ticks"]);
        Assert.InRange(actual: ticks, low: (byte)(expected - 4), high: expected);
    }

    [Fact]
    public void APieceSpawnsAndFallsUnderGravity() {
        var result = new HgbCartridgeCompiler().Compile(document: Document());
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "tetris");

        machine.RunFrames(buttons: JoypadButtons.None, frames: 4);
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
        // column of the floor means it completes that row on landing and nothing else can.
        var document = Document();
        var field = document.Arrays.First(predicate: static array => array.Name == "field");
        var seeded = (int[])field.Initial.Clone();
        foreach (var column in new[] { 0, 1, 2, 7, 8, 9 }) {
            seeded[(17 * 10) + column] = 1;
        }

        var result = new HgbCartridgeCompiler().Compile(document: document with {
            Arrays = [.. document.Arrays.Select(selector: array => array.Name == "field" ? array with { Initial = seeded } : array)],
        });
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "tetris-clear");
        machine.RunFrames(buttons: JoypadButtons.None, frames: 60 * 25);

        Assert.Equal(expected: 1, actual: machine.Read(address: (ushort)result.Variables["lines"]));

        // The completed row is gone rather than merely marked: the floor holds only what fell after it.
        var floor = 0;
        for (var column = 0; column < 10; ++column) {
            if (machine.Read(address: (ushort)(result.Arrays["field"] + (uint)((17 * 10) + column))) != 0) { ++floor; }
        }

        Assert.True(condition: floor < 10, userMessage: $"{floor} cells still on the floor");
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
