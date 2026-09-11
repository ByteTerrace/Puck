using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers fading the picture toward black or white, however each machine reaches it.</summary>
public sealed class CartridgeFadeTests {
    [Theory]
    [InlineData("cgb", "black")]
    [InlineData("cgb", "white")]
    [InlineData("agb", "black")]
    [InlineData("agb", "white")]
    public void FadingMovesThePictureTowardItsTarget(string target, string toward) {
        var rest = Render(target: target, toward: toward, amount: 0);
        var part = Render(target: target, toward: toward, amount: 8);
        var full = Render(target: target, toward: toward, amount: 16);

        Assert.NotEqual(expected: rest, actual: part);
        Assert.NotEqual(expected: part, actual: full);

        // At full fade every sampled pixel is the target colour, so they all agree with each other.
        Assert.Single(collection: full.Distinct());
    }

    [Fact]
    public void ValidationChecksTheFadeAmountAndTarget() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "FADEBAD") with {
            Variables = [new CartridgeVariable(Name: "x", Initial: 0)],
        };
        Refuses(
            document: document with { Rules = [new CartridgeRule(Name: "r", When: [], Body: [new CartridgeStatement(Kind: "fade", Amount: new CartridgeValue(Constant: 40), Toward: "black")])] },
            fragment: "fade runs 0 through 16");
        Refuses(
            document: document with { Rules = [new CartridgeRule(Name: "r", When: [], Body: [new CartridgeStatement(Kind: "fade", Amount: new CartridgeValue(Constant: 4), Toward: "grey")])] },
            fragment: "Expected black or white");
    }

    private static uint[] Render(string target, string toward, int amount) {
        var cells = new int[1024];
        for (var index = 0; index < cells.Length; ++index) {
            cells[index] = 1;
        }

        var colors = target == "agb" ? 16 : 4;
        var palette = new int[colors];
        palette[1] = 0x3DEF;

        var document = CartridgeDocuments.Create(target: target, title: "FADE") with {
            Palettes = new CartridgePalettes(Background: [palette], Object: [palette]),
            Tiles = [
                new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
                new CartridgeTile(Name: "solid", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]),
            ],
            Map = cells,
            Rules = [new CartridgeRule(Name: "dim", When: [], Body: [
                new CartridgeStatement(Kind: "fade", Amount: new CartridgeValue(Constant: amount), Toward: toward),
            ])],
        };
        ICartridgeCompiler compiler = target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
        var result = compiler.Compile(document: document);

        using var machine = new FadeProbe(result: result);
        machine.Run(frames: 16);

        return [.. Enumerable.Range(start: 0, count: 6).Select(selector: index => machine.Pixel(x: 20 + (index * 20), y: 40))];
    }

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }

    private sealed class FadeProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;
        public FadeProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "fade"); }
            else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "fade"); }
        }
        public void Run(int frames) {
            m_agb?.RunFrames(keys: AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: JoypadButtons.None, frames: frames);
        }
        public uint Pixel(int x, int y) => m_agb?.ReadPixel(x: x, y: y) ?? m_hgb!.ReadPixel(x: x, y: y);
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }
}
