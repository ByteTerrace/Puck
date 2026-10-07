using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The shared directional-shadow anchor rule for image history and residency light maps.</summary>
public static class SdfLightMotion {
    /// <summary>Tests whether a light left the retained one-eighth-penumbra anchor or changed its penumbra.</summary>
    /// <param name="direction">The current direction toward the light.</param>
    /// <param name="penumbra">The current angular-radius tangent.</param>
    /// <param name="anchorDirection">The retained unit direction.</param>
    /// <param name="anchorPenumbra">The retained angular-radius tangent.</param>
    /// <returns>Whether the shadow representation must be rebuilt. The anchor moves only after a full publication.</returns>
    public static bool Changed(Vector3 direction, float penumbra, Vector3 anchorDirection, float anchorPenumbra) {
        var unit = SdfLights.UnitDirection(direction: direction);
        var angle = (Math.Atan(d: Math.Min(val1: penumbra, val2: anchorPenumbra)) / 8.0);
        var delta = (unit - anchorDirection);
        var chord = (2.0 * Math.Sin(a: (angle / 2.0)));

        return ((penumbra != anchorPenumbra) || (Vector3.Dot(vector1: delta, vector2: delta) > (chord * chord)));
    }
}
