using Puck.GamingBricks.Forge;

using Puck.State;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers a sequence of sampled notes authored entirely as document state, which is the evidence that driving sampled
/// instruments needs no primitive of its own beyond a resampled voice.
/// </summary>
public sealed class CartridgeTrackerTests {
    private const uint BufferAddress = 0x02000160u;
    private const int RowFrames = 4;
    private const int SamplesPerFrame = 288;
    private const uint VoiceAddress = BufferAddress + (SamplesPerFrame * 2) + 4;

    [Fact]
    public void ADocumentAuthoredSequenceStartsEachRowsNoteAtItsOwnPitch() {
        var result = new AgbCartridgeCompiler().Compile(document: Document());
        using var machine = new AgbVerifyMachineDriver(rom: result.Rom, label: "tracker");

        // Each row holds for RowFrames, so sampling inside successive rows reads successive steps.
        var steps = new List<uint>();
        for (var row = 0; row < 4; ++row) {
            machine.RunFrames(keys: AgbKeys.None, frames: RowFrames);
            steps.Add(item: Step(machine: machine));
        }

        // The authored rates rise, so the steps the voices carry rise with them and no two rows sound alike.
        Assert.Equal(expected: steps.Count, actual: steps.Distinct().Count());
        Assert.All(collection: steps, action: static step => Assert.NotEqual(expected: 0u, actual: step));
    }

    // The step of whichever voice was started most recently; voices are claimed in order and retire in order.
    private static uint Step(AgbVerifyMachineDriver machine) {
        var latest = 0u;
        for (var voice = 0u; voice < CartridgeLimits.SampleVoiceCount; ++voice) {
            var word = 0u;
            for (var index = 0u; index < 4u; ++index) {
                word |= (uint)machine.ReadByte(address: VoiceAddress + (voice * 16u) + 12u + index) << (int)(index * 8);
            }

            latest = Math.Max(val1: latest, val2: word);
        }

        return latest;
    }

    // A pattern in an array, a frame clock, and a play whose rate is read from that array: no sequencer primitive.
    private static CartridgeDocument Document() {
        var sample = new int[128];
        for (var index = 0; index < sample.Length; ++index) {
            sample[index] = ((index * 200) / sample.Length) - 100;
        }

        return CartridgeDocuments.Create(target: "agb", title: "TRACKER") with {
            Variables = [
                new CartridgeVariable(Name: "tick", Initial: 0),
                new CartridgeVariable(Name: "row", Initial: 0),
            ],
            Arrays = [new CartridgeArray(Name: "rates", Initial: [48, 64, 96, 128])],
            Sounds = [new CartridgeSound(Name: "instrument", Sample: sample)],
            Rules = [
                new CartridgeRule(Name: "clock", When: [], Body: [
                    new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "tick"), Operation: ExpressionOp.Add, Value: new CartridgeValue(Constant: 1)),
                    new CartridgeStatement(
                        Kind: "if",
                        When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "tick"), Comparison: ActionStateComparison.GreaterOrEqual, Right: new CartridgeValue(Constant: RowFrames))],
                        Then: [
                            new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "tick"), Operation: null, Value: new CartridgeValue(Constant: 0)),
                            new CartridgeStatement(
                                Kind: "play",
                                Sound: "instrument",
                                Rate: new CartridgeValue(Array: "rates", Index: new CartridgeValue(Variable: "row"))),
                            new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "row"), Operation: ExpressionOp.Add, Value: new CartridgeValue(Constant: 1)),
                            new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "row"), Operation: ExpressionOp.Modulo, Value: new CartridgeValue(Constant: 4)),
                        ]),
                ]),
            ],
        };
    }
}
