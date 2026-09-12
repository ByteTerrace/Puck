using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;


namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers each machine running its processor as fast as it can: the Color machine's double-speed switch and the
/// advanced machine's cartridge prefetch and wait states.
/// </summary>
public sealed class CartridgeSpeedTests {
    [Fact]
    public void TheColourMachineRunsAtDoubleSpeedAfterBoot() {
        var result = new HgbCartridgeCompiler().Compile(document: Document(target: "cgb", outer: 1, inner: 1));
        using var machine = new VerifyMachineDriver(rom: result.Rom, label: "speed");
        machine.RunFrames(buttons: JoypadButtons.None, frames: 8);

        // KEY1's top bit reads the speed the processor is actually running at.
        Assert.Equal(expected: 0x80, actual: machine.Read(address: 0xFF4D) & 0x80);
    }

    [Fact]
    public void TheAdvancedMachineBootsWithTheCartridgePrefetchRunning() {
        var result = new AgbCartridgeCompiler().Compile(document: Document(target: "agb", outer: 1, inner: 1));
        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "speed");
        machine.RunFrames(keys: AgbKeys.None, frames: 4);

        // The wait-state register's bit 14 is the prefetch buffer; the cartridge sets it rather than leaving the
        // slowest boot default in place.
        var wait = machine.ReadHalf(address: 0x04000204);
        Assert.Equal(expected: 0x4000, actual: wait & 0x4000);
        // The first wait state drops to 3/1 from the boot default of 4/2.
        Assert.Equal(expected: 0x0014, actual: wait & 0x001C);
    }

    private static CartridgeDocument Document(string target, int outer, int inner) =>
        CartridgeDocuments.Create(target: target, title: "SPEED") with {
            Variables = [
                new CartridgeVariable(Name: "i", Initial: 0),
                new CartridgeVariable(Name: "j", Initial: 0),
                new CartridgeVariable(Name: "sink", Initial: 0),
                new CartridgeVariable(Name: "ticks", Initial: 0),
            ],
            Rules = [new CartridgeRule(Name: "work", Body: [
                new CartridgeStatement(Kind: "repeat", Count: outer, Index: "j", Body: [
                    new CartridgeStatement(Kind: "repeat", Count: inner, Index: "i", Body: [
                        new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "sink"), Operation: ExpressionOp.Add, Value: CartridgeExpressions.Of(constant: 1)),
                    ]),
                ]),
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(State: "ticks"), Operation: ExpressionOp.Add, Value: CartridgeExpressions.Of(constant: 1)),
            ])],
        };
}
