using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.SdfVm.Views;
using Puck.World.Authoring;

namespace Puck.World.Client;

/// <summary>A camera that shows the body holding a carried screen's device (<see cref="WorldScreenCarry.Camera"/>), as one
/// frame resolved it.</summary>
/// <param name="Eye">The eye, in world units.</param>
/// <param name="Target">The point it looks at, in world units.</param>
/// <param name="FieldOfViewRadians">The vertical field of view, in radians.</param>
public readonly record struct WorldHeldView(Vector3 Eye, Vector3 Target, float FieldOfViewRadians) {
    /// <summary>The held camera program's authored lens override; absent inherits the presented world's lens.</summary>
    public Puck.Hosting.CameraBloom? Bloom { get; init; }
}
/// <summary>
/// The engaged view: a seat captured on a screen it engaged pans and zooms its own camera onto the screen's picture (the
/// face, or a CRT's glass inside its bezel band), square-on, until the picture fills the matte (<see cref="Matte"/>), the rect a desktop
/// emulator would show the console's picture in, and the window shows nothing else. Every number derives from the screen
/// row's own frame (origin, right, up, half extents and depth), a CRT's bezel (<see cref="WorldScreenMappings.WarpOf"/>),
/// the source's extent, the window's size and the seat camera's field of view, so a resized window refits at once. One
/// instance per seat eases between the seat's ordinary camera and that framing on the seat rig's own reported dynamics,
/// and back again when the seat leaves; a rig reporting no dynamics cuts. The ease's weight is the view's screen fidelity:
/// at one the screen's pixels reach the window untouched, and below it a CRT's treatment and the world's atmosphere and
/// tone fade back in. While framed, the camera's near plane stands just short of the glass, so the engaging avatar and
/// anything else between the eye and the device draws nothing, and once the ease settles (<see cref="Framed"/>) the
/// root shows only the matte's part of the seat's view, which renders at native scale, so the pixels inside it are the
/// ones the view drew there and only the world around the picture becomes the matte's fill. A screen a body carries with
/// a held view stands the camera on that view first (<see cref="WorldHeldView"/>), and the framing eases from it. Presentation
/// only: nothing here feeds the simulation.
/// </summary>
public sealed class WorldScreenFraming {
    private readonly WorldFramingEase m_ease = new();
    private readonly WorldFramingEase m_heldEase = new();

    private Vector3 m_frameEye;
    private float m_frameNear;
    private Vector3 m_frameTarget;
    private bool m_hasFrame;
    private bool m_hasHeld;
    private WorldHeldView m_held;

    /// <summary>Gets how far the seat's camera stands toward the framing, which is also the view's screen fidelity: 0 is
    /// the seat's ordinary camera or the held view, 1 the framed picture.</summary>
    public float Weight => m_ease.Weight;
    /// <summary>Gets the camera's vertical field of view as of the last <see cref="Apply"/>, in radians: the seat
    /// camera's, the held view's while the seat stands on it, and the seat camera's again once framed.</summary>
    public float FieldOfView { get; private set; }
    /// <summary>Gets the screen whose framing the ease has settled on as of the last <see cref="Apply"/>, whose matte
    /// the seat's view narrows to, or <see langword="null"/> while the seat engages none, is easing in or out, or the
    /// screen's frame is degenerate.</summary>
    public WorldScreen? Framed { get; private set; }

    /// <summary>Returns the half extents of a screen's picture: the whole face, or for a CRT its face inside the tube's
    /// bezel band (<see cref="WorldScreenMappings.WarpOf"/>).</summary>
    /// <param name="screen">The screen row.</param>
    /// <returns>The picture's half width and half height, in world units, along the row's right and up axes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screen"/> is <see langword="null"/>.</exception>
    public static Vector2 PictureHalfExtents(WorldScreen screen) {
        ArgumentNullException.ThrowIfNull(argument: screen);

        if (
            (WorldScreenMappings.WarpOf(screen: screen)?.Inverse is not SourceWarpInverse.Affine glass) ||
            !(glass.M11 > 0f) ||
            !(glass.M22 > 0f)
        ) {
            return new Vector2(x: screen.HalfWidth, y: screen.HalfHeight);
        }

        // The glass maps a face point to the image by M11 and M22, so the image spans 1/M11 and 1/M22 of the face.
        return new Vector2(
            x: (screen.HalfWidth / glass.M11),
            y: (screen.HalfHeight / glass.M22)
        );
    }
    /// <summary>Returns the matte a screen's picture fills inside a seat's view, the rect a desktop emulator shows the
    /// picture in: each source row the largest whole number of display pixels that lets the picture fit the view on both
    /// axes, its width following the picture's own shape (so a console's pixel aspect is the glass's), centred, with its
    /// edges on whole display pixels; when not even one pixel a row fits, the picture fills the view's limiting axis
    /// instead. Recomputed from the display's size every frame, so a resized window refits at once and the limiting axis
    /// flips when the window's shape crosses the picture's.</summary>
    /// <param name="screen">The engaged screen.</param>
    /// <param name="view">The seat's view, in normalized display coordinates.</param>
    /// <param name="displayWidth">The display's width, in pixels.</param>
    /// <param name="displayHeight">The display's height, in pixels.</param>
    /// <param name="sourceRows">The rows the picture shows (the source's height), or 0 when unknown, which fills.</param>
    /// <returns>The matte, in normalized display coordinates; <paramref name="view"/> itself when the picture or the
    /// view is degenerate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screen"/> is <see langword="null"/>.</exception>
    public static NormalizedRect Matte(WorldScreen screen, NormalizedRect view, uint displayWidth, uint displayHeight, int sourceRows) {
        var picture = PictureHalfExtents(screen: screen);
        var viewWidth = (((double)view.Width) * displayWidth);
        var viewHeight = (((double)view.Height) * displayHeight);

        if (
            !(picture.X > 0f) ||
            !(picture.Y > 0f) ||
            !(viewWidth >= 1d) ||
            !(viewHeight >= 1d)
        ) {
            return view;
        }

        var aspect = (((double)picture.X) / picture.Y);
        var height = Math.Min(
            val1: viewHeight,
            val2: (viewWidth / aspect)
        );
        // A height a whole number of rows short only by the single-precision shape's rounding takes that whole number.
        var scale = ((sourceRows > 0)
            ? Math.Floor(d: ((height / sourceRows) + 1e-4d))
            : 0d);

        height = ((scale >= 1d)
            ? (scale * sourceRows)
            : Math.Floor(d: height));

        var width = Math.Min(
            val1: Math.Floor(d: viewWidth),
            val2: Math.Round(a: (height * aspect))
        );
        var left = Math.Round(a: ((((double)view.X) * displayWidth) + (0.5d * (viewWidth - width))));
        var top = Math.Round(a: ((((double)view.Y) * displayHeight) + (0.5d * (viewHeight - height))));

        return new NormalizedRect(
            Height: ((float)(height / displayHeight)),
            Width: ((float)(width / displayWidth)),
            X: ((float)(left / displayWidth)),
            Y: ((float)(top / displayHeight))
        );
    }
    /// <summary>Returns how far a matte's centre stands from its view's centre, in matte heights: right and down are
    /// positive. A matte centred on whole pixels sits half a pixel off the view's centre whenever the view's gutter is
    /// odd, and the framing camera (<see cref="TryFrame"/>) shifts by this much so each source row still lands on whole
    /// window pixels.</summary>
    /// <param name="matte">The matte, in normalized display coordinates (<see cref="Matte"/>).</param>
    /// <param name="view">The seat's view the matte lies in, in normalized display coordinates.</param>
    /// <param name="displayWidth">The display's width, in pixels.</param>
    /// <param name="displayHeight">The display's height, in pixels.</param>
    /// <returns>The offset, in matte heights; zero for a matte with no height.</returns>
    public static Vector2 MatteOffset(NormalizedRect matte, NormalizedRect view, uint displayWidth, uint displayHeight) {
        var height = (((double)matte.Height) * displayHeight);

        if (!(height > 0d)) {
            return Vector2.Zero;
        }

        var x = (((((double)matte.X) + (0.5d * matte.Width)) - (((double)view.X) + (0.5d * view.Width))) * displayWidth);
        var y = (((((double)matte.Y) + (0.5d * matte.Height)) - (((double)view.Y) + (0.5d * view.Height))) * displayHeight);

        return new Vector2(
            x: ((float)(x / height)),
            y: ((float)(y / height))
        );
    }
    /// <summary>Returns the matte colour a screen's display authors, linear RGBA as the letterbox writes it:
    /// <c>#RRGGBB</c> straight through <c>/255</c>, opaque, as every authored colour reaches the display.</summary>
    /// <param name="screen">The engaged screen.</param>
    /// <returns>The colour; black for an unauthored or malformed fill, which the validator refuses.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screen"/> is <see langword="null"/>.</exception>
    public static Vector4 Fill(WorldScreen screen) {
        ArgumentNullException.ThrowIfNull(argument: screen);

        return (HexColor.TryParse(
            rgb: out var rgb,
            value: (screen.Route.Display?.Fill ?? WorldScreenDisplay.DefaultFill)
        )
            ? new Vector4(value: rgb, w: 1f)
            : new Vector4(w: 1f, x: 0f, y: 0f, z: 0f));
    }
    /// <summary>Frames a screen's picture square-on: the eye stands on the face's normal, far enough that the picture's
    /// height is a share of the viewport's, looking at a point of the face that puts the picture's centre where the
    /// matte's centre is (<paramref name="offset"/>).</summary>
    /// <param name="screen">The screen row whose picture is framed.</param>
    /// <param name="fieldOfViewRadians">The camera's vertical field of view, in radians; must be in (0, pi).</param>
    /// <param name="share">The share of the viewport's height the picture fills: the matte's height over the view's, in
    /// (0, 1].</param>
    /// <param name="eye">The framing eye, in world units.</param>
    /// <param name="target">The point of the face the eye looks at: the picture's centre shifted against the offset.</param>
    /// <param name="near">The framing's near-plane distance from the eye, in world units: the glass's distance less
    /// the slab's thickness, so the device face draws and nothing nearer does.</param>
    /// <param name="offset">Where the picture's centre lands from the viewport's centre, in picture heights, right and
    /// down positive (<see cref="MatteOffset"/>); zero centres it.</param>
    /// <returns><see langword="false"/> when the row's frame is degenerate (a zero axis, parallel axes, or a
    /// non-positive half extent) or the field of view or share is out of range; the outputs are then zero.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="screen"/> is <see langword="null"/>.</exception>
    public static bool TryFrame(WorldScreen screen, float fieldOfViewRadians, float share, out Vector3 eye, out Vector3 target, out float near, Vector2 offset = default) {
        ArgumentNullException.ThrowIfNull(argument: screen);

        eye = default;
        target = default;
        near = 0f;

        var normal = Vector3.Cross(
            vector1: screen.Right,
            vector2: screen.Up
        );
        var picture = PictureHalfExtents(screen: screen);

        if (
            !(normal.LengthSquared() > 0f) ||
            !(picture.X > 0f) ||
            !(picture.Y > 0f) ||
            !(fieldOfViewRadians > 0f) ||
            !(fieldOfViewRadians < MathF.PI) ||
            !(share > 0f) ||
            !(share <= 1f)
        ) {
            return false;
        }

        var distance = (picture.Y / (MathF.Tan(x: (fieldOfViewRadians * 0.5f)) * share));
        // Square-on, a shift of the eye along the face moves the picture the other way by the same world distance.
        var shift = (2f * picture.Y);

        target = (screen.Origin + (((Vector3.Normalize(value: screen.Up) * offset.Y) - (Vector3.Normalize(value: screen.Right) * offset.X)) * shift));
        eye = (target + (Vector3.Normalize(value: normal) * distance));
        near = MathF.Max(
            x: 0f,
            y: (distance - (2f * screen.HalfDepth))
        );

        return true;
    }
    /// <summary>Advances the eases one frame and blends the seat's camera toward the held view and the screen's framing
    /// by them: the seat's camera first goes to the held view (<paramref name="held"/>) by its ease, then that goes to
    /// the framing by the framing's, so a framed seat arrives at the picture from the held view when it has one and from
    /// the seat's own camera when it has none.</summary>
    /// <param name="screen">The screen the seat frames, or <see langword="null"/> when it frames none; the ease then
    /// returns from the last framing it held.</param>
    /// <param name="held">The view that shows the body holding the engaged screen, or <see langword="null"/> when the seat
    /// has none this frame; the ease then returns from the last one it held.</param>
    /// <param name="dynamics">The seat rig's reported dynamics, which time both eases; <see cref="SdfCameraDynamics.None"/>
    /// cuts.</param>
    /// <param name="fieldOfViewRadians">The seat camera's vertical field of view, in radians; the framing's field of view
    /// too.</param>
    /// <param name="share">The share of the view's height the picture fills once framed (<see cref="Matte"/>).</param>
    /// <param name="deltaSeconds">The frame step, in seconds.</param>
    /// <param name="eye">The seat camera's eye, blended in place.</param>
    /// <param name="target">The seat camera's target, blended in place.</param>
    /// <param name="offset">Where the picture's centre lands from the view's centre, in picture heights
    /// (<see cref="MatteOffset"/>).</param>
    /// <returns>The camera's near-plane distance, in world units: zero for the seat's own camera and the held view, the
    /// framing's near plane once framed, and the blend between while easing. <see cref="FieldOfView"/> holds the field of
    /// view the blend ends on.</returns>
    public float Apply(WorldScreen? screen, WorldHeldView? held, in SdfCameraDynamics dynamics, float fieldOfViewRadians, float share, float deltaSeconds, ref Vector3 eye, ref Vector3 target, Vector2 offset = default) {
        var frameGoal = 0f;
        var heldGoal = 0f;

        Framed = null;
        FieldOfView = fieldOfViewRadians;

        if (held is { } view) {
            m_held = view;
            m_hasHeld = true;
            heldGoal = 1f;
        }
        if (
            (screen is not null) &&
            TryFrame(
                eye: out var frameEye,
                fieldOfViewRadians: fieldOfViewRadians,
                near: out var frameNear,
                offset: offset,
                screen: screen,
                share: share,
                target: out var frameTarget
            )
        ) {
            m_frameEye = frameEye;
            m_frameTarget = frameTarget;
            m_frameNear = frameNear;
            m_hasFrame = true;
            frameGoal = 1f;
        }

        var heldWeight = 0f;
        var weight = 0f;

        if (m_hasHeld) {
            heldWeight = m_heldEase.Step(
                deltaSeconds: deltaSeconds,
                dynamics: in dynamics,
                goal: heldGoal
            );

            if (heldWeight <= 0f) {
                m_hasHeld = false;
            }
        }
        if (m_hasFrame) {
            weight = m_ease.Step(
                deltaSeconds: deltaSeconds,
                dynamics: in dynamics,
                goal: frameGoal
            );

            if (weight <= 0f) {
                m_hasFrame = false;
            }
        }
        if (heldWeight > 0f) {
            eye = Vector3.Lerp(
                value1: eye,
                value2: m_held.Eye,
                amount: heldWeight
            );
            target = Vector3.Lerp(
                value1: target,
                value2: m_held.Target,
                amount: heldWeight
            );
            FieldOfView = float.Lerp(
                value1: FieldOfView,
                value2: m_held.FieldOfViewRadians,
                amount: heldWeight
            );
        }
        if (weight <= 0f) {
            return 0f;
        }
        if (
            (frameGoal >= 1f) &&
            (weight >= 1f)
        ) {
            Framed = screen;
        }

        eye = Vector3.Lerp(
            amount: weight,
            value1: eye,
            value2: m_frameEye
        );
        target = Vector3.Lerp(
            amount: weight,
            value1: target,
            value2: m_frameTarget
        );
        FieldOfView = float.Lerp(
            value1: FieldOfView,
            value2: fieldOfViewRadians,
            amount: weight
        );

        return (weight * m_frameNear);
    }
}
