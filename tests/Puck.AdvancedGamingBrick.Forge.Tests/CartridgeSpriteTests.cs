using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers object mirroring, palette selection and the tall sprite shape on both machines.</summary>
public sealed class CartridgeSpriteTests {
    private static CartridgeDocument Blank(string target, string title) => CartridgeDocuments.Create(
        target: target,
        title: title
    );

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void MirroringTurnsTheSpriteOver(string target) {
        // An L shape: solid across the top row and down the left column, so a mirror is visible.
        string[] shape = ["11111111", "10000000", "10000000", "10000000", "10000000", "10000000", "10000000", "10000000"];
        var document = Blank(
            target: target,
            title: "SPRITE"
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
                Name: "corner",
                Pixels: shape
            ),
            ],
            Sprites = [
                new CartridgeSprite(
                Name: "plain",
                Tile: CartridgeExpressions.Of(constant: 1),
                X: CartridgeExpressions.Of(constant: 0),
                Y: CartridgeExpressions.Of(constant: 0),
                Visible: CartridgeExpressions.Of(constant: 1)
            ),
                new CartridgeSprite(
                Name: "mirrored",
                Tile: CartridgeExpressions.Of(constant: 1),
                X: CartridgeExpressions.Of(constant: 32),
                Y: CartridgeExpressions.Of(constant: 0),
                Visible: CartridgeExpressions.Of(constant: 1),
                FlipX: CartridgeExpressions.Of(constant: 1)
            ),
            ],
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 20,
            label: "sprite"
        );

        // The unmirrored sprite is solid at its left edge and clear at its right; the mirrored one is the reverse.
        var ink = machine.Pixel(
            x: 0,
            y: 4
        );

        Assert.NotEqual(
            expected: ink,
            actual: machine.Pixel(
                x: 7,
                y: 4
            )
        );
        Assert.Equal(
            expected: ink,
            actual: machine.Pixel(
                x: 39,
                y: 4
            )
        );
        Assert.NotEqual(
            expected: ink,
            actual: machine.Pixel(
                x: 32,
                y: 4
            )
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void TallSpritesCoverSixteenRows(string target) {
        var document = Blank(
            target: target,
            title: "TALL"
        ) with {
            TallSprites = true,
            Tiles = [
                new CartridgeTile(
                Name: "blank",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "00000000"
                    )]
            ),
                new CartridgeTile(
                Name: "solid",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "11111111"
                    )]
            ),
                new CartridgeTile(
                Name: "upper",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "11111111"
                    )]
            ),
                new CartridgeTile(
                Name: "lower",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "11111111"
                    )]
            ),
            ],
            // In tall mode the index names a PAIR and its low bit is ignored, so tiles 2 and 3 both draw.
            Sprites = [new CartridgeSprite(
                Name: "tall",
                Tile: CartridgeExpressions.Of(constant: 2),
                X: CartridgeExpressions.Of(constant: 0),
                Y: CartridgeExpressions.Of(constant: 0),
                Visible: CartridgeExpressions.Of(constant: 1)
            )],
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 20,
            label: "sprite"
        );
        var top = machine.Pixel(
            x: 4,
            y: 2
        );

        Assert.Equal(
            expected: top,
            actual: machine.Pixel(
                x: 4,
                y: 12
            )
        );
        Assert.NotEqual(
            expected: top,
            actual: machine.Pixel(
                x: 4,
                y: 20
            )
        );
    }
}
