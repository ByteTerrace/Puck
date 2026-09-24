using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers the panel drawn over the background: it appears where placed and hides on request.</summary>
public sealed class CartridgeWindowTests {
    private static readonly CartridgeRefusal[] Refusals = [
        new(
            Name: "a map that is not full size",
            Document: Panel(
                map: new int[16],
                palettes: null
            ),
            Path: "window.map",
            Fragment: "Expected an array with 1024..1024"
        ),
        new(
            Name: "a tile outside the bank",
            Document: Panel(
                map: [.. Enumerable.Repeat(
                        count: 1024,
                        element: 9
                    )],
                palettes: null
            ),
            Path: "window.map",
            Fragment: "outside the authored tile bank"
        ),
        new(
            Name: "a short cell palette map",
            Document: Panel(
                map: new int[1024],
                palettes: new int[8]
            ),
            Path: "window.mapPalettes",
            Fragment: "1024 entries"
        ),
    ];

    public static TheoryData<string> RefusalNames => CartridgeRefusal.Names(table: Refusals);

    private static CartridgeDocument Panel(int[] map, int[]? palettes) => CartridgeDocuments.Create(
        target: "cgb",
        title: "PANELBAD"
    ) with {
        Window = new CartridgeWindow(
        Map: map,
        MapPalettes: palettes,
        X: CartridgeExpressions.Of(constant: 0),
        Y: CartridgeExpressions.Of(constant: 0),
        Visible: CartridgeExpressions.Of(constant: 1)
    ),
    };
    private static int[] Shade(string target, int ink) {
        var entries = new int[((target == "agb")
            ? 16
            : 4)];

        entries[1] = ink;
        entries[2] = 0x7C00;

        return entries;
    }

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void ThePanelCoversTheBackgroundBelowAndRightOfItsCorner(string target) {
        var background = new int[1024];
        var panel = new int[1024];

        for (var index = 0; (index < 1024); ++index) {
            background[index] = 1;
            panel[index] = 2;
        }

        var document = CartridgeDocuments.Create(
            target: target,
            title: "PANEL"
        ) with {
            Palettes = new CartridgePalettes(
            Background: [Shade(
                    ink: 0x001F,
                    target: target
                )],
            Object: [Shade(
                    ink: 0x001F,
                    target: target
                )]
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
            Variables = [new CartridgeVariable(
                Name: "shown",
                Initial: 1
            )],
            Window = new CartridgeWindow(
            Map: panel,
            MapPalettes: null,
            X: CartridgeExpressions.Of(constant: 80),
            Y: CartridgeExpressions.Of(constant: 72),
            Visible: CartridgeExpressions.Of(state: "shown")
        ),
            Rules = [new CartridgeRule(
                Name: "hold",
                Body: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "shown"),
                        Operation: null,
                        Value: CartridgeExpressions.Of(state: "shown")
                    ),
            ]
            )],
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 20,
            label: "panel"
        );

        // Above and left of the corner is background; below and right is the panel's own tile.
        var outside = machine.Pixel(
            x: 20,
            y: 20
        );
        var inside = machine.Pixel(
            x: 120,
            y: 100
        );

        Assert.NotEqual(
            actual: inside,
            expected: outside
        );
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void ValidationChecksThePanelsMapAndPlacement(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: Refusals
    );
}
