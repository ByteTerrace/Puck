namespace Puck.World;

// Selection and easing are shared by value evaluation and the compiler of exact rate integrals.
internal static class WorldKeyInterpolation {
    public static ReadOnlySpan<double> ControlPoints(WorldKeyEase ease) => ease switch {
        WorldKeyEase.Smooth => [0d, 0d, 1d, 1d],
        WorldKeyEase.Step => [0d],
        _ => [0d, 1d],
    };

    public static double Ease(WorldKeyEase ease, double fraction) {
        return WorldRatePolynomial.Evaluate(ControlPoints(ease), fraction);
    }

    public static void Select<T>(WorldKeys<T> curve, double span, double at, out WorldKey<T> from, out WorldKey<T> to, out double duration, out double offset) {
        var index = curve.Keys.Count - 1;
        for (var candidate = 0; candidate < curve.Keys.Count; candidate++) {
            if (curve.Keys[candidate].At > at) { break; }
            index = candidate;
        }
        from = curve.Keys[index];
        to = curve.Keys[(index + 1) % curve.Keys.Count];
        duration = to.At - from.At;
        if (duration <= 0d) { duration += span; }
        offset = at - from.At;
        if (offset < 0d) { offset += span; }
    }
}
