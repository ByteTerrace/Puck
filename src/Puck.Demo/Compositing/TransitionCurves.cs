namespace Puck.Demo.Compositing;

/// <summary>Easing functions for layout transitions, keyed by <see cref="TransitionCurve"/>.</summary>
internal static class TransitionCurves {
    private const float OvershootTension = 1.70158f;

    /// <summary>Eases a linear progress in [0, 1] by the given curve. <see cref="TransitionCurve.Warp"/>
    /// shares the quadratic ease for the rect motion (its ripple is applied in the compositor shader).</summary>
    public static float Ease(float progress, TransitionCurve curve) {
        var t = Math.Clamp(
            max: 1f,
            min: 0f,
            value: progress
        );

        return curve switch {
            TransitionCurve.Linear => t,
            TransitionCurve.Overshoot => Overshoot(t: t),
            _ => EaseInOutQuadratic(t: t),
        };
    }

    private static float EaseInOutQuadratic(float t) {
        return ((t < 0.5f)
            ? ((2f * t) * t)
            : (1f - ((((-2f * t) + 2f) * ((-2f * t) + 2f)) / 2f)));
    }
    private static float Overshoot(float t) {
        var u = (t - 1f);

        return ((1f + ((((OvershootTension + 1f) * u) * u) * u)) + ((OvershootTension * u) * u));
    }
}
