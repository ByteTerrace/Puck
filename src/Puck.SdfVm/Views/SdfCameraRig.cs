using System.Numerics;
using Puck.Maths;

namespace Puck.SdfVm.Views;

/// <summary>
/// Poses a camera against a live <see cref="SdfAnchor"/>: the anchor supplies where the rig's subject is this frame,
/// and the rig supplies how a camera looks at it. A world's cameras are authored camera programs, which
/// <see cref="SdfCameraProgramRig"/> wears this seam for.
/// </summary>
public interface ISdfCameraRig {
    /// <summary>Resolves this rig's concrete eye/target/field-of-view for the current frame.</summary>
    /// <param name="anchor">The live pose the rig's subject resolved to this frame (see <see cref="SdfAnchor"/>).</param>
    /// <param name="clock">The presentation clocks available to camera-only motion.</param>
    /// <returns>The eye position, the look-at target, and the vertical field of view (radians) to render this frame
    /// with.</returns>
    (Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, in SdfCameraClock clock);
}
/// <summary>The camera-only clock inputs published by presentation.</summary>
/// <param name="PresentationSeconds">The accumulated presentation time in seconds.</param>
/// <param name="AuthoritativeTick">The latest authoritative simulation tick visible to presentation.</param>
public readonly record struct SdfCameraClock(float PresentationSeconds, ulong AuthoritativeTick);
/// <summary>The orbit math every object-intent camera reduces to, and the field of view every lens defaults to.</summary>
public static class OrbitRig {
    /// <summary>The default vertical field of view, about 55°, every diegetic lens defaults to.</summary>
    public const float DefaultFieldOfViewRadians = (55f * (MathF.PI / 180f));

    /// <summary>The forward vector at <paramref name="yaw"/>/<paramref name="pitch"/>, scaled to
    /// <paramref name="distance"/>: added to a target position, this is the eye; negated and added to an eye, the
    /// near-side point a subject-relative probe wants.</summary>
    /// <param name="yaw">The heading, radians (0 = +Z).</param>
    /// <param name="pitch">The tilt, radians (positive = up).</param>
    /// <param name="distance">The scale to apply to the resulting direction.</param>
    /// <returns>The offset vector from the orbit target to the orbit eye.</returns>
    public static Vector3 Offset(float yaw, float pitch, float distance) =>
        (-VectorFunctions.Heading(
            pitch: -pitch,
            yaw: yaw
        ) * distance);
}
