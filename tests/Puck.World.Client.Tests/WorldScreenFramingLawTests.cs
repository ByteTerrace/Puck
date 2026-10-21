using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.SdfVm.Views;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// THE LAW: the emulator view frames an engaged screen from the row alone. The glass fills the viewport's limiting axis
/// exactly, square-on, the near plane stands one slab thickness short of the glass, and the seat's camera eases there
/// and back on the rig's dynamics, or cuts when the rig reports none.
/// </summary>
public sealed class WorldScreenFramingLawTests {
    private const float FieldOfView = 0.96f;

    // A screen facing +Z: 2 m right of the origin, 1 m up, 0.32 m by 0.288 m, 1 cm deep.
    private static WorldScreen Screen() => new(
        HalfDepth: 0.005f,
        HalfHeight: 0.144f,
        HalfWidth: 0.16f,
        Index: 0,
        Origin: new Vector3(x: 2f, y: 1f, z: 0f),
        Right: Vector3.UnitX,
        Round: 0f,
        Route: WorldScreenRoute.Passive,
        Source: new WorldScreenSource.None(),
        Up: Vector3.UnitY
    );
    // The glass's corners in normalized device coordinates, through a camera at the framing.
    private static (float MaxX, float MaxY) CornerExtent(WorldScreen screen, float aspect) {
        Assert.True(condition: WorldScreenFraming.TryFrame(
            aspectRatio: aspect,
            eye: out var eye,
            fieldOfViewRadians: FieldOfView,
            near: out _,
            screen: screen,
            target: out var target
        ));

        var camera = CameraSnapshot.LookAt(
            fieldOfViewRadians: FieldOfView,
            position: eye,
            target: target,
            viewportHeight: 900f,
            viewportWidth: (900f * aspect)
        );
        var maxX = 0f;
        var maxY = 0f;

        foreach (var (x, y) in new[] { (-1f, -1f), (-1f, 1f), (1f, -1f), (1f, 1f) }) {
            var corner = (((Vector3)screen.Origin) + (Vector3.UnitX * (x * screen.HalfWidth)) + (Vector3.UnitY * (y * screen.HalfHeight)));

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
    public void TheGlassFillsTheLimitingAxisSquareOnWithTheNearPlaneJustShortOfIt() {
        var screen = Screen();

        // A wide viewport is limited by the glass's height, a tall one by its width; the other axis stays inside.
        var (wideX, wideY) = CornerExtent(aspect: (16f / 9f), screen: screen);

        Assert.Equal(expected: 1f, actual: wideY, tolerance: 1e-4f);
        Assert.True(condition: (wideX < 1f));

        var (tallX, tallY) = CornerExtent(aspect: 0.5f, screen: screen);

        Assert.Equal(expected: 1f, actual: tallX, tolerance: 1e-4f);
        Assert.True(condition: (tallY < 1f));

        Assert.True(condition: WorldScreenFraming.TryFrame(
            aspectRatio: (16f / 9f),
            eye: out var eye,
            fieldOfViewRadians: FieldOfView,
            near: out var near,
            screen: screen,
            target: out var target
        ));
        Assert.Equal(expected: (Vector3)screen.Origin, actual: target);
        Assert.Equal(expected: 0f, actual: (eye.X - 2f), tolerance: 1e-6f);
        Assert.Equal(expected: 0f, actual: (eye.Y - 1f), tolerance: 1e-6f);
        Assert.Equal(expected: (eye.Z - (2f * screen.HalfDepth)), actual: near, tolerance: 1e-6f);

        // A degenerate frame or lens frames nothing.
        Assert.False(condition: WorldScreenFraming.TryFrame(
            aspectRatio: 1f,
            eye: out _,
            fieldOfViewRadians: FieldOfView,
            near: out _,
            screen: (screen with { Up = Vector3.UnitX }),
            target: out _
        ));
        Assert.False(condition: WorldScreenFraming.TryFrame(
            aspectRatio: 0f,
            eye: out _,
            fieldOfViewRadians: FieldOfView,
            near: out _,
            screen: screen,
            target: out _
        ));
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

        Assert.Equal(expected: 0f, actual: framing.Apply(aspectRatio: 1.5f, deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, screen: null, target: ref target));
        Assert.Equal(expected: chaseEye, actual: eye);

        // Engaged, the weight climbs monotonically and settles on the framing; only the settled framing hands the seat
        // its emulator view.
        var last = 0f;

        for (var frame = 0; (frame < 120); frame++) {
            eye = chaseEye;
            target = chaseTarget;
            _ = framing.Apply(aspectRatio: 1.5f, deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, screen: screen, target: ref target);
            Assert.True(condition: (framing.Weight >= last));
            Assert.Equal(expected: (framing.Weight >= 1f), actual: (framing.Framed is not null));
            last = framing.Weight;
        }

        Assert.Equal(expected: 1f, actual: framing.Weight);
        Assert.Same(expected: screen, actual: framing.Framed);
        Assert.Equal(expected: (Vector3)screen.Origin, actual: target);

        // Left, it eases back to the seat's camera, from the framing it last held, and shows no emulator view.
        eye = chaseEye;
        target = chaseTarget;
        _ = framing.Apply(aspectRatio: 1.5f, deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, screen: null, target: ref target);
        Assert.InRange(actual: framing.Weight, high: 0.999f, low: 0.001f);
        Assert.Null(@object: framing.Framed);
        Assert.NotEqual(expected: chaseEye, actual: eye);

        for (var frame = 0; (frame < 120); frame++) {
            eye = chaseEye;
            target = chaseTarget;
            _ = framing.Apply(aspectRatio: 1.5f, deltaSeconds: (1f / 60f), dynamics: in dynamics, eye: ref eye, fieldOfViewRadians: FieldOfView, screen: null, target: ref target);
        }

        Assert.Equal(expected: 0f, actual: framing.Weight);
        Assert.Equal(expected: chaseEye, actual: eye);

        // A rig reporting no dynamics cuts straight to the framing.
        eye = chaseEye;
        target = chaseTarget;
        var near = framing.Apply(aspectRatio: 1.5f, deltaSeconds: (1f / 60f), dynamics: SdfCameraDynamics.None, eye: ref eye, fieldOfViewRadians: FieldOfView, screen: screen, target: ref target);

        Assert.Equal(expected: 1f, actual: framing.Weight);
        Assert.Same(expected: screen, actual: framing.Framed);
        Assert.True(condition: (near > 0f));
    }
    // THE LAW: an emulator view shows its display's crop at the display's pixel aspect, whole and centred inside the
    // seat's view, reaching the view's edges on its limiting axis; a crop that leaves nothing shows the whole source.
    [Fact]
    public void AnEmulatorViewFitsItsCropAtItsPixelAspectInsideTheSeatsView() {
        var whole = new Puck.Abstractions.Presentation.NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f);
        var deck = new WorldScreenDisplay(
            Crop: new WorldScreenDisplayCrop(Bottom: 8, Top: 8),
            PixelAspect: (8f / 7f)
        );

        // The deck's 256 by 224 lines at 8:7 pixels are 1.306 wide for each 1 tall: on a 1920 by 1080 display they
        // reach top to bottom and sit centred across.
        Assert.True(condition: WorldEmulatorView.TryFit(
            crop: out var crop,
            display: deck,
            displayHeight: 1080,
            displayWidth: 1920,
            drawn: out var drawn,
            frame: whole,
            sourceHeight: 240,
            sourceWidth: 256
        ));
        Assert.Equal(expected: new Puck.Commands.SourcePixelRect(Height: 224, Width: 256, X: 0, Y: 8), actual: crop);
        Assert.Equal(expected: 1f, actual: drawn.Height, tolerance: 1e-6f);
        Assert.Equal(expected: (((1080f * 256f * 8f) / (7f * 224f)) / 1920f), actual: drawn.Width, tolerance: 1e-5f);
        Assert.Equal(expected: (0.5f * (1f - drawn.Width)), actual: drawn.X, tolerance: 1e-6f);
        Assert.Equal(expected: 0f, actual: drawn.Y, tolerance: 1e-6f);

        // A square-pixel brick in the right half of a tall display is limited by the half's width instead.
        var right = new Puck.Abstractions.Presentation.NormalizedRect(Height: 1f, Width: 0.5f, X: 0.5f, Y: 0f);

        Assert.True(condition: WorldEmulatorView.TryFit(
            crop: out crop,
            display: null,
            displayHeight: 1920,
            displayWidth: 1080,
            drawn: out drawn,
            frame: right,
            sourceHeight: 144,
            sourceWidth: 160
        ));
        Assert.Equal(expected: new Puck.Commands.SourcePixelRect(Height: 144, Width: 160, X: 0, Y: 0), actual: crop);
        Assert.Equal(expected: 0.5f, actual: drawn.Width, tolerance: 1e-6f);
        Assert.Equal(expected: 0.5f, actual: drawn.X, tolerance: 1e-6f);
        Assert.Equal(expected: (((540f * 144f) / 160f) / 1920f), actual: drawn.Height, tolerance: 1e-6f);
        Assert.Equal(expected: (0.5f * (1f - drawn.Height)), actual: drawn.Y, tolerance: 1e-6f);

        // A crop leaving nothing shows the whole source; no extent or an empty view fits nothing.
        Assert.True(condition: WorldEmulatorView.TryFit(
            crop: out crop,
            display: new WorldScreenDisplay(Crop: new WorldScreenDisplayCrop(Left: 100, Right: 100)),
            displayHeight: 64,
            displayWidth: 64,
            drawn: out _,
            frame: whole,
            sourceHeight: 144,
            sourceWidth: 160
        ));
        Assert.Equal(expected: new Puck.Commands.SourcePixelRect(Height: 144, Width: 160, X: 0, Y: 0), actual: crop);
        Assert.False(condition: WorldEmulatorView.TryFit(crop: out _, display: null, displayHeight: 64, displayWidth: 64, drawn: out _, frame: whole, sourceHeight: 0, sourceWidth: 160));
        Assert.False(condition: WorldEmulatorView.TryFit(crop: out _, display: null, displayHeight: 64, displayWidth: 64, drawn: out _, frame: (whole with { Width = 0f }), sourceHeight: 144, sourceWidth: 160));

        // The fill reaches the display as authored, opaque; an unauthored one is black.
        Assert.Equal(expected: new Vector4(x: 1f, y: 0f, z: 0f, w: 1f), actual: WorldEmulatorView.FillOf(display: new WorldScreenDisplay(Fill: "#FF0000")));
        Assert.Equal(expected: new Vector4(x: 0f, y: 0f, z: 0f, w: 1f), actual: WorldEmulatorView.FillOf(display: null));
        Assert.Equal(expected: new Vector4(x: 0f, y: 0f, z: 0f, w: 1f), actual: WorldEmulatorView.FillOf(display: new WorldScreenDisplay(Fill: "red")));
    }
}
