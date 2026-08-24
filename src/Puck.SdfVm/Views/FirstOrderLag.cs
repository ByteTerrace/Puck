namespace Puck.SdfVm.Views;

/// <summary>The one-pole ease shared by every non-oscillating presentation lag in this library — no velocity lane,
/// so it is not expressible as a <see cref="SecondOrderResponse"/>.</summary>
public static class FirstOrderLag {
    /// <summary>Returns the blend fraction <c>α = 1 − e^(−rate·dt)</c> a caller lerps its lagged value toward its
    /// target by this step. A non-positive <paramref name="rate"/> or <paramref name="deltaSeconds"/> clamps to
    /// zero rather than reversing the ease.</summary>
    /// <param name="rate">The exponential closing rate, per second.</param>
    /// <param name="deltaSeconds">The elapsed time this step, in seconds.</param>
    /// <returns>The blend fraction, in <c>[0, 1)</c>.</returns>
    public static float Alpha(float rate, float deltaSeconds) => (1f - MathF.Exp(x: (-MathF.Max(
        x: rate,
        y: 0f
    ) * MathF.Max(
        x: deltaSeconds,
        y: 0f
    ))));
}
