using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Cameras;
using Puck.Commands;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance.Queries;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a window shows what a traveller at the eye would see through the door. For the <c>portal-window</c>
/// canary's documents, <see cref="WorldWindowFrustumFit.TryResolveApertures"/> resolves the door's two apertures and
/// <see cref="WorldPrototypeFacets"/> derives the glass the door's screen draws, the three frames
/// <c>WorldScreenBinder.FitWindow</c> fits every frame, and the camera
/// <see cref="WorldWindowFrustumFit.TryFitWindow"/> fits from an eye casts, through the image point at the glass's
/// <c>(x, y)</c>, the isometry's image of the ray from that eye through the same point of the glass: the rays pass
/// through the mapped glass point (a mirrored fit passes through its reflection instead), and cast through
/// <see cref="SourceRay.Through"/> they carry the frustum's shear. The glass shows the image edge to edge: its mapping
/// carries no bezel, so the point of the glass a ray meets is the image point the window renders there. Two eyes frame
/// the destination differently: its marker appears on the side of the glass the eye moved toward, and the session view
/// the binder composes frames the marker at exactly that image point. The window's near plane is the mapped glass, so
/// its rays start on the aperture: the destination's occluder, standing between the mapped eye and the glass, is never
/// met, and the marker beyond it is.
/// </summary>
public sealed class WorldWindowFrustumFitLawTests {
    internal const string Destination = "tests/Puck.World.Canaries/portal-window/beyond.world.json";

    private const string Local = "tests/Puck.World.Canaries/portal-window/fixture.world.json";
    private const float Tolerance = 2e-3f;

    // The destination's marker: a ball of radius 0.5 six units behind the arch.
    internal static readonly Vector3 Marker = new(x: 0f, y: 1.5f, z: -6f);

    // The destination's occluder: a slab four units in front of the arch, between the first two eyes, mapped, and the
    // arch's glass.
    private static readonly Vector3 Occluder = new(x: 0f, y: 1.5f, z: 4f);

    internal static readonly Vector3[] Eyes = [
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

    // The screen row the door's glass face derives, as the presenter hands it to the binder.
    internal static WorldScreen DoorRow() {
        var local = AuthoredGameFixtures.Load(relativePath: Local);
        var screen = DoorScreen(local: local);

        return Assert.Single(
            collection: WorldPrototypeFacets.Derive(
                definition: local,
                derivedFaceBase: WorldPrototypeFacets.DerivedFaceBase,
                derivedFaceScreens: local.Authoring.DerivedFaceScreens
            ).Faces,
            predicate: row => (row.Index == screen)
        );
    }

    // The screen index the door's glass face is seated at.
    private static int DoorScreen(WorldDefinition local) => Assert.Single(collection: WorldFaceCatalog.For(definition: local).Rows).ScreenIndex;

    internal static CameraSnapshot Fit(Vector3 eye) {
        var (source, destination) = Apertures();

        Assert.True(condition: WorldWindowFrustumFit.TryFitWindow(
            camera: out var camera,
            destination: destination,
            glass: WorldWindowFrustumFit.Glass(screen: DoorRow()),
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
        var glass = WorldWindowFrustumFit.Glass(screen: DoorRow());

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
                        face: new WorldFaceGeometry(
                            HalfHeight: glass.HalfHeight,
                            HalfWidth: glass.HalfWidth,
                            Normal: destination.Normal,
                            Origin: WorldWindowProjectionMath.MapPoint(destination: destination, point: glass.Origin, source: source),
                            Right: destination.Right,
                            Up: destination.Up
                        ),
                        ray: Through(camera: camera, x: (x / 4f), y: (y / 4f))
                    );
                    var expected = WorldWindowProjectionMath.MapPoint(
                        destination: destination,
                        point: FacePoint(face: glass, x: (x / 4f), y: (y / 4f)),
                        source: source
                    );

                    Assert.True(
                        condition: (Vector3.Distance(value1: through, value2: expected) < Tolerance),
                        userMessage: $"from eye {eye}, image ({(x / 4f)}, {(y / 4f)}) crosses the mapped glass at {through}, not at the mapped glass point {expected}"
                    );
                }
            }
        }
    }
    [Fact]
    public void TwoEyesFrameTheDestinationOnTheSideTheEyeMovedToward() {
        var glass = WorldWindowFrustumFit.Glass(screen: DoorRow());

        // The door maps every point to itself (its two faces stand at one origin, facing opposite ways), so the marker
        // shows where the line from the eye to it crosses the glass.
        foreach (var eye in Eyes[..2]) {
            var across = ((glass.Origin.Z - eye.Z) / (Marker.Z - eye.Z));
            var crossing = (eye + ((Marker - eye) * across));
            var image = ImageOf(camera: Fit(eye: eye), point: Marker);

            Assert.Equal(
                expected: ((((crossing.X - glass.Origin.X) / glass.HalfWidth) + 1f) * 0.5f),
                actual: image.X,
                tolerance: Tolerance
            );
            Assert.Equal(
                expected: ((1f - ((crossing.Y - glass.Origin.Y) / glass.HalfHeight)) * 0.5f),
                actual: image.Y,
                tolerance: Tolerance
            );
            // The eye one unit right of the door sees the marker right of the glass's centre, and the eye one unit
            // left sees it left.
            Assert.Equal(expected: MathF.Sign(x: eye.X), actual: MathF.Sign(x: (image.X - 0.5f)));
        }
    }
    [Fact]
    public void TheGlassShowsTheWindowEdgeToEdge() {
        var row = DoorRow();
        var glass = WorldWindowFrustumFit.Glass(screen: row);
        var mapping = WorldScreenMappings.Of(
            screen: row,
            source: SourceHandle.Instance(name: WorldViewNames.Session(screen: row.Index)),
            sourceHeight: 120,
            sourceWidth: 144
        );

        Assert.Null(@object: mapping.Warp);

        // A ray through the centre of pixel (px, py) of the glass meets that pixel of the image, the corners included.
        foreach (var (px, py) in ((ReadOnlySpan<(long, long)>)[(0L, 0L), (143L, 119L), (36L, 90L), (108L, 30L), (72L, 60L)])) {
            var point = FacePoint(face: glass, x: ((px + 0.5f) / 144f), y: ((py + 0.5f) / 120f));
            var hit = mapping.MapRay(ray: new SourceRay(
                Direction: FixedVector3.FromVector3(value: (point - Eyes[0])),
                Origin: FixedVector3.FromVector3(value: Eyes[0])
            ));

            Assert.True(condition: hit.IsOnSource);
            Assert.Equal(expected: (px, py), actual: (hit.PixelX, hit.PixelY));
        }
    }
    [Fact]
    public void AnEyeBehindTheGlassFitsNoWindow() {
        var (source, destination) = Apertures();

        Assert.False(condition: WorldWindowFrustumFit.TryFitWindow(
            camera: out _,
            destination: destination,
            glass: WorldWindowFrustumFit.Glass(screen: DoorRow()),
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

            emitter.SetWindowFit(fit: () => fitted);

            var frame = composition.CaptureFrame(
                deltaSeconds: 0f,
                height: 120,
                interpolationAlpha: 0f,
                width: 144
            );
            var camera = frame.Views[0].Camera;

            Assert.Equal(actual: camera, expected: fitted);

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
            // The marker's near surface, half a unit short of its centre along the ray, which starts on the glass.
            Assert.Equal(
                expected: (Vector3.Distance(value1: ray.Origin.ToVector3(), value2: Marker) - 0.5),
                actual: ((double)hit.Distance),
                tolerance: 0.02
            );
        }
    }
    // The window's near plane is its glass: along every corner ray, the camera's near distance reaches the mapped glass's
    // corner, so it equals the eye-to-aperture distance along that ray, and the ray SourceRay.Through casts through the
    // corner starts there. A camera with no near plane would start every ray at the mapped eye.
    [Fact]
    public void TheWindowsNearPlaneIsTheGlassAlongEveryCornerRay() {
        var (source, destination) = Apertures();
        var glass = WorldWindowFrustumFit.Glass(screen: DoorRow());

        foreach (var eye in Eyes) {
            var camera = Fit(eye: eye);

            Assert.True(condition: (camera.Near > 0f));

            foreach (var (x, y) in ((ReadOnlySpan<(float, float)>)[(0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f)])) {
                var corner = WorldWindowProjectionMath.MapPoint(
                    destination: destination,
                    point: FacePoint(face: glass, x: x, y: y),
                    source: source
                );
                // The corner ray's direction as cameraRayDirection casts it, before normalizing: its forward component
                // is one, so the near distance along it lands on the near plane.
                var horizontal = (((((2f * x) - 1f) * camera.AspectRatio) * camera.TanHalfFieldOfView) + camera.FrustumOffset.X);
                var vertical = (((1f - (2f * y)) * camera.TanHalfFieldOfView) + camera.FrustumOffset.Y);
                var direction = ((camera.Forward + (horizontal * camera.Right)) + (vertical * camera.Up));
                var onNear = (camera.Position + (direction * camera.Near));

                Assert.True(
                    condition: (Vector3.Distance(value1: onNear, value2: corner) < Tolerance),
                    userMessage: $"from eye {eye}, the near plane along corner ({x}, {y}) is at {onNear}, not at the mapped glass corner {corner}"
                );
                Assert.Equal(
                    expected: Vector3.Distance(value1: camera.Position, value2: corner),
                    actual: (direction.Length() * camera.Near),
                    tolerance: Tolerance
                );
                Assert.True(
                    condition: (Vector3.Distance(value1: Through(camera: camera, x: x, y: y).Origin.ToVector3(), value2: corner) < Tolerance),
                    userMessage: $"from eye {eye}, the ray through corner ({x}, {y}) does not start on the mapped glass corner {corner}"
                );
            }
        }
    }
    // A pick through the window reaches exactly as far as the window renders: the far distance is measured from the
    // mapped eye, as the view pass measures it, not from the glass the ray starts on. The marker's near surface lies
    // about 14.5 units from the first eye; a far distance of 14 ends the view short of it, and one of 15 reaches it.
    [Fact]
    public void AWindowPickEndsAtTheFarDistanceMeasuredFromTheMappedEye() {
        var destination = AuthoredGameFixtures.Load(relativePath: Destination);
        var eye = Eyes[0];

        bool Picks(float farDistance) {
            var emitter = new WorldSessionSceneEmitter(
                effectiveCameraName: null,
                mirror: new WorldSessionMirror(placeholder: (destination with { RenderRaw = new WorldRenderDefaults(FarDistance: farDistance) }))
            );
            var camera = Fit(eye: eye);

            emitter.SetWindowFit(fit: () => camera);
            _ = new SdfCompositionFrameSource(
                dresser: emitter,
                emitters: [emitter]
            ).CaptureFrame(
                deltaSeconds: 0f,
                height: 120,
                interpolationAlpha: 0f,
                width: 144
            );

            var image = ImageOf(camera: camera, point: Marker);

            return emitter.TrySurface(
                point: out _,
                ray: Through(camera: camera, x: image.X, y: image.Y)
            );
        }

        var surface = (Vector3.Distance(value1: Fit(eye: eye).Position, value2: Marker) - 0.5f);

        Assert.InRange(actual: surface, high: 15f, low: 14f);
        Assert.False(condition: Picks(farDistance: 14f));
        Assert.True(condition: Picks(farDistance: 15f));
    }
    // A pick through the window sees only what lies beyond the aperture: the destination's occluder stands on the ray
    // between the mapped eye and the glass, so a ray from the eye itself meets it, while the window's ray, which starts
    // on the glass, passes it and meets the marker beyond.
    [Fact]
    public void AWindowPickPassesWhatStandsBetweenTheEyeAndTheApertureAndMeetsWhatLiesBeyond() {
        var emitter = new WorldSessionSceneEmitter(
            effectiveCameraName: null,
            mirror: new WorldSessionMirror(placeholder: AuthoredGameFixtures.Load(relativePath: Destination))
        );
        var composition = new SdfCompositionFrameSource(
            dresser: emitter,
            emitters: [emitter]
        );

        foreach (var eye in Eyes[..2]) {
            var camera = Fit(eye: eye);

            emitter.SetWindowFit(fit: () => camera);
            _ = composition.CaptureFrame(
                deltaSeconds: 0f,
                height: 120,
                interpolationAlpha: 0f,
                width: 144
            );

            var image = ImageOf(camera: camera, point: Marker);
            var ray = Through(camera: camera, x: image.X, y: image.Y);

            Assert.True(condition: emitter.TrySurface(
                point: out var beyond,
                ray: ray
            ));
            Assert.Equal(
                expected: 0.5f,
                actual: Vector3.Distance(value1: beyond.ToVector3(), value2: Marker),
                tolerance: 0.02f
            );
            Assert.True(
                condition: emitter.TrySurface(
                    point: out var between,
                    ray: (ray with { Origin = FixedVector3.FromVector3(value: camera.Position) })
                ),
                userMessage: $"from eye {eye} the ray from the mapped eye meets nothing"
            );
            Assert.Equal(
                expected: (Occluder.Z + 0.05f),
                actual: between.ToVector3().Z,
                tolerance: 0.02f
            );
        }
    }
    // The canary moves the viewer with body.pose, and a window fits its eye to the camera the seat's view renders with
    // in the same frame (WorldWindowFrustumFit.FitFrom), so the canary's seat renders through a rig that rides its body:
    // the eye the presenter publishes for seat 0 moves with the body, and the window fitted from it shows the marker on
    // the side of the glass the body stepped toward. A seat bound to a fixed camera would fit both poses from one eye
    // and frame the marker at the glass's centre each time.
    [Fact]
    public void TheCanarysSeatViewRidesItsBody_SoAStepReframesTheWindow() {
        using var state = new TemporaryDirectory(prefix: "puck-portal-window-eye-");
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: state,
            world: Local
        ).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var client = host.Services.GetRequiredService<WorldClient>();
        var viewports = host.Services.GetRequiredService<WorldSeatViewports>();
        var destination = AuthoredGameFixtures.Load(relativePath: Destination);
        var row = DoorRow();
        var fit = WorldWindowFrustumFit.FitFrom(
            destination: () => destination,
            local: () => client.Definition,
            screen: () => row,
            viewports: viewports
        );

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            _ = client.Roster.VacateSeat(slot: slot);
        }
        _ = client.Roster.OccupySeat(profile: null, slot: 0);

        foreach (var side in ((ReadOnlySpan<float>)[1f, -1f])) {
            client.DeliverSnapshot(snapshot: new WorldSnapshot(
                Authority: WorldInstanceHost.BootInstanceName,
                EngineTick: 1680UL,
                Entries: new[] { new EntitySnapshot(
                    Active: true,
                    BodyColor: Vector3.One,
                    CatalogRig: 0,
                    Continuity: EntityContinuity.Continuous,
                    Generation: 1,
                    Index: 0,
                    Kit: 0,
                    Look: 0,
                    Orientation: Quaternion.Identity,
                    Position: new Vector3(x: side, y: 0f, z: 9f)
                ) },
                Revision: 0,
                StepTicks: 1680UL,
                Tick: 1UL
            ));
            _ = presenter.CaptureFrame(
                deltaSeconds: 0f,
                height: 144U,
                interpolationAlpha: 1f,
                width: 256U
            );

            var seat = viewports.Seat(slot: 0);

            Assert.True(condition: seat.Present);
            Assert.Equal(
                expected: side,
                actual: seat.Camera.Position.X,
                tolerance: Tolerance
            );

            var camera = fit();

            Assert.NotNull(@object: camera);
            Assert.Equal(expected: MathF.Sign(x: side), actual: MathF.Sign(x: (ImageOf(camera: camera.Value, point: Marker).X - 0.5f)));
        }
    }
}
