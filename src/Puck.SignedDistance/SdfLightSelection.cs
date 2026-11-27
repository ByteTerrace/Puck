using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.SignedDistance;

/// <summary>
/// The lights one view shades: every light of the table that can reach a surface the view shows, one bit a record of
/// <see cref="SdfLights"/> in a mask the view's pass block carries. A point or spot light reaches only the sphere of its
/// range (<see cref="SdfPhotometry.RangeOf"/>), where its windowed falloff ends exactly, and within it only as far as its
/// light stays above the view's cutoff at the view's exposure (<see cref="ReachAt"/>), so a view whose frustum that
/// sphere misses cannot show any of its light and skips it (a line light the capsule of its range about its segment). A
/// directional or rim light reaches every surface and is always kept; a light of no strength, a world occluder row's
/// place among them included, is never kept. A positional light that rides a dynamic transform, whose position the host
/// does not hold, is always kept. The views pass narrows this mask again for each tile it shades, by the same reaches
/// against the tile's hit points (<c>sdfViewsTileLights</c> and <c>sdfLightReachesBox</c>, which holds a point light to
/// the same cutoff). An analytic occluder (<see cref="SdfOccluder"/>) is selected the same way into a mask of its own
/// (<see cref="SelectOccluders(SdfOccluders, in CameraSnapshot, float)"/>): past its <see cref="SdfOccluder.Reach"/> it adds
/// nothing.
/// </summary>
public static class SdfLightSelection {
    /// <summary>The fraction the view's frustum sides are widened by: past any temporal jitter or render-scale rounding,
    /// so a light whose sphere only grazes the frustum is kept.</summary>
    public const float Margin = 0.05f;
    /// <summary>The share a point light's reach at an exposure is widened by, past the rounding between it and the
    /// shading's own fade at the view's cutoff (<c>sdfLightCutoffFade</c>), which the tile test widens by the same
    /// share (<c>SdfLightReachMargin</c>).</summary>
    public const float CutoffMargin = 1.0e-3f;

    /// <summary>Returns the mask of the lights a view shades.</summary>
    /// <param name="lights">The frame's lights table.</param>
    /// <param name="camera">The view's camera.</param>
    /// <param name="far">The frame's far distance, in world units.</param>
    /// <param name="exposure">The view's exposure multiplier, the one its pass block carries
    /// (<c>passGroup.exposure</c>), past which a point light's light falls under the view's cutoff
    /// (<see cref="ReachAt"/>); a value that is not positive cuts nothing.</param>
    /// <returns>Bit <c>i</c> set for each light <c>i</c> below <see cref="SdfLights.Count"/> the view shades.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lights"/> is <see langword="null"/>.</exception>
    public static UInt128 Select(SdfLights lights, in CameraSnapshot camera, float far, float exposure) {
        ArgumentNullException.ThrowIfNull(argument: lights);

        var mask = UInt128.Zero;

        for (var index = 0; (index < lights.Count); index++) {
            if (Reaches(camera: in camera, exposure: exposure, far: far, light: lights[index])) {
                mask |= (UInt128.One << index);
            }
        }

        return mask;
    }
    /// <summary>Returns the mask of the analytic occluders a view shades: each whose sphere of reach
    /// (<see cref="SdfOccluder.Reach"/> about its center) the view's frustum, widened by <see cref="Margin"/>, may see,
    /// since an occluder adds nothing past its reach.</summary>
    /// <param name="occluders">The frame's occluders table.</param>
    /// <param name="camera">The view's camera.</param>
    /// <param name="far">The frame's far distance, in world units.</param>
    /// <returns>Bit <c>i</c> set for each occluder <c>i</c> below <see cref="SdfOccluders.Count"/> the view
    /// shades.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="occluders"/> is <see langword="null"/>.</exception>
    public static UInt128 SelectOccluders(SdfOccluders occluders, in CameraSnapshot camera, float far) {
        ArgumentNullException.ThrowIfNull(argument: occluders);

        var mask = UInt128.Zero;
        var records = occluders.Records;

        for (var index = 0; (index < records.Length); index++) {
            if (camera.MaySee(center: records[index].Center, halfX: Vector3.Zero, halfY: Vector3.Zero, halfZ: Vector3.Zero, margin: Margin, far: far, reach: records[index].Reach)) {
                mask |= (UInt128.One << index);
            }
        }

        return mask;
    }
    /// <summary>Returns the distance from its position past which a light lights nothing: a point's or a line's range,
    /// and infinity for a light that reaches every surface.</summary>
    /// <param name="light">The light.</param>
    /// <returns>The reach, in world units.</returns>
    public static float ReachOf(in SdfLight light) => (light.Kind switch {
        SdfLightKind.Point or SdfLightKind.Line => light.Param,
        _ => float.PositiveInfinity,
    });
    /// <summary>Returns the distance from its position past which a light adds nothing a view at an exposure shows:
    /// <see cref="ReachOf"/>, and for a point or spot light no farther than where it gives a white surface facing it the
    /// view's cutoff (<see cref="SdfPhotometry.RangeOf"/> at the view's exposure, widened by <see cref="CutoffMargin"/>).
    /// Its window and cone are at most one, so its light at a distance <c>d</c> is at most its weight over
    /// <c>d²</c>, and the shading fades it to nothing under the cutoff (<c>sdfLightCutoffFade</c>).</summary>
    /// <param name="light">The light.</param>
    /// <param name="exposure">The view's exposure multiplier; a value that is not positive cuts nothing.</param>
    /// <returns>The reach, in world units.</returns>
    public static float ReachAt(in SdfLight light, float exposure) {
        var reach = ReachOf(light: in light);

        if (!(exposure > 0f)) {
            return reach;
        }

        return light.Kind switch {
            SdfLightKind.Point => MathF.Min(x: reach, y: (SdfPhotometry.RangeOf(exposure: exposure, weight: light.Weight) * (1f + CutoffMargin))),
            SdfLightKind.Line => MathF.Min(x: reach, y: (SdfPhotometry.LineRangeOf(exposure: exposure, length: light.LineLength, weight: light.Weight) * (1f + CutoffMargin))),
            _ => reach,
        };
    }
    /// <summary>Returns whether a light can reach a surface a view shows.</summary>
    /// <param name="light">The light.</param>
    /// <param name="camera">The view's camera.</param>
    /// <param name="far">The frame's far distance, in world units.</param>
    /// <param name="exposure">The view's exposure multiplier (<see cref="ReachAt"/>); a value that is not positive cuts
    /// nothing.</param>
    /// <returns><see langword="true"/> when the view must shade the light.</returns>
    public static bool Reaches(in SdfLight light, in CameraSnapshot camera, float far, float exposure) {
        if (!(light.Weight > 0f)) {
            return false;
        }

        var reach = ReachAt(exposure: exposure, light: in light);

        if (float.IsPositiveInfinity(f: reach) || (light.DynamicSlot != SdfProgram.NoDynamicTransformSlot)) {
            return true;
        }
        if (!(reach > 0f)) {
            return false;
        }

        // A line's reach is about its segment: the box of no width along the half segment, widened by the reach.
        return camera.MaySee(
            center: light.Direction,
            halfX: ((light.Kind == SdfLightKind.Line) ? light.SpotAxis : Vector3.Zero),
            halfY: Vector3.Zero,
            halfZ: Vector3.Zero,
            margin: Margin,
            far: far,
            reach: reach
        );
    }
}
