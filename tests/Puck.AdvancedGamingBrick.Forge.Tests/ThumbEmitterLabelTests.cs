namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Pins the Thumb emitter's half of the shared forge label table: ids allocated in order, and each branch
/// family refusing an unbound target under its own mnemonic rather than patching a zero displacement.</summary>
public sealed class ThumbEmitterLabelTests {
    // Each branch family, keyed by the mnemonic its refusal names.
    private static readonly Dictionary<string, Action<ThumbEmitter, int>> Branches = new(comparer: StringComparer.Ordinal) {
        ["bl"] = static (emitter, label) => emitter.Call(label: label),
        ["b<cond>"] = static (emitter, label) => emitter.Branch(
            condition: ThumbCondition.Equal,
            label: label
        ),
        ["b"] = static (emitter, label) => emitter.Branch(label: label),
    };

    [InlineData("bl")]
    [InlineData("b<cond>")]
    [InlineData("b")]
    [Theory]
    public void AnUnboundTargetIsRefusedByItsMnemonic(string mnemonic) {
        var emitter = new ThumbEmitter();

        Branches[mnemonic](arg1: emitter, arg2: emitter.NewLabel());

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => emitter.ToArray(baseAddress: 0x08000000u));

        Assert.Equal(
            actual: refusal.Message,
            expected: $"{mnemonic} targets an unbound label 0."
        );
    }
    [Fact]
    public void LabelsAreAllocatedInOrder() {
        var emitter = new ThumbEmitter();
        var first = emitter.NewLabel();

        Assert.Equal(
            actual: emitter.NewLabel(),
            expected: (first + 1)
        );
        Assert.Equal(
            actual: emitter.NewLabel(),
            expected: (first + 2)
        );
    }
}
