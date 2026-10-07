using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.SignedDistance;

namespace Puck.SdfVm.Views;

/// <summary>
/// The CPU reference for how the viewer's sky samples an infinity view's image: the <see cref="SdfSkyView"/> record a
/// frame packs for a fitted view, and the image coordinate its module (<c>sky/kinds/view.hlsli</c>) reads a pixel's world
/// direction at. The instance renders the rectangle of the viewer's camera plane <see cref="InfinityViewFit"/> chose, so a
/// direction indexes it by the tangent it has on the viewer's basis (its components on the right and up axes over its
/// component on the forward axis), scaled across the rectangle; the module computes exactly this.
/// </summary>
public static class InfinityViewSampling {
    /// <summary>Returns the layer parameters for a view fitted to a viewer.</summary>
    /// <param name="spec">The view.</param>
    /// <param name="viewer">The camera the view was fitted to; the sampling reads its basis.</param>
    /// <param name="frame">The frame the view renders (<see cref="InfinityViewFit.Fit"/>), which must be visible.</param>
    /// <param name="imageSlot">The consuming view's infinity-image binding slot, or −1 for none.</param>
    /// <returns>The packed record: the viewer's basis, the rectangle, the image slot, the fallback colour, and image alpha
    /// as coverage for far geometry.</returns>
    public static SdfSkyView Describe(InfinityViewSpec spec, CameraSnapshot viewer, InfinityViewFrame frame, int imageSlot) {
        ArgumentNullException.ThrowIfNull(argument: spec);

        return new SdfSkyView {
            Coverage = ((spec.Kind == InfinityViewKind.Far) ? 1u : 0u),
            Fallback = spec.Fallback,
            Forward = viewer.Forward,
            ImageSlot = imageSlot,
            Intensity = 1f,
            Rect = frame.Rect,
            Right = viewer.Right,
            Up = viewer.Up,
        };
    }
    /// <summary>Returns where a world direction lies in the instance's image, as the module reads it.</summary>
    /// <param name="view">The layer parameters.</param>
    /// <param name="direction">The pixel's world direction, any nonzero length.</param>
    /// <param name="uv">The image coordinate in <c>[0, 1]²</c>, the top row at zero, when the direction lies in the
    /// rectangle.</param>
    /// <returns><see langword="false"/> for a direction behind the camera plane or outside the rectangle, which the layer
    /// leaves undrawn.</returns>
    public static bool TryUv(in SdfSkyView view, Vector3 direction, out Vector2 uv) {
        uv = default;

        var forward = Vector3.Dot(vector1: direction, vector2: view.Forward);

        if (!(forward > 0f)) {
            return false;
        }

        var tangent = new Vector2(
            x: (Vector3.Dot(vector1: direction, vector2: view.Right) / forward),
            y: (Vector3.Dot(vector1: direction, vector2: view.Up) / forward)
        );
        var span = new Vector2(x: (view.Rect.Z - view.Rect.X), y: (view.Rect.W - view.Rect.Y));

        if (!((span.X > 0f) && (span.Y > 0f))) {
            return false;
        }

        var u = ((tangent.X - view.Rect.X) / span.X);
        var v = ((tangent.Y - view.Rect.Y) / span.Y);

        if (!((u is >= 0f and <= 1f) && (v is >= 0f and <= 1f))) {
            return false;
        }

        uv = new Vector2(x: u, y: (1f - v));

        return true;
    }
}
