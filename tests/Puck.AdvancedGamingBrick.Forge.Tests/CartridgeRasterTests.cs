using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers scroll changes applied part way down the picture, which split it into independently moving bands.</summary>
public sealed class CartridgeRasterTests {
    private static readonly CartridgeRefusal[] Refusals = [
        new(
            Name: "a line past the picture",
            Document: Rows(lines: [200]),
            Path: "raster[0].line",
            Fragment: "scanline in 1..143"
        ),
        new(
            Name: "rows out of order",
            Document: Rows(lines: [80, 40]),
            Path: "raster[1].line",
            Fragment: "ascending scanline order"
        ),
    ];

    public static TheoryData<string> RefusalNames => CartridgeRefusal.Names(table: Refusals);

    private static CartridgeDocument Rows(int[] lines) => CartridgeDocuments.Create(
        target: "cgb",
        title: "RASTERBAD"
    ) with {
        Raster = [.. lines.Select(selector: static line => new CartridgeRasterRow(
            Line: line,
            ScrollX: CartridgeExpressions.Of(constant: 0),
            ScrollY: CartridgeExpressions.Of(constant: 0)
        ))],
    };
    // Where the solid run starts on the given scanline, or -1 when it is not on screen. The backdrop is sampled far
    // from the run, because the run itself can sit at column zero once a band has scrolled.
    private static int Column(CartridgeProbe machine, int y) {
        var width = ((machine.Result.Target == "agb")
            ? 240
            : 160
        );
        var backdrop = machine.Pixel(
            x: 100,
            y: y
        );

        for (var x = 0; (x < width); ++x) {
            if (machine.Pixel(
                x: x,
                y: y
            ) != backdrop) {
                return x;
            }
        }

        return -1;
    }

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void ABandBelowTheRowScrollsIndependentlyOfTheOneAbove(string target) {
        // A column of solid cells: scrolling a band sideways moves where that column lands on those scanlines.
        var cells = new int[1024];

        for (var row = 0; (row < 32); ++row) {
            cells[((row * 32) + 4)] = 1;
        }

        var document = CartridgeDocuments.Create(
            target: target,
            title: "RASTER"
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
            Map = cells,
            Raster = [new CartridgeRasterRow(
                Line: 80,
                ScrollX: CartridgeExpressions.Of(constant: 32),
                ScrollY: CartridgeExpressions.Of(constant: 0)
            )],
        };
        var result = CartridgeProbe.Compile(document: document);

        // The Color machine splits from an interrupt, so its status vector must name the handler or the row never
        // fires; the advanced machine fills a per-line table instead and needs no handler.
        if (target == "cgb") {
            Assert.Equal(
                expected: 0xC3,
                actual: result.Rom[0x0048]
            );
        }

        using var machine = new CartridgeProbe(
            label: "raster",
            result: result
        );

        machine.Run(frames: ((target == "agb")
            ? 8
            : 16));

        // Above the row the column sits at its authored place; below it the band has shifted left by the scroll.
        Assert.Equal(
            actual: Column(
                machine: machine,
                y: 40
            ),
            expected: 32
        );
        Assert.Equal(
            actual: Column(
                machine: machine,
                y: 120
            ),
            expected: 0
        );

        // The authored line is the first shifted one: never the line before it, and never one line late.
        Assert.Equal(
            expected: 32,
            actual: Column(
                machine: machine,
                y: 79
            )
        );
        Assert.Equal(
            expected: 0,
            actual: Column(
                machine: machine,
                y: 80
            )
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AFullBudgetStillRunsAtFrameRateWithEveryRowInUse(string target) {
        // The rows cost the machine real work — an interrupt each on the Color machine, a table fill on the advanced
        // one — that the admission model does not charge for. This is what says that is honest.
        var document = CartridgeDocuments.Create(
            target: target,
            title: "RASTERFULL"
        ) with {
            Variables = [new CartridgeVariable(
                Name: "i",
                Initial: 0
            ), new CartridgeVariable(
                Name: "sink",
                Initial: 0
            ), new CartridgeVariable(
                Name: "ticks",
                Initial: 0
            )],
            Raster = [.. Enumerable.Range(
                count: CartridgeLimits.RasterRowCount,
                start: 0
            ).Select(selector: index => new CartridgeRasterRow(
                Line: ((index * 16) + 8),
                ScrollX: CartridgeExpressions.Of(state: "sink"),
                ScrollY: CartridgeExpressions.Of(constant: 0)
            ))],
            Rules = [new CartridgeRule(
                Name: "work",
                Body: [
                new CartridgeStatement(
                        Kind: "repeat",
                        Count: 200,
                        Index: "i",
                        Body: [
                    new CartridgeStatement(
                                Kind: "set",
                                Target: new CartridgeTarget(State: "sink"),
                                Operation: ExpressionOp.Add,
                                Value: CartridgeExpressions.Of(constant: 1)
                            ),
                ]
                    ),
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "ticks"),
                        Operation: ExpressionOp.Add,
                        Value: CartridgeExpressions.Of(constant: 1)
                    ),
            ]
            )],
        };

        // The document must sit inside the model's reservation, or this measures a refusal rather than a machine.
        Assert.Empty(collection: CartridgeDocuments.Validate(document: document));

        const int Frames = 40;

        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: Frames,
            label: "raster-full"
        );

        Assert.True(condition: (machine.Read(variable: "ticks") >= (Frames - 4)));
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void ValidationChecksRowOrderAndRange(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: Refusals
    );
}
