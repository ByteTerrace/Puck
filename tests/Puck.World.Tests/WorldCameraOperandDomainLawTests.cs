using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Assets.Documents;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: every finite number a camera program's operand admits presents a finite camera. A keyed
/// operand blends between its keys without leaving the interval they span, a path fraction wraps (closed) or clamps
/// (open) in fraction space, so no finite operand reaches the curve as an infinite arc length, and the pose a rig
/// resolves builds a <see cref="CameraSnapshot"/>.
/// </summary>
public sealed class WorldCameraOperandDomainLawTests {
    private const string CurveName = "loop";
    private const float LoopRadius = 10f;

    private static readonly SdfAnchor Origin = new(
        Orientation: Quaternion.Identity,
        Position: Vector3.Zero
    );

    // Three 120-degree arcs close a circle of LoopRadius.
    private static WorldCurveRow Loop(bool closed) {
        static WorldCurveKnot Knot(int index) {
            var angle = (index * ((2f * MathF.PI) / 3f));

            return new WorldCurveKnot(
                Position: new DocumentVector3(
                    x: (-LoopRadius + (LoopRadius * MathF.Cos(x: angle))),
                    y: 0f,
                    z: (LoopRadius * MathF.Sin(x: angle))
                ),
                TangentYaw: MathF.IEEERemainder(
                    x: (angle + (MathF.PI / 2f)),
                    y: (2f * MathF.PI)
                ),
                Curvature: (1f / LoopRadius)
            );
        }

        return new WorldCurveRow(
            Name: CurveName,
            Knots: [Knot(index: 0), Knot(index: 1), Knot(index: 2)],
            Closed: closed
        );
    }
    private static WorldDefinition PathDocument(BindableScalar fraction, bool closed) => Fixtures.BuildDocument() with {
        CamerasRaw = [
            new WorldCamera(
                Anchor: null,
                Name: "probe",
                RenderHeight: 240u,
                RenderWidth: 320u,
                Rig: new WorldCameraProgram(
                    Name: "probe-rig",
                    Operations: [
                        new WorldCameraProgramOp.Path(Curve: CurveName, Fraction: fraction),
                        new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 1f)),
                    ],
                    Version: WorldCameraProgram.CurrentVersion
                )
            ),
        ],
        CurvesRaw = [Loop(closed: closed)],
        TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: "day", PeriodSeconds: 100d)]),
    };
    // The fraction halfway between two keys on a clock of 100 seconds: the keys sit at 0 and 50, the presentation at 25.
    private static BindableScalar Keyed(float first, float second) => new(keys: new WorldKeyTrack<float>(
        clock: "day",
        keys: [
            new WorldKey<float>(At: 0d, Ease: WorldEase.Linear, Value: first),
            new WorldKey<float>(At: 50d, Ease: WorldEase.Linear, Value: second),
        ]
    ));
    private static (Vector3 Eye, Vector3 Target, float FovRadians) Present(WorldDefinition definition) {
        var mirror = ClientFixtures.StateMirror(
            definition: definition,
            engineTick: (25UL * EngineTicks.PerSecond)
        );

        return WorldCameraRigCompiler.Compile(
            definition: definition,
            mirror: mirror,
            program: definition.Cameras[0].Rig
        ).Resolve(
            anchor: in Origin,
            clock: new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f)
        );
    }
    private static void AssertBuildsACamera((Vector3 Eye, Vector3 Target, float FovRadians) pose, string context) {
        Assert.True(condition: float.IsFinite(f: pose.Eye.X) && float.IsFinite(f: pose.Eye.Y) && float.IsFinite(f: pose.Eye.Z), userMessage: $"{context}: eye {pose.Eye}");
        Assert.True(condition: float.IsFinite(f: pose.Target.X) && float.IsFinite(f: pose.Target.Y) && float.IsFinite(f: pose.Target.Z), userMessage: $"{context}: target {pose.Target}");

        _ = CameraSnapshot.LookAt(
            fieldOfViewRadians: pose.FovRadians,
            position: pose.Eye,
            target: pose.Target,
            viewportHeight: 240u,
            viewportWidth: 320u
        );
    }

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void A_keyed_path_fraction_between_the_extreme_finite_keys_blends_to_their_midpoint(bool closed) {
        var extreme = PathDocument(closed: closed, fraction: Keyed(first: -3e38f, second: 3e38f));
        var origin = PathDocument(closed: closed, fraction: new BindableScalar(literal: 0f));

        // Both keys are finite, so the validator admits the document.
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: extreme, neighbours: null, reason: out var reason), userMessage: reason);

        var presented = Present(definition: extreme);

        AssertBuildsACamera(context: "keys -3e38 and 3e38 sampled halfway", pose: presented);
        // The halfway point of the two keys is zero: the curve's start, which the control rig presents exactly.
        Assert.Equal(expected: Present(definition: origin).Eye, actual: presented.Eye);
    }
    [InlineData(3e38f, true)]
    [InlineData(-3e38f, true)]
    [InlineData(3e38f, false)]
    [InlineData(-3e38f, false)]
    [Theory]
    public void A_finite_literal_path_fraction_presents_a_finite_camera(float fraction, bool closed) {
        var definition = PathDocument(closed: closed, fraction: new BindableScalar(literal: fraction));

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason), userMessage: reason);
        AssertBuildsACamera(
            context: $"fraction {fraction}, {(closed ? "closed" : "open")}",
            pose: Present(definition: definition)
        );
    }
    [InlineData(-1d)]
    [InlineData(0d)]
    [InlineData(4d)]
    [Theory]
    public void A_bound_field_of_view_outside_the_cameras_range_presents_a_camera_that_builds(double written) {
        var bound = Fixtures.BuildDocument() with {
            CamerasRaw = [
                new WorldCamera(
                    Anchor: null,
                    Name: "probe",
                    RenderHeight: 240u,
                    RenderWidth: 320u,
                    Rig: new WorldCameraProgram(
                        Name: "probe-rig",
                        Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(binding: $"state.{WorldValueDomainLawTests.Row}"))],
                        Version: WorldCameraProgram.CurrentVersion
                    )
                ),
            ],
        };
        var live = WorldValueDomainLawTests.WithRow(definition: bound, value: written);

        AssertBuildsACamera(
            context: $"field of view row written {written}",
            pose: Present(definition: live)
        );
    }
}
