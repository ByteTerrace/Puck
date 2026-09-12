using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers the scrolling backgrounds beside the document's own: each scrolls, hides and stacks on its own.</summary>
public sealed class CartridgeLayerTests {
    [Fact]
    public void ALayerScrollsIndependentlyOfTheDocumentsOwnBackground() {
        // The document's own background is empty, so the column seen is the layer's and its scroll alone moves it.
        Assert.Equal(expected: 32, actual: ColumnOfRun(layerScrollX: 0));
        Assert.Equal(expected: 16, actual: ColumnOfRun(layerScrollX: 16));
    }

    [Fact]
    public void AHiddenLayerLeavesOnlyWhatIsBehindIt() {
        // Nothing is behind it but the backdrop, so hiding it takes the layer's colour off the screen entirely.
        Assert.Equal(expected: -1, actual: ColumnOfRun(layerScrollX: 0, visible: 0));
    }

    [Fact]
    public void PriorityDecidesWhichOfTwoLayersCovers() {
        // Two opaque layers over each other, one on each palette entry: the nearer priority is the one on screen.
        Assert.Equal(expected: 0xFFFF0000u, actual: Stacked(nearPriority: 1, farPriority: 2));
        Assert.Equal(expected: 0xFF0000FFu, actual: Stacked(nearPriority: 2, farPriority: 1));
    }

    [Fact]
    public void ValidationBoundsTheCountShapeAndDepth() {
        var document = CartridgeDocuments.Create(target: "agb", title: "LAYERBAD") with {
            Tiles = [
                new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
                new CartridgeTile(Name: "solid", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]),
            ],
        };
        Refuses(document: document with { Layers = [Layer(map: new int[16])] }, fragment: "Expected an array with 1024..1024");
        Refuses(document: document with { Layers = [Layer(map: [.. Enumerable.Repeat(element: 9, count: 1024)])] }, fragment: "outside the authored tile bank");
        Refuses(document: document with { Layers = [Layer(priority: 4)] }, fragment: "priority in 0..3");
        Refuses(document: document with { Layers = [Layer(), Layer(), Layer()] }, fragment: "Expected an array with 0..2");
        Refuses(
            document: CartridgeDocuments.Create(target: "cgb", title: "LAYERBAD") with { Layers = [Layer()] },
            fragment: "need the advanced machine");
        Refuses(
            document: document with {
                Layers = [Layer(), Layer()],
                Affine = new CartridgeAffine(
                    Map: new int[256], Angle: CartridgeExpressions.Of(constant: 0), Scale: CartridgeExpressions.Of(constant: 16),
                    CentreX: CartridgeExpressions.Of(constant: 120), CentreY: CartridgeExpressions.Of(constant: 80),
                    Visible: CartridgeExpressions.Of(constant: 1)),
            },
            fragment: "leaving room for 1");
    }

    private static CartridgeLayer Layer(int[]? map = null, int priority = 2, int scrollX = 0, int visible = 1) => new(
        Map: map ?? new int[1024],
        MapPalettes: null,
        ScrollX: CartridgeExpressions.Of(constant: scrollX),
        ScrollY: CartridgeExpressions.Of(constant: 0),
        Priority: priority,
        Visible: CartridgeExpressions.Of(constant: visible));

    // Where the layer's solid column lands on screen, or -1 when it is not drawn at all.
    private static int ColumnOfRun(int layerScrollX, int visible = 1) {
        var column = new int[1024];
        for (var row = 0; row < 32; ++row) {
            column[(row * 32) + 4] = 1;
        }

        var document = Document() with { Layers = [Layer(map: column, scrollX: layerScrollX, visible: visible)] };
        var result = new AgbCartridgeCompiler().Compile(document: document);
        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "layer-scroll");
        machine.RunFrames(keys: AgbKeys.None, frames: 8);

        var backdrop = machine.ReadPixel(x: 200, y: 80);
        for (var x = 0; x < 240; ++x) {
            if (machine.ReadPixel(x: x, y: 80) != backdrop) {
                return x;
            }
        }

        return -1;
    }

    private static uint Stacked(int nearPriority, int farPriority) {
        var ones = new int[1024];
        var twos = new int[1024];
        for (var index = 0; index < 1024; ++index) {
            ones[index] = 1;
            twos[index] = 2;
        }

        return Sample(document: Document() with {
            Layers = [Layer(map: ones, priority: nearPriority), Layer(map: twos, priority: farPriority)],
        });
    }

    private static uint Sample(CartridgeDocument document) {
        var result = new AgbCartridgeCompiler().Compile(document: document);
        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "layer");
        machine.RunFrames(keys: AgbKeys.None, frames: 8);

        return machine.ReadPixel(x: 120, y: 80);
    }

    // The document's own background is empty, so what is sampled is whichever layer wins beneath it.
    private static CartridgeDocument Document() {
        var palette = new int[16];
        palette[1] = 0x7C00;
        palette[2] = 0x001F;
        return CartridgeDocuments.Create(target: "agb", title: "LAYER") with {
            Palettes = new CartridgePalettes(Background: [palette], Object: [palette]),
            Tiles = [
                new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
                new CartridgeTile(Name: "one", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]),
                new CartridgeTile(Name: "two", Pixels: [.. Enumerable.Repeat(element: "22222222", count: 8)]),
            ],
            Map = new int[1024],
        };
    }

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }
}
