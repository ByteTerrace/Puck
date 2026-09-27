using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Commands;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance.Queries;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a window shows what a traveller at the eye would see through the door. For the <c>portal-window</c>
/// canary's documents, <see cref="WorldWindowFrustumFit.TryResolveApertures"/> resolves the door's two apertures, the
/// pair <c>WorldScreenBinder.UpdateWindowCameras</c> fits every frame, and the camera
/// <see cref="WorldWindowFrustumFit.TryFitWindow"/> fits from an eye casts, through the image point at the face's
/// <c>(x, y)</c>, the isometry's image of the ray from that eye through the same point of the source face: the rays
/// pass through the mapped face point (a mirrored fit passes through its reflection instead), and cast through
/// <see cref="SourceRay.Through"/> they carry the frustum's shear. Two eyes frame the destination differently: its
/// marker appears on the side of the glass the eye moved toward, and the session view the binder composes frames the
/// marker at exactly that image point.
/// </summary>
public sealed class WorldWindowFrustumFitLawTests {
    private const string Destination = "tests/Puck.World.Canaries/portal-window/beyond.world.json";
    private const string Local = "tests/Puck.World.Canaries/portal-window/fixture.world.json";
    private const float Tolerance = 2e-3f;

    // The destination's marker: a ball of radius 0.5 six units behind the arch.
    private static readonly Vector3 Marker = new(x: 0f, y: 1.5f, z: -6f);
    private static readonly Vector3[] Eyes = [
        new(x: 1f, y: 1.6f, z: 9f),
        new(x: -1f, y: 1.6f, z: 9f),
        new(x: 0.4f, y: 2.1f, z: 3f),
    ];

    private static (WorldFaceGeometry Source, WorldFaceGeometry Destination) Apertures() {
        var local = AuthoredGameFixtures.Load(relativePath: Local);

        Assert.True(condition: WorldWindowFrustumFit.TryResolveApertures(
            counterpart: out var destination,
            destination: AuthoredGameFixtures.Load(relativePath: Destination),
            local: local,
            screenIndex: DoorScreen(local: local),
            source: out var source
        ));

        return (source, destination);
    }
    // The screen index the door's glass face is seated at.
    private static int DoorScreen(WorldDefinition local) => Assert.Single(collection: WorldFaceCatalog.For(definition: local).Rows).ScreenIndex;
    private static CameraSnapshot Fit(Vector3 eye) {
        var (source, destination) = Apertures();

        Assert.True(condition: WorldWindowFrustumFit.TryFitWindow(
            camera: out var camera,
            destination: destination,
            localEye: eye,
            source: source
        ));

        return camera;
    }
    // The point of a face at image coordinate (x, y): x across its Right, y down its Up, each in [0, 1].
    private static Vector3 FacePoint(WorldFaceGeometry face, float x, float y) =>
        ((face.Origin + ((((2f * x) - 1f) * face.HalfWidth) * face.Right)) + (((1f - (2f * y)) * face.HalfHeight) * face.Up));
    // Where a ray meets a face's plane.
    private static Vector3 OnPlane(SourceRay ray, WorldFaceGeometry face) {
        var origin = ray.Origin.ToVector3();
        var direction = ray.Direction.ToVector3();
        var t = (Vector3.Dot(
            vector1: (face.Origin - origin),
            vector2: face.Normal
        ) / Vector3.Dot(
            vector1: direction,
            vector2: face.Normal
        ));

        return (origin + (direction * t));
    }
    private static SourceRay Through(CameraSnapshot camera, float x, float y) => SourceRay.Through(
        camera: camera,
        image: new FixedVector2(
            X: FixedQ4816.FromDouble(value: x),
            Y: FixedQ4816.FromDouble(value: y)
        )
    );
    // The image point a camera shows a world point at, inverting SourceRay.Through's projection.
    private static Vector2 ImageOf(CameraSnapshot camera, Vector3 point) {
        var direction = (point - camera.Position);
        var forward = Vector3.Dot(
            vector1: direction,
            vector2: camera.Forward
        );
        var horizontal = ((Vector3.Dot(vector1: direction, vector2: camera.Right) / forward) - camera.FrustumOffset.X);
        var vertical = ((Vector3.Dot(vector1: direction, vector2: camera.Up) / forward) - camera.FrustumOffset.Y);

        return new Vector2(
            x: (((horizontal / (camera.AspectRatio * camera.TanHalfFieldOfView)) + 1f) * 0.5f),
            y: ((1f - (vertical / camera.TanHalfFieldOfView)) * 0.5f)
        );
    }

    [Fact]
    public void TheWindowCastsTheTravellersRaysThroughTheDoor() {
        var (source, destination) = Apertures();

        foreach (var eye in Eyes) {
            var camera = Fit(eye: eye);
            var mappedEye = WorldWindowProjectionMath.MapPoint(
                destination: destination,
                point: eye,
                source: source
            );

            Assert.True(
                condition: (Vector3.Distance(value1: camera.Position, value2: mappedEye) < Tolerance),
                userMessage: $"the window's eye {camera.Position} is not the mapped eye {mappedEye}"
            );

            for (var x = 0; (x <= 4); x++) {
                for (var y = 0; (y <= 4); y++) {
                    var through = OnPlane(
                        face: destination,
                        ray: Through(camera: camera, x: (x / 4f), y: (y / 4f))
                    );
                    var expected = WorldWindowProjectionMath.MapPoint(
                        destination: destination,
                        point: FacePoint(face: source, x: (x / 4f), y: (y / 4f)),
                        source: source
                    );

                    Assert.True(
                        condition: (Vector3.Distance(value1: through, value2: expected) < Tolerance),
                        userMessage: $"from eye {eye}, image ({x / 4f}, {y / 4f}) crosses the counterpart at {through}, not at the mapped face point {expected}"
                    );
                }
            }
        }
    }
    [Fact]
    public void TwoEyesFrameTheDestinationOnTheSideTheEyeMovedToward() {
        var right = ImageOf(camera: Fit(eye: Eyes[0]), point: Marker);
        var left = ImageOf(camera: Fit(eye: Eyes[1]), point: Marker);

        // Line from the eye (±1, 1.6, 9) to the marker, which the identity-mapped door shows at the same world point,
        // crosses the glass (z = 0, half width 1.2) at x = ±(1 − 9/15) = ±0.4: image x = (0.4 / 1.2 + 1) / 2.
        Assert.Equal(expected: (2f / 3f), actual: right.X, tolerance: Tolerance);
        Assert.Equal(expected: (1f / 3f), actual: left.X, tolerance: Tolerance);
        Assert.Equal(expected: right.Y, actual: left.Y, tolerance: Tolerance);
    }
    [Fact]
    public void AnEyeBehindTheGlassFitsNoWindow() {
        var (source, destination) = Apertures();

        Assert.False(condition: WorldWindowFrustumFit.TryFitWindow(
            camera: out _,
            destination: destination,
            localEye: new Vector3(x: 0f, y: 1.6f, z: -2f),
            source: source
        ));
    }
    [Fact]
    public void ADestinationWithoutTheCounterpartResolvesNoApertures() {
        var local = AuthoredGameFixtures.Load(relativePath: Local);

        Assert.False(condition: WorldWindowFrustumFit.TryResolveApertures(
            counterpart: out _,
            destination: AuthoredGameFixtures.Load(relativePath: "tests/Puck.World.Canaries/uploaded-sources/session.world.json"),
            local: local,
            screenIndex: DoorScreen(local: local),
            source: out _
        ));
        Assert.False(condition: WorldWindowFrustumFit.TryResolveApertures(
            counterpart: out _,
            destination: AuthoredGameFixtures.Load(relativePath: Destination),
            local: local,
            screenIndex: (DoorScreen(local: local) + 1),
            source: out _
        ));
    }
    [Fact]
    public void TheSessionViewFramesTheMarkerWhereTheWindowShowsIt() {
        var mirror = new WorldSessionMirror(placeholder: AuthoredGameFixtures.Load(relativePath: Destination));
        var emitter = new WorldSessionSceneEmitter(
            effectiveCameraName: null,
            mirror: mirror
        );
        var composition = new SdfCompositionFrameSource(
            dresser: emitter,
            emitters: [emitter]
        );

        foreach (var eye in Eyes[..2]) {
            var fitted = Fit(eye: eye);

            emitter.SetWindowCamera(camera: fitted);

            var frame = composition.CaptureFrame(
                deltaSeconds: 0f,
                height: 120,
                interpolationAlpha: 0f,
                width: 144
            );
            var camera = frame.Views[0].Camera;

            Assert.Equal(expected: fitted, actual: camera);

            var field = new SdfFieldEvaluator(program: frame.Program);
            var image = ImageOf(camera: camera, point: Marker);
            var ray = Through(camera: camera, x: image.X, y: image.Y);

            Assert.True(
                condition: field.Raycast(
                    dir: ray.Direction,
                    hit: out var hit,
                    maxDist: FixedQ4816.FromDouble(value: frame.FarDistance),
                    origin: FixedPosition.FromLocal(local: ray.Origin)
                ),
                userMessage: $"from eye {eye} the window's ray through the marker's image point {image} meets no surface"
            );
            // The marker's near surface, half a unit short of its centre along the ray.
            Assert.Equal(
                expected: (Vector3.Distance(value1: camera.Position, value2: Marker) - 0.5),
                actual: ((double)hit.Distance),
                tolerance: 0.02
            );
        }
    }
}
