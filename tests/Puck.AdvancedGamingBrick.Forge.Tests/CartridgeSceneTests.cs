using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

using Puck.State;

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

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void ADeclaredSceneRunsOneArmPerFrame(string target) {
        var result = Compiler(target: target).Compile(document: Document(target: target, scene: "ph"));

        using var machine = new SceneProbe(result: result);
        machine.Run(frames: Frames);

        foreach (var counter in new[] { "a", "b", "c" }) {
            var runs = machine.Read(address: result.Variables[counter]);

            Assert.InRange(actual: runs, low: (Frames / 3) - 2, high: (Frames / 3) + 2);
        }
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void AnUndeclaredGuardLetsEveryArmRunInOneFrame(string target) {
        // The control. Without it the test above would also pass on a machine that never ran the rules at all.
        var result = Compiler(target: target).Compile(document: Document(target: target, scene: null));

        using var machine = new SceneProbe(result: result);
        machine.Run(frames: Frames);

        foreach (var counter in new[] { "a", "b", "c" }) {
            Assert.True(condition: (machine.Read(address: result.Variables[counter]) > ((Frames / 3) + 2)));
        }
    }

    private static ICartridgeCompiler Compiler(string target) => ((target == "agb")
        ? new AgbCartridgeCompiler()
        : new HgbCartridgeCompiler());

    private static CartridgeDocument Document(string target, string? scene) =>
        CartridgeDocuments.Create(target: target, title: "SCENE") with {
            Scene = scene,
            Variables = [
                new CartridgeVariable(Name: "ph", Initial: 0),
                new CartridgeVariable(Name: "a", Initial: 0),
                new CartridgeVariable(Name: "b", Initial: 0),
                new CartridgeVariable(Name: "c", Initial: 0),
            ],
            Rules = [
                Arm(phase: 0, counter: "a", next: 1),
                Arm(phase: 1, counter: "b", next: 2),
                Arm(phase: 2, counter: "c", next: 0),
            ],
        };

    private static CartridgeRule Arm(int phase, string counter, int next) =>
        new(
            Name: $"scene{phase}",
            When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "ph"), Comparison: ActionStateComparison.Equal, Right: new CartridgeValue(Constant: phase))],
            Body: [
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: counter), Operation: ExpressionOp.Add, Value: new CartridgeValue(Constant: 1)),
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "ph"), Value: new CartridgeValue(Constant: next)),
            ]);

    private sealed class SceneProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;

        public SceneProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "scene"); } else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "scene"); }
        }

        public void Run(int frames) {
            m_agb?.RunFrames(keys: AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: JoypadButtons.None, frames: frames);
        }
        public byte Read(uint address) => (m_agb?.ReadByte(address: address) ?? m_hgb!.Read(address: (ushort)address));
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }
}
