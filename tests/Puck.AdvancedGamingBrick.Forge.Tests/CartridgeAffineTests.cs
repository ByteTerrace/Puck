using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Renders the layer turned an eighth once for every affine test that compares against it.</summary>
public sealed class AffineFixture {
    private readonly Lazy<string> m_turned = new(valueFactory: static () => CartridgeAffineTests.Render(
        angle: 32,
        scale: 16
    ));

    /// <summary>Gets the sampled frame of the background-only cartridge turned by 32 steps at unit scale.</summary>
    public string Turned => m_turned.Value;
}
/// <summary>Covers the rotating and scaling background: the matrix follows the authored angle and zoom.</summary>
public sealed class CartridgeAffineTests(AffineFixture affine) : IClassFixture<AffineFixture> {
    private static readonly CartridgeRefusal[] Refusals = [
        new(
            Name: "the colour machine",
            Document: CartridgeDocuments.Create(
                target: "cgb",
                title: "AFFCGB"
            ) with { Affine = Turning(
                map: new int[256],
                scale: 16
            ) },
            Path: "affine",
            Fragment: "needs the advanced machine"
        ),
        new(
            Name: "a map that is not square",
            Document: CartridgeDocuments.Create(
                target: "agb",
                title: "AFFBAD"
            ) with { Affine = Turning(
                map: new int[300],
                scale: 16
            ) },
            Path: "affine.map",
            Fragment: "forming a square map"
        ),
        new(
            Name: "a zero scale",
            Document: CartridgeDocuments.Create(
                target: "agb",
                title: "AFFBAD"
            ) with { Affine = Turning(
                map: new int[256],
                scale: 0
            ) },
            Path: "affine.scale",
            Fragment: "zero scale has no inverse"
        ),
    ];

    public static TheoryData<string> RefusalNames => CartridgeRefusal.Names(table: Refusals);

    // An upright, visible turning layer at the origin, so only the map and scale vary between refusals.
    private static CartridgeAffine Turning(int[] map, int scale) => new(
        Map: map,
        Angle: CartridgeExpressions.Of(constant: 0),
        Scale: CartridgeExpressions.Of(constant: scale),
        CentreX: CartridgeExpressions.Of(constant: 0),
        CentreY: CartridgeExpressions.Of(constant: 0),
        Visible: CartridgeExpressions.Of(constant: 1)
    );

    /// <summary>Renders a wedge of solid cells in one corner, so a turn is visible rather than symmetric.</summary>
    /// <param name="angle">The authored angle, in 256ths of a turn.</param>
    /// <param name="scale">The authored scale, where 16 is unit.</param>
    /// <param name="visible">The authored visibility.</param>
    /// <param name="solid">Whether every cell is solid rather than the wedge.</param>
    /// <param name="single">One pixel to return instead of the sampled grid.</param>
    /// <param name="turningSprite">Whether to add an off-screen turning sprite sharing the turn table.</param>
    /// <returns>The sampled pixels as hexadecimal text.</returns>
    internal static string Render(int angle, int scale, int visible = 1, bool solid = false, (int X, int Y)? single = null, bool turningSprite = false) {
        var map = new int[256];

        for (var row = 0; (row < 16); ++row) {
            for (var column = 0; (column < 16); ++column) {
                map[((row * 16) + column)] = ((solid || (column <= row))
                    ? 1
                    : 0
                );
            }
        }

        var document = CartridgeDocuments.Create(
            target: "agb",
            title: "AFFINE"
        ) with {
            Tiles = [
                new CartridgeTile(
                Name: "blank",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "00000000"
                    )]
            ),
                new CartridgeTile(
                Name: "solid",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "11111111"
                    )]
            ),
            ],
            Affine = new CartridgeAffine(
            Map: map,
            Angle: CartridgeExpressions.Of(constant: angle),
            Scale: CartridgeExpressions.Of(constant: scale),
            CentreX: CartridgeExpressions.Of(constant: 120),
            CentreY: CartridgeExpressions.Of(constant: 80),
            Visible: CartridgeExpressions.Of(constant: visible)
        ),
        };

        if (turningSprite) {
            // Put the object outside the visible area so its affine registers can be checked while the
            // background's pixels remain directly comparable with the background-only cartridge.
            document = document with {
                Sprites = [new CartridgeSprite(
                    Name: "turn",
                    Tile: CartridgeExpressions.Of(constant: 1),
                    X: CartridgeExpressions.Of(constant: 240),
                    Y: CartridgeExpressions.Of(constant: 160),
                    Visible: CartridgeExpressions.Of(constant: 1),
                    Turn: CartridgeExpressions.Of(constant: 64)
                )],
            };
        }
        var result = new AgbCartridgeCompiler().Compile(document: document);
        using var machine = new AgbVerifyMachineDriver(
            rom: result.Rom,
            label: "affine"
        );

        machine.RunFrames(
            frames: 8,
            keys: AgbKeys.None
        );
        if (turningSprite) {
            var table = AgbAffineBackground.BuildTurnTable();
            var first = result.Rom.AsSpan().IndexOf(value: table);

            Assert.True(condition: (first >= 0));
            Assert.Equal(
                expected: -1,
                actual: result.Rom.AsSpan(start: (first + table.Length)).IndexOf(value: table)
            );
            Assert.Equal(
                expected: result.Rom,
                actual: new AgbCartridgeCompiler().Compile(document: document).Rom
            );
            Assert.Equal(
                expected: ((ushort)0),
                actual: machine.ReadHalf(address: 0x07000006)
            );
            Assert.Equal(
                expected: unchecked((ushort)-256),
                actual: machine.ReadHalf(address: 0x0700000E)
            );
            Assert.Equal(
                expected: ((ushort)256),
                actual: machine.ReadHalf(address: 0x07000016)
            );
            Assert.Equal(
                expected: ((ushort)0),
                actual: machine.ReadHalf(address: 0x0700001E)
            );
        }

        var sample = new System.Text.StringBuilder();

        if (single is { } point) {
            return machine.ReadPixel(
                x: point.X,
                y: point.Y
            ).ToString(format: "X8");
        }

        for (var y = 8; (y < 152); y += 16) {
            for (var x = 8; (x < 232); x += 16) {
                sample.Append(value: machine.ReadPixel(
                    x: x,
                    y: y
                ).ToString(format: "X8"));
            }
        }

        return sample.ToString();
    }

    private static string RenderCentre(int visible) => Render(
        angle: 0,
        scale: 16,
        visible: visible,
        solid: true,
        single: (120, 80)
    );

    [Fact]
    public void AHiddenLayerDrawsNothing() {
        // The reference point maps the layer's centre to itself, so that pixel is inside the texture for certain.
        Assert.NotEqual(
            expected: RenderCentre(visible: 0),
            actual: RenderCentre(visible: 1)
        );
    }
    [Fact]
    public void TheTurnTableIsExactAtTheCardinalDirections() {
        var table = AgbAffineBackground.BuildTurnTable();

        static int Entry(byte[] bytes, int step) => ((short)(bytes[(step * 2)] | (bytes[((step * 2) + 1)] << 8)));

        Assert.Equal(
            expected: 0,
            actual: Entry(
                bytes: table,
                step: 0
            )
        );
        Assert.Equal(
            expected: 256,
            actual: Entry(
                bytes: table,
                step: 64
            )
        );
        Assert.Equal(
            expected: 0,
            actual: Entry(
                bytes: table,
                step: 128
            )
        );
        Assert.Equal(
            expected: -256,
            actual: Entry(
                bytes: table,
                step: 192
            )
        );
    }
    // The matrix registers are write-only, so the layer is judged by what it draws rather than by reading them back.
    [Fact]
    public void TurningAndZoomingTheLayerChangeWhatItDraws() {
        var upright = Render(
            angle: 0,
            scale: 16
        );
        var turned = affine.Turned;
        var enlarged = Render(
            angle: 0,
            scale: 8
        );

        Assert.NotEqual(
            actual: turned,
            expected: upright
        );
        Assert.NotEqual(
            actual: enlarged,
            expected: upright
        );

        // The same authored angle and zoom always draw the same frame.
        Assert.Equal(
            expected: upright,
            actual: Render(
                angle: 0,
                scale: 16
            )
        );
    }
    [Fact]
    public void TurningSpritesAndBackgroundShareOneTableAndKeepTheirTransforms() {
        Assert.Equal(
            expected: affine.Turned,
            actual: Render(
                angle: 32,
                scale: 16,
                turningSprite: true
            )
        );
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void ValidationGatesTheTurningLayer(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: Refusals
    );
}
