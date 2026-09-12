using Puck.GamingBricks.Forge;

using Puck.State;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers the per-pixel drawing surface, where a picture is plotted point by point rather than built of tiles.</summary>
public sealed class CartridgeBitmapTests {
    [Fact]
    public void APlottedPixelShowsInItsOwnColourAndItsNeighboursDoNot() {
        using var machine = Run(plots: [(40, 60, 1), (41, 60, 2)]);

        // Both halves of one halfword of video memory, so a plot that clobbered its neighbour would show here.
        Assert.Equal(expected: 0xFFFF0000u, actual: machine.ReadPixel(x: 40, y: 60));
        Assert.Equal(expected: 0xFF0000FFu, actual: machine.ReadPixel(x: 41, y: 60));
        Assert.Equal(expected: 0xFF000000u, actual: machine.ReadPixel(x: 42, y: 60));
    }

    [Fact]
    public void AnOddColumnIsWrittenWithoutDisturbingTheEvenOneBesideIt() {
        // Video memory takes no single-byte write, so each plot rebuilds a halfword; plotting only the odd column
        // must leave the even one at the clear colour.
        using var machine = Run(plots: [(101, 20, 1)]);

        Assert.Equal(expected: 0xFFFF0000u, actual: machine.ReadPixel(x: 101, y: 20));
        Assert.Equal(expected: 0xFF000000u, actual: machine.ReadPixel(x: 100, y: 20));
    }

    [Fact]
    public void APlotOffTheSurfaceIsDroppedRatherThanWrappedOntoAnotherRow() {
        using var machine = Run(plots: [(CartridgeBitmap.Width, 10, 1), (5, CartridgeBitmap.Height, 2)]);

        // Wrapping would put the first plot at the start of the row below it.
        Assert.Equal(expected: 0xFF000000u, actual: machine.ReadPixel(x: 0, y: 11));
        Assert.Equal(expected: 0xFF000000u, actual: machine.ReadPixel(x: 5, y: 0));
    }

    [Fact]
    public void TheSurfaceClearsEachFrameSoAPlotDoesNotAccumulate() {
        // The clear runs before the rules, so a pixel plotted only on an early frame is gone by a later one.
        var document = Document() with {
            Variables = [new CartridgeVariable(Name: "phase", Initial: 0)],
            Rules = [
                new CartridgeRule(
                    Name: "once",
                    When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "phase"), Comparison: ActionStateComparison.Less, Right: new CartridgeValue(Constant: 3))],
                    Body: [Plot(x: 70, y: 70, colour: 1)]),
                new CartridgeRule(Name: "tick", When: [], Body: [
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "phase"), Operation: ExpressionOp.Add, Value: new CartridgeValue(Constant: 1)),
                ]),
            ],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);
        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "bitmap-clear");

        machine.RunFrames(keys: AgbKeys.None, frames: 2);
        Assert.Equal(expected: 0xFFFF0000u, actual: machine.ReadPixel(x: 70, y: 70));

        machine.RunFrames(keys: AgbKeys.None, frames: 10);
        Assert.Equal(expected: 0xFF000000u, actual: machine.ReadPixel(x: 70, y: 70));
    }

    [Fact]
    public void ValidationGatesTheSurfaceOnTargetAndOnWhatItReplaces() {
        var tiles = new CartridgeTile[] {
            new(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
        };
        Refuses(
            document: CartridgeDocuments.Create(target: "cgb", title: "BITBAD") with { Bitmap = new CartridgeBitmap() },
            fragment: "cgb target draws only from tiles");
        Refuses(
            document: CartridgeDocuments.Create(target: "agb", title: "BITBAD") with {
                Tiles = tiles,
                Bitmap = new CartridgeBitmap(),
                Window = new CartridgeWindow(
                    Map: new int[1024], MapPalettes: null, X: new CartridgeValue(Constant: 0),
                    Y: new CartridgeValue(Constant: 0), Visible: new CartridgeValue(Constant: 1)),
            },
            fragment: "replaces the tile background");
        Refuses(
            document: CartridgeDocuments.Create(target: "agb", title: "BITBAD") with {
                Rules = [new CartridgeRule(Name: "r", When: [], Body: [Plot(x: 0, y: 0, colour: 1)])],
            },
            fragment: "requires a declared bitmap");
    }

    private static CartridgeStatement Plot(int x, int y, int colour) => new(
        Kind: "plot",
        Row: new CartridgeValue(Constant: y),
        Column: new CartridgeValue(Constant: x),
        Colour: new CartridgeValue(Constant: colour));

    private static AgbVerifyMachineDriver Run((int X, int Y, int Colour)[] plots) {
        var document = Document() with {
            Rules = [new CartridgeRule(Name: "draw", When: [], Body: [.. plots.Select(selector: static plot => Plot(x: plot.X, y: plot.Y, colour: plot.Colour))])],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);
        var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "bitmap");
        machine.RunFrames(keys: AgbKeys.None, frames: 6);

        return machine;
    }

    private static CartridgeDocument Document() {
        // The surface reads the background palette bank as one flat run, so a colour is the declared palette's entry.
        var palette = new int[16];
        palette[1] = 0x7C00;
        palette[2] = 0x001F;
        return CartridgeDocuments.Create(target: "agb", title: "BITMAP") with {
            Palettes = new CartridgePalettes(Background: [palette], Object: [new int[16]]),
            Tiles = [new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)])],
            Bitmap = new CartridgeBitmap(Clear: new CartridgeValue(Constant: 0)),
        };
    }

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }
}
