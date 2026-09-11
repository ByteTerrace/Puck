using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers runtime background writes reaching the display, and the bound that keeps the queue from dropping one.</summary>
public sealed class CartridgeMapWriteTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void MapWritesReachTheBackgroundOnBothMachines(string target) {
        var document = Blank(target: target, title: "MAPWRITE") with {
            Variables = [
                new CartridgeVariable(Name: "col", Initial: 0),
                new CartridgeVariable(Name: "done", Initial: 0),
            ],
            Tiles = [
                new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
                new CartridgeTile(Name: "solid", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]),
            ],
            Rules = [new CartridgeRule(
                Name: "paint",
                When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "done"), Comparison: "eq", Right: new CartridgeValue(Constant: 0))],
                Body: [
                    // A run of solid cells along the top row, written through the queue at run time.
                    new CartridgeStatement(Kind: "repeat", Count: 4, Index: "col", Body: [
                        new CartridgeStatement(
                            Kind: "map",
                            Row: new CartridgeValue(Constant: 0),
                            Column: new CartridgeValue(Variable: "col"),
                            Tile: new CartridgeValue(Constant: 1)),
                    ]),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "done"), Operation: "set", Value: new CartridgeValue(Constant: 1)),
                ])],
        };
        using var machine = Run(document: document, frames: 16, out _);

        // The painted run differs from an untouched cell well to its right.
        var painted = machine.Pixel(x: 4, y: 4);
        var untouched = machine.Pixel(x: 100, y: 4);
        Assert.NotEqual(expected: untouched, actual: painted);
        Assert.Equal(expected: painted, actual: machine.Pixel(x: 28, y: 4));
        Assert.Equal(expected: untouched, actual: machine.Pixel(x: 36, y: 4));
    }

    [Fact]
    public void ValidationBoundsMapWritesToTheQueueAndTheBlitWindow() {
        var document = Blank(target: "cgb", title: "BOUNDS") with {
            Variables = [new CartridgeVariable(Name: "i", Initial: 0)],
            Screens = [new CartridgeScreen(Name: "panel", Width: 4, Tiles: new int[8])],
        };
        var write = new CartridgeStatement(Kind: "map", Row: new CartridgeValue(Constant: 0), Column: new CartridgeValue(Variable: "i"), Tile: new CartridgeValue(Constant: 0));

        // A loop past the queue's capacity is refused rather than silently dropping the overflow.
        Refuses(
            document: document with { Rules = [new CartridgeRule(Name: "r", When: [], Body: [new CartridgeStatement(Kind: "repeat", Count: 25, Index: "i", Body: [write])])] },
            fragment: "against a queue of");

        // Branch arms are exclusive, so gated redraws are counted once, not summed.
        Assert.Empty(collection: CartridgeDocuments.Validate(document: document with {
            Rules = [new CartridgeRule(Name: "r", When: [], Body: [new CartridgeStatement(
                Kind: "if",
                When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "i"), Comparison: "eq", Right: new CartridgeValue(Constant: 0))],
                Then: [new CartridgeStatement(Kind: "repeat", Count: 20, Index: "i", Body: [write])],
                Else: [new CartridgeStatement(Kind: "repeat", Count: 20, Index: "i", Body: [write])])])],
        }));

        // A blit past the measured display-off window is refused; one inside it is admitted.
        Refuses(
            document: document with {
                Screens = [new CartridgeScreen(Name: "wide", Width: 20, Tiles: new int[20 * 8])],
                Rules = [new CartridgeRule(Name: "r", When: [], Body: [new CartridgeStatement(Kind: "blit", Screen: "wide", Row: new CartridgeValue(Constant: 0), Column: new CartridgeValue(Constant: 0))])],
            },
            fragment: "before the display-off window costs frames");

        Refuses(
            document: document with { Rules = [new CartridgeRule(Name: "r", When: [], Body: [new CartridgeStatement(Kind: "blit", Screen: "missing", Row: new CartridgeValue(Constant: 0), Column: new CartridgeValue(Constant: 0))])] },
            fragment: "Unknown screen");

        Refuses(
            document: document with { Rules = [new CartridgeRule(Name: "r", When: [], Body: [new CartridgeStatement(Kind: "blit", Screen: "panel", Row: new CartridgeValue(Constant: 31), Column: new CartridgeValue(Constant: 0))])] },
            fragment: "runs past the 32 by 32 map");

        Refuses(
            document: document with { Rules = [new CartridgeRule(Name: "r", When: [], Body: [new CartridgeStatement(Kind: "blit", Screen: "panel", Row: new CartridgeValue(Variable: "i"), Column: new CartridgeValue(Constant: 0))])] },
            fragment: "literal row and column");
    }

    private static CartridgeDocument Blank(string target, string title) => CartridgeDocuments.Create(target: target, title: title);

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }

    private static MachineProbe Run(CartridgeDocument document, int frames, out CartridgeCompilation result) {
        ICartridgeCompiler compiler = document.Target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
        result = compiler.Compile(document: document);
        var machine = new MachineProbe(result: result);
        machine.Run(frames: frames);
        return machine;
    }

    private sealed class MachineProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;
        public MachineProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "map"); }
            else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "map"); }
        }
        public void Run(int frames) {
            m_agb?.RunFrames(keys: AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: JoypadButtons.None, frames: frames);
        }
        public uint Pixel(int x, int y) => m_agb?.ReadPixel(x: x, y: y) ?? m_hgb!.ReadPixel(x: x, y: y);
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }
}
