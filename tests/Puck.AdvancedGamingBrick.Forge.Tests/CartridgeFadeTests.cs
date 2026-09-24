using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers fading the picture toward black or white, however each machine reaches it.</summary>
public sealed class CartridgeFadeTests {
    private static readonly CartridgeRefusal[] Refusals = [
        new(
            Name: "an amount past full",
            Document: Refused(statement: new CartridgeStatement(
                Kind: "fade",
                Amount: CartridgeExpressions.Of(constant: 40),
                Toward: "black"
            )),
            Path: "rules[0].body[0].amount",
            Fragment: "fade runs 0 through 16"
        ),
        new(
            Name: "an unknown target colour",
            Document: Refused(statement: new CartridgeStatement(
                Kind: "fade",
                Amount: CartridgeExpressions.Of(constant: 4),
                Toward: "grey"
            )),
            Path: "rules[0].body[0].toward",
            Fragment: "Expected black or white"
        ),
    ];

    public static TheoryData<string> RefusalNames => CartridgeRefusal.Names(table: Refusals);

    private static CartridgeDocument Refused(CartridgeStatement statement) => CartridgeDocuments.Create(
        target: "cgb",
        title: "FADEBAD"
    ) with {
        Variables = [new CartridgeVariable(
            Name: "x",
            Initial: 0
        )],
        Rules = [new CartridgeRule(
            Name: "r",
            Body: [statement]
        )],
    };
    private static uint[] Render(string target, string toward, int amount) {
        var cells = new int[1024];

        for (var index = 0; (index < cells.Length); ++index) {
            cells[index] = 1;
        }

        var colors = ((target == "agb")
            ? 16
            : 4
        );
        var palette = new int[colors];

        palette[1] = 0x3DEF;

        var document = CartridgeDocuments.Create(
            target: target,
            title: "FADE"
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
                Name: "solid",
                Pixels: [.. Enumerable.Repeat(
                        count: 8,
                        element: "11111111"
                    )]
            ),
            ],
            Map = cells,
            Rules = [new CartridgeRule(
                Name: "dim",
                Body: [
                new CartridgeStatement(
                        Kind: "fade",
                        Amount: CartridgeExpressions.Of(constant: amount),
                        Toward: toward
                    ),
            ]
            )],
        };
        var result = CartridgeProbe.Compile(document: document);

        using var machine = new CartridgeProbe(
            label: "fade",
            result: result
        );

        machine.Run(frames: 16);

        return [.. Enumerable.Range(
                count: 6,
                start: 0
            ).Select(selector: index => machine.Pixel(
                x: (20 + (index * 20)),
                y: 40
            ))];
    }

    [InlineData("cgb", "black")]
    [InlineData("cgb", "white")]
    [InlineData("agb", "black")]
    [InlineData("agb", "white")]
    [Theory]
    public void FadingMovesThePictureTowardItsTarget(string target, string toward) {
        var rest = Render(
            amount: 0,
            target: target,
            toward: toward
        );
        var part = Render(
            amount: 8,
            target: target,
            toward: toward
        );
        var full = Render(
            amount: 16,
            target: target,
            toward: toward
        );

        Assert.NotEqual(
            actual: part,
            expected: rest
        );
        Assert.NotEqual(
            actual: full,
            expected: part
        );

        // At full fade every sampled pixel is the target colour, so they all agree with each other.
        Assert.Single(collection: full.Distinct());
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void ValidationChecksTheFadeAmountAndTarget(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: Refusals
    );
}
