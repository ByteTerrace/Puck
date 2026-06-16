namespace Puck.Compositing;

public static class TransitionCurves {
    private const float OvershootTension = 1.70158f;

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
