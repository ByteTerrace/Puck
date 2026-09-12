using Puck.GamingBricks.Forge;

using Puck.State;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers recorded one-shots reaching the advanced machine's digital sound path.</summary>
public sealed class CartridgeDirectSoundTests {
    private const uint BufferAddress = 0x02000160u;
    private const uint DmaControlAddress = 0x040000C6u;
    private const int SamplesPerFrame = 288;

    [Fact]
    public void APlayedSampleIsMixedIntoTheBufferAndTheTransferRuns() {
        var sample = new int[600];
        for (var index = 0; index < sample.Length; ++index) {
            sample[index] = (index % 2) == 0 ? 100 : -100;
        }

        var document = CartridgeDocuments.Create(target: "agb", title: "PCM") with {
            Variables = [new CartridgeVariable(Name: "phase", Initial: 0)],
            Sounds = [new CartridgeSound(Name: "boom", Sample: sample)],
            Rules = [
                new CartridgeRule(Name: "fire", When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "phase"), Comparison: ActionStateComparison.Equal, Right: new CartridgeValue(Constant: 3))],
                    Body: [new CartridgeStatement(Kind: "play", Sound: "boom")]),
                new CartridgeRule(Name: "tick", When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "phase"), Comparison: ActionStateComparison.Less, Right: new CartridgeValue(Constant: 200))],
                    Body: [new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "phase"), Operation: ExpressionOp.Add, Value: new CartridgeValue(Constant: 1))]),
            ],
        };
        var result = new AgbCartridgeCompiler().Compile(document: document);
        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "pcm");

        machine.RunFrames(keys: AgbKeys.None, frames: 2);

        // Before anything plays the buffer is silent and the transfer is already armed.
        Assert.True(condition: (machine.ReadHalf(address: DmaControlAddress) & 0x8000) != 0);
        Assert.True(condition: Silent(machine: machine));

        machine.RunFrames(keys: AgbKeys.None, frames: 6);
        Assert.False(condition: Silent(machine: machine));

        // The sample is 600 long against 288 per frame, so it retires and the buffer falls silent again.
        machine.RunFrames(keys: AgbKeys.None, frames: 12);
        Assert.True(condition: Silent(machine: machine));
    }

    [Fact]
    public void ValidationRefusesRecordedSoundOnTheHumbleTarget() {
        var document = CartridgeDocuments.Create(target: "cgb", title: "PCMBAD") with {
            Sounds = [new CartridgeSound(Name: "boom", Sample: [1, 2, 3])],
        };
        var errors = CartridgeDocuments.Validate(document: document);
        Assert.Contains(collection: errors, filter: error => error.Message.Contains(value: "digital sound path", comparisonType: StringComparison.Ordinal));

        var tooLoud = CartridgeDocuments.Create(target: "agb", title: "PCMLOUD") with {
            Sounds = [new CartridgeSound(Name: "boom", Sample: [200])],
        };
        Assert.Contains(collection: CartridgeDocuments.Validate(document: tooLoud), filter: error => error.Message.Contains(value: "signed eight-bit samples", comparisonType: StringComparison.Ordinal));
    }

    private static bool Silent(AgbVerifyMachineDriver machine) {
        for (var index = 0; index < SamplesPerFrame * 2; ++index) {
            if (machine.ReadByte(address: BufferAddress + (uint)index) != 0) {
                return false;
            }
        }

        return true;
    }
}
