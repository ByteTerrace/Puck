using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

using Puck.State;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers scroll changes applied part way down the picture, which split it into independently moving bands.</summary>
public sealed class CartridgeRasterTests {
    [Fact]
    public void ABandBelowTheRowScrollsIndependentlyOfTheOneAbove() {
        // A column of solid cells: scrolling a band sideways moves where that column lands on those scanlines.
        var cells = new int[1024];
        for (var row = 0; row < 32; ++row) {
            cells[(row * 32) + 4] = 1;
        }

        var document = CartridgeDocuments.Create(target: "cgb", title: "RASTER") with {
            Tiles = [
                new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
                new CartridgeTile(Name: "solid", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]),
            ],
            Map = cells,
            Raster = [new CartridgeRasterRow(Line: 80, ScrollX: new CartridgeValue(Constant: 32), ScrollY: new CartridgeValue(Constant: 0))],
        };
        var result = new HgbCartridgeCompiler().Compile(document: document);

        // The status vector must name the handler, or the row never fires.
        Assert.Equal(expected: 0xC3, actual: result.Rom[0x0048]);

        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "raster");
        machine.RunFrames(buttons: JoypadButtons.None, frames: 16);

        // Above the row the column sits at its authored place; below it the band has shifted left by the scroll.
        var above = Column(machine: machine, y: 40);
        var below = Column(machine: machine, y: 120);
        Assert.NotEqual(expected: above, actual: below);
        Assert.Equal(expected: 32, actual: above);
        Assert.Equal(expected: 0, actual: below);

        // The match fires before the line is drawn, so the authored line is the first shifted one.
        Assert.Equal(expected: 32, actual: Column(machine: machine, y: 79));
        Assert.Equal(expected: 0, actual: Column(machine: machine, y: 80));
    }

    [Fact]
    public void TheAdvancedMachineSplitsTheSameWayWithoutAnInterruptHandler() {
        var cells = new int[1024];
        for (var row = 0; row < 32; ++row) {
            cells[(row * 32) + 4] = 1;
        }

        var document = CartridgeDocuments.Create(target: "agb", title: "RASTER") with {
            Tiles = [
                new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
                new CartridgeTile(Name: "solid", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]),
            ],
            Map = cells,
            Raster = [new CartridgeRasterRow(Line: 80, ScrollX: new CartridgeValue(Constant: 32), ScrollY: new CartridgeValue(Constant: 0))],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);

        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "raster");
        machine.RunFrames(keys: AgbKeys.None, frames: 8);

        var above = AdvancedColumn(machine: machine, y: 40);
        var below = AdvancedColumn(machine: machine, y: 120);
        Assert.Equal(expected: 32, actual: above);
        Assert.Equal(expected: 0, actual: below);
    }

    [Fact]
    public void TheBandBoundaryLandsOnTheAuthoredScanline() {
        var cells = new int[1024];
        for (var row = 0; row < 32; ++row) {
            cells[(row * 32) + 4] = 1;
        }

        var document = CartridgeDocuments.Create(target: "agb", title: "RASTEREDGE") with {
            Tiles = [
                new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
                new CartridgeTile(Name: "solid", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]),
            ],
            Map = cells,
            Raster = [new CartridgeRasterRow(Line: 80, ScrollX: new CartridgeValue(Constant: 32), ScrollY: new CartridgeValue(Constant: 0))],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);

        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "raster-edge");
        machine.RunFrames(keys: AgbKeys.None, frames: 8);

        // The burst for one scanline governs the next, so the authored line is the first shifted one — never the one
        // before it, and never one line late.
        Assert.Equal(expected: 32, actual: AdvancedColumn(machine: machine, y: 79));
        Assert.Equal(expected: 0, actual: AdvancedColumn(machine: machine, y: 80));
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void AFullBudgetStillRunsAtFrameRateWithEveryRowInUse(string target) {
        // The rows cost the machine real work — an interrupt each on the Color machine, a table fill on the advanced
        // one — that the admission model does not charge for. This is what says that is honest.
        var document = CartridgeDocuments.Create(target: target, title: "RASTERFULL") with {
            Variables = [new CartridgeVariable(Name: "i", Initial: 0), new CartridgeVariable(Name: "sink", Initial: 0), new CartridgeVariable(Name: "ticks", Initial: 0)],
            Raster = [.. Enumerable.Range(start: 0, count: CartridgeLimits.RasterRowCount).Select(selector: index => new CartridgeRasterRow(
                Line: (index * 16) + 8,
                ScrollX: new CartridgeValue(Variable: "sink"),
                ScrollY: new CartridgeValue(Constant: 0)))],
            Rules = [new CartridgeRule(Name: "work", When: [], Body: [
                new CartridgeStatement(Kind: "repeat", Count: 200, Index: "i", Body: [
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "sink"), Operation: ExpressionOp.Add, Value: new CartridgeValue(Constant: 1)),
                ]),
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "ticks"), Operation: ExpressionOp.Add, Value: new CartridgeValue(Constant: 1)),
            ])],
        };

        // The document must sit inside the model's reservation, or this measures a refusal rather than a machine.
        Assert.Empty(collection: CartridgeDocuments.Validate(document: document));

        const int Frames = 40;
        if (target == "agb") {
            var built = new AgbCartridgeCompiler().Compile(document: document);
            using var advanced = new AgbVerifyMachineDriver(rom: built.Rom, label: "raster-full");
            advanced.RunFrames(keys: AgbKeys.None, frames: Frames);
            Assert.True(condition: advanced.ReadByte(address: built.Variables["ticks"]) >= Frames - 4);
            return;
        }

        var result = new HgbCartridgeCompiler().Compile(document: document);
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "raster-full");
        machine.RunFrames(buttons: JoypadButtons.None, frames: Frames);
        Assert.True(condition: machine.Read(address: (ushort)result.Variables["ticks"]) >= Frames - 4);
    }

    [Fact]
    public void ValidationChecksRowOrderAndRange() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "RASTERBAD");
        Refuses(
            document: document with { Raster = [new CartridgeRasterRow(Line: 200, ScrollX: new CartridgeValue(Constant: 0), ScrollY: new CartridgeValue(Constant: 0))] },
            fragment: "scanline in 1..143");
        Refuses(
            document: document with {
                Raster = [
                    new CartridgeRasterRow(Line: 80, ScrollX: new CartridgeValue(Constant: 0), ScrollY: new CartridgeValue(Constant: 0)),
                    new CartridgeRasterRow(Line: 40, ScrollX: new CartridgeValue(Constant: 0), ScrollY: new CartridgeValue(Constant: 0)),
                ],
            },
            fragment: "ascending scanline order");
    }

    // Where the solid run starts on the given scanline, or -1 when it is not on screen. The backdrop is sampled far
    // from the run, because the run itself can sit at column zero once a band has scrolled.
    private static int Column(VerifyMachineDriver machine, int y) {
        var backdrop = machine.ReadPixel(x: 100, y: y);
        for (var x = 0; x < 160; ++x) {
            if (machine.ReadPixel(x: x, y: y) != backdrop) {
                return x;
            }
        }

        return -1;
    }

    private static int AdvancedColumn(AgbVerifyMachineDriver machine, int y) {
        var backdrop = machine.ReadPixel(x: 100, y: y);
        for (var x = 0; x < 240; ++x) {
            if (machine.ReadPixel(x: x, y: y) != backdrop) {
                return x;
            }
        }

        return -1;
    }

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }
}
