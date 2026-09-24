using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers runtime background writes reaching the display, and the bound that keeps the queue from dropping one.</summary>
public sealed class CartridgeMapWriteTests {
    private static readonly CartridgeStatement ColumnWrite = new(
        Kind: "map",
        Row: CartridgeExpressions.Of(constant: 0),
        Column: CartridgeExpressions.Of(state: "i"),
        Tile: CartridgeExpressions.Of(constant: 0)
    );
    private static readonly CartridgeRefusal[] Refusals = [
        new(
            // Refused rather than silently dropping the overflow.
            Name: "a loop past the queue",
            Document: Bounded(body: new CartridgeStatement(
                Kind: "repeat",
                Count: 25,
                Index: "i",
                Body: [ColumnWrite]
            )),
            Path: "rules",
            Fragment: "against a queue of"
        ),
        new(
            Name: "a blit past the display-off window",
            Document: Bounded(body: Blit(
                row: CartridgeExpressions.Of(constant: 0),
                screen: "wide"
            )) with {
                Screens = [new CartridgeScreen(
                    Name: "wide",
                    Width: 20,
                    Tiles: new int[(20 * 8)]
                )],
            },
            Path: "rules[0].body[0].screen",
            Fragment: "before the display-off window costs frames"
        ),
        new(
            Name: "an unknown screen",
            Document: Bounded(body: Blit(
                row: CartridgeExpressions.Of(constant: 0),
                screen: "missing"
            )),
            Path: "rules[0].body[0].screen",
            Fragment: "Unknown screen"
        ),
        new(
            Name: "a blit off the map's edge",
            Document: Bounded(body: Blit(
                row: CartridgeExpressions.Of(constant: 31),
                screen: "panel"
            )),
            Path: "rules[0].body[0]",
            Fragment: "runs past the 32 by 32 map"
        ),
        new(
            Name: "a blit at a computed row",
            Document: Bounded(body: Blit(
                row: CartridgeExpressions.Of(state: "i"),
                screen: "panel"
            )),
            Path: "rules[0].body[0]",
            Fragment: "literal row and column"
        ),
    ];

    public static TheoryData<string> RefusalNames => CartridgeRefusal.Names(table: Refusals);

    private static CartridgeStatement Blit(ExpressionProgram row, string screen) => new(
        Kind: "blit",
        Screen: screen,
        Row: row,
        Column: CartridgeExpressions.Of(constant: 0)
    );
    // A four-wide screen and a column cursor, with the one statement under test as the whole rule.
    private static CartridgeDocument Bounded(CartridgeStatement body) => Blank(
        target: "cgb",
        title: "BOUNDS"
    ) with {
        Variables = [new CartridgeVariable(
            Name: "i",
            Initial: 0
        )],
        Screens = [new CartridgeScreen(
            Name: "panel",
            Width: 4,
            Tiles: new int[8]
        )],
        Rules = [new CartridgeRule(
            Name: "r",
            Body: [body]
        )],
    };
    private static CartridgeDocument Blank(string target, string title) => CartridgeDocuments.Create(
        target: target,
        title: title
    );

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void MapWritesReachTheBackgroundOnBothMachines(string target) {
        var document = Blank(
            target: target,
            title: "MAPWRITE"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "col",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "done",
                Initial: 0
            ),
            ],
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
            Rules = [new CartridgeRule(
                Name: "paint",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "done"),
                    comparison: ExpressionOp.Equal,
                    right: CartridgeExpressions.Of(constant: 0)
                ),
                Body: [
                    // A run of solid cells along the top row, written through the queue at run time.
                    new CartridgeStatement(
                        Kind: "repeat",
                        Count: 4,
                        Index: "col",
                        Body: [
                        new CartridgeStatement(
                                Kind: "map",
                                Row: CartridgeExpressions.Of(constant: 0),
                                Column: CartridgeExpressions.Of(state: "col"),
                                Tile: CartridgeExpressions.Of(constant: 1)
                            ),
                    ]
                    ),
                    new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "done"),
                        Operation: null,
                        Value: CartridgeExpressions.Of(constant: 1)
                    ),
                ]
            )],
        };
        using var machine = CartridgeProbe.Boot(
            document: document,
            frames: 16,
            label: "map"
        );

        // The painted run differs from an untouched cell well to its right.
        var painted = machine.Pixel(
            x: 4,
            y: 4
        );
        var untouched = machine.Pixel(
            x: 100,
            y: 4
        );

        Assert.NotEqual(
            actual: painted,
            expected: untouched
        );
        Assert.Equal(
            expected: painted,
            actual: machine.Pixel(
                x: 28,
                y: 4
            )
        );
        Assert.Equal(
            expected: untouched,
            actual: machine.Pixel(
                x: 36,
                y: 4
            )
        );
    }
    [Fact]
    public void ExclusiveBranchArmsCountTheirMapWritesOnce() {
        var redraw = new CartridgeStatement(
            Kind: "repeat",
            Count: 20,
            Index: "i",
            Body: [ColumnWrite]
        );

        // Two arms of twenty writes each fit a queue of 24 because only one of them runs in a frame.
        Assert.Empty(collection: CartridgeDocuments.Validate(document: Bounded(body: new CartridgeStatement(
            Kind: "if",
            When: CartridgeExpressions.Gate(
                left: CartridgeExpressions.Of(state: "i"),
                comparison: ExpressionOp.Equal,
                right: CartridgeExpressions.Of(constant: 0)
            ),
            Then: [redraw],
            Else: [redraw]
        ))));
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void ValidationBoundsMapWritesToTheQueueAndTheBlitWindow(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: Refusals
    );
}
