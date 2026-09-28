using System.Numerics;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Laws for <see cref="GridSnap"/>, the editor's snapping math: the world lattice snaps each axis at its own pitch and
/// leaves an axis at zero pitch free, a value resting on a node stays there until it leaves the release band, a 90°
/// orientation snap lands on exactly the 24 cube orientations, a reference's face and center candidates win inside its
/// capture radius, a yaw snaps to any step, and a disabled configuration returns its input.
/// </summary>
public sealed class GridSnapLawTests {
    private static readonly Vector3 NoHistory = new(value: float.NaN);

    private static SnapConfig World(Vector3 pitch) => new(
        AngleStepDegrees: 0f,
        Enabled: true,
        Pitch: pitch,
        Reference: null
    );

    [Fact]
    public void EachAxisSnapsToItsOwnPitchAndAZeroPitchLeavesItFree() {
        Assert.Equal(
            actual: GridSnap.Apply(
                candidateLocalHalfExtents: Vector3.Zero,
                config: World(pitch: new Vector3(x: 1f, y: 0.5f, z: 2f)),
                intent: new Vector3(x: 1.4f, y: 0.8f, z: 3.1f),
                previousSnapped: NoHistory
            ),
            expected: new Vector3(x: 1f, y: 1f, z: 4f)
        );
        Assert.Equal(
            actual: GridSnap.Apply(
                candidateLocalHalfExtents: Vector3.Zero,
                config: World(pitch: new Vector3(x: 1f, y: 0f, z: 1f)),
                intent: new Vector3(x: 2.6f, y: 0.37f, z: -1.2f),
                previousSnapped: NoHistory
            ),
            expected: new Vector3(x: 3f, y: 0.37f, z: -1f)
        );
    }
    [Fact]
    public void AValueOnANodeStaysUntilItLeavesTheReleaseBand() {
        var config = World(pitch: Vector3.One);
        var resting = Vector3.One;

        // Within 0.6 of a pitch of its node, the previous value holds; past it, the nearest node takes over.
        Assert.Equal(
            actual: GridSnap.Apply(candidateLocalHalfExtents: Vector3.Zero, config: config, intent: new Vector3(x: 1.55f, y: 1f, z: 1f), previousSnapped: resting),
            expected: resting
        );
        Assert.Equal(
            actual: GridSnap.Apply(candidateLocalHalfExtents: Vector3.Zero, config: config, intent: new Vector3(x: 1.7f, y: 1f, z: 1f), previousSnapped: resting),
            expected: new Vector3(x: 2f, y: 1f, z: 1f)
        );
        // With no history the same value snaps to its nearest node.
        Assert.Equal(
            actual: GridSnap.Apply(candidateLocalHalfExtents: Vector3.Zero, config: config, intent: new Vector3(x: 1.55f, y: 1f, z: 1f), previousSnapped: NoHistory),
            expected: new Vector3(x: 2f, y: 1f, z: 1f)
        );
    }
    [Fact]
    public void ANinetyDegreeSnapLandsOnExactlyTheTwentyFourCubeOrientations() {
        var landed = new List<Quaternion>();
        var random = new Random(Seed: 7);

        for (var sample = 0; (sample < 4000); sample++) {
            var orientation = Quaternion.Normalize(value: new Quaternion(
                w: ((random.NextSingle() * 2f) - 1f),
                x: ((random.NextSingle() * 2f) - 1f),
                y: ((random.NextSingle() * 2f) - 1f),
                z: ((random.NextSingle() * 2f) - 1f)
            ));
            var snapped = GridSnap.SnapRotation(orientation: orientation, stepDegrees: 90f);

            // Every snapped orientation maps each coordinate axis onto a coordinate axis.
            foreach (var axis in ((ReadOnlySpan<Vector3>)[Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ])) {
                var mapped = Vector3.Transform(rotation: snapped, value: axis);

                Assert.Equal(expected: 1f, actual: MathF.Max(x: MathF.Abs(x: mapped.X), y: MathF.Max(x: MathF.Abs(x: mapped.Y), y: MathF.Abs(x: mapped.Z))), precision: 4);
            }

            if (!landed.Any(predicate: existing => (MathF.Abs(x: Quaternion.Dot(quaternion1: existing, quaternion2: snapped)) > 0.9999f))) {
                landed.Add(item: snapped);
            }
        }

        Assert.Equal(expected: 24, actual: landed.Count);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => GridSnap.SnapRotation(orientation: Quaternion.Identity, stepDegrees: 7f));
    }
    [Fact]
    public void AReferencesFaceAndCenterCandidatesWinInsideItsCaptureRadius() {
        var config = new SnapConfig(
            AngleStepDegrees: 0f,
            Enabled: true,
            Pitch: Vector3.Zero,
            Reference: new SnapReference(
                FaceRadius: 0.3f,
                Frame: Quaternion.CreateFromAxisAngle(angle: (MathF.PI / 2f), axis: Vector3.UnitY),
                LocalHalfExtents: Vector3.One,
                Origin: new Vector3(x: 10f, y: 0f, z: 0f),
                Pitch: new Vector3(x: 0.25f, y: 0f, z: 0.25f)
            )
        );
        var half = new Vector3(value: 0.5f);

        // In the reference's frame, local X runs along world -Z. A moved shape 1.4 out along local X butts its face
        // against the reference's at 1.5; one 0.1 out centers on it; one 0.8 out, beyond every candidate's radius, lands on
        // the object lattice.
        Vector3 Local(float x) => (new Vector3(x: 10f, y: 0f, z: 0f) + Vector3.Transform(rotation: config.Reference!.Value.Frame, value: new Vector3(x: x, y: 0f, z: 0f)));
        void Lands(float intent, float expected) {
            var snapped = GridSnap.Apply(candidateLocalHalfExtents: half, config: config, intent: Local(x: intent), previousSnapped: NoHistory);
            var target = Local(x: expected);

            Assert.True(condition: (Vector3.Distance(value1: snapped, value2: target) < 1.0e-4f), userMessage: $"{intent} landed at {snapped}, not {target}");
        }

        Lands(expected: 1.5f, intent: 1.4f);
        Lands(expected: 0f, intent: 0.1f);
        Lands(expected: 0.75f, intent: 0.8f);
    }
    [Fact]
    public void AYawSnapsToAnyStepAndAZeroStepLeavesItFree() {
        Assert.Equal(expected: 30f, actual: GridSnap.SnapYawDegrees(stepDegrees: 15f, yawDegrees: 34f));
        Assert.Equal(expected: -90f, actual: GridSnap.SnapYawDegrees(stepDegrees: 90f, yawDegrees: -80f));
        Assert.Equal(expected: 34f, actual: GridSnap.SnapYawDegrees(stepDegrees: 0f, yawDegrees: 34f));
    }
    // The red leg of every law above: with snapping off, every position is its intent.
    [Fact]
    public void ADisabledConfigurationReturnsItsInput() {
        var intent = new Vector3(x: 1.4f, y: 0.8f, z: 3.1f);

        Assert.Equal(
            actual: GridSnap.Apply(
                candidateLocalHalfExtents: Vector3.Zero,
                config: (World(pitch: Vector3.One) with { Enabled = false }),
                intent: intent,
                previousSnapped: NoHistory
            ),
            expected: intent
        );
    }
}
