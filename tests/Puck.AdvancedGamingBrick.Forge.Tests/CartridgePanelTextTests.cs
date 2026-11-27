using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers the two reaches a learning game's text needs, on both machines: a map write into the panel's own
/// map rather than the background's, and an array longer than a byte index reaches, read through a two-byte index.</summary>
public sealed class CartridgePanelTextTests {
    private const int FarElement = 299;
    private const int FarValue = 7;
    private const int NearElement = 100;
    private const int NearValue = 5;

    private static CartridgeDocument Document(string target) {
        var colours = ((target == "agb") ? 16 : 4);
        var initial = new int[(FarElement + 1)];

        initial[FarElement] = FarValue;
        initial[NearElement] = NearValue;

        return (CartridgeDocuments.Create(target: target, title: "PANELTEXT") with {
            Tiles = [.. Enumerable.Range(start: 0, count: 8).Select(selector: static i => new CartridgeTile(Name: $"t{i}", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]))],
            Palettes = new CartridgePalettes(Background: [new int[colours]], Object: [new int[colours]]),
            Variables = [
                new CartridgeVariable(Name: "cursor", Initial: FarElement, Max: 1000),
                new CartridgeVariable(Name: "far", Initial: 0),
                new CartridgeVariable(Name: "near", Initial: 0),
            ],
            Arrays = [new CartridgeArray(Name: "text", Initial: initial)],
            Window = new CartridgeWindow(Map: new int[1024], MapPalettes: null, X: CartridgeExpressions.Of(constant: 0), Y: CartridgeExpressions.Of(constant: 80), Visible: CartridgeExpressions.Of(constant: 1)),
            Rules = [new CartridgeRule(Name: "write", Body: [
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "far"), Value: CartridgeExpressions.Of(state: "text", key: "cursor")),
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "near"), Value: CartridgeExpressions.Of(state: "text", key: $"{NearElement}")),
                new CartridgeStatement(Kind: "map", Row: CartridgeExpressions.Of(constant: 1), Column: CartridgeExpressions.Of(constant: 2), Tile: CartridgeExpressions.Of(state: "far"), Surface: "panel"),
                new CartridgeStatement(Kind: "map", Row: CartridgeExpressions.Of(constant: 3), Column: CartridgeExpressions.Of(constant: 4), Tile: CartridgeExpressions.Of(state: "near")),
            ])],
        });
    }

    // The Deck has no panel, so it is held to the long array alone: a far element read and written through a wide index.
    [Fact]
    public void TheDeckReadsAndWritesAFarElementThroughAWideIndex() {
        var initial = new int[(FarElement + 1)];

        initial[FarElement] = FarValue;
        initial[NearElement] = NearValue;

        var document = (CartridgeDocuments.Create(target: "hgd", title: "LONGARRAY") with {
            Variables = [
                new CartridgeVariable(Name: "cursor", Initial: FarElement, Max: 1000),
                new CartridgeVariable(Name: "far", Initial: 0),
                new CartridgeVariable(Name: "near", Initial: 0),
                new CartridgeVariable(Name: "written", Initial: 0),
            ],
            Arrays = [new CartridgeArray(Name: "text", Initial: initial)],
            Rules = [new CartridgeRule(Name: "read", Body: [
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "far"), Value: CartridgeExpressions.Of(state: "text", key: "cursor")),
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "near"), Value: CartridgeExpressions.Of(state: "text", key: $"{NearElement}")),
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "text", Key: "cursor"), Operation: Puck.State.ExpressionOp.Add, Value: CartridgeExpressions.Of(constant: 1)),
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "written"), Value: CartridgeExpressions.Of(state: "text", key: "cursor")),
            ])],
        });

        Assert.Empty(collection: CartridgeDocuments.Validate(document: document));

        var compiled = new Puck.HumbleGamingDeck.Forge.DeckCartridgeCompiler().Compile(document: document);

        using var instance = Puck.HumbleGamingDeck.HgdMachineFactory.Create(configuration: new Puck.HumbleGamingDeck.HgdMachineConfiguration(cartridge: Puck.HumbleGamingDeck.HgdCartridge.Load(image: compiled.Rom)));
        var machine = instance.Machine;

        while (machine.FrameIndex < 1) {
            machine.RunCycles(masterTicks: 4096UL);
        }

        var frame = machine.FrameIndex;

        // The rule adds one to the far element every frame, and reads it back after the add.
        while (machine.FrameIndex < (frame + 4)) {
            machine.RunCycles(masterTicks: 4096UL);
        }

        var far = machine.Bus.Peek(address: ((ushort)compiled.Variables["far"]));
        var written = machine.Bus.Peek(address: ((ushort)compiled.Variables["written"]));

        Assert.Equal(expected: NearValue, actual: machine.Bus.Peek(address: ((ushort)compiled.Variables["near"])));
        Assert.True(condition: (far > FarValue), userMessage: $"far {far}");
        Assert.Equal(expected: (far + 1), actual: written);
    }

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void APanelWriteAndAFarElementLandWhereTheyAreAddressed(string target) {
        var document = Document(target: target);

        Assert.Empty(collection: CartridgeDocuments.Validate(document: document));
        if (target == "agb") {
            var compiled = new AgbCartridgeCompiler().Compile(document: document);
            using var driver = new AgbVerifyMachineDriver(rom: compiled.Rom, label: "panel-text");

            driver.RunFrames(keys: AgbKeys.None, frames: 10);
            Assert.Equal(expected: FarValue, actual: driver.ReadByte(address: compiled.Variables["far"]));
            Assert.Equal(expected: NearValue, actual: driver.ReadByte(address: compiled.Variables["near"]));
            // The panel's map is screenblock 30 and the background's screenblock 31, two bytes a cell.
            Assert.Equal(expected: FarValue, actual: driver.ReadHalf(address: (0x0600F000u + (((1 * 32) + 2) * 2))));
            Assert.Equal(expected: NearValue, actual: driver.ReadHalf(address: (0x0600F800u + (((3 * 32) + 4) * 2))));
            Assert.Equal(expected: 0, actual: driver.ReadHalf(address: (0x0600F800u + (((1 * 32) + 2) * 2))));

            return;
        }

        var image = new HgbCartridgeCompiler().Compile(document: document);
        using var humble = new VerifyMachineDriver(rom: image.Rom, label: "panel-text", model: ConsoleModel.CgbE);

        humble.RunFrames(buttons: JoypadButtons.None, frames: 10);
        Assert.Equal(expected: FarValue, actual: humble.Read(address: ((ushort)image.Variables["far"])));
        Assert.Equal(expected: NearValue, actual: humble.Read(address: ((ushort)image.Variables["near"])));
        // The window map starts at 0x9C00 and the background map at 0x9800, one byte a cell.
        Assert.Equal(expected: FarValue, actual: humble.Read(address: (0x9C00 + (1 * 32) + 2)));
        Assert.Equal(expected: NearValue, actual: humble.Read(address: (0x9800 + (3 * 32) + 4)));
        Assert.Equal(expected: 0, actual: humble.Read(address: (0x9800 + (1 * 32) + 2)));
    }
}
