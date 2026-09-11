using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers turning an object, which only the advanced machine can do.</summary>
public sealed class CartridgeSpriteTurnTests {
    [Fact]
    public void TurningAnObjectChangesWhatItDraws() {
        var upright = Render(turn: 0);
        var turned = Render(turn: 32);

        Assert.NotEqual(expected: upright, actual: turned);
        Assert.Equal(expected: upright, actual: Render(turn: 0));
    }

    [Fact]
    public void ValidationRefusesTurningOnTheHumbleTarget() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "TURNCGB") with {
            Sprites = [new CartridgeSprite(
                Name: "s",
                Tile: new CartridgeValue(Constant: 0),
                X: new CartridgeValue(Constant: 0),
                Y: new CartridgeValue(Constant: 0),
                Visible: new CartridgeValue(Constant: 1),
                Turn: new CartridgeValue(Constant: 32))],
        };
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: "can only mirror one", comparisonType: StringComparison.Ordinal));
    }

    // A wedge tile, so a turn moves ink rather than looking identical.
    private static string Render(int turn) {
        string[] wedge = ["11111111", "11111110", "11111100", "11111000", "11110000", "11100000", "11000000", "10000000"];
        var document = CartridgeDocuments.Create(target: "agb", title: "TURN") with {
            Tiles = [
                new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
                new CartridgeTile(Name: "wedge", Pixels: wedge),
            ],
            Sprites = [new CartridgeSprite(
                Name: "spinner",
                Tile: new CartridgeValue(Constant: 1),
                X: new CartridgeValue(Constant: 60),
                Y: new CartridgeValue(Constant: 60),
                Visible: new CartridgeValue(Constant: 1),
                Turn: new CartridgeValue(Constant: turn))],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);
        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "turn");
        machine.RunFrames(keys: AgbKeys.None, frames: 8);

        var sample = new System.Text.StringBuilder();
        for (var y = 56; y < 76; ++y) {
            for (var x = 56; x < 76; ++x) {
                sample.Append(value: machine.ReadPixel(x: x, y: y).ToString(format: "X8"));
            }
        }

        return sample.ToString();
    }
}
