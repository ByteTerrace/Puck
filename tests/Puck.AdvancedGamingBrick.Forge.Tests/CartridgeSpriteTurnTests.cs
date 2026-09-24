using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers turning an object, which only the advanced machine can do.</summary>
public sealed class CartridgeSpriteTurnTests {
    // A wedge tile, so a turn moves ink rather than looking identical.
    private static string Render(int turn) {
        string[] wedge = ["11111111", "11111110", "11111100", "11111000", "11110000", "11100000", "11000000", "10000000"];
        var document = CartridgeDocuments.Create(
            target: "agb",
            title: "TURN"
        ) with {
            Tiles = [
                new CartridgeTile(
                Name: "blank",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "00000000"
                    )]
            ),
                new CartridgeTile(
                Name: "wedge",
                Pixels: wedge
            ),
            ],
            Sprites = [new CartridgeSprite(
                Name: "spinner",
                Tile: CartridgeExpressions.Of(constant: 1),
                X: CartridgeExpressions.Of(constant: 60),
                Y: CartridgeExpressions.Of(constant: 60),
                Visible: CartridgeExpressions.Of(constant: 1),
                Turn: CartridgeExpressions.Of(constant: turn)
            )],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);
        using var machine = new AgbVerifyMachineDriver(
            rom: result.Rom,
            label: "turn"
        );

        machine.RunFrames(
            frames: 8,
            keys: AgbKeys.None
        );

        var sample = new System.Text.StringBuilder();

        for (var y = 56; (y < 76); ++y) {
            for (var x = 56; (x < 76); ++x) {
                sample.Append(value: machine.ReadPixel(
                    x: x,
                    y: y
                ).ToString(format: "X8"));
            }
        }

        return sample.ToString();
    }

    [Fact]
    public void TurningAnObjectChangesWhatItDraws() {
        var upright = Render(turn: 0);
        var turned = Render(turn: 32);

        Assert.NotEqual(
            actual: turned,
            expected: upright
        );
        Assert.Equal(
            expected: upright,
            actual: Render(turn: 0)
        );
    }
    [Fact]
    public void ValidationRefusesTurningOnTheHumbleTarget() {
        var document = CartridgeDocuments.Create(
            target: "cgb",
            title: "TURNCGB"
        ) with {
            Sprites = [new CartridgeSprite(
                Name: "s",
                Tile: CartridgeExpressions.Of(constant: 0),
                X: CartridgeExpressions.Of(constant: 0),
                Y: CartridgeExpressions.Of(constant: 0),
                Visible: CartridgeExpressions.Of(constant: 1),
                Turn: CartridgeExpressions.Of(constant: 32)
            )],
        };

        new CartridgeRefusal(
            Document: document,
            Fragment: "can only mirror one",
            Name: "a turn on the colour machine",
            Path: "sprites[0].turn"
        ).Holds();
    }
}
