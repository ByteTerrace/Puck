using Xunit;

namespace Puck.HumbleGamingBrick.Forge.Tests;

/// <summary>Pins the SM83 emitter's half of the shared forge label table: ids allocated in order, both fixup families
/// resolving against the same bound offsets, and a branch naming a label nothing bound refused by mnemonic rather
/// than patched with a zero.</summary>
public sealed class Sm83EmitterLabelTests {
    [Fact]
    public void LabelsAreAllocatedInOrderAndBothFixupFamiliesResolveThem() {
        var emitter = new Sm83Emitter();
        var start = emitter.NewLabel();
        var end = emitter.NewLabel();

        Assert.Equal(
            actual: end,
            expected: (start + 1)
        );

        emitter.MarkLabel(label: start);
        emitter.Nop();
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: start
        );
        emitter.JumpAbsolute(label: end);
        emitter.MarkLabel(label: end);
        emitter.Return();

        var code = emitter.ToArray(baseAddress: 0x0150);

        // nop, jr nz -3 (measured from the byte after the offset), jp 0x0156, ret.
        Assert.Equal(
            actual: code,
            expected: new byte[] { 0x00, 0x20, 0xFD, 0xC3, 0x56, 0x01, 0xC9 }
        );
    }
    [Fact]
    public void AnUnboundRelativeTargetIsRefusedByMnemonic() {
        var emitter = new Sm83Emitter();

        emitter.JumpRelative(label: emitter.NewLabel());

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => emitter.ToArray());

        Assert.Equal(
            actual: refusal.Message,
            expected: "jr targets an unbound label 0."
        );
    }
    [Fact]
    public void AnUnboundAbsoluteTargetIsRefusedByMnemonic() {
        var emitter = new Sm83Emitter();

        emitter.Nop();
        emitter.Call(label: emitter.NewLabel());

        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => emitter.ToArray());

        Assert.Equal(
            actual: refusal.Message,
            expected: "jp targets an unbound label 0."
        );
    }
}
