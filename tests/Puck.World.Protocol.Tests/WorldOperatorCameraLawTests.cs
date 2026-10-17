using System.Numerics;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the operator camera is an absolute pose whose forms agree with each other. A look-at pose faces
/// its target and takes it as its pivot, an orbit pose sits the distance behind its pivot along its heading, a swing
/// keeps the pivot where it was, a turn keeps the eye, and the pose crosses the composition wire unchanged.
/// </summary>
public sealed class WorldOperatorCameraLawTests {
    private static void AssertNear(Vector3 expected, Vector3 actual) => Assert.True(
        condition: (Vector3.Distance(value1: expected, value2: actual) < 1e-3f),
        userMessage: $"expected {expected} but was {actual}"
    );

    [Fact]
    public void LookAtFacesItsTargetAndOrbitsIt() {
        var eye = new Vector3(x: 3f, y: 1.6f, z: -8f);
        var target = new Vector3(x: -4f, y: 0.5f, z: -24f);
        var pose = WorldOperatorCamera.FromLookAt(
            eye: eye,
            fieldOfViewRadians: WorldOperatorCamera.DefaultFieldOfViewRadians,
            target: target
        )!;

        AssertNear(expected: Vector3.Normalize(value: (target - eye)), actual: pose.Forward());
        AssertNear(expected: target, actual: pose.Pivot());
        AssertNear(expected: eye, actual: pose.Eye());
    }
    [Fact]
    public void HeadingFollowsThePlacementConvention() {
        var origin = Vector3.Zero;

        AssertNear(
            expected: new Vector3(x: 0f, y: 0f, z: -1f),
            actual: WorldOperatorCamera.FromPose(eye: origin, fieldOfViewRadians: 1f, pitchDegrees: 0f, yawDegrees: 0f).Forward()
        );
        AssertNear(
            expected: new Vector3(x: 0f, y: 0f, z: 1f),
            actual: WorldOperatorCamera.FromPose(eye: origin, fieldOfViewRadians: 1f, pitchDegrees: 0f, yawDegrees: 180f).Forward()
        );
        AssertNear(
            expected: new Vector3(x: -1f, y: 0f, z: 0f),
            actual: WorldOperatorCamera.FromPose(eye: origin, fieldOfViewRadians: 1f, pitchDegrees: 0f, yawDegrees: 90f).Forward()
        );
        AssertNear(
            expected: new Vector3(x: 0f, y: MathF.Sin(x: (30f * (MathF.PI / 180f))), z: -MathF.Cos(x: (30f * (MathF.PI / 180f)))),
            actual: WorldOperatorCamera.FromPose(eye: origin, fieldOfViewRadians: 1f, pitchDegrees: 30f, yawDegrees: 0f).Forward()
        );
    }
    [Fact]
    public void OrbitSitsTheDistanceBehindItsPivotAndSwingKeepsThePivot() {
        var target = new Vector3(x: 0f, y: 0f, z: -24f);
        var pose = WorldOperatorCamera.FromOrbit(
            distance: 20f,
            fieldOfViewRadians: 1f,
            pitchDegrees: -10f,
            target: target,
            yawDegrees: 45f
        );

        AssertNear(expected: target, actual: pose.Pivot());
        Assert.Equal(expected: 20f, actual: Vector3.Distance(value1: pose.Eye(), value2: target), precision: 3);

        var swung = pose.Swing(distance: -5f, pitchDegrees: 12f, yawDegrees: 90f);

        AssertNear(expected: target, actual: swung.Pivot());
        Assert.Equal(expected: 15f, actual: Vector3.Distance(value1: swung.Eye(), value2: target), precision: 3);
    }
    [Fact]
    public void TurnKeepsTheEyeAndPitchStopsShortOfThePole() {
        var pose = WorldOperatorCamera.FromPose(eye: new Vector3(x: 1f, y: 2f, z: 3f), fieldOfViewRadians: 1f, pitchDegrees: 0f, yawDegrees: 10f);
        var turned = pose.Turn(pitchDegrees: 500f, yawDegrees: 350f);

        AssertNear(expected: pose.Eye(), actual: turned.Eye());
        Assert.Equal(expected: WorldOperatorCamera.MaxPitchRadians, actual: turned.PitchRadians);
        Assert.InRange(actual: turned.YawRadians, low: -MathF.PI, high: MathF.PI);
        Assert.Equal(expected: 0f, actual: turned.YawRadians, precision: 4);
    }
    [Fact]
    public void MoveFollowsTheCameraAxes() {
        var pose = WorldOperatorCamera.FromPose(eye: Vector3.Zero, fieldOfViewRadians: 1f, pitchDegrees: 0f, yawDegrees: 90f);

        // Facing -X: forward is -X, right is -Z, up is +Y.
        AssertNear(expected: new Vector3(x: -2f, y: 0f, z: 0f), actual: pose.Move(forward: 2f, right: 0f, up: 0f).Eye());
        AssertNear(expected: new Vector3(x: 0f, y: 0f, z: -3f), actual: pose.Move(forward: 0f, right: 3f, up: 0f).Eye());
        AssertNear(expected: new Vector3(x: 0f, y: 4f, z: 0f), actual: pose.Move(forward: 0f, right: 0f, up: 4f).Eye());
    }
    [Fact]
    public void CoincidentLookIsRefusedAndTheLensChanges() {
        var eye = new Vector3(x: 1f, y: 1f, z: 1f);

        Assert.Null(@object: WorldOperatorCamera.FromLookAt(eye: eye, fieldOfViewRadians: 1f, target: eye));
        Assert.Equal(
            expected: 0.5f,
            actual: WorldOperatorCamera.FromPose(eye: eye, fieldOfViewRadians: 1f, pitchDegrees: 0f, yawDegrees: 0f).WithFieldOfView(fieldOfViewRadians: 0.5f).FieldOfViewRadians
        );
    }
    [Fact]
    public void PoseCrossesTheCompositionWireUnchanged() {
        var pose = WorldOperatorCamera.FromOrbit(
            distance: 12.5f,
            fieldOfViewRadians: 0.8f,
            pitchDegrees: 25f,
            target: new Vector3(x: 1f, y: 2f, z: 3f),
            yawDegrees: -40f
        );

        foreach (var composition in new WorldComposition[] {
            new WorldComposition.SetOperatorCamera(Pose: pose),
            new WorldComposition.SetOperatorCamera(Pose: null),
        }) {
            Assert.True(
                condition: WorldSubmissionCodec.TryEncodeComposition(
                    bytes: out var bytes,
                    composition: composition,
                    failure: out var encodeFailure
                ),
                userMessage: $"encode refused: {encodeFailure}"
            );
            Assert.True(
                condition: WorldSubmissionCodec.TryDecodeComposition(
                    bytes: bytes,
                    composition: out var decoded,
                    failure: out var decodeFailure
                ),
                userMessage: $"decode refused: {decodeFailure}"
            );
            Assert.Equal(
                actual: decoded,
                expected: composition
            );
        }
    }
}
