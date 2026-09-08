namespace Puck.SdfVm.Views;

/// <summary>Shared render-clock easing curves, applied to an already-clamped <c>[0, 1]</c> progress fraction.</summary>
public static class Easing {
    /// <summary>The Hermite smoothstep <c>t²(3 − 2t)</c>: zero slope at both ends, monotonic in between.</summary>
    /// <param name="t">The progress fraction, in <c>[0, 1]</c>.</param>
    /// <returns>The eased fraction.</returns>
    public static float Smoothstep(float t) => ((t * t) * (3f - (2f * t)));
}
