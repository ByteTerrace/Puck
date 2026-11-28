using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.SignedDistance;

/// <summary>
/// The lights one view shades: every light of the table that can reach a surface the view shows, one bit a record of
/// <see cref="SdfLights"/> in a mask the view's pass block carries. A point or spot light reaches only the sphere of its
/// range (<see cref="SdfPhotometry.RangeOf"/>), where its windowed falloff ends exactly, and within it only as far as its
/// light stays above the view's cutoff at the view's exposure (<see cref="ReachAt"/>), so a view whose frustum that
/// sphere misses cannot show any of its light and skips it (a line light the capsule of its range about its segment, a
/// screen's or an emitter face's light, <see cref="SdfLightKind.Face"/>, the sphere of its range about its center). A
/// directional or rim light reaches every surface and is always kept; a light of no strength, a world occluder row's
/// place among them included, is never kept. A positional light that rides a dynamic transform, whose position the host
/// does not hold, is always kept. The views pass narrows this mask again for each tile it shades, by the same reaches
/// against the tile's hit points (<c>sdfViewsTileLights</c> and <c>sdfLightReachesBox</c>, which holds a point light to
/// the same cutoff). An analytic occluder (<see cref="SdfOccluder"/>) is selected into a mask of its own
/// (<see cref="SelectOccluders"/>): by its <see cref="SdfOccluder.Reach"/>, past which its ambient term adds nothing, and
/// by where its shadow toward a light the view shades can fall.
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
    /// (<see cref="SdfOccluder.Reach"/> about its center), past which its ambient term adds nothing, the view's frustum,
    /// widened by <see cref="Margin"/>, may see, and each whose shadow toward a ranged light the view shades (a point,
    /// spot, line or face light) may fall where the frustum sees (<see cref="ShadowMaySee"/>). The views pass narrows the
    /// mask again for each tile, by the same two rules (<c>sdfOccluderReachesWalk</c>).</summary>
    /// <param name="occluders">The frame's occluders table.</param>
    /// <param name="lights">The frame's lights table.</param>
    /// <param name="lightMask">The lights the view shades (<see cref="Select"/>).</param>
    /// <param name="camera">The view's camera.</param>
    /// <param name="far">The frame's far distance, in world units.</param>
    /// <param name="exposure">The view's exposure multiplier the light mask was selected at (<see cref="ReachAt"/>).</param>
    /// <returns>Bit <c>i</c> set for each occluder <c>i</c> below <see cref="SdfOccluders.Count"/> the view
    /// shades.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="occluders"/> or <paramref name="lights"/> is
    /// <see langword="null"/>.</exception>
    public static UInt128 SelectOccluders(SdfOccluders occluders, SdfLights lights, UInt128 lightMask, in CameraSnapshot camera, float far, float exposure) {
        ArgumentNullException.ThrowIfNull(argument: occluders);
        ArgumentNullException.ThrowIfNull(argument: lights);

        var mask = UInt128.Zero;
        var records = occluders.Records;

        for (var index = 0; (index < records.Length); index++) {
            if (camera.MaySee(center: records[index].Center, halfX: Vector3.Zero, halfY: Vector3.Zero, halfZ: Vector3.Zero, margin: Margin, far: far, reach: records[index].Reach)
                || ShadowMaySee(camera: in camera, exposure: exposure, far: far, lightMask: lightMask, lights: lights, occluder: in records[index])) {
                mask |= (UInt128.One << index);
            }
        }

        return mask;
    }
    /// <summary>Returns whether an occluder's shadow toward a ranged light of a view's light mask (a point, spot, line or
    /// face light) may fall where the view's frustum sees. <c>sdfOccludersShadow</c> darkens a point only where the segment from it to the
    /// light passes the occluder's bounding sphere by less than the penumbra's half width, the light's radius plus the
    /// segment's length times the occluder's <see cref="SdfOccluder.Softness"/> at most; a point the light reaches lies
    /// within its reach (<see cref="ReachAt"/>) of its segment. So the shadowed points lie in the cone from the light through
    /// the occluder's sphere grown by that width, out to the reach, which a capsule from the occluder's center to the
    /// cone's far end, as wide as the cone there and the grown sphere together, holds. A light riding a dynamic transform,
    /// whose position the host does not hold, keeps every occluder that casts a shadow.</summary>
    /// <param name="occluder">The occluder.</param>
    /// <param name="lights">The frame's lights table.</param>
    /// <param name="lightMask">The lights the view shades.</param>
    /// <param name="camera">The view's camera.</param>
    /// <param name="far">The frame's far distance, in world units.</param>
    /// <param name="exposure">The view's exposure multiplier.</param>
    /// <returns><see langword="true"/> when the view must shade the occluder for its shadow.</returns>
    public static bool ShadowMaySee(in SdfOccluder occluder, SdfLights lights, UInt128 lightMask, in CameraSnapshot camera, float far, float exposure) {
        ArgumentNullException.ThrowIfNull(argument: lights);

        if (!(occluder.Shadow > 0f)) {
            return false;
        }

        var bound = occluder.BoundingRadius;

        for (var bits = lightMask; (bits != UInt128.Zero); bits &= (bits - UInt128.One)) {
            var index = (int)UInt128.TrailingZeroCount(value: bits);

            if (index >= lights.Count) {
                break;
            }

            var light = lights[index];

            if (!light.IsRanged) {
                continue;
            }
            if (light.RidesSlot) {
                return true;
            }

            var reach = ReachAt(exposure: exposure, light: in light);

            if (!(reach > 0f)) {
                continue;
            }
            // An opening's face reaches every distance, so its shadow may fall anywhere.
            if (float.IsPositiveInfinity(f: reach)) {
                return true;
            }

            // A line's points lie within its half segment of its midpoint, and its disc and a face's are as wide as
            // sdfLightInputs takes them (SourceRadius).
            var extent = ((light.Kind == SdfLightKind.Line) ? light.SpotAxis.Length() : 0f);
            var radius = light.SourceRadius;
            var length = (reach + (2f * extent));
            // The shader's least penumbra half width, 1e-4 m, beside the widest.
            var grown = (((((bound + radius) + (length * occluder.Softness)) + 1.0e-4f) + extent) * (1f + CutoffMargin));
            var toOccluder = (occluder.Center - light.Direction);
            var distance = toOccluder.Length();

            if (distance >= (length + grown)) {
                continue;
            }
            if (distance <= grown) {
                if (camera.MaySee(center: light.Direction, halfX: Vector3.Zero, halfY: Vector3.Zero, halfZ: Vector3.Zero, margin: Margin, far: far, reach: (reach + extent))) {
                    return true;
                }

                continue;
            }

            var end = (light.Direction + (toOccluder * (length / distance)));
            var width = (((length * grown) / MathF.Sqrt(x: ((distance * distance) - (grown * grown)))) + grown);

            if (camera.MaySee(center: ((occluder.Center + end) * 0.5f), halfX: ((end - occluder.Center) * 0.5f), halfY: Vector3.Zero, halfZ: Vector3.Zero, margin: Margin, far: far, reach: width)) {
                return true;
            }
        }

        return false;
    }
    /// <summary>Returns the distance from its position past which a light lights nothing: a point's, a line's or a face's
    /// range, and infinity for a light that reaches every surface.</summary>
    /// <param name="light">The light.</param>
    /// <returns>The reach, in world units.</returns>
    public static float ReachOf(in SdfLight light) => (light.IsRanged ? light.Param : float.PositiveInfinity);
    /// <summary>Returns the distance from its position past which a light adds nothing a view at an exposure shows:
    /// <see cref="ReachOf"/>, and for a point or spot light no farther than where it gives a white surface facing it the
    /// view's cutoff (<see cref="SdfPhotometry.RangeOf"/> at the view's exposure, widened by <see cref="CutoffMargin"/>).
    /// Its window and cone are at most one, so its light at a distance <c>d</c> is at most its weight over
    /// <c>d²</c>, and the shading fades it to nothing under the cutoff (<c>sdfLightCutoffFade</c>). A face's luminance is
    /// the GPU's reduction, which the host does not hold, so its range alone bounds it here and each tile holds it to the
    /// cutoff (<c>sdfLightReachesBox</c>).</summary>
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

        if (float.IsPositiveInfinity(f: reach) || light.RidesSlot) {
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
