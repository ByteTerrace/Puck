using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// A point or occluder light's dynamic slot reaches the lights table only as <see cref="SdfProgram.NoDynamicTransformSlot"/>
/// or a slot in [0, <see cref="SdfProgram.MaxDynamicTransformSlot"/>], through <see cref="SdfLights.Set"/>, the one door
/// that writes a light; any other slot is refused by name and leaves the table as it was. A table copies whole only from
/// another table (<see cref="SdfLights.CopyFrom(SdfLights)"/>), whose records that door wrote.
/// </summary>
public sealed class LightTableSlotLawTests {
    private static SdfLight Light(SdfLightKind kind, int slot) => new(
        Kind: kind,
        Direction: new Vector3(x: 1f, y: 2f, z: 3f),
        Color: Vector3.One,
        Weight: 1f,
        Param: 4f,
        Shadows: false,
        DynamicSlot: slot
    );

    [InlineData(SdfLightKind.Point)]
    [InlineData(SdfLightKind.Occluder)]
    [Theory]
    public void ALightCarriesOnlyALegalSlot(SdfLightKind kind) {
        var lights = new SdfLights();

        // A legal slot, the static sentinel and the table's last slot are written, and a copy carries them whole.
        foreach (var slot in ((int[])[5, SdfProgram.NoDynamicTransformSlot, SdfProgram.MaxDynamicTransformSlot])) {
            lights.Set(index: 1, light: Light(kind: kind, slot: slot));

            var copy = new SdfLights();

            copy.CopyFrom(source: lights);
            Assert.Equal(actual: copy[1].DynamicSlot, expected: slot);
        }

        // Anything else is refused by name, and the table keeps the records it held.
        var held = lights.Records.ToArray();

        foreach (var slot in ((int[])[(SdfProgram.NoDynamicTransformSlot - 1), (SdfProgram.MaxDynamicTransformSlot + 1)])) {
            var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => lights.Set(index: 1, light: Light(kind: kind, slot: slot)));

            Assert.Contains(actualString: refusal.Message, expectedSubstring: "light 1");
            Assert.Equal(actual: lights.Records.ToArray(), expected: held);
        }
    }
    [Fact]
    public void ALightThatIsNotPointOrOccluderCarriesNoSlotToRefuse() {
        // The kernels read the slot of a point or occluder light only, so Set checks it only for those, and packs zero.
        var lights = new SdfLights();

        lights.Set(index: 0, light: (Light(kind: SdfLightKind.Directional, slot: 0) with { DynamicSlot = (SdfProgram.MaxDynamicTransformSlot + 1) }));
        Assert.Equal(expected: 0, actual: lights[0].DynamicSlot);
    }
}
