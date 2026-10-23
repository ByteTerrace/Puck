using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SdfVm.Views;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// THE LAW: an engaged screen is framed from the row, its source's rows and the window alone. The picture (the glass
/// inside its bezel band) fills the matte a desktop emulator would show it in, each source row a whole number of window
/// pixels and its width the glass's own shape, square-on with the near plane one slab thickness short of the glass; the
/// picture's corners land on the matte's whole pixels; and the seat's camera eases there and back
/// on the rig's dynamics, its weight the view's screen fidelity, or cuts when the rig reports none.
/// </summary>
public sealed class WorldScreenFramingLawTests {
    private const float FieldOfView = 0.96f;
    private static readonly NormalizedRect Whole = new(Height: 1f, Width: 1f, X: 0f, Y: 0f);

    // A 4:3 screen facing +Z: 2 m right of the origin, 1 m up, 0.4 m by 0.3 m, 1 cm deep.
    private static WorldScreen Screen() => new(
        HalfDepth: 0.005f,
        HalfHeight: 0.15f,
        HalfWidth: 0.2f,
        Index: 0,
        Origin: new Vector3(x: 2f, y: 1f, z: 0f),
        Right: Vector3.UnitX,
        Round: 0f,
        Route: WorldScreenRoute.Passive,
        Source: new WorldScreenSource.None(),
        Up: Vector3.UnitY
    );
    // The picture's corners in normalized device coordinates through a camera.
    private static (float MaxX, float MaxY) CornerExtent(WorldScreen screen, in CameraSnapshot camera) {
        var picture = WorldScreenFraming.PictureHalfExtents(screen: screen);
        var maxX = 0f;
        var maxY = 0f;

        foreach (var (x, y) in new[] { (-1f, -1f), (-1f, 1f), (1f, -1f), (1f, 1f) }) {
            var corner = (((Vector3)screen.Origin) + (Vector3.UnitX * (x * picture.X)) + (Vector3.UnitY * (y * picture.Y)));

            Assert.True(condition: camera.TryProject(
                depth: out _,
                ndc: out var ndc,
                world: corner
            ));
            maxX = MathF.Max(x: maxX, y: MathF.Abs(x: ndc.X));
            maxY = MathF.Max(x: maxY, y: MathF.Abs(x: ndc.Y));
        }

        return (maxX, maxY);
    }

    [Fact]
    public void ThePictureFillsItsShareOfTheViewSquareOnWithTheNearPlaneJustShortOfIt() {
        var screen = Screen();
        var picture = WorldScreenFraming.PictureHalfExtents(screen: screen);

        // The glass's bezel band takes three hundredths of the face on each side.
        Assert.Equal(expected: (0.2f * 0.94f), actual: picture.X, tolerance: 1e-6f);
        Assert.Equal(expected: (0.15f * 0.94f), actual: picture.Y, tolerance: 1e-6f);
        // A window into another world is an opening with no bezel: its picture is the whole face.
        Assert.Equal(
            expected: new Vector2(x: 0.2f, y: 0.15f),
            actual: WorldScreenFraming.PictureHalfExtents(screen: (screen with { Source = new WorldScreenSource.Session(Destination: "garden", Projection: WorldScreenProjection.Window) }))
        );

        // At a share of 0.8 of a 1280 by 900 view the picture is 0.8 of its height, its width the picture's shape.
        Assert.True(condition: WorldScreenFraming.TryFrame(
            eye: out var eye,
            fieldOfViewRadians: FieldOfView,
            near: out var near,
            screen: screen,
            share: 0.8f,
            target: out var target
        ));

        var (x, y) = CornerExtent(
            camera: CameraSnapshot.LookAt(fieldOfViewRadians: FieldOfView, position: eye, target: target, viewportHeight: 900f, viewportWidth: 1280f),
            screen: screen
        );

        Assert.Equal(expected: 0.8f, actual: y, tolerance: 1e-4f);
        Assert.Equal(expected: ((0.8f * (4f / 3f)) / (1280f / 900f)), actual: x, tolerance: 1e-4f);
        Assert.Equal(expected: (Vector3)screen.Origin, actual: target);
        Assert.Equal(expected: 0f, actual: (eye.X - 2f), tolerance: 1e-6f);
        Assert.Equal(expected: (eye.Z - (2f * screen.HalfDepth)), actual: near, tolerance: 1e-6f);

        // A degenerate frame, lens or share frames nothing.
        Assert.False(condition: WorldScreenFraming.TryFrame(eye: out _, fieldOfViewRadians: FieldOfView, near: out _, screen: (screen with { Up = Vector3.UnitX }), share: 1f, target: out _));
        Assert.False(condition: WorldScreenFraming.TryFrame(eye: out _, fieldOfViewRadians: 0f, near: out _, screen: screen, share: 1f, target: out _));
        Assert.False(condition: WorldScreenFraming.TryFrame(eye: out _, fieldOfViewRadians: FieldOfView, near: out _, screen: screen, share: 0f, target: out _));
    }
    [Fact]
    public void TheMatteShowsEachSourceRowAtAWholeNumberOfWindowPixelsAndRefitsToTheWindow() {
        var screen = Screen();

        // A 256 by 240 console in a 1280 by 900 window: three pixels a row, 720 tall and 960 wide at the glass's 4:3,
        // centred on whole pixels.
        Assert.Equal(
            expected: new NormalizedRect(Height: (720f / 900f), Width: (960f / 1280f), X: (160f / 1280f), Y: (90f / 900f)),
            actual: WorldScreenFraming.Matte(displayHeight: 900, displayWidth: 1280, screen: screen, sourceRows: 240, view: Whole)
        );

        // A tall window flips the limiting axis: 600 wide holds 450 rows of picture at 4:3, so one pixel a row.
        Assert.Equal(
            expected: new NormalizedRect(Height: (240f / 1200f), Width: (320f / 600f), X: (140f / 600f), Y: (480f / 1200f)),
            actual: WorldScreenFraming.Matte(displayHeight: 1200, displayWidth: 600, screen: screen, sourceRows: 240, view: Whole)
        );

        // A window too small for one pixel a row, or a source of unknown extent, fills the limiting axis.
        Assert.Equal(
            expected: new NormalizedRect(Height: 1f, Width: (267f / 400f), X: (66f / 400f), Y: 0f),
            actual: WorldScreenFraming.Matte(displayHeight: 200, displayWidth: 400, screen: screen, sourceRows: 240, view: Whole)
        );
        Assert.Equal(
            expected: new NormalizedRect(Height: 1f, Width: (1200f / 1280f), X: (40f / 1280f), Y: 0f),
            actual: WorldScreenFraming.Matte(displayHeight: 900, displayWidth: 1280, screen: screen, sourceRows: 0, view: Whole)
        );

        // A view under one pixel tall mattes nothing.
        var sliver = new NormalizedRect(Height: 0.0001f, Width: 1f, X: 0f, Y: 0f);

        Assert.Equal(
            expected: sliver,
            actual: WorldScreenFraming.Matte(displayHeight: 900, displayWidth: 1280, screen: screen, sourceRows: 240, view: sliver)
        );

        // A seat's half of a split display is matted inside its own half.
        Assert.Equal(
            expected: new NormalizedRect(Height: (480f / 900f), Width: (640f / 1280f), X: (640f / 1280f), Y: (210f / 900f)),
            actual: WorldScreenFraming.Matte(displayHeight: 900, displayWidth: 1280, screen: screen, sourceRows: 240, view: new NormalizedRect(Height: 1f, Width: 0.5f, X: 0.5f, Y: 0f))
        );
    }
    // THE LAW: the picture lands on the matte's whole pixels. A window with odd gutters puts the matte half a pixel off
    // the view's centre, and the framing camera shifts by that offset, so the picture's corners project onto the
    // matte's pixel edges exactly and each source row covers whole window pixels.
    [Fact]
    public void ThePictureLandsOnTheMattesWholePixels() {
        var screen = Screen();
        var matte = WorldScreenFraming.Matte(displayHeight: 901, displayWidth: 1281, screen: screen, sourceRows: 240, view: Whole);
        var offset = WorldScreenFraming.MatteOffset(displayHeight: 901, displayWidth: 1281, matte: matte, view: Whole);

        Assert.Equal(expected: new NormalizedRect(Height: (720f / 901f), Width: (960f / 1281f), X: (160f / 1281f), Y: (90f / 901f)), actual: matte);
        Assert.Equal(expected: (-0.5f / 720f), actual: offset.X, tolerance: 1e-6f);
        Assert.Equal(expected: (-0.5f / 720f), actual: offset.Y, tolerance: 1e-6f);
        Assert.Equal(expected: Vector2.Zero, actual: WorldScreenFraming.MatteOffset(displayHeight: 901, displayWidth: 1281, matte: (matte with { Height = 0f }), view: Whole));
        Assert.True(condition: WorldScreenFraming.TryFrame(eye: out var eye, fieldOfViewRadians: FieldOfView, near: out _, offset: offset, screen: screen, share: matte.Height, target: out var target));

        var camera = CameraSnapshot.LookAt(fieldOfViewRadians: FieldOfView, position: eye, target: target, viewportHeight: 901f, viewportWidth: 1281f);
        var picture = WorldScreenFraming.PictureHalfExtents(screen: screen);

        foreach (var (x, y, pixelX, pixelY) in new[] { (-1f, 1f, 160f, 90f), (1f, -1f, 1120f, 810f) }) {
            Assert.True(condition: camera.TryProject(
                depth: out _,
                ndc: out var ndc,
                world: (((Vector3)screen.Origin) + (Vector3.UnitX * (x * picture.X)) + (Vector3.UnitY * (y * picture.Y)))
            ));
            Assert.Equal(expected: pixelX, actual: ((ndc.X + 1f) * 640.5f), tolerance: 0.01f);
            Assert.Equal(expected: pixelY, actual: ((1f - ndc.Y) * 450.5f), tolerance: 0.01f);
        }
    }

    [Fact]
    public void TheCameraEasesToTheFramingOnTheRigsDynamicsAndBackAndCutsWithoutThem() {
        var screen = Screen();
        var chaseEye = new Vector3(x: 0f, y: 2f, z: 5f);
        var chaseTarget = new Vector3(x: 0f, y: 1f, z: 1f);
        var dynamics = new SdfCameraDynamics(Damping: 1f, Frequency: 4f, Response: 0f);
        var framing = new WorldScreenFraming();

        // Unengaged, the seat's camera passes through untouched.
        var eye = chaseEye;
        var target = chaseTarget;

        Assert.Equal(expected: 0f, actual: framing.Apply(deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, held: null, screen: null, share: 1f, target: ref target));
        Assert.Equal(expected: chaseEye, actual: eye);

        // Engaged, the weight (the view's screen fidelity) climbs monotonically and settles on the framing; only the
        // settled framing mattes the seat's view.
        var last = 0f;

        for (var frame = 0; (frame < 120); frame++) {
            eye = chaseEye;
            target = chaseTarget;
            _ = framing.Apply(deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, held: null, screen: screen, share: 0.8f, target: ref target);
            Assert.True(condition: (framing.Weight >= last));
            Assert.Equal(expected: (framing.Weight >= 1f), actual: (framing.Framed is not null));
            last = framing.Weight;
        }

        Assert.Equal(expected: 1f, actual: framing.Weight);
        Assert.Same(expected: screen, actual: framing.Framed);
        Assert.Equal(expected: (Vector3)screen.Origin, actual: target);

        // Left, it eases back to the seat's camera, from the framing it last held, matting nothing.
        eye = chaseEye;
        target = chaseTarget;
        _ = framing.Apply(deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, held: null, screen: null, share: 1f, target: ref target);
        Assert.InRange(actual: framing.Weight, high: 0.999f, low: 0.001f);
        Assert.Null(@object: framing.Framed);
        Assert.NotEqual(expected: chaseEye, actual: eye);

        for (var frame = 0; (frame < 120); frame++) {
            eye = chaseEye;
            target = chaseTarget;
            _ = framing.Apply(deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, held: null, screen: null, share: 1f, target: ref target);
        }

        Assert.Equal(expected: 0f, actual: framing.Weight);
        Assert.Equal(expected: chaseEye, actual: eye);

        // A rig reporting no dynamics cuts straight to the framing.
        eye = chaseEye;
        target = chaseTarget;
        var near = framing.Apply(deltaSeconds: (1f / 60f), dynamics: SdfCameraDynamics.None, eye: ref eye, fieldOfViewRadians: FieldOfView, held: null, screen: screen, share: 1f, target: ref target);

        Assert.Equal(expected: 1f, actual: framing.Weight);
        Assert.Same(expected: screen, actual: framing.Framed);
        Assert.True(condition: (near > 0f));
    }
    [Fact]
    public void AHeldViewIsStoodOnFirstAndTheFramingEasesFromItWithTheLensGoingBack() {
        var screen = Screen();
        var chaseEye = new Vector3(x: 0f, y: 2f, z: 5f);
        var chaseTarget = new Vector3(x: 0f, y: 1f, z: 1f);
        var held = new WorldHeldView(Eye: new Vector3(x: -3f, y: 1.5f, z: 2f), FieldOfViewRadians: 0.5f, Target: new Vector3(x: -3f, y: 1.5f, z: 0f));
        var dynamics = new SdfCameraDynamics(Damping: 1f, Frequency: 4f, Response: 0f);
        var framing = new WorldScreenFraming();
        var eye = chaseEye;
        var target = chaseTarget;
        var near = 0f;

        // The held view with nothing framed settles on the held camera: the picture's fidelity and the near plane stay off.
        for (var frame = 0; (frame < 240); frame++) {
            eye = chaseEye;
            target = chaseTarget;
            near = framing.Apply(deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, held: held, screen: null, share: 1f, target: ref target);
        }

        Assert.Equal(expected: held.Eye, actual: eye);
        Assert.Equal(expected: held.Target, actual: target);
        Assert.Equal(expected: 0.5f, actual: framing.FieldOfView);
        Assert.Equal(expected: 0f, actual: framing.Weight);
        Assert.Equal(expected: 0f, actual: near);
        Assert.Null(@object: framing.Framed);

        // Framed on top of it, the framing eases from the held view and takes the seat camera's lens.
        for (var frame = 0; (frame < 240); frame++) {
            eye = chaseEye;
            target = chaseTarget;
            near = framing.Apply(deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, held: held, screen: screen, share: 1f, target: ref target);
        }

        Assert.Equal(expected: FieldOfView, actual: framing.FieldOfView);
        Assert.Equal(expected: 1f, actual: framing.Weight);
        Assert.Same(expected: screen, actual: framing.Framed);
        Assert.Equal(expected: (Vector3)screen.Origin, actual: target);
        Assert.True(condition: (near > 0f));

        // Back to the held view, then out of it to the seat's camera.
        for (var frame = 0; (frame < 240); frame++) {
            eye = chaseEye;
            target = chaseTarget;
            _ = framing.Apply(deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, held: held, screen: null, share: 1f, target: ref target);
        }

        Assert.Equal(expected: held.Eye, actual: eye);

        for (var frame = 0; (frame < 240); frame++) {
            eye = chaseEye;
            target = chaseTarget;
            _ = framing.Apply(deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, held: null, screen: null, share: 1f, target: ref target);
        }

        Assert.Equal(expected: chaseEye, actual: eye);
        Assert.Equal(expected: FieldOfView, actual: framing.FieldOfView);
    }
    [Fact]
    public void TheMatteIsTheDisplaysFillOrBlack() {
        var screen = Screen();

        Assert.Equal(expected: new Vector4(x: 1f, y: 0f, z: 0f, w: 1f), actual: WorldScreenFraming.Fill(screen: (screen with { Route = new WorldScreenRoute(Display: new WorldScreenDisplay(Fill: "#FF0000"), EngageRadius: 3f, Engageable: true) })));
        Assert.Equal(expected: new Vector4(x: 0f, y: 0f, z: 0f, w: 1f), actual: WorldScreenFraming.Fill(screen: screen));
        Assert.Equal(expected: new Vector4(x: 0f, y: 0f, z: 0f, w: 1f), actual: WorldScreenFraming.Fill(screen: (screen with { Route = new WorldScreenRoute(Display: new WorldScreenDisplay(Fill: "red"), EngageRadius: 3f, Engageable: true) })));
    }
}
