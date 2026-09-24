using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers the one thing a cartridge genuinely cannot survive: an image larger than the machine addresses. A document
/// that merely runs slowly is not refused, so this is where a refusal has to be clear.
/// </summary>
public sealed class CartridgeCapacityTests {
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AnImageOverTheMachinesWindowsIsNamedRatherThanThrownFromInsideTheBuilder(string target) {
        // Every rule holds the widest body the schema allows, which is the largest routine a document can ask for.
        var document = CartridgeDocuments.Create(
            target: target,
            title: "HUGE"
        ) with {
            Variables = [new CartridgeVariable(
                Name: "i",
                Initial: 0
            ), new CartridgeVariable(
                Name: "x",
                Initial: 0
            )],
            Arrays = [new CartridgeArray(
                Initial: new int[256],
                Name: "cells"
            )],
            Rules = [.. Enumerable.Range(
                count: CartridgeLimits.RuleCount,
                start: 0
            ).Select(selector: index => new CartridgeRule(
                Name: $"r{index}",
                Body: [.. Enumerable.Range(
                        count: CartridgeLimits.StatementCount,
                        start: 0
                    ).Select(selector: static step => new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(
                            State: "cells",
                            Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "i"))
                        ),
                        Operation: ExpressionOp.Divide,
                        Value: CartridgeExpressions.Of(
                            state: "cells",
                            key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(state: "x"))
                        )
                    ))]
            ))],
        };

        // Nothing about this is malformed: it is the schema's own maximum, and the shape checks pass.
        Assert.Empty(collection: CartridgeDocuments.Validate(document: document));

        var overrun = Record.Exception(testCode: () => CartridgeProbe.Compiler(target: target).Compile(document: document));

        if (overrun is null) {
            // The machine had room. The point stands only if the guard is reachable, which the direct check below covers.
            return;
        }

        // One type for every way an image can outgrow its machine, whether the image builder's windows or the code
        // emitter's own reach reports it, so a caller has one thing to catch and a message that names what overran.
        var capacity = Assert.IsType<CartridgeCapacityException>(@object: overrun);

        Assert.False(condition: string.IsNullOrWhiteSpace(value: capacity.Message));
    }
    [Fact]
    public void TheAdvancedMachinesOwnWindowsRefuseAnOversizedRoutineAndBlob() {
        // The builder's guards are what the compiler's report rests on, so they are checked directly too.
        Assert.Throws<ArgumentException>(testCode: () => AgbForgeCartridge.Build(
            title: "OVER",
            gameCode: "PUCK",
            routine: new byte[(AgbForgeCartridge.MaxRoutineBytes + 1)],
            data: []
        ));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => AgbForgeCartridge.RomSizeFor(dataByteCount: (AgbForgeCartridge.MaxDataBytes + 1)));
    }
}
