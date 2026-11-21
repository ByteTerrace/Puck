using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.SignedDistance.Queries;

namespace Puck.World.Client;

/// <summary>What a ray through a camera meets in a presentation query field, and the surface's normal there: the one
/// reading the boot frame's pick, a routed scene's pick and the editor's pointer share. Presentation only.</summary>
public static class WorldFieldSurface {
    /// <summary>Finds the surface a ray meets in a static field, marched in fixed point out to a far distance measured
    /// from the camera the ray was cast through, so a pick through a window lands on the surface the window
    /// shows.</summary>
    /// <param name="field">The field, or <see langword="null"/> for none, which meets nothing.</param>
    /// <param name="ray">The ray, in the field's space.</param>
    /// <param name="from">The position of the camera the ray was cast through, which the far distance is measured from.</param>
    /// <param name="farDistance">The far distance, in world units.</param>
    /// <param name="point">The point the ray meets, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the ray proves a surface within the far distance; a march that ends without
    /// proving one answers <see langword="false"/>.</returns>
    public static bool TryMeet(SdfFieldEvaluator? field, SourceRay ray, Vector3 from, double farDistance, out FixedPosition point) {
        point = default;

        // The ray's origin already lies this far along the line from the camera.
        var reach = (FixedQ4816.FromDouble(value: farDistance) - (ray.Origin - FixedVector3.FromVector3(value: from)).Length);

        if (
            (field is null) ||
            (reach <= FixedQ4816.Zero) ||
            !field.Raycast(
                dir: ray.Direction,
                hit: out var hit,
                maxDist: reach,
                origin: FixedPosition.FromLocal(local: ray.Origin)
            ) ||
            (hit.Confidence != WorldQueryConfidence.Exact)
        ) {
            return false;
        }

        point = hit.Point;

        return true;
    }
    /// <summary>Reads a field's unit surface normal at a point: its gradient there.</summary>
    /// <param name="field">The field.</param>
    /// <param name="point">The point, on or near a surface.</param>
    /// <returns>The unit normal, or zero where the field gives no gradient.</returns>
    public static Vector3 NormalAt(SdfFieldEvaluator field, FixedPosition point) {
        ArgumentNullException.ThrowIfNull(argument: field);

        if (!field.TryFieldGradient(gradient: out var gradient, position: point)) {
            return Vector3.Zero;
        }

        var unit = Vector3.Normalize(value: gradient.ToVector3());

        return (VectorFunctions.IsFinite(vector: unit) ? unit : Vector3.Zero);
    }
}
