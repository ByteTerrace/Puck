using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.SignedDistance;

/// <summary>
/// The lights one view shades: every light of the table that can reach a surface the view shows, one bit a record of
/// <see cref="SdfLights"/> in a mask the view's pass block carries. A point or spot light reaches only the sphere of its
/// range (<see cref="SdfPhotometry.RangeOf"/>), where its windowed falloff ends exactly, so a view whose frustum that
/// sphere misses cannot show any of its light and skips it; an occluder likewise past three of its radii, where its
/// Gaussian is below a ten-thousandth. A directional or rim light reaches every surface and is always kept; a light of
/// no strength is never kept. A positional light that rides a dynamic transform, whose position the host does not hold,
/// is always kept.
/// </summary>
public static class SdfLightSelection {
    /// <summary>The fraction the view's frustum sides are widened by: past any temporal jitter or render-scale rounding,
    /// so a light whose sphere only grazes the frustum is kept.</summary>
    public const float Margin = 0.05f;
    /// <summary>The occluder radii past which an occluder's attenuation is taken as none: its Gaussian is
    /// <c>e^−9</c> there.</summary>
    public const float OccluderReach = 3f;

    /// <summary>Returns the mask of the lights a view shades.</summary>
    /// <param name="lights">The frame's lights table.</param>
    /// <param name="camera">The view's camera.</param>
    /// <param name="far">The frame's far distance, in world units.</param>
    /// <returns>Bit <c>i</c> set for each light <c>i</c> below <see cref="SdfLights.Count"/> the view shades.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lights"/> is <see langword="null"/>.</exception>
    public static UInt128 Select(SdfLights lights, in CameraSnapshot camera, float far) {
        ArgumentNullException.ThrowIfNull(argument: lights);

        var mask = UInt128.Zero;

        for (var index = 0; (index < lights.Count); index++) {
            if (Reaches(camera: in camera, far: far, light: lights[index])) {
                mask |= (UInt128.One << index);
            }
        }

        return mask;
    }
    /// <summary>Returns whether a light can reach a surface a view shows.</summary>
    /// <param name="light">The light.</param>
    /// <param name="camera">The view's camera.</param>
    /// <param name="far">The frame's far distance, in world units.</param>
    /// <returns><see langword="true"/> when the view must shade the light.</returns>
    public static bool Reaches(in SdfLight light, in CameraSnapshot camera, float far) {
        if (!(light.Weight > 0f)) {
            return false;
        }

        var reach = (light.Kind switch {
            SdfLightKind.Point => light.Param,
            SdfLightKind.Occluder => (OccluderReach * light.Param),
            _ => float.PositiveInfinity,
        });

        if (float.IsPositiveInfinity(f: reach) || (light.DynamicSlot != SdfProgram.NoDynamicTransformSlot)) {
            return true;
        }
        if (!(reach > 0f)) {
            return false;
        }

        return camera.MaySee(
            center: light.Direction,
            halfX: Vector3.Zero,
            halfY: Vector3.Zero,
            halfZ: Vector3.Zero,
            margin: Margin,
            far: far,
            reach: reach
        );
    }
}
