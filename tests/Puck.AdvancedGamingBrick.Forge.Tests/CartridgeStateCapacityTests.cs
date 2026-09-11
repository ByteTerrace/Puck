using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers state that runs past the fixed work-RAM page into the switchable bank pinned at boot.</summary>
public sealed class CartridgeStateCapacityTests {
    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void ArraysBeyondTheFixedPageStillReadAndWrite(string target) {
        // Enough arrays to run past the fixed page's end, so the last one lives in the switchable bank.
        var arrays = new CartridgeArray[28];
        for (var index = 0; index < arrays.Length; ++index) {
            arrays[index] = new CartridgeArray(Name: $"block{index}", Initial: [.. Enumerable.Repeat(element: index, count: 250)]);
        }

        var document = CartridgeDocuments.Create(target: target, title: "CAPACITY") with {
            Variables = [new CartridgeVariable(Name: "readBack", Initial: 0), new CartridgeVariable(Name: "done", Initial: 0)],
            Arrays = arrays,
            Rules = [new CartridgeRule(
                Name: "touch",
                When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "done"), Comparison: "eq", Right: new CartridgeValue(Constant: 0))],
                Body: [
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Array: "block27", Index: new CartridgeValue(Constant: 249)), Operation: "set", Value: new CartridgeValue(Constant: 200)),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "readBack"), Operation: "set", Value: new CartridgeValue(Array: "block27", Index: new CartridgeValue(Constant: 249))),
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "done"), Operation: "set", Value: new CartridgeValue(Constant: 1)),
                ])],
        };
        ICartridgeCompiler compiler = target == "agb" ? new AgbCartridgeCompiler() : new HgbCartridgeCompiler();
        var result = compiler.Compile(document: document);

        // The last array starts past the fixed page on the humble machine, which is the point of the test.
        if (target == "cgb") {
            Assert.True(condition: result.Arrays["block27"] >= 0xD000);
        }

        using var machine = new CapacityProbe(result: result);
        machine.Run(frames: 12);
        Assert.Equal(expected: 200, actual: machine.Read(address: result.Variables["readBack"]));
        Assert.Equal(expected: 200, actual: machine.Read(address: result.Arrays["block27"] + 249));
        Assert.Equal(expected: 27, actual: machine.Read(address: result.Arrays["block27"]));
    }

    private sealed class CapacityProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;
        public CapacityProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(rom: result.Rom, label: "capacity"); }
            else { m_hgb = new VerifyMachineDriver(rom: result.Rom, label: "capacity"); }
        }
        public void Run(int frames) {
            m_agb?.RunFrames(keys: AgbKeys.None, frames: frames);
            m_hgb?.RunFrames(buttons: JoypadButtons.None, frames: frames);
        }
        public byte Read(uint address) => m_agb?.ReadByte(address: address) ?? m_hgb!.Read(address: (ushort)address);
        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
    }
}
