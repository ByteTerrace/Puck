using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: a rigid segment's slot lane and a part binding's pose lane store a transform slot packed by one named pair,
/// <see cref="SdfProgram.PackTransformSlot"/> and <see cref="SdfProgram.UnpackTransformSlot"/>: the static slot packs to
/// <see cref="SdfProgram.StaticTransformSlotWord"/>, every legal slot round-trips, and a slot outside the table is
/// refused rather than wrapped.
/// </summary>
public sealed class TransformSlotWordLawTests {
    [Fact]
    public void EveryLegalSlotRoundTripsAndTheStaticSlotPacksToTheStaticWord() {
        Assert.Equal(
            actual: SdfProgram.PackTransformSlot(slot: SdfProgram.NoDynamicTransformSlot),
            expected: SdfProgram.StaticTransformSlotWord
        );
        Assert.Equal(
            actual: SdfProgram.UnpackTransformSlot(word: SdfProgram.StaticTransformSlotWord),
            expected: SdfProgram.NoDynamicTransformSlot
        );

        foreach (var slot in ((int[])[SdfProgram.NoDynamicTransformSlot, 0, 1, 4096, SdfProgram.MaxDynamicTransformSlot])) {
            var word = SdfProgram.PackTransformSlot(slot: slot);

            Assert.Equal(
                actual: SdfProgram.UnpackTransformSlot(word: word),
                expected: slot
            );
            // A dynamic slot never packs to the static word.
            Assert.Equal(
                actual: (word == SdfProgram.StaticTransformSlotWord),
                expected: (slot == SdfProgram.NoDynamicTransformSlot)
            );
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => SdfProgram.PackTransformSlot(slot: (SdfProgram.NoDynamicTransformSlot - 1)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => SdfProgram.PackTransformSlot(slot: (SdfProgram.MaxDynamicTransformSlot + 1)));
    }
}
