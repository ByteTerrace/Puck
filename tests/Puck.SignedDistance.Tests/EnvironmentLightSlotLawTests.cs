using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// THE LAW: a point or occluder light's dynamic slot reaches the environment's lanes only as
/// <see cref="SdfProgram.NoDynamicTransformSlot"/> or a slot in [0, <see cref="SdfProgram.MaxDynamicTransformSlot"/>],
/// whichever door writes it: <see cref="SdfEnvironment.SetLight"/>, or a whole lane array copied in through
/// <see cref="SdfEnvironment.CopyFrom(ReadOnlySpan{float})"/>, which refuses any other slot lane by name and leaves the
/// environment as it was.
/// </summary>
public sealed class EnvironmentLightSlotLawTests {
    // The lane a light's dynamic slot packs into: the third row of its rows, lane 2.
    private static int SlotLane(int light) => ((((SdfEnvironment.LightsRow + (light * SdfEnvironment.RowsPerLight)) + 2) * 4) + 2);

    [Fact]
    public void ACopiedLaneArrayCarriesOnlyALegalLightSlot() {
        var source = new SdfEnvironment();

        source.SetLight(
            index: 1,
            light: new SdfLight(
                Kind: SdfLightKind.Point,
                Direction: new Vector3(x: 1f, y: 2f, z: 3f),
                Color: Vector3.One,
                Weight: 1f,
                Param: 4f,
                Shadows: false,
                DynamicSlot: 5
            )
        );

        var lanes = source.Lanes.ToArray();
        var copy = new SdfEnvironment();

        // A legal slot, the static sentinel and the table's last slot copy in.
        foreach (var slot in ((int[])[5, SdfProgram.NoDynamicTransformSlot, SdfProgram.MaxDynamicTransformSlot])) {
            lanes[SlotLane(light: 1)] = slot;
            copy.CopyFrom(lanes: lanes);
            Assert.Equal(
                actual: copy.Lanes[SlotLane(light: 1)],
                expected: slot
            );
        }

        // Anything else is refused, and the environment keeps the lanes it held.
        var held = copy.Lanes.ToArray();

        foreach (var slot in ((float[])[(SdfProgram.NoDynamicTransformSlot - 1), (SdfProgram.MaxDynamicTransformSlot + 1f), 2.5f, float.NaN])) {
            lanes[SlotLane(light: 1)] = slot;

            var refusal = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => copy.CopyFrom(lanes: lanes));

            Assert.Contains(
                actualString: refusal.Message,
                expectedSubstring: "light 1"
            );
            Assert.Equal(
                actual: copy.Lanes.ToArray(),
                expected: held
            );
        }
    }
    [Fact]
    public void ALightThatIsNotPointOrOccluderCarriesNoSlotToRefuse() {
        var source = new SdfEnvironment();

        source.SetLight(
            index: 0,
            light: new SdfLight(
                Kind: SdfLightKind.Directional,
                Direction: Vector3.UnitY,
                Color: Vector3.One,
                Weight: 1f,
                Param: 0f,
                Shadows: true,
                DynamicSlot: SdfProgram.NoDynamicTransformSlot
            )
        );

        var lanes = source.Lanes.ToArray();

        // The kernels read the slot lane of a point or occluder light only, as SetLight checks it only for those.
        lanes[SlotLane(light: 0)] = (SdfProgram.MaxDynamicTransformSlot + 1f);
        new SdfEnvironment().CopyFrom(lanes: lanes);
    }
}
