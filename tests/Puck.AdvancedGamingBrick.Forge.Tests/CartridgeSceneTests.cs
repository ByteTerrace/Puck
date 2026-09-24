using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers the declared scene on both real machines: the frame reads the scene variable once, before any rule
/// evaluates, so a rule that writes it names the NEXT frame's scene and can never open a second one in this frame.
/// </summary>
public sealed class CartridgeSceneTests {
    // Three scenes in a ring, each naming the next as its own last act. With the snapshot exactly one runs per
    // frame, so over N frames each counter reaches about N/3; without it all three run every frame and each reaches
    // about N. The gap is the whole claim, and it does not depend on which frame the machine starts evaluating on.
    private const int Frames = 30;

    private static CartridgeRule Arm(int phase, string counter, int next) =>
        new(
            Name: $"scene{phase}",
            When: CartridgeExpressions.Gate(
                left: CartridgeExpressions.Of(state: "ph"),
                comparison: ExpressionOp.Equal,
                right: CartridgeExpressions.Of(constant: phase)
            ),
            Body: [
                new CartridgeStatement(
                    Kind: "set",
                    Target: new CartridgeTarget(State: counter),
                    Operation: ExpressionOp.Add,
                    Value: CartridgeExpressions.Of(constant: 1)
                ),
                new CartridgeStatement(
                    Kind: "set",
                    Target: new CartridgeTarget(State: "ph"),
                    Value: CartridgeExpressions.Of(constant: next)
                ),
            ]
        );
    private static CartridgeDocument Document(string target, string? scene) =>
        CartridgeDocuments.Create(
            target: target,
            title: "SCENE"
        ) with {
            Scene = scene,
            Variables = [
                new CartridgeVariable(
                Name: "ph",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "a",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "b",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "c",
                Initial: 0
            ),
            ],
            Rules = [
                Arm(
                counter: "a",
                next: 1,
                phase: 0
            ),
                Arm(
                counter: "b",
                next: 2,
                phase: 1
            ),
                Arm(
                counter: "c",
                next: 0,
                phase: 2
            ),
            ],
        };

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void ADeclaredSceneRunsOneArmPerFrame(string target) {
        var result = CartridgeProbe.Compiler(target: target).Compile(document: Document(
            scene: "ph",
            target: target
        ));

        using var machine = new CartridgeProbe(
            label: "scene",
            result: result
        );

        machine.Run(frames: Frames);

        foreach (var counter in new[] { "a", "b", "c" }) {
            var runs = machine.Read(address: result.Variables[counter]);

            Assert.InRange(
                actual: runs,
                high: ((Frames / 3) + 2),
                low: ((Frames / 3) - 2)
            );
        }
    }
    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void AnUndeclaredGuardLetsEveryArmRunInOneFrame(string target) {
        // The control. Without it the test above would also pass on a machine that never ran the rules at all.
        var result = CartridgeProbe.Compiler(target: target).Compile(document: Document(
            scene: null,
            target: target
        ));

        using var machine = new CartridgeProbe(
            label: "scene",
            result: result
        );

        machine.Run(frames: Frames);

        foreach (var counter in new[] { "a", "b", "c" }) {
            Assert.True(condition: (machine.Read(address: result.Variables[counter]) > ((Frames / 3) + 2)));
        }
    }
}
