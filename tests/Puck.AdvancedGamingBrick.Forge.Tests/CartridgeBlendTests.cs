using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers mixing one surface with what is drawn beneath it, which is how a translucent panel is made.</summary>
public sealed class CartridgeBlendTests {
    [Fact]
    public void TheWeightMovesThePanelBetweenItsOwnColourAndTheBackgroundBeneath() {
        var opaque = Panel(weight: 16);
        var mixed = Panel(weight: 8);
        var clear = Panel(weight: 0);

        // Full weight leaves the panel exactly as an unblended one; no weight leaves only what is beneath it.
        Assert.Equal(expected: Unblended(), actual: opaque);
        Assert.Equal(expected: Beside(), actual: clear);

        // Half weight is neither, and every channel lies between the two ends.
        Assert.NotEqual(expected: mixed, actual: opaque);
        Assert.NotEqual(expected: mixed, actual: clear);
        for (var shift = 0; shift < 24; shift += 8) {
            Assert.InRange(
                actual: (int)((mixed >> shift) & 0xFF),
                low: (int)Math.Min(val1: (opaque >> shift) & 0xFF, val2: (clear >> shift) & 0xFF),
                high: (int)Math.Max(val1: (opaque >> shift) & 0xFF, val2: (clear >> shift) & 0xFF));
        }
    }

    [Fact]
    public void AWeightPastFullIsHeldAtFull() {
        // Validation refuses an authored constant past full, so the clamp exists for the values state supplies.
        var document = Document() with {
            Variables = [new CartridgeVariable(Name: "w", Initial: 200)],
            Rules = [new CartridgeRule(Name: "mix", When: [], Body: [
                new CartridgeStatement(Kind: "blend", Surface: "panel", Weight: new CartridgeValue(Variable: "w")),
            ])],
        };

        Assert.Equal(expected: Panel(weight: 16), actual: Sample(document: document, x: 160, y: 120));
    }

    [Fact]
    public void ValidationNamesTheSurfaceAndRefusesTheColourMachine() {
        var document = CartridgeDocuments.Create(target: "agb", title: "BLENDBAD");
        Refuses(document: document with { Rules = [Rule(surface: "nowhere", weight: 8)] }, fragment: "background, panel, affine, sprites or backdrop");
        Refuses(document: document with { Rules = [Rule(surface: "panel", weight: 8)] }, fragment: "needs a declared window");
        Refuses(document: document with { Rules = [Rule(surface: "affine", weight: 8)] }, fragment: "needs a declared affine background");
        Refuses(document: document with { Rules = [Rule(surface: "backdrop", weight: 40)] }, fragment: "weight runs 0 through 16");
        Refuses(
            document: CartridgeDocuments.Create(target: "cgb", title: "BLENDBAD") with { Rules = [Rule(surface: "backdrop", weight: 8)] },
            fragment: "cgb target has no blend unit");
    }

    private static CartridgeRule Rule(string surface, int weight) => new(
        Name: "mix", When: [],
        Body: [new CartridgeStatement(Kind: "blend", Surface: surface, Weight: new CartridgeValue(Constant: weight))]);

    // Inside the panel's corner, where a blend mixes it with the background.
    private static uint Panel(int weight) => Sample(document: Document() with { Rules = [Rule(surface: "panel", weight: weight)] }, x: 160, y: 120);

    private static uint Unblended() => Sample(document: Document(), x: 160, y: 120);

    // Outside the panel, where only the background draws.
    private static uint Beside() => Sample(document: Document(), x: 20, y: 20);

    private static uint Sample(CartridgeDocument document, int x, int y) {
        var result = new AgbCartridgeCompiler().Compile(document: document);
        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "blend");
        machine.RunFrames(keys: AgbKeys.None, frames: 8);

        return machine.ReadPixel(x: x, y: y);
    }

    // Two flatly coloured surfaces, so a mix of them is distinguishable from either on its own.
    private static CartridgeDocument Document() {
        var background = new int[1024];
        var panel = new int[1024];
        for (var index = 0; index < 1024; ++index) {
            background[index] = 1;
            panel[index] = 2;
        }

        var palette = new int[16];
        palette[1] = 0x7C00;
        palette[2] = 0x001F;
        return CartridgeDocuments.Create(target: "agb", title: "BLEND") with {
            Palettes = new CartridgePalettes(Background: [palette], Object: [palette]),
            Tiles = [
                new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
                new CartridgeTile(Name: "one", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]),
                new CartridgeTile(Name: "two", Pixels: [.. Enumerable.Repeat(element: "22222222", count: 8)]),
            ],
            Map = background,
            Window = new CartridgeWindow(
                Map: panel,
                MapPalettes: null,
                X: new CartridgeValue(Constant: 80),
                Y: new CartridgeValue(Constant: 72),
                Visible: new CartridgeValue(Constant: 1)),
        };
    }

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }
}
