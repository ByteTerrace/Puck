using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers a slot whose declared ceiling does not fit a byte: both machines spend two bytes on it, little-endian, and
/// its arithmetic wraps at the declared ceiling rather than at 256.
/// </summary>
public sealed class CartridgeWideStateTests {
    private static CartridgeDocument Base(string target) =>
        CartridgeDocuments.Create(
            target: target,
            title: "WIDE"
        ) with {
            Variables = [new CartridgeVariable(
                Initial: 0,
                Max: 65535,
                Name: "score"
            )],
        };

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AWideComparisonReadsBothBytes(string target) {
        // 0x0200 against 0x00FF: the low bytes alone would answer the wrong way round.
        var result = CartridgeProbe.Compiler(target: target).Compile(document: Base(target: target) with {
            Variables = [
                new CartridgeVariable(
                Initial: 512,
                Max: 65535,
                Name: "score"
            ),
                new CartridgeVariable(
                Initial: 255,
                Max: 65535,
                Name: "bar"
            ),
                new CartridgeVariable(
                Name: "greater",
                Initial: 0
            ),
            ],
            Rules = [new CartridgeRule(
                Name: "rank",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "score"),
                    comparison: ExpressionOp.Greater,
                    right: CartridgeExpressions.Of(state: "bar")
                ),
                Body: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "greater"),
                        Value: CartridgeExpressions.Of(constant: 1)
                    ),
            ]
            )],
        });

        using var machine = new CartridgeProbe(
            label: "wide",
            result: result
        );

        machine.Run(frames: 20);

        Assert.Equal(
            expected: 1,
            actual: machine.Read(address: result.Variables["greater"])
        );
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AWideSlotCountsPastAByteAndReadsBackLittleEndian(string target) {
        // 300 additions of one: a byte slot would have wrapped to 44 long before the end.
        var result = CartridgeProbe.Compiler(target: target).Compile(document: Base(target: target) with {
            Rules = [new CartridgeRule(
                Name: "count",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "score"),
                    comparison: ExpressionOp.Less,
                    right: CartridgeExpressions.Of(constant: 300)
                ),
                Body: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "score"),
                        Operation: ExpressionOp.Add,
                        Value: CartridgeExpressions.Of(constant: 7)
                    ),
            ]
            )],
        });

        using var machine = new CartridgeProbe(
            label: "wide",
            result: result
        );

        machine.Run(frames: 60);

        // The gate stops it in 0..306: the last admitted add starts below 300 and carries at most six past it.
        Assert.InRange(
            actual: machine.ReadWide(variable: "score"),
            high: 306,
            low: 300
        );
    }
    [Fact]
    public void AWideSlotInAByteFieldIsRefused() {
        var document = Base(target: "cgb") with {
            Sprites = [new CartridgeSprite(
                Name: "cursor",
                Tile: CartridgeExpressions.Of(constant: 0),
                X: CartridgeExpressions.Of(state: "score"),
                Y: CartridgeExpressions.Of(constant: 0),
                Visible: CartridgeExpressions.Of(constant: 1)
            )],
        };

        new CartridgeRefusal(
            Document: document,
            Fragment: "wide slot",
            Name: "a wide slot in a sprite position",
            Path: "sprites[0].x"
        ).Holds();
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AWideSubtractBorrowsAcrossTheLowByte(string target) {
        // 0x0100 - 1 is the case a byte-at-a-time subtract gets wrong without the borrow.
        var result = CartridgeProbe.Compiler(target: target).Compile(document: Base(target: target) with {
            Variables = [new CartridgeVariable(
                Initial: 256,
                Max: 65535,
                Name: "score"
            ), new CartridgeVariable(
                Name: "done",
                Initial: 0
            )],
            Rules = [new CartridgeRule(
                Name: "spend",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "done"),
                    comparison: ExpressionOp.Equal,
                    right: CartridgeExpressions.Of(constant: 0)
                ),
                Body: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "score"),
                        Operation: ExpressionOp.Subtract,
                        Value: CartridgeExpressions.Of(constant: 1)
                    ),
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "done"),
                        Value: CartridgeExpressions.Of(constant: 1)
                    ),
            ]
            )],
        });

        using var machine = new CartridgeProbe(
            label: "wide",
            result: result
        );

        machine.Run(frames: 30);

        var address = result.Variables["score"];

        Assert.Equal(
            expected: 0xFF,
            actual: machine.Read(address: address)
        );
        Assert.Equal(
            expected: 0x00,
            actual: machine.Read(address: (address + 1))
        );
    }
    [Fact]
    public void AnOperationWithNoSixteenBitFormIsRefused() {
        var document = Base(target: "cgb") with {
            Rules = [new CartridgeRule(
                Name: "scale",
                Body: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "score"),
                        Operation: ExpressionOp.Multiply,
                        Value: CartridgeExpressions.Of(constant: 2)
                    ),
            ]
            )],
        };

        new CartridgeRefusal(
            Document: document,
            Fragment: "has no sixteen-bit form",
            Name: "a wide multiply",
            Path: "rules[0].body[0].operation"
        ).Holds();
    }
    [Fact]
    public void TheWindowBoundsTheDeclaredSlotsByBytesRatherThanByCount() {
        var document = Base(target: "cgb") with {
            Variables = [.. Enumerable.Range(
                count: ((CartridgeLimits.VariableCount / 2) + 1),
                start: 0
            )
                .Select(selector: index => new CartridgeVariable(
                Initial: 0,
                Max: CartridgeLimits.WideMaximum,
                Name: $"w{index}"
            ))],
        };

        new CartridgeRefusal(
            Document: document,
            Fragment: "the variable window holds",
            Name: "slots past the variable window",
            Path: "variables"
        ).Holds();
    }
}
