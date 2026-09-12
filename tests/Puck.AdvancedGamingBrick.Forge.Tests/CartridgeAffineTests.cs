using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers the rotating and scaling background: the matrix follows the authored angle and zoom.</summary>
public sealed class CartridgeAffineTests {
    // The matrix registers are write-only, so the layer is judged by what it draws rather than by reading them back.
    [Fact]
    public void TurningAndZoomingTheLayerChangeWhatItDraws() {
        var upright = Render(angle: 0, scale: 16);
        var turned = Render(angle: 32, scale: 16);
        var enlarged = Render(angle: 0, scale: 8);

        Assert.NotEqual(expected: upright, actual: turned);
        Assert.NotEqual(expected: upright, actual: enlarged);

        // The same authored angle and zoom always draw the same frame.
        Assert.Equal(expected: upright, actual: Render(angle: 0, scale: 16));
    }

    [Fact]
    public void AHiddenLayerDrawsNothing() {
        // The reference point maps the layer's centre to itself, so that pixel is inside the texture for certain.
        Assert.NotEqual(
            expected: RenderCentre(visible: 0),
            actual: RenderCentre(visible: 1));
    }

    [Fact]
    public void TurningSpritesAndBackgroundShareOneTableAndKeepTheirTransforms() {
        Assert.Equal(expected: Render(angle: 32, scale: 16), actual: Render(angle: 32, scale: 16, turningSprite: true));
    }
    private static string RenderCentre(int visible) => Render(angle: 0, scale: 16, visible: visible, solid: true, single: (120, 80));

    // A wedge of solid cells in one corner, so a turn is visible rather than symmetric.
    private static string Render(int angle, int scale, int visible = 1, bool solid = false, (int X, int Y)? single = null, bool turningSprite = false) {
        var map = new int[256];
        for (var row = 0; row < 16; ++row) {
            for (var column = 0; column < 16; ++column) {
                map[(row * 16) + column] = solid || column <= row ? 1 : 0;
            }
        }

        var document = CartridgeDocuments.Create(target: "agb", title: "AFFINE") with {
            Tiles = [
                new CartridgeTile(Name: "blank", Pixels: [.. Enumerable.Repeat(element: "00000000", count: 8)]),
                new CartridgeTile(Name: "solid", Pixels: [.. Enumerable.Repeat(element: "11111111", count: 8)]),
            ],
            Affine = new CartridgeAffine(
                Map: map,
                Angle: CartridgeExpressions.Of(constant: angle),
                Scale: CartridgeExpressions.Of(constant: scale),
                CentreX: CartridgeExpressions.Of(constant: 120),
                CentreY: CartridgeExpressions.Of(constant: 80),
                Visible: CartridgeExpressions.Of(constant: visible)),
        };
        if (turningSprite) {
            // Put the object outside the visible area so its affine registers can be checked while the
            // background's pixels remain directly comparable with the background-only cartridge.
            document = document with {
                Sprites = [new CartridgeSprite(Name: "turn", Tile: CartridgeExpressions.Of(constant: 1),
                    X: CartridgeExpressions.Of(constant: 240), Y: CartridgeExpressions.Of(constant: 160),
                    Visible: CartridgeExpressions.Of(constant: 1), Turn: CartridgeExpressions.Of(constant: 64))],
            };
        }
        var result = new AgbCartridgeCompiler().Compile(document: document);
        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "affine");
        machine.RunFrames(keys: AgbKeys.None, frames: 8);
        if (turningSprite) {
            var table = AgbAffineBackground.BuildTurnTable();
            var first = result.Rom.AsSpan().IndexOf(table);
            Assert.True(condition: first >= 0);
            Assert.Equal(expected: -1, actual: result.Rom.AsSpan(first + table.Length).IndexOf(table));
            Assert.Equal(expected: result.Rom, actual: new AgbCartridgeCompiler().Compile(document: document).Rom);
            Assert.Equal(expected: (ushort)0, actual: machine.ReadHalf(address: 0x07000006));
            Assert.Equal(expected: unchecked((ushort)-256), actual: machine.ReadHalf(address: 0x0700000E));
            Assert.Equal(expected: (ushort)256, actual: machine.ReadHalf(address: 0x07000016));
            Assert.Equal(expected: (ushort)0, actual: machine.ReadHalf(address: 0x0700001E));
        }

        var sample = new System.Text.StringBuilder();
        if (single is { } point) {
            return machine.ReadPixel(x: point.X, y: point.Y).ToString(format: "X8");
        }

        for (var y = 8; y < 152; y += 16) {
            for (var x = 8; x < 232; x += 16) {
                sample.Append(value: machine.ReadPixel(x: x, y: y).ToString(format: "X8"));
            }
        }

        return sample.ToString();
    }

    [Fact]
    public void TheTurnTableIsExactAtTheCardinalDirections() {
        var table = AgbAffineBackground.BuildTurnTable();

        static int Entry(byte[] bytes, int step) => (short)(bytes[step * 2] | (bytes[(step * 2) + 1] << 8));

        Assert.Equal(expected: 0, actual: Entry(bytes: table, step: 0));
        Assert.Equal(expected: 256, actual: Entry(bytes: table, step: 64));
        Assert.Equal(expected: 0, actual: Entry(bytes: table, step: 128));
        Assert.Equal(expected: -256, actual: Entry(bytes: table, step: 192));
    }

    [Fact]
    public void ValidationGatesTheTurningLayer() {
        var square = new int[256];
        var humble = CartridgeDocuments.Create(target: "cgb", title: "AFFCGB") with {
            Affine = new CartridgeAffine(Map: square, Angle: CartridgeExpressions.Of(constant: 0), Scale: CartridgeExpressions.Of(constant: 16), CentreX: CartridgeExpressions.Of(constant: 0), CentreY: CartridgeExpressions.Of(constant: 0), Visible: CartridgeExpressions.Of(constant: 1)),
        };
        Refuses(document: humble, fragment: "needs the advanced machine");

        var advanced = CartridgeDocuments.Create(target: "agb", title: "AFFBAD");
        Refuses(
            document: advanced with { Affine = new CartridgeAffine(Map: new int[300], Angle: CartridgeExpressions.Of(constant: 0), Scale: CartridgeExpressions.Of(constant: 16), CentreX: CartridgeExpressions.Of(constant: 0), CentreY: CartridgeExpressions.Of(constant: 0), Visible: CartridgeExpressions.Of(constant: 1)) },
            fragment: "forming a square map");
        Refuses(
            document: advanced with { Affine = new CartridgeAffine(Map: square, Angle: CartridgeExpressions.Of(constant: 0), Scale: CartridgeExpressions.Of(constant: 0), CentreX: CartridgeExpressions.Of(constant: 0), CentreY: CartridgeExpressions.Of(constant: 0), Visible: CartridgeExpressions.Of(constant: 1)) },
            fragment: "zero scale has no inverse");
    }

    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: fragment, comparisonType: StringComparison.Ordinal));
    }
}
