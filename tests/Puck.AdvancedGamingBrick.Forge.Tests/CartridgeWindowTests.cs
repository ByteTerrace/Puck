using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers the panel drawn over the background: it appears where placed and hides on request.</summary>
public sealed class CartridgeWindowTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void ThePanelCoversTheBackgroundBelowAndRightOfItsCorner(string target) {
        var background = new int[1024];
        var panel = new int[1024];
        for (var index = 0; index < 1024; ++index) {
            background[index] = 1;
            panel[index] = 2;
        }

        var document = CartridgeDocuments.Create(target: target, title: "PANEL") with {
            Palettes = new CartridgePalettes(
                Background: [Shade(target: target, ink: 0x001F)],
                Object: [Shade(target: target, ink: 0x001F)]),
            Tiles = [
                new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
                new CartridgeTile(Name: "one", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]),
                new CartridgeTile(Name: "two", Pixels: [.. Enumerable.Repeat(element: "22222222", count: 8)]),
            ],
            Map = background,
            Variables = [new CartridgeVariable(Name: "shown", Initial: 1)],
            Window = new CartridgeWindow(
                Map: panel,
                MapPalettes: null,
                X: new CartridgeValue(Constant: 80),
                Y: new CartridgeValue(Constant: 72),
                Visible: new CartridgeValue(Variable: "shown")),
            Rules = [new CartridgeRule(Name: "hold", When: [], Body: [
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "shown"), Operation: null, Value: new CartridgeValue(Variable: "shown")),
            ])],
        };
        using var machine = Run(document: document, frames: 20);

        // Above and left of the corner is background; below and right is the panel's own tile.
        var outside = machine.Pixel(x: 20, y: 20);
        var inside = machine.Pixel(x: 120, y: 100);
        Assert.NotEqual(expected: outside, actual: inside);
    }

    [Fact]
    public void ValidationChecksThePanelsMapAndPlacement() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "PANELBAD");
        var full = new int[1024];
        Refuses(
            document: document with { Window = new CartridgeWindow(Map: new int[16], MapPalettes: null, X: new CartridgeValue(Constant: 0), Y: new CartridgeValue(Constant: 0), Visible: new CartridgeValue(Constant: 1)) },
            fragment: "Expected an array with 1024..1024");
        Refuses(
            document: document with { Window = new CartridgeWindow(Map: [.. Enumerable.Repeat(element: 9, count: 1024)], MapPalettes: null, X: new CartridgeValue(Constant: 0), Y: new CartridgeValue(Constant: 0), Visible: new CartridgeValue(Constant: 1)) },
            fragment: "outside the authored tile bank");
        Refuses(
            document: document with { Window = new CartridgeWindow(Map: full, MapPalettes: new int[8], X: new CartridgeValue(Constant: 0), Y: new CartridgeValue(Constant: 0), Visible: new CartridgeValue(Constant: 1)) },
            fragment: "1024 entries");
    }

    private static int[] Shade(string target, int ink) {
        var entries = new int[target == "agb" ? 16 : 4];
        entries[1] = ink;
        entries[2] = 0x7C00;

        return entries;
    }

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }

    private static WindowProbe Run(CartridgeDocument document, int frames) {
        ICartridgeCompiler compiler = document.Target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
        var machine = new WindowProbe(result: compiler.Compile(document: document));
        machine.Run(frames: frames);
        return machine;
    }

    private sealed class WindowProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;
        public WindowProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "panel"); }
            else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "panel"); }
        }
        public void Run(int frames) {
            m_agb?.RunFrames(keys: AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: JoypadButtons.None, frames: frames);
        }
        public uint Pixel(int x, int y) => m_agb?.ReadPixel(x: x, y: y) ?? m_hgb!.ReadPixel(x: x, y: y);
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }
}
