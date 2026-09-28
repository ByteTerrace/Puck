using System.Numerics;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Camera operands use the same clock and typed interpolation as the schema's pure resolver.</summary>
public sealed class WorldCameraKeyLawTests {
    private static BindableScalar Keys(float first, float second) => new(new WorldKeys<BindableScalar>("day", [new(0d, first), new(4d, second)]));
    private static WorldCameraProgram Program(string name, params WorldCameraProgramOp[] operations) => new(Name: name, Version: WorldCameraProgram.CurrentVersion, Operations: operations);
    private static WorldCamera Camera(WorldCameraProgram program) => new(program.Name, program, 64u, 64u);
    private static WorldStateMirror Mirror(WorldDefinition definition) {
        var mirror = new WorldStateMirror(new WorldDocumentStateView(() => definition));
        mirror.Install(0UL, 0UL);
        mirror.Refresh(new WorldStateStamp(1UL, 2UL * EngineTicks.PerSecond, ReadOnlyMemory<int>.Empty, false));
        mirror.Apply(1f);
        return mirror;
    }
    private static (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(WorldCameraProgram program, WorldDefinition definition) =>
        WorldCameraRigCompiler.Compile(program, definition, Mirror(definition)).Resolve(new SdfAnchor(Vector3.Zero, Quaternion.Identity), default);

    [Fact]
    public void Keyed_orbit_and_lens_angles_use_the_mirror_tick_and_short_arc() {
        var program = Program("orbit", new WorldCameraProgramOp.Orbit(5f, Keys(350f * MathF.PI / 180f, 10f * MathF.PI / 180f), 0f),
            new WorldCameraProgramOp.FieldOfView(Keys(0.5f, 1.5f)));
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d)]), CamerasRaw = [Camera(program)],
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        var (eye, _, fov) = Resolve(program, definition);
        Assert.InRange(Vector3.Distance(OrbitRig.Offset(distance: 5f, pitch: 0f, yaw: 0f), eye), 0f, 0.00001f);
        Assert.Equal(1f, fov, 6);
    }

    [Fact]
    public void A_keyed_camera_blend_resolves_as_an_ordinary_scalar() {
        var left = Program("left", new WorldCameraProgramOp.Offset(new Vector3(1f, 0f, 0f)), new WorldCameraProgramOp.FieldOfView(1f));
        var right = Program("right", new WorldCameraProgramOp.Offset(new Vector3(3f, 0f, 0f)), new WorldCameraProgramOp.FieldOfView(1f));
        var blend = Program("blend", new WorldCameraProgramOp.Blend("left", "right", Keys(0f, 1f)));
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d)]), CamerasRaw = [Camera(left), Camera(right), Camera(blend)],
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        Assert.Equal(new Vector3(2f, 0f, 0f), Resolve(blend, definition).Eye);
    }
}
