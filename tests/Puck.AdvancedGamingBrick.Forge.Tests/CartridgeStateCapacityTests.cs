using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers state that runs past the fixed work-RAM page into the switchable bank pinned at boot.</summary>
public sealed class CartridgeStateCapacityTests {
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void ArraysBeyondTheFixedPageStillReadAndWrite(string target) {
        // Enough arrays to run past the fixed page's end, so the last one lives in the switchable bank.
        var arrays = new CartridgeArray[28];

        for (var index = 0; (index < arrays.Length); ++index) {
            arrays[index] = new CartridgeArray(
                Name: $"block{index}",
                Initial: [.. Enumerable.Repeat(
                        count: 250,
                        element: index
                    )]
            );
        }

        var document = CartridgeDocuments.Create(
            target: target,
            title: "CAPACITY"
        ) with {
            Variables = [new CartridgeVariable(
                Name: "readBack",
                Initial: 0
            ), new CartridgeVariable(
                Name: "done",
                Initial: 0
            )],
            Arrays = arrays,
            Rules = [new CartridgeRule(
                Name: "touch",
                When: CartridgeExpressions.Gate(
                    left: CartridgeExpressions.Of(state: "done"),
                    comparison: ExpressionOp.Equal,
                    right: CartridgeExpressions.Of(constant: 0)
                ),
                Body: [
                    new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(
                            State: "block27",
                            Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(constant: 249))
                        ),
                        Operation: null,
                        Value: CartridgeExpressions.Of(constant: 200)
                    ),
                    new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "readBack"),
                        Operation: null,
                        Value: CartridgeExpressions.Of(
                            state: "block27",
                            key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(constant: 249))
                        )
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
        var result = CartridgeProbe.Compile(document: document);

        // The last array starts past the fixed page on the humble machine, which is the point of the test.
        if (target == "cgb") {
            Assert.True(condition: (result.Arrays["block27"] >= 0xD000));
        }

        using var machine = new CartridgeProbe(
            label: "capacity",
            result: result
        );

        machine.Run(frames: 12);
        Assert.Equal(
            expected: 200,
            actual: machine.Read(address: result.Variables["readBack"])
        );
        Assert.Equal(
            expected: 200,
            actual: machine.Read(address: (result.Arrays["block27"] + 249))
        );
        Assert.Equal(
            expected: 27,
            actual: machine.Read(address: result.Arrays["block27"])
        );
    }
}
