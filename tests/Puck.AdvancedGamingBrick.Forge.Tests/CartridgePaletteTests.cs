using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers per-cell background palettes and per-sprite object palettes reaching the screen.</summary>
public sealed class CartridgePaletteTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void CellsOnDifferentPalettesRenderDifferentColors(string target) {
        var colors = target == "agb" ? 16 : 4;
        var red = Palette(colors: colors, ink: 0x001F);
        var blue = Palette(colors: colors, ink: 0x7C00);

        // Every cell holds the same solid tile; only the palette each cell selects differs.
        var cells = new int[1024];
        var palettes = new int[1024];
        for (var index = 0; index < 1024; ++index) {
            cells[index] = 1;
            palettes[index] = (index % 32) < 8 ? 0 : 1;
        }

        var document = CartridgeDocuments.Create(target: target, title: "PALETTE") with {
            Palettes = new CartridgePalettes(Background: [red, blue], Object: [red, blue]),
            Tiles = [
                new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
                new CartridgeTile(Name: "solid", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]),
            ],
            Map = cells,
            MapPalettes = palettes,
        };
        using var machine = Run(document: document, frames: 20);

        // Columns 0..7 sit on palette zero and 8.. on palette one, so compare across that boundary.
        var onFirst = machine.Pixel(x: 4, y: 4);
        var onSecond = machine.Pixel(x: 100, y: 4);
        Assert.Equal(expected: onFirst, actual: machine.Pixel(x: 20, y: 4));
        Assert.NotEqual(expected: onFirst, actual: onSecond);
        Assert.Equal(expected: onSecond, actual: machine.Pixel(x: 76, y: 12));
    }

    [Fact]
    public void ValidationChecksPaletteShapeAndReferences() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "PALBAD");
        Refuses(document: document with { Palettes = new CartridgePalettes(Background: [], Object: [Palette(colors: 4, ink: 0)]) }, fragment: "Expected an array with 1..8");
        Refuses(document: document with { Palettes = new CartridgePalettes(Background: [[1, 2]], Object: [Palette(colors: 4, ink: 0)]) }, fragment: "Expected 4 RGB555 integers");
        Refuses(document: document with { Palettes = new CartridgePalettes(Background: [[1, 2, 3, 99999]], Object: [Palette(colors: 4, ink: 0)]) }, fragment: "RGB555 integers in 0..32767");
        Refuses(document: document with { MapPalettes = new int[16] }, fragment: "1024 entries");
        Refuses(document: document with { MapPalettes = [.. Enumerable.Repeat(element: 3, count: 1024)] }, fragment: "background palette that is not declared");
        Refuses(
            document: document with {
                Sprites = [new CartridgeSprite(Name: "s", Tile: CartridgeExpressions.Of(constant: 0), X: CartridgeExpressions.Of(constant: 0), Y: CartridgeExpressions.Of(constant: 0), Visible: CartridgeExpressions.Of(constant: 1), Palette: CartridgeExpressions.Of(constant: 5))],
            },
            fragment: "object palette that is not declared");
    }

    private static int[] Palette(int colors, int ink) {
        var entries = new int[colors];
        for (var index = 1; index < colors; ++index) {
            entries[index] = ink;
        }

        return entries;
    }

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }

    private static PaletteProbe Run(CartridgeDocument document, int frames) {
        ICartridgeCompiler compiler = document.Target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
        var machine = new PaletteProbe(result: compiler.Compile(document: document));
        machine.Run(frames: frames);
        return machine;
    }

    private sealed class PaletteProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;
        public PaletteProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "palette"); }
            else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "palette"); }
        }
        public void Run(int frames) {
            m_agb?.RunFrames(keys: AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: JoypadButtons.None, frames: frames);
        }
        public uint Pixel(int x, int y) => m_agb?.ReadPixel(x: x, y: y) ?? m_hgb!.ReadPixel(x: x, y: y);
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }
}
