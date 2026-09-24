using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers images larger than one bank: the switchable window pages correctly and the fixed window survives it.</summary>
public sealed class CartridgeBankingTests {
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AnImageBeyondOneBankStillBootsAndRenders(string target) {
        // A full tile bank pushes the video payload past a single switchable window on the humble machine.
        var tiles = new CartridgeTile[256];

        tiles[0] = new CartridgeTile(
            Name: "blank",
            Pixels: [.. Enumerable.Repeat(
                    count: 8,
                    element: "00000000"
                )]
        );
        for (var index = 1; (index < tiles.Length); ++index) {
            tiles[index] = new CartridgeTile(
                Name: $"t{index}",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "11111111"
                    )]
            );
        }

        var cells = new int[1024];

        for (var index = 0; (index < cells.Length); ++index) {
            cells[index] = 1;
        }

        var document = CartridgeDocuments.Create(
            target: target,
            title: "BANKED"
        ) with {
            Tiles = tiles,
            Map = cells,
            Variables = [new CartridgeVariable(
                Name: "beat",
                Initial: 0
            )],
            Arrays = [new CartridgeArray(
                Initial: [3, 1, 4],
                Name: "seed"
            )],
            // Music and array seeds live in the fixed window; a stranded bank would leave both unreadable.
            Sounds = [new CartridgeSound(
                Name: "theme",
                Music: [CartridgeCostMeasurement.Lead]
            )],
            Rules = [new CartridgeRule(
                Name: "run",
                Body: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "beat"),
                        Operation: null,
                        Value: CartridgeExpressions.Of(
                            state: "seed",
                            key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(constant: 2))
                        )
                    ),
                new CartridgeStatement(
                        Kind: "play",
                        Sound: "theme"
                    ),
            ]
            )],
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 20,
            label: "bank"
        );

        // The banked tiles reached video memory, and the fixed window's array seed and music both still work.
        Assert.NotEqual(
            expected: 0u,
            actual: machine.Pixel(
                x: 4,
                y: 4
            )
        );
        Assert.Equal(
            expected: 4,
            actual: machine.Read(variable: "beat")
        );
        Assert.NotEqual(
            expected: 0u,
            actual: machine.SoundStatus() & 2u
        );
    }
    [Fact]
    public void TheHumbleImageGrowsInWholeBanksAndTheHeaderSaysSo() {
        var small = new HgbCartridgeCompiler().Compile(document: CartridgeDocuments.Create(
            target: "cgb",
            title: "SMALL"
        ));

        Assert.Equal(
            expected: 0x8000,
            actual: small.Rom.Length
        );

        // Bank count is a power of two and the header's size code is its logarithm less one.
        Assert.Equal(
            expected: 0x1B,
            actual: small.Rom[0x0147]
        );
        Assert.Equal(
            expected: 0x00,
            actual: small.Rom[0x0148]
        );
    }
}
