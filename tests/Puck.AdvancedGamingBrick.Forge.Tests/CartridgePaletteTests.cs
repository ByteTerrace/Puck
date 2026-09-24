using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers per-cell background palettes and per-sprite object palettes reaching the screen.</summary>
public sealed class CartridgePaletteTests {
    private static readonly CartridgeRefusal[] Refusals = [
        new(
            Name: "no background palette",
            Document: Bad() with { Palettes = new CartridgePalettes(
                Background: [],
                Object: [Palette(
                        colors: 4,
                        ink: 0
                    )]
            ) },
            Path: "palettes.background",
            Fragment: "Expected an array with 1..8"
        ),
        new(
            Name: "a short palette",
            Document: Bad() with { Palettes = new CartridgePalettes(
                Background: [[1, 2]],
                Object: [Palette(
                        colors: 4,
                        ink: 0
                    )]
            ) },
            Path: "palettes.background[0]",
            Fragment: "Expected 4 RGB555 integers"
        ),
        new(
            Name: "a colour past fifteen bits",
            Document: Bad() with { Palettes = new CartridgePalettes(
                Background: [[1, 2, 3, 99999]],
                Object: [Palette(
                        colors: 4,
                        ink: 0
                    )]
            ) },
            Path: "palettes.background[0]",
            Fragment: "RGB555 integers in 0..32767"
        ),
        new(
            Name: "a short cell palette map",
            Document: Bad() with { MapPalettes = new int[16] },
            Path: "mapPalettes",
            Fragment: "1024 entries"
        ),
        new(
            Name: "a cell on an undeclared palette",
            Document: Bad() with { MapPalettes = [.. Enumerable.Repeat(
                    count: 1024,
                    element: 3
                )] },
            Path: "mapPalettes",
            Fragment: "background palette that is not declared"
        ),
        new(
            Name: "a sprite on an undeclared palette",
            Document: Bad() with {
                Sprites = [new CartridgeSprite(
                    Name: "s",
                    Tile: CartridgeExpressions.Of(constant: 0),
                    X: CartridgeExpressions.Of(constant: 0),
                    Y: CartridgeExpressions.Of(constant: 0),
                    Visible: CartridgeExpressions.Of(constant: 1),
                    Palette: CartridgeExpressions.Of(constant: 5)
                )],
            },
            Path: "sprites[0].palette",
            Fragment: "object palette that is not declared"
        ),
    ];

    public static TheoryData<string> RefusalNames => CartridgeRefusal.Names(table: Refusals);

    private static CartridgeDocument Bad() => CartridgeDocuments.Create(
        target: "cgb",
        title: "PALBAD"
    );
    private static int[] Palette(int colors, int ink) {
        var entries = new int[colors];

        for (var index = 1; (index < colors); ++index) {
            entries[index] = ink;
        }

        return entries;
    }

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void CellsOnDifferentPalettesRenderDifferentColors(string target) {
        var colors = ((target == "agb")
            ? 16
            : 4
        );
        var red = Palette(
            colors: colors,
            ink: 0x001F
        );
        var blue = Palette(
            colors: colors,
            ink: 0x7C00
        );

        // Every cell holds the same solid tile; only the palette each cell selects differs.
        var cells = new int[1024];
        var palettes = new int[1024];

        for (var index = 0; (index < 1024); ++index) {
            cells[index] = 1;
            palettes[index] = (((index % 32) < 8)
                ? 0
                : 1
            );
        }

        var document = CartridgeDocuments.Create(
            target: target,
            title: "PALETTE"
        ) with {
            Palettes = new CartridgePalettes(
            Background: [red, blue],
            Object: [red, blue]
        ),
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
            ],
            Map = cells,
            MapPalettes = palettes,
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 20,
            label: "palette"
        );

        // Columns 0..7 sit on palette zero and 8.. on palette one, so compare across that boundary.
        var onFirst = machine.Pixel(
            x: 4,
            y: 4
        );
        var onSecond = machine.Pixel(
            x: 100,
            y: 4
        );

        Assert.Equal(
            expected: onFirst,
            actual: machine.Pixel(
                x: 20,
                y: 4
            )
        );
        Assert.NotEqual(
            actual: onSecond,
            expected: onFirst
        );
        Assert.Equal(
            expected: onSecond,
            actual: machine.Pixel(
                x: 76,
                y: 12
            )
        );
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void ValidationChecksPaletteShapeAndReferences(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: Refusals
    );
}
