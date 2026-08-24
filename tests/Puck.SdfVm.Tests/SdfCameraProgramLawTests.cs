using System.Numerics;

using Xunit;

using Puck.SdfVm.Views;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for the camera-program evaluator. The pinning law states the OLD closed chase rig's arithmetic directly, in
/// this file, and requires the transcribed program to reproduce it bit for bit on fixed inputs — a basis inheritor
/// that reframes is a failure, not a diff to re-record. Nothing here constructs a document: the evaluator's whole
/// contract is plain poses, plain scalars, and a look sample.
/// </summary>
public sealed class SdfCameraProgramLawTests {
    // The framing standard.world.json's seatRig authors, transcribed op for op.
    private const float ChaseDistance = 5.4626001f;
    private const float ChaseFov = 0.9599311f;
    private const float ChasePitch = 0.4145069f;
    private const float ChaseYaw = 0f;

    private static readonly SdfCameraDynamics s_chaseDynamics = new(Frequency: 0.9549f, Damping: 1f, Response: 1f);

    private static readonly Vector3 s_chaseAimOffset = new(x: 0f, y: 1f, z: 0f);
    private static readonly Vector3 s_chasePivotOffset = Vector3.Zero;

    // The old closed rig: an Orbit motion about the anchor's pivot, an Anchor aim in the anchor's own axes, a fixed
    // lens. The live seat delta was baked into the orbit's own yaw/pitch before the rig resolved.
    private static (Vector3 Eye, Vector3 Target, float FovRadians) ResolveClosedChase(in SdfAnchor anchor, float liveYaw, float livePitch) {
        var pivot = (anchor.Position + s_chasePivotOffset);
        var eye = (pivot + OrbitRig.Offset(
            distance: ChaseDistance,
            pitch: (ChasePitch + livePitch),
            yaw: (ChaseYaw + liveYaw)
        ));
        var target = (anchor.Position + Vector3.Transform(
            rotation: anchor.Orientation,
            value: s_chaseAimOffset
        ));

        return (eye, target, ChaseFov);
    }
    private static SdfCameraProgramSet ChaseProgram() => new(Programs: [
        new SdfCameraProgram(
            Name: "seatChase",
            Operations: [
                new SdfCameraOp.Orbit(
                    AppliesLook: true,
                    Distance: SdfCameraScalar.FromLiteral(value: ChaseDistance),
                    Pitch: SdfCameraScalar.FromLiteral(value: ChasePitch),
                    PivotOffset: s_chasePivotOffset,
                    Yaw: SdfCameraScalar.FromLiteral(value: ChaseYaw)
                ),
                new SdfCameraOp.LookAt(
                    FocusDistance: SdfCameraScalar.FromLiteral(value: 0f),
                    SubjectSlot: SdfCameraProgram.ReferenceSubject,
                    TargetOffset: s_chaseAimOffset,
                    WorldAxes: false
                ),
                new SdfCameraOp.Fov(FieldOfViewRadians: SdfCameraScalar.FromLiteral(value: ChaseFov)),
                new SdfCameraOp.Dynamics(Value: s_chaseDynamics),
            ]
        ),
    ]);
    private static SdfAnchor[] Anchors() => [
        new(Position: Vector3.Zero, Orientation: Quaternion.Identity),
        new(Position: new Vector3(x: 3.5f, y: -2f, z: 11.25f), Orientation: Quaternion.CreateFromYawPitchRoll(yaw: 0.7f, pitch: 0f, roll: 0f)),
        new(Position: new Vector3(x: -140f, y: 62.5f, z: 0.125f), Orientation: Quaternion.CreateFromYawPitchRoll(yaw: -2.3f, pitch: 0.4f, roll: 0.9f)),
    ];

    [Fact]
    public void TranscribedChaseProgram_FramesIdenticallyToTheClosedChaseRig() {
        var rig = new SdfCameraProgramRig(programs: ChaseProgram(), scalarCount: 0, subjectCount: 0);
        var clock = new SdfCameraClock(AuthoritativeTick: 17UL, PresentationSeconds: 0.5f);

        foreach (var anchor in Anchors()) {
            foreach (var liveYaw in new[] { 0f, 0.25f, -1.75f, 3.0f }) {
                foreach (var livePitch in new[] { 0f, -0.35f, 0.4f, 0.7854f }) {
                    rig.Look = new SdfCameraLook(Pitch: livePitch, Yaw: liveYaw);

                    var expected = ResolveClosedChase(anchor: in anchor, liveYaw: liveYaw, livePitch: livePitch);
                    var actual = rig.ResolvePose(anchor: in anchor, clock: in clock);

                    Assert.Equal(expected: expected.Eye, actual: actual.Eye);
                    Assert.Equal(expected: expected.Target, actual: actual.Target);
                    Assert.Equal(expected: expected.FovRadians, actual: actual.FovRadians);
                    Assert.Equal(expected: s_chaseDynamics, actual: actual.Dynamics);
                }
            }
        }
    }
    // A non-interactive program renders its authored angles: the same op list with AppliesLook cleared ignores the
    // look sample entirely, which is what separates a named camera from a seat rig.
    [Fact]
    public void OrbitWithoutAppliesLook_IgnoresTheLookSample() {
        var program = ChaseProgram();
        var operations = new List<SdfCameraOp>(collection: program.Programs[0].Operations);

        operations[0] = ((SdfCameraOp.Orbit)operations[0] with { AppliesLook = false });

        var rig = new SdfCameraProgramRig(
            programs: new SdfCameraProgramSet(Programs: [(program.Programs[0] with { Operations = operations })]),
            scalarCount: 0,
            subjectCount: 0
        );
        var anchor = new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity);
        var clock = new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f);
        var still = rig.ResolvePose(anchor: in anchor, clock: in clock);

        rig.Look = new SdfCameraLook(Pitch: 0.9f, Yaw: 2.1f);

        Assert.Equal(expected: still.Eye, actual: rig.ResolvePose(anchor: in anchor, clock: in clock).Eye);

        // Control: the same program WITH AppliesLook does move under the identical sample.
        var interactive = new SdfCameraProgramRig(programs: ChaseProgram(), scalarCount: 0, subjectCount: 0) {
            Look = new SdfCameraLook(Pitch: 0.9f, Yaw: 2.1f),
        };

        Assert.NotEqual(expected: still.Eye, actual: interactive.ResolvePose(anchor: in anchor, clock: in clock).Eye);
    }
    [Fact]
    public void ClampPitch_BoundsTheSummedOrbitPitch() {
        var program = ChaseProgram();
        var operations = new List<SdfCameraOp> {
            new SdfCameraOp.ClampPitch(
                MaxPitch: SdfCameraScalar.FromLiteral(value: 0.5f),
                MinPitch: SdfCameraScalar.FromLiteral(value: -0.5f)
            ),
        };

        operations.AddRange(collection: program.Programs[0].Operations);

        var clamped = new SdfCameraProgramRig(
            programs: new SdfCameraProgramSet(Programs: [(program.Programs[0] with { Operations = operations })]),
            scalarCount: 0,
            subjectCount: 0
        ) {
            Look = new SdfCameraLook(Pitch: 4f, Yaw: 0f),
        };
        var anchor = new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity);
        var clock = new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f);

        Assert.Equal(
            actual: clamped.ResolvePose(anchor: in anchor, clock: in clock).Eye,
            expected: (s_chasePivotOffset + OrbitRig.Offset(
                distance: ChaseDistance,
                pitch: 0.5f,
                yaw: ChaseYaw
            ))
        );

        // Control: an unclamped program takes the whole summed pitch.
        var unclamped = new SdfCameraProgramRig(programs: ChaseProgram(), scalarCount: 0, subjectCount: 0) {
            Look = new SdfCameraLook(Pitch: 0.05f, Yaw: 0f),
        };

        Assert.Equal(
            actual: unclamped.ResolvePose(anchor: in anchor, clock: in clock).Eye,
            expected: (s_chasePivotOffset + OrbitRig.Offset(
                distance: ChaseDistance,
                pitch: (ChasePitch + 0.05f),
                yaw: ChaseYaw
            ))
        );
    }
    // A subject slot is a plain pose the host refilled this frame: an anchor op reseats the eye on it, and a lookAt
    // op aims at it — neither needs to know what the host resolved it FROM.
    [Fact]
    public void SubjectSlots_ReseatTheEyeAndTheAim() {
        var rig = new SdfCameraProgramRig(
            programs: new SdfCameraProgramSet(Programs: [
                new SdfCameraProgram(
                    Name: "framed",
                    Operations: [
                        new SdfCameraOp.Anchor(SubjectSlot: 0),
                        new SdfCameraOp.LookAt(
                            FocusDistance: SdfCameraScalar.FromLiteral(value: 0f),
                            SubjectSlot: 1,
                            TargetOffset: Vector3.Zero,
                            WorldAxes: true
                        ),
                        new SdfCameraOp.Fov(FieldOfViewRadians: SdfCameraScalar.FromLiteral(value: 0.6f)),
                    ]
                ),
            ]),
            scalarCount: 0,
            subjectCount: 2
        );

        rig.Subjects[0] = new SdfAnchor(Position: new Vector3(x: 0f, y: 45f, z: 18f), Orientation: Quaternion.Identity);
        rig.Subjects[1] = new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity);

        var pose = rig.ResolvePose(
            anchor: new SdfAnchor(Position: new Vector3(value: 999f), Orientation: Quaternion.Identity),
            clock: new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f)
        );

        Assert.Equal(expected: new Vector3(x: 0f, y: 45f, z: 18f), actual: pose.Eye);
        Assert.Equal(expected: Vector3.Zero, actual: pose.Target);
        Assert.Equal(expected: 0.6f, actual: pose.FovRadians);
    }
    // A slot-backed scalar reads the frame buffer; a literal never does. This is the whole binding seam: the host
    // resolves an authored binding into the slot and the evaluator stays document-blind.
    [Fact]
    public void ScalarSlots_ReadTheFrameBuffer() {
        var rig = new SdfCameraProgramRig(
            programs: new SdfCameraProgramSet(Programs: [
                new SdfCameraProgram(
                    Name: "bound",
                    Operations: [new SdfCameraOp.Fov(FieldOfViewRadians: SdfCameraScalar.FromSlot(fallback: 0.1f, slot: 0))]
                ),
            ]),
            scalarCount: 1,
            subjectCount: 0
        );
        var anchor = new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity);
        var clock = new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f);

        rig.Scalars[0] = 1.25f;

        Assert.Equal(expected: 1.25f, actual: rig.ResolvePose(anchor: in anchor, clock: in clock).FovRadians);

        rig.Scalars[0] = 0.75f;

        Assert.Equal(expected: 0.75f, actual: rig.ResolvePose(anchor: in anchor, clock: in clock).FovRadians);
    }
    [Fact]
    public void Blend_InterpolatesEyeTargetFovAndDynamics() {
        var aDynamics = new SdfCameraDynamics(Frequency: 2f, Damping: 1f, Response: 0f);
        var bDynamics = new SdfCameraDynamics(Frequency: 6f, Damping: 1f, Response: 0f);
        var a = new SdfCameraProgram(
            Name: "a",
            Operations: [
                new SdfCameraOp.Offset(
                    Scale: SdfCameraScalar.FromLiteral(value: 1f),
                    Value: new Vector3(x: 0f, y: 0f, z: 10f),
                    WorldAxes: true
                ),
                new SdfCameraOp.LookAt(
                    FocusDistance: SdfCameraScalar.FromLiteral(value: 0f),
                    SubjectSlot: SdfCameraProgram.ReferenceSubject,
                    TargetOffset: Vector3.Zero,
                    WorldAxes: true
                ),
                new SdfCameraOp.Fov(FieldOfViewRadians: SdfCameraScalar.FromLiteral(value: 1f)),
                new SdfCameraOp.Dynamics(Value: aDynamics),
            ]
        );
        var b = (a with {
            Name = "b",
            Operations = [
                new SdfCameraOp.Offset(
                    Scale: SdfCameraScalar.FromLiteral(value: 1f),
                    Value: new Vector3(x: 0f, y: 0f, z: 20f),
                    WorldAxes: true
                ),
                new SdfCameraOp.LookAt(
                    FocusDistance: SdfCameraScalar.FromLiteral(value: 0f),
                    SubjectSlot: SdfCameraProgram.ReferenceSubject,
                    TargetOffset: new Vector3(x: 0f, y: 4f, z: 0f),
                    WorldAxes: true
                ),
                new SdfCameraOp.Fov(FieldOfViewRadians: SdfCameraScalar.FromLiteral(value: 3f)),
                new SdfCameraOp.Dynamics(Value: bDynamics),
            ],
        });
        var rig = new SdfCameraProgramRig(
            programs: new SdfCameraProgramSet(Programs: [
                new SdfCameraProgram(
                    Name: "root",
                    Operations: [
                        new SdfCameraOp.Blend(
                            ProgramA: 1,
                            ProgramB: 2,
                            Weight: SdfCameraScalar.FromSlot(fallback: 0f, slot: 0)
                        ),
                    ]
                ),
                a,
                b,
            ]),
            scalarCount: 1,
            subjectCount: 0
        );
        var anchor = new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity);
        var clock = new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f);

        rig.Scalars[0] = 0.5f;

        var blended = rig.ResolvePose(anchor: in anchor, clock: in clock);

        Assert.Equal(expected: new Vector3(x: 0f, y: 0f, z: 15f), actual: blended.Eye);
        Assert.Equal(expected: new Vector3(x: 0f, y: 2f, z: 0f), actual: blended.Target);
        Assert.Equal(expected: 2f, actual: blended.FovRadians);
        Assert.Equal(expected: new SdfCameraDynamics(Frequency: 4f, Damping: 1f, Response: 0f), actual: blended.Dynamics);

        // Controls: the endpoints resolve their own sub-program exactly.
        rig.Scalars[0] = 0f;

        Assert.Equal(expected: new Vector3(x: 0f, y: 0f, z: 10f), actual: rig.ResolvePose(anchor: in anchor, clock: in clock).Eye);

        rig.Scalars[0] = 1f;

        Assert.Equal(expected: 3f, actual: rig.ResolvePose(anchor: in anchor, clock: in clock).FovRadians);
    }
    // A blend naming an index the set does not carry resolves the degenerate reference framing rather than throwing:
    // a compiler that cannot resolve a name must never hand this evaluator a crash.
    [Fact]
    public void Blend_WithAnIndexOutsideTheSet_ResolvesTheReferenceFraming() {
        var rig = new SdfCameraProgramRig(
            programs: new SdfCameraProgramSet(Programs: [
                new SdfCameraProgram(
                    Name: "root",
                    Operations: [
                        new SdfCameraOp.Blend(
                            ProgramA: 7,
                            ProgramB: 9,
                            Weight: SdfCameraScalar.FromLiteral(value: 0.5f)
                        ),
                    ]
                ),
            ]),
            scalarCount: 0,
            subjectCount: 0
        );
        var anchor = new SdfAnchor(Position: new Vector3(x: 1f, y: 2f, z: 3f), Orientation: Quaternion.Identity);
        var pose = rig.ResolvePose(
            anchor: in anchor,
            clock: new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f)
        );

        Assert.Equal(expected: anchor.Position, actual: pose.Eye);
        Assert.Equal(expected: OrbitRig.DefaultFieldOfViewRadians, actual: pose.FovRadians);
    }
    // A cycle the compiler admitted costs a bounded walk, never a stack overflow.
    [Fact]
    public void Blend_ThatCyclesBackOnItself_Terminates() {
        var rig = new SdfCameraProgramRig(
            programs: new SdfCameraProgramSet(Programs: [
                new SdfCameraProgram(
                    Name: "root",
                    Operations: [
                        new SdfCameraOp.Blend(
                            ProgramA: 0,
                            ProgramB: 0,
                            Weight: SdfCameraScalar.FromLiteral(value: 0.5f)
                        ),
                    ]
                ),
            ]),
            scalarCount: 0,
            subjectCount: 0
        );

        _ = rig.ResolvePose(
            anchor: new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity),
            clock: new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f)
        );
    }
    // A program authoring no lookAt aims along the current subject's forward at the default focus distance.
    [Fact]
    public void ProgramWithoutALookAt_AimsAlongTheSubjectsForward() {
        var rig = new SdfCameraProgramRig(
            programs: new SdfCameraProgramSet(Programs: [
                new SdfCameraProgram(
                    Name: "bare",
                    Operations: [new SdfCameraOp.Fov(FieldOfViewRadians: SdfCameraScalar.FromLiteral(value: 1f))]
                ),
            ]),
            scalarCount: 0,
            subjectCount: 0
        );
        var anchor = new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity);
        var pose = rig.ResolvePose(
            anchor: in anchor,
            clock: new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f)
        );

        Assert.Equal(expected: (-Vector3.UnitZ * SdfCameraProgramEvaluator.DefaultFocusDistance), actual: pose.Target);
    }
    // The boom ease: None passes the pose through untouched, a live response seeds on the first frame and lags the
    // boom after, and the target is never moved.
    [Fact]
    public void BoomFollower_SeedsThenLagsOnlyTheBoom() {
        var follower = new SdfCameraBoomFollower();
        var live = new SdfCameraDynamics(Frequency: 0.9549297f, Damping: 1f, Response: 0f);
        var eye = new Vector3(x: 0f, y: 0f, z: 10f);
        var target = Vector3.Zero;

        follower.Apply(dynamics: SdfCameraDynamics.None, deltaSeconds: 1f, eye: ref eye, target: ref target);

        Assert.Equal(expected: new Vector3(x: 0f, y: 0f, z: 10f), actual: eye);
        Assert.False(condition: follower.Seeded);

        follower.Apply(dynamics: in live, deltaSeconds: 1f, eye: ref eye, target: ref target);

        Assert.True(condition: follower.Seeded);
        Assert.Equal(expected: new Vector3(x: 0f, y: 0f, z: 10f), actual: eye);

        var movedEye = new Vector3(x: 0f, y: 0f, z: 30f);
        var movedTarget = new Vector3(x: 5f, y: 0f, z: 0f);

        follower.Apply(dynamics: in live, deltaSeconds: 1f, eye: ref movedEye, target: ref movedTarget);

        Assert.Equal(expected: new Vector3(x: 5f, y: 0f, z: 0f), actual: movedTarget);
        Assert.True(condition: ((movedEye - movedTarget).Z < 30f));
        Assert.True(condition: ((movedEye - movedTarget).Z > 10f));

        follower.Reseed();

        Assert.False(condition: follower.Seeded);
    }
}
