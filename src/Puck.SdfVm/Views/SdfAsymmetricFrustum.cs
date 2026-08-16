using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.SdfVm.Views;

/// <summary>
/// An off-axis (asymmetric) perspective frustum fitted to a fixed rectangular aperture as seen from an eye point
/// that need not be centered on it — the Kooima generalized-perspective-projection construction (as used by portal-
/// style rendering: a camera fixed to the screen's own orientation, with the eye's off-center position absorbed
/// entirely into the frustum's shear), specialized to ray directions rather than a projection matrix, because the
/// SDF march consumes a ray origin + direction (<c>sdf-world.hlsli</c>'s <c>cameraRayDirection</c>) and never
/// projects a vertex through a near/far clip.
/// </summary>
/// <remarks>
/// <para><b>Why no near/far scaling.</b> A rasterizer's off-axis frustum bounds (<c>left</c>/<c>right</c>/
/// <c>bottom</c>/<c>top</c>) are measured at the near-plane distance because a projection matrix needs a concrete
/// plane to project onto. A ray direction only needs the tangent of each bound — the same value regardless of which
/// distance it is measured at — so this type fits directly in tangent space and no near/far distance ever appears.</para>
/// <para><b>The fit.</b> Fix the camera's orientation to the aperture's own basis (<see cref="CameraSnapshot.Right"/>/
/// <see cref="CameraSnapshot.Up"/> = the aperture's Right/Up; <see cref="CameraSnapshot.Forward"/> = the aperture's
/// inward direction, i.e. the negated outward <c>Normal</c> a <c>WorldFaceFrame</c> carries) rather than aiming at
/// its center. Because the aperture's Right/Up are orthogonal to its Normal, the eye's perpendicular distance from
/// the aperture plane (<see cref="TryFit"/>'s <c>depth</c>) is the same for every point on the rectangle — so for a
/// point at in-plane offset <c>(x, y)</c> from the aperture center, the ray's tangent along Right/Up is the affine
/// function <c>(x - eyeRight) / depth</c> / <c>(y - eyeUp) / depth</c>. Splitting that affine map into a symmetric
/// half-extent (<see cref="HalfWidthTangent"/>/<see cref="HalfHeightTangent"/>, which the existing
/// <see cref="CameraSnapshot.TanHalfFieldOfView"/>/<see cref="CameraSnapshot.AspectRatio"/> pair already carries, no
/// row growth) plus a constant shear (<see cref="CenterOffset"/>, the two render-scale spares
/// <see cref="Puck.SdfVm.SdfViewSnapshot.AsymmetricFrustumOffset"/> repacks) reproduces the same ray
/// <c>sdf-world.hlsli</c>'s <c>cameraRayDirection</c> already computes for a symmetric camera, plus one trailing
/// offset term — see that shader function's own remarks for why the offset must be a trailing addition, not a
/// reassociated one, to keep an ordinary (zero-offset) camera bit-exact.</para>
/// </remarks>
public readonly record struct SdfAsymmetricFrustum(
    Vector3 Right,
    Vector3 Up,
    Vector3 Forward,
    float HalfWidthTangent,
    float HalfHeightTangent,
    Vector2 CenterOffset
) {
    /// <summary>The smallest perpendicular eye-to-aperture-plane distance (world units) a sound frustum can fit
    /// against — below this the tangent terms blow up (division by a near-zero depth) or the eye has crossed to the
    /// far side of the aperture plane. A presentation guard, not a document-authored bound.</summary>
    public const float MinEyeDepth = 0.05f;

    /// <summary>Fits an off-axis frustum whose near-plane rectangle is exactly the aperture as seen from
    /// <paramref name="eye"/>.</summary>
    /// <param name="eye">The camera's eye position, in the same space as the aperture (already mapped through any
    /// border-pair isometry the caller applies — this type knows nothing about portals).</param>
    /// <param name="apertureOrigin">The aperture rectangle's world-space center.</param>
    /// <param name="apertureRight">The aperture's unit Right axis.</param>
    /// <param name="apertureUp">The aperture's unit Up axis.</param>
    /// <param name="apertureNormal">The aperture's unit outward Normal (Right x Up convention) — the frustum looks
    /// into the scene, i.e. along <c>-apertureNormal</c>.</param>
    /// <param name="apertureHalfWidth">The aperture's half-width along <paramref name="apertureRight"/>.</param>
    /// <param name="apertureHalfHeight">The aperture's half-height along <paramref name="apertureUp"/>.</param>
    /// <param name="frustum">The fitted frustum, on success.</param>
    /// <returns><see langword="true"/> when the eye stands at least <see cref="MinEyeDepth"/> in front of the
    /// aperture plane (a sound frustum exists); <see langword="false"/> otherwise (<paramref name="frustum"/> is
    /// <see langword="default"/> — the caller falls back to its ordinary projection for this frame).</returns>
    public static bool TryFit(
        Vector3 eye,
        Vector3 apertureOrigin,
        Vector3 apertureRight,
        Vector3 apertureUp,
        Vector3 apertureNormal,
        float apertureHalfWidth,
        float apertureHalfHeight,
        out SdfAsymmetricFrustum frustum
    ) {
        // delta is EYE relative to the aperture's own origin (not the other way around): with the eye standing d
        // world units out along the outward Normal, delta = d*Normal, so dot(delta, Normal) = d > 0 — the sign this
        // depth guard and every tangent term below depends on.
        var delta = (eye - apertureOrigin);
        var depth = Vector3.Dot(vector1: delta, vector2: apertureNormal);

        if (depth < MinEyeDepth) {
            frustum = default;

            return false;
        }

        var offsetRight = Vector3.Dot(vector1: delta, vector2: apertureRight);
        var offsetUp = Vector3.Dot(vector1: delta, vector2: apertureUp);

        frustum = new SdfAsymmetricFrustum(
            Right: apertureRight,
            Up: apertureUp,
            Forward: -apertureNormal,
            HalfWidthTangent: (apertureHalfWidth / depth),
            HalfHeightTangent: (apertureHalfHeight / depth),
            CenterOffset: new Vector2(x: (-offsetRight / depth), y: (-offsetUp / depth))
        );

        return true;
    }
    /// <summary>Packs this fit into a <see cref="CameraSnapshot"/> apexed at <paramref name="eye"/> — reusing
    /// <see cref="CameraSnapshot.TanHalfFieldOfView"/>/<see cref="CameraSnapshot.AspectRatio"/> for the symmetric
    /// half-extent (<see cref="HalfHeightTangent"/> and <see cref="HalfWidthTangent"/>/<see cref="HalfHeightTangent"/>
    /// respectively — the aperture's own physical aspect ratio, independent of the render target's pixel dimensions)
    /// and returning <see cref="CenterOffset"/> separately for the caller to set on
    /// <see cref="Puck.SdfVm.SdfViewSnapshot.AsymmetricFrustumOffset"/>.</summary>
    /// <param name="eye">The frustum's apex (the same eye <see cref="TryFit"/> was fitted against).</param>
    public CameraSnapshot ToCameraSnapshot(Vector3 eye) => new(
        Position: eye,
        Right: Right,
        Up: Up,
        Forward: Forward,
        TanHalfFieldOfView: HalfHeightTangent,
        AspectRatio: (HalfWidthTangent / HalfHeightTangent)
    );
}
