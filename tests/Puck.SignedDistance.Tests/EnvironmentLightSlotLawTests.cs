using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: a point or occluder light's dynamic slot reaches the environment's lanes only as
/// <see cref="SdfProgram.NoDynamicTransformSlot"/> or a slot in [0, <see cref="SdfProgram.MaxDynamicTransformSlot"/>],
/// through <see cref="SdfEnvironment.SetLight"/>, the one door that writes a light; any other slot is refused by name and
/// leaves the environment as it was. An environment copies whole only from another environment
/// (<see cref="SdfEnvironment.CopyFrom(SdfEnvironment)"/>), whose lanes that door wrote.
/// </summary>
public sealed class EnvironmentLightSlotLawTests {
    [Fact]
    public void Raw_row_writes_cannot_bypass_the_typed_light_setter() {
        // SetVector(LightsRow + 2, new Vector3(1, 0, MaxDynamicTransformSlot + 1)) used to overwrite a point's
        // slot directly. Removing that public door is part of the contract, as with the raw-lane CopyFrom door.
        Assert.Null(@object: typeof(SdfEnvironment).GetMethod(name: "SetVector"));
        Assert.Null(@object: typeof(SdfEnvironment).GetMethod(name: "SetLane"));
    }

    // The lane a light's dynamic slot packs into: the third row of its rows, lane 2.
    private static int SlotLane(int light) => ((((SdfEnvironment.LightsRow + (light * SdfEnvironment.RowsPerLight)) + 2) * 4) + 2);
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
        var environment = new SdfEnvironment();

        // A legal slot, the static sentinel and the table's last slot are written, and a copy carries them whole.
        foreach (var slot in ((int[])[5, SdfProgram.NoDynamicTransformSlot, SdfProgram.MaxDynamicTransformSlot])) {
            environment.SetLight(index: 1, light: Light(kind: kind, slot: slot));

            var copy = new SdfEnvironment();

            copy.CopyFrom(source: environment);
            Assert.Equal(actual: copy.Lanes[SlotLane(light: 1)], expected: slot);
        }

        // Anything else is refused by name, and the environment keeps the lanes it held.
        var held = environment.Lanes.ToArray();

        foreach (var slot in ((int[])[(SdfProgram.NoDynamicTransformSlot - 1), (SdfProgram.MaxDynamicTransformSlot + 1)])) {
            var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => environment.SetLight(index: 1, light: Light(kind: kind, slot: slot)));

            Assert.Contains(actualString: refusal.Message, expectedSubstring: "light 1");
            Assert.Equal(actual: environment.Lanes.ToArray(), expected: held);
        }
    }
    [Fact]
    public void ALightThatIsNotPointOrOccluderCarriesNoSlotToRefuse() {
        // The kernels read the slot lane of a point or occluder light only, so SetLight checks it only for those.
        new SdfEnvironment().SetLight(index: 0, light: Light(kind: SdfLightKind.Directional, slot: (SdfProgram.MaxDynamicTransformSlot + 1)));
    }
}
