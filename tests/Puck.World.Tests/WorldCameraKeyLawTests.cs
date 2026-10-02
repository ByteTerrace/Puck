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
    private static BindableScalar Keys(float first, float second) => new(keys: new WorldKeys<BindableScalar>(Clock: "day", Keys: [new(0d, first), new(4d, second)]));
    private static WorldCameraProgram Program(string name, params WorldCameraProgramOp[] operations) => new(Name: name, Operations: operations, Version: WorldCameraProgram.CurrentVersion);
    private static WorldCamera Camera(WorldCameraProgram program) => new(program.Name, program, 64u, 64u);
    private static WorldStateMirror Mirror(WorldDefinition definition) {
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        mirror.Install(engineTick: 0UL, tick: 0UL);
        mirror.Refresh(stamp: new WorldStateStamp(1UL, (2UL * EngineTicks.PerSecond), ReadOnlyMemory<int>.Empty, false));
        mirror.Apply(fraction: 1f);
        return mirror;
    }
    private static (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(WorldCameraProgram program, WorldDefinition definition) =>
        WorldCameraRigCompiler.Compile(program, definition, Mirror(definition: definition)).Resolve(anchor: new SdfAnchor(Vector3.Zero, Quaternion.Identity), clock: default);

    [Fact]
    public void Keyed_orbit_and_lens_angles_use_the_mirror_tick_and_short_arc() {
        var program = Program("orbit", new WorldCameraProgramOp.Orbit(5f, Keys(first: ((350f * MathF.PI) / 180f), second: ((10f * MathF.PI) / 180f)), 0f),
            new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: Keys(first: 0.5f, second: 1.5f)));
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 8d)]),
            CamerasRaw = [Camera(program: program)],
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
        var (eye, _, fov) = Resolve(definition: definition, program: program);
        Assert.InRange(Vector3.Distance(value1: OrbitRig.Offset(distance: 5f, pitch: 0f, yaw: 0f), value2: eye), 0f, 0.00001f);
        Assert.Equal(actual: fov, expected: 1f, precision: 6);
    }
    [Fact]
    public void A_keyed_camera_blend_resolves_as_an_ordinary_scalar() {
        var left = Program("left", new WorldCameraProgramOp.Offset(new Vector3(x: 1f, y: 0f, z: 0f)), new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: 1f));
        var right = Program("right", new WorldCameraProgramOp.Offset(new Vector3(x: 3f, y: 0f, z: 0f)), new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: 1f));
        var blend = Program("blend", new WorldCameraProgramOp.Blend(A: "left", B: "right", Weight: Keys(first: 0f, second: 1f)));
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 8d)]),
            CamerasRaw = [Camera(program: left), Camera(program: right), Camera(program: blend)],
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
        Assert.Equal(new Vector3(x: 2f, y: 0f, z: 0f), Resolve(definition: definition, program: blend).Eye);
    }
}
