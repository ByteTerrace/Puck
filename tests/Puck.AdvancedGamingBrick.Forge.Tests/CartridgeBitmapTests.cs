using Puck.GamingBricks.Forge;


namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers the per-pixel drawing surface, where a picture is plotted point by point rather than built of tiles.</summary>
public sealed class CartridgeBitmapTests {
    private static CartridgeDocument Document() {
        // The surface reads the background palette bank as one flat run, so a colour is the declared palette's entry.
        var palette = new int[16];

        palette[1] = 0x7C00;
        palette[2] = 0x001F;
        return CartridgeDocuments.Create(
            target: "agb",
            title: "BITMAP"
        ) with {
            Palettes = new CartridgePalettes(
            Background: [palette],
            Object: [new int[16]]
        ),
            Tiles = [new CartridgeTile(
                Name: "blank",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "00000000"
                    )]
            )],
            Bitmap = new CartridgeBitmap(Clear: CartridgeExpressions.Of(constant: 0)),
        };
    }
    private static CartridgeStatement Plot(int x, int y, int colour) => new(
        Kind: "plot",
        Row: CartridgeExpressions.Of(constant: y),
        Column: CartridgeExpressions.Of(constant: x),
        Colour: CartridgeExpressions.Of(constant: colour)
    );
    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);

        Assert.Contains(
            collection: errors,
            filter: error => error.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: fragment
            )
        );
    }
    private static AgbVerifyMachineDriver Run((int X, int Y, int Colour)[] plots) {
        var document = Document() with {
            Rules = [new CartridgeRule(
                Name: "draw",
                Body: [.. plots.Select(selector: static plot => Plot(
                        colour: plot.Colour,
                        x: plot.X,
                        y: plot.Y
                    ))]
            )],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);
        var machine = new AgbVerifyMachineDriver(
            rom: result.Rom,
            label: "bitmap"
        );

        machine.RunFrames(
            frames: 6,
            keys: AgbKeys.None
        );

        return machine;
    }

    [Fact]
    public void APlotOffTheSurfaceIsDroppedRatherThanWrappedOntoAnotherRow() {
        using var machine = Run(plots: [(CartridgeBitmap.Width, 10, 1), (5, CartridgeBitmap.Height, 2)]);

        // Wrapping would put the first plot at the start of the row below it.
        Assert.Equal(
            expected: 0xFF000000u,
            actual: machine.ReadPixel(
                x: 0,
                y: 11
            )
        );
        Assert.Equal(
            expected: 0xFF000000u,
            actual: machine.ReadPixel(
                x: 5,
                y: 0
            )
        );
    }
    [Fact]
    public void APlottedPixelShowsInItsOwnColourAndItsNeighboursDoNot() {
        using var machine = Run(plots: [(40, 60, 1), (41, 60, 2)]);

        // Both halves of one halfword of video memory, so a plot that clobbered its neighbour would show here.
        Assert.Equal(
            expected: 0xFFFF0000u,
            actual: machine.ReadPixel(
                x: 40,
                y: 60
            )
        );
        Assert.Equal(
            expected: 0xFF0000FFu,
            actual: machine.ReadPixel(
                x: 41,
                y: 60
            )
        );
        Assert.Equal(
            expected: 0xFF000000u,
            actual: machine.ReadPixel(
                x: 42,
                y: 60
            )
        );
    }
    [Fact]
    public void AnOddColumnIsWrittenWithoutDisturbingTheEvenOneBesideIt() {
        // Video memory takes no single-byte write, so each plot rebuilds a halfword; plotting only the odd column
        // must leave the even one at the clear colour.
        using var machine = Run(plots: [(101, 20, 1)]);

        Assert.Equal(
            expected: 0xFFFF0000u,
            actual: machine.ReadPixel(
                x: 101,
                y: 20
            )
        );
        Assert.Equal(
            expected: 0xFF000000u,
            actual: machine.ReadPixel(
                x: 100,
                y: 20
            )
        );
    }
    [Fact]
    public void TheSurfaceClearsEachFrameSoAPlotDoesNotAccumulate() {
        // The clear runs before the rules, so a pixel plotted only on an early frame is gone by a later one.
        var document = Document() with {
            Variables = [new CartridgeVariable(
                Name: "phase",
                Initial: 0
            )],
            Rules = [
                new CartridgeRule(
                Name: "once",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "phase"),
                    comparison: ActionStateComparison.Less,
                    right: CartridgeExpressions.Of(constant: 3)
                ),
                Body: [Plot(
                        colour: 1,
                        x: 70,
                        y: 70
                    )]
            ),
                new CartridgeRule(
                Name: "tick",
                Body: [
                    new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "phase"),
                        Operation: ExpressionOp.Add,
                        Value: CartridgeExpressions.Of(constant: 1)
                    ),
                ]
            ),
            ],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);
        using var machine = new AgbVerifyMachineDriver(
            rom: result.Rom,
            label: "bitmap-clear"
        );

        machine.RunFrames(
            frames: 2,
            keys: AgbKeys.None
        );
        Assert.Equal(
            expected: 0xFFFF0000u,
            actual: machine.ReadPixel(
                x: 70,
                y: 70
            )
        );

        machine.RunFrames(
            frames: 10,
            keys: AgbKeys.None
        );
        Assert.Equal(
            expected: 0xFF000000u,
            actual: machine.ReadPixel(
                x: 70,
                y: 70
            )
        );
    }
    [Fact]
    public void ValidationGatesTheSurfaceOnTargetAndOnWhatItReplaces() {
        var tiles = new CartridgeTile[] {
            new(
            Name: "blank",
            Pixels: [.. Enumerable.Repeat(
                    count: 8,
                    element: "00000000"
                )]
        ),
        };

        Refuses(
            document: CartridgeDocuments.Create(
                target: "cgb",
                title: "BITBAD"
            ) with { Bitmap = new CartridgeBitmap() },
            fragment: "cgb target draws only from tiles"
        );
        Refuses(
            document: CartridgeDocuments.Create(
                target: "agb",
                title: "BITBAD"
            ) with {
                Tiles = tiles,
                Bitmap = new CartridgeBitmap(),
                Window = new CartridgeWindow(
                Map: new int[1024],
                MapPalettes: null,
                X: CartridgeExpressions.Of(constant: 0),
                Y: CartridgeExpressions.Of(constant: 0),
                Visible: CartridgeExpressions.Of(constant: 1)
            ),
            },
            fragment: "replaces the tile background"
        );
        Refuses(
            document: CartridgeDocuments.Create(
                target: "agb",
                title: "BITBAD"
            ) with {
                Rules = [new CartridgeRule(
                    Name: "r",
                    Body: [Plot(
                            colour: 1,
                            x: 0,
                            y: 0
                        )]
                )],
            },
            fragment: "requires a declared bitmap"
        );
    }
}
