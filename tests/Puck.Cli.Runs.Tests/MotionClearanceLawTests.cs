using System.Numerics;
using Puck.Cli.Motion;

using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the camera clearance check of <c>puck motion</c>, over the debug world's own studio. The four
/// <c>motion</c> cameras, resolved from their rigs against body 0's pose, stand in open air with a clear line to the aim
/// point at the grid lane's start and on the course; a pose against a wall puts an eye in it and the check names the
/// camera, the tick and what blocked it; a pole between an eye and the body blocks the line and is named.
/// </summary>
public sealed class MotionClearanceLawTests {
    private static readonly string[] MotionCameras = ["motion-profile", "motion-front", "motion-feet", "motion-turn"];
    private static readonly string[] CourseCameras = ["motion-profile", "motion-rear", "motion-feet", "motion-turn"];
    private static readonly Lazy<MotionClearance> Studio = new(valueFactory: static () => {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));
        Assert.True(condition: MotionClearance.TryCreate(clearance: out var clearance, error: out var error, worldPath: Path.Combine(path1: root, path2: "worlds/debug")), userMessage: error);

        return clearance!;
    });

    private static string Where(float x, float y, float z, int yaw = 0) => string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"[body.where: body:0 pos=({x:0.00}, {y:0.00}, {z:0.00}) yaw={yaw}° pitch=0° roll=0° facts=grounded]");
    private static MotionPlannedScenario Planned() {
        var scenario = new MotionScenario(Name: "probe", Note: string.Empty, Form: MotionForm.Suited, Pose: new MotionPose(Spawn: null, X: 0d, Y: 0d, YawDegrees: 0d, Z: 0d), Settle: 10, Tail: 0, Steps: [], Capture: [0]);

        return new MotionPlannedScenario(ArmTick: 0UL, Lines: [], ResetTick: 0UL, Scenario: scenario, Stills: [], ZeroTick: 10UL);
    }
    private static MotionStillResult Still(string where, int offset = 0) => new(Contacts: string.Empty, Hash: null, Offset: offset, Pose: null, Rig: null, Slots: new Dictionary<string, string>(), Tick: (10UL + ((ulong)offset)), Where: where);

    [InlineData(32f, -30f)]
    [InlineData(32f, -38f)]
    [InlineData(32f, -50f)]
    [InlineData(48f, -41f)]
    [Theory]
    public void TheMotionCamerasAreClearOnTheOpenFloor(float x, float z) {
        var problems = Studio.Value.Check(cameras: MotionCameras, scenario: Planned(), stills: [Still(where: Where(x: x, y: 0.01f, z: z))]);

        Assert.Empty(collection: problems);
    }
    [Fact]
    public void TheCourseCamerasAreClearAtEveryCourseScenarioStart() {
        foreach (var z in new[] { -30f, -37f, -44.5f, -55.4f, -63f }) {
            var problems = Studio.Value.Check(cameras: CourseCameras, scenario: Planned(), stills: [Still(where: Where(x: 60f, y: 0.01f, z: z))]);

            Assert.True(condition: (problems.Count == 0), userMessage: string.Join(separator: Environment.NewLine, values: problems));
        }
    }
    [Fact]
    public void AnEyeInAWallIsNamedByScenarioTickCameraAndWhatBlockedIt() {
        // The back wall stands at z -24: 2.5 m from the wall the world-axes eye, 3.8 m behind the body, is outside the room and sees her through it.
        var problems = Studio.Value.Check(cameras: MotionCameras, scenario: Planned(), stills: [Still(offset: 7, where: Where(x: 32f, y: 0.01f, z: -26.5f))]);
        var turn = Assert.Single(collection: problems, predicate: static problem => problem.Contains(comparisonType: StringComparison.Ordinal, value: "'motion-turn'"));

        Assert.StartsWith(actualString: turn, expectedStartString: "scenario 'probe' tick 17 (t+7): camera 'motion-turn' is blocked: ");
        Assert.Contains(actualString: turn, expectedSubstring: "avatar-studio");
    }
    [Fact]
    public void APoleBetweenAnEyeAndTheBodyBlocksTheLine() {
        // The profile eye stands 5.2 m to the body's left: a body at x 40.5 puts it 0.25 m from the pole at x 35, z -38.
        Assert.True(condition: Studio.Value.TryResolve(camera: "motion-profile", eye: out var eye, position: new Vector3(x: 40.5f, y: 0.01f, z: -38f), target: out var target, yawDegrees: 0d));

        var why = Studio.Value.Blocked(eye: eye, target: target);

        Assert.NotNull(@object: why);
        Assert.Contains(actualString: why, expectedSubstring: "studio-grid");
    }
    [Fact]
    public void ACameraTheWorldDoesNotHaveAndAMissingPoseAreNamedNotSkipped() {
        var missing = Studio.Value.Check(cameras: ["no-such-camera"], scenario: Planned(), stills: [Still(where: Where(x: 32f, y: 0.01f, z: -38f))]);

        Assert.Contains(expectedSubstring: "no camera program", actualString: Assert.Single(collection: missing));

        var unread = Studio.Value.Check(cameras: MotionCameras, scenario: Planned(), stills: [Still(where: string.Empty)]);

        Assert.Contains(expectedSubstring: "pose was not read", actualString: Assert.Single(collection: unread));
        Assert.False(condition: Studio.Value.TryResolve(camera: "no-such-camera", eye: out _, position: default, target: out _, yawDegrees: 0d));
    }
}
