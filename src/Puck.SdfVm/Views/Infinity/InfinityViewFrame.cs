using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;

namespace Puck.SdfVm.Views;

/// <summary>
/// The camera and extent one infinity view renders a frame with (<see cref="InfinityViewFit.Fit"/>).
/// </summary>
/// <param name="Visible">Whether any of the view's region lies in the viewer's frustum; when not, nothing renders.</param>
/// <param name="Camera">The instance's camera: at the anchor, turned as the viewer is, its frustum the sub-rectangle of
/// the viewer's that the region covers. Meaningful only when <paramref name="Visible"/>.</param>
/// <param name="Width">The pixels across the instance renders, at least 1 when visible.</param>
/// <param name="Height">The pixels down the instance renders, at least 1 when visible.</param>
/// <param name="Rect">The tangent-space rectangle it renders on the viewer's camera plane,
/// <c>(minX, minY, maxX, maxY)</c>.</param>
/// <param name="WholeFrustum">Whether the region could not be bounded (it reaches or passes the viewer's horizon plane),
/// so the instance renders the viewer's whole frustum.</param>
public readonly record struct InfinityViewFrame(bool Visible, CameraSnapshot Camera, uint Width, uint Height, Vector4 Rect, bool WholeFrustum);
/// <summary>
/// The CPU reference for what an infinity view renders. The view's region is the cone that bounds it
/// (<see cref="InfinityViewMask"/>); seen from the viewer's camera, a cone wholly in front of the camera projects onto the
/// camera plane as an ellipse, and the instance renders exactly the ellipse's bounding rectangle, clipped to the viewer's
/// frustum, through an off-axis frustum of the viewer's own (<see cref="CameraSnapshot.FrustumOffset"/> carries the
/// rectangle's centre), so every pixel of the instance casts the ray of the viewer pixel it covers. A cone wholly behind the camera plane renders nothing, and one that reaches the
/// viewer's horizon plane has no bound on that plane and renders the viewer's whole frustum.
/// <para>With the cone's axis <c>a</c> at components <c>(aᵣ, aᵤ, a_f)</c> on the camera's right, up and forward, half-angle
/// α, <c>s = sin α</c> and <c>d = a_f² − s²</c> (positive exactly when the cone is wholly in front), the ellipse's centre
/// is <c>(a_f·aᵣ, a_f·aᵤ) / d</c> and its bounding half-extents are <c>s·√(d + aᵣ²) / d</c> and
/// <c>s·√(d + aᵤ²) / d</c>.</para>
/// <para>The camera sits at the view's anchor whatever the viewer does: nothing here reads the viewer's position, so the
/// view turns with the viewer and is never translated by it. The instance's basis is the viewer's carried by the view's
/// orientation.</para>
/// </summary>
public static class InfinityViewFit {
    /// <summary>Returns the frame a view renders for a viewer.</summary>
    /// <param name="viewer">The viewer's camera, in its own world.</param>
    /// <param name="viewerWidth">The pixels across the viewer renders.</param>
    /// <param name="viewerHeight">The pixels down the viewer renders.</param>
    /// <param name="spec">The view.</param>
    /// <param name="tier">The quality tier the viewer draws at; below <see cref="InfinityViewSpec.MinimumTier"/> nothing
    /// renders, and below <see cref="QualityTier.High"/> the view renders at half its scale.</param>
    /// <returns>The frame; <see cref="InfinityViewFrame.Visible"/> is <see langword="false"/> when nothing renders.</returns>
    public static InfinityViewFrame Fit(CameraSnapshot viewer, uint viewerWidth, uint viewerHeight, InfinityViewSpec spec, QualityTier tier) {
        ArgumentNullException.ThrowIfNull(argument: spec);

        if (!spec.DrawsAt(tier: tier) || (viewerWidth == 0u) || (viewerHeight == 0u)) {
            return default;
        }

        var halfHeight = ((double)viewer.TanHalfFieldOfView);
        var halfWidth = (halfHeight * viewer.AspectRatio);
        double minX = (viewer.FrustumOffset.X - halfWidth), maxX = (viewer.FrustumOffset.X + halfWidth);
        double minY = (viewer.FrustumOffset.Y - halfHeight), maxY = (viewer.FrustumOffset.Y + halfHeight);
        var whole = true;

        if (spec.Mask is { } mask) {
            var axis = mask.Direction;
            double forward = Vector3.Dot(vector1: axis, vector2: viewer.Forward);
            double right = Vector3.Dot(vector1: axis, vector2: viewer.Right);
            double up = Vector3.Dot(vector1: axis, vector2: viewer.Up);
            var sine = Math.Sin(a: mask.HalfAngle);
            var reach = ((forward * forward) - (sine * sine));

            // A cone every direction of which lies at or behind the camera plane has nothing in the frustum.
            if (forward <= -sine) {
                return default;
            }
            if ((forward > 0.0) && (reach > 1e-9)) {
                var centerX = ((forward * right) / reach);
                var centerY = ((forward * up) / reach);
                var extentX = ((sine * Math.Sqrt(d: (reach + (right * right)))) / reach);
                var extentY = ((sine * Math.Sqrt(d: (reach + (up * up)))) / reach);

                minX = Math.Max(val1: minX, val2: (centerX - extentX));
                maxX = Math.Min(val1: maxX, val2: (centerX + extentX));
                minY = Math.Max(val1: minY, val2: (centerY - extentY));
                maxY = Math.Min(val1: maxY, val2: (centerY + extentY));
                whole = false;
            }
        }

        if (!((maxX > minX) && (maxY > minY))) {
            return default;
        }

        var shownHeight = (maxY - minY);
        var shownWidth = (maxX - minX);
        var rotation = spec.Orientation;
        var camera = new CameraSnapshot(
            Position: spec.Anchor,
            Right: Vector3.Normalize(value: Vector3.Transform(value: viewer.Right, rotation: rotation)),
            Up: Vector3.Normalize(value: Vector3.Transform(value: viewer.Up, rotation: rotation)),
            Forward: Vector3.Normalize(value: Vector3.Transform(value: viewer.Forward, rotation: rotation)),
            TanHalfFieldOfView: ((float)(shownHeight / 2.0)),
            AspectRatio: ((float)(shownWidth / shownHeight))
        ) {
            FrustumOffset = new Vector2(
                x: ((float)((minX + maxX) / 2.0)),
                y: ((float)((minY + maxY) / 2.0))
            ),
        };
        var scale = spec.EffectiveScale(tier: tier);

        return new InfinityViewFrame(
            Visible: true,
            Camera: camera,
            Width: Pixels(scale: scale, share: (shownWidth / (2.0 * halfWidth)), viewerPixels: viewerWidth),
            Height: Pixels(scale: scale, share: (shownHeight / (2.0 * halfHeight)), viewerPixels: viewerHeight),
            Rect: new Vector4(
                w: ((float)maxY),
                x: ((float)minX),
                y: ((float)minY),
                z: ((float)maxX)
            ),
            WholeFrustum: whole
        );
    }
    /// <summary>Returns the ray a camera casts through an image point, as the view pass's <c>cameraRayDirection</c> sums it:
    /// the forward axis, the point scaled by the half-extents on the right and up axes, then the frustum offset as a
    /// trailing term. The reference the laws hold the instance's rays to.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="ndc">The point in <c>[-1, 1]²</c>, up positive.</param>
    /// <returns>The unnormalized direction.</returns>
    public static Vector3 RayThrough(CameraSnapshot camera, Vector2 ndc) {
        var direction = (
            (camera.Forward +
            (((ndc.X * camera.AspectRatio) * camera.TanHalfFieldOfView) * camera.Right)) +
            ((ndc.Y * camera.TanHalfFieldOfView) * camera.Up)
        );

        return (direction + ((camera.FrustumOffset.X * camera.Right) + (camera.FrustumOffset.Y * camera.Up)));
    }

    // The pixels a share of the viewer's extent takes at a scale of its density: at least one, never above the viewer's
    // own extent.
    private static uint Pixels(uint viewerPixels, double share, float scale) {
        var pixels = Math.Ceiling(a: ((viewerPixels * share) * scale));

        return ((uint)Math.Clamp(
            max: viewerPixels,
            min: 1.0,
            value: pixels
        ));
    }
}
