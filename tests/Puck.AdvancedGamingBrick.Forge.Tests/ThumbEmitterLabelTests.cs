namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Pins the Thumb emitter's half of the shared forge label table: ids allocated in order, and each branch
/// family refusing an unbound target under its own mnemonic rather than patching a zero displacement.</summary>
public sealed class ThumbEmitterLabelTests {
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
    [Fact]
    public void AnUnboundConditionalTargetIsRefusedByMnemonic() {
        var emitter = new ThumbEmitter();

        emitter.Branch(
            condition: ThumbCondition.Equal,
            label: emitter.NewLabel()
        );

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => emitter.ToArray(baseAddress: 0x08000000u));

        Assert.Equal(
            actual: refusal.Message,
            expected: "b<cond> targets an unbound label 0."
        );
    }
    [Fact]
    public void AnUnboundUnconditionalTargetIsRefusedByMnemonic() {
        var emitter = new ThumbEmitter();

        emitter.Branch(label: emitter.NewLabel());

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => emitter.ToArray(baseAddress: 0x08000000u));

        Assert.Equal(
            actual: refusal.Message,
            expected: "b targets an unbound label 0."
        );
    }
    [Fact]
    public void AnUnboundCallTargetIsRefusedByMnemonic() {
        var emitter = new ThumbEmitter();

        emitter.Call(label: emitter.NewLabel());

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => emitter.ToArray(baseAddress: 0x08000000u));

        Assert.Equal(
            actual: refusal.Message,
            expected: "bl targets an unbound label 0."
        );
    }
}
