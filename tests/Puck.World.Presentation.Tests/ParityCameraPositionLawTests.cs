using System.Numerics;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>The parity stations author absolute camera eyes that both the rendered rig and the server-side
/// capture inside check resolve. A relative offset from the default origin renders at the same position but leaves
/// the capture's document-only check without an absolute anchor.</summary>
public sealed class ParityCameraPositionLawTests {
    [InlineData("sky-cam", 0f, 80f, 0f)]
    [InlineData("materials-cam", 0f, 1.9f, 12f)]
    [InlineData("lattice-cam", 288f, 6f, -10f)]
    [InlineData("noise-cam", 400f, 1f, -4f)]
    [InlineData("vocabulary-cam", 604f, 9f, -26f)]
    [Theory]
    public void EveryStationInsideCheckUsesItsRenderedEye(string cameraName, float x, float y, float z) {
        var authored = AuthoredGameFixtures.Load(relativePath: "tests/Puck.Parity/parity.puck");
        var camera = authored.Cameras.Single(predicate: candidate => (candidate.Name == cameraName));
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            CamerasRaw = [camera],
            ViewsRaw = basis.Views with {
                Layouts = [new WorldViewLayout(Name: "station", Slots: [new WorldViewSlot(Camera: cameraName)])],
            },
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var scheduler = new WorldCaptureScheduler(
            backend: "vulkan", captureTarget: null, directory: string.Empty,
            server: fixture.Server, worldFile: "tests/Puck.Parity/parity.puck");

        Assert.True(condition: scheduler.TryResolveCameraPosition(position: out var position, reason: out var reason),
            userMessage: reason);
        Assert.Equal(expected: new Vector3(x: x, y: y, z: z), actual: position);

        var program = camera.Rig;
        var origin = new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity);
        var rendered = WorldCameraRigCompiler.Compile(
            definition: definition, domains: new WorldValueDomainGuard(),
            mirror: ClientFixtures.StateMirror(definition: definition), program: program).Resolve(
                anchor: in origin, clock: new SdfCameraClock(AuthoritativeTick: 0UL, PresentationSeconds: 0f));

        Assert.Equal(actual: rendered.Eye, expected: position);
        Assert.Equal(expected: Assert.IsType<WorldCameraSubject.WorldPoint>(@object: program.LookAtOp!.Subject).Point.Value,
            actual: rendered.Target);
        Assert.Equal(expected: program.Operations.OfType<WorldCameraProgramOp.FieldOfView>().Single().FieldOfViewRadians.Literal!.Value,
            actual: rendered.FovRadians);
    }
}
