using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Samples the fully weighted panel once for every blend test that compares against it.</summary>
public sealed class BlendFixture {
    private readonly Lazy<uint> m_opaque = new(valueFactory: static () => CartridgeBlendTests.Panel(weight: 16));

    /// <summary>Gets the panel's corner at full weight.</summary>
    public uint Opaque => m_opaque.Value;
}
/// <summary>Covers mixing one surface with what is drawn beneath it, which is how a translucent panel is made.</summary>
public sealed class CartridgeBlendTests(BlendFixture blend) : IClassFixture<BlendFixture> {
    private static readonly CartridgeRefusal[] Refusals = [
        new(
            Name: "an unknown surface",
            Document: Bad() with { Rules = [Rule(
                    surface: "nowhere",
                    weight: 8
                )] },
            Path: "rules[0].body[0].surface",
            Fragment: "background, panel, middle, far, sprites or backdrop"
        ),
        new(
            Name: "the panel without a window",
            Document: Bad() with { Rules = [Rule(
                    surface: "panel",
                    weight: 8
                )] },
            Path: "rules[0].body[0].surface",
            Fragment: "needs a declared window"
        ),
        new(
            Name: "the middle surface without a layer",
            Document: Bad() with { Rules = [Rule(
                    surface: "middle",
                    weight: 8
                )] },
            Path: "rules[0].body[0].surface",
            Fragment: "needs a turning background or a declared layer"
        ),
        new(
            Name: "the far surface without a second layer",
            Document: Bad() with { Rules = [Rule(
                    surface: "far",
                    weight: 8
                )] },
            Path: "rules[0].body[0].surface",
            Fragment: "needs a layer behind the middle one"
        ),
        new(
            Name: "a weight past full",
            Document: Bad() with { Rules = [Rule(
                    surface: "backdrop",
                    weight: 40
                )] },
            Path: "rules[0].body[0].weight",
            Fragment: "weight runs 0 through 16"
        ),
        new(
            Name: "the colour machine",
            Document: Bad(target: "cgb") with { Rules = [Rule(
                    surface: "backdrop",
                    weight: 8
                )] },
            Path: "rules[0].body[0]",
            Fragment: "cgb target has no blend unit"
        ),
    ];

    public static TheoryData<string> RefusalNames => CartridgeRefusal.Names(table: Refusals);

    private static CartridgeDocument Bad(string target = "agb") => CartridgeDocuments.Create(
        target: target,
        title: "BLENDBAD"
    );
    // Two flatly coloured surfaces, so a mix of them is distinguishable from either on its own.
    private static CartridgeDocument Document() {
        var background = new int[1024];
        var panel = new int[1024];

        for (var index = 0; (index < 1024); ++index) {
            background[index] = 1;
            panel[index] = 2;
        }

        var palette = new int[16];

        palette[1] = 0x7C00;
        palette[2] = 0x001F;
        return CartridgeDocuments.Create(
            target: "agb",
            title: "BLEND"
        ) with {
            Palettes = new CartridgePalettes(
            Background: [palette],
            Object: [palette]
        ),
            Tiles = [
                new CartridgeTile(
                Name: "blank",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "00000000"
                    )]
            ),
                new CartridgeTile(
                Name: "one",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "11111111"
                    )]
            ),
                new CartridgeTile(
                Name: "two",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "22222222"
                    )]
            ),
            ],
            Map = background,
            Window = new CartridgeWindow(
            Map: panel,
            MapPalettes: null,
            X: CartridgeExpressions.Of(constant: 80),
            Y: CartridgeExpressions.Of(constant: 72),
            Visible: CartridgeExpressions.Of(constant: 1)
        ),
        };
    }

    /// <summary>Samples inside the panel's corner, where a blend mixes it with the background.</summary>
    /// <param name="weight">The authored blend weight, 0 through 16.</param>
    /// <returns>The packed pixel.</returns>
    internal static uint Panel(int weight) => Sample(
        document: Document() with {
            Rules = [Rule(
                surface: "panel",
                weight: weight
            )],
        },
        x: 160,
        y: 120
    );

    private static CartridgeRule Rule(string surface, int weight) => new(
        Name: "mix",
        Body: [new CartridgeStatement(
                Kind: "blend",
                Surface: surface,
                Weight: CartridgeExpressions.Of(constant: weight)
            )]
    );
    private static uint Sample(CartridgeDocument document, int x, int y) => Sample(
        document: document,
        points: [(x, y)]
    )[0];
    private static uint[] Sample(CartridgeDocument document, (int X, int Y)[] points) {
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 8,
            label: "blend"
        );

        return [.. points.Select(selector: point => machine.Pixel(
            x: point.X,
            y: point.Y
        ))];
    }

    [Fact]
    public void AWeightPastFullIsHeldAtFull() {
        // Validation refuses an authored constant past full, so the clamp exists for the values state supplies.
        var document = Document() with {
            Variables = [new CartridgeVariable(
                Name: "w",
                Initial: 200
            )],
            Rules = [new CartridgeRule(
                Name: "mix",
                Body: [
                new CartridgeStatement(
                        Kind: "blend",
                        Surface: "panel",
                        Weight: CartridgeExpressions.Of(state: "w")
                    ),
            ]
            )],
        };

        Assert.Equal(
            expected: blend.Opaque,
            actual: Sample(
                document: document,
                x: 160,
                y: 120
            )
        );
    }
    [Fact]
    public void TheWeightMovesThePanelBetweenItsOwnColourAndTheBackgroundBeneath() {
        var opaque = blend.Opaque;
        var mixed = Panel(weight: 8);
        var clear = Panel(weight: 0);
        // Inside the panel's corner with no blend at all, and outside the panel where only the background draws.
        var unblended = Sample(
            document: Document(),
            points: [(160, 120), (20, 20)]
        );

        // Full weight leaves the panel exactly as an unblended one; no weight leaves only what is beneath it.
        Assert.Equal(
            expected: unblended[0],
            actual: opaque
        );
        Assert.Equal(
            expected: unblended[1],
            actual: clear
        );

        // Half weight is neither, and every channel lies between the two ends.
        Assert.NotEqual(
            actual: opaque,
            expected: mixed
        );
        Assert.NotEqual(
            actual: clear,
            expected: mixed
        );
        for (var shift = 0; (shift < 24); shift += 8) {
            Assert.InRange(
                actual: ((int)((mixed >> shift) & 0xFF)),
                low: ((int)Math.Min(
                    val1: (opaque >> shift) & 0xFF,
                    val2: (clear >> shift) & 0xFF
                )),
                high: ((int)Math.Max(
                    val1: (opaque >> shift) & 0xFF,
                    val2: (clear >> shift) & 0xFF
                ))
            );
        }
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void ValidationNamesTheSurfaceAndRefusesTheColourMachine(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: Refusals
    );
}
