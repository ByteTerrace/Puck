namespace Puck.SdfVm.Views;

/// <summary>The one-pole ease shared by every non-oscillating presentation lag in this library — no velocity lane,
/// so it is not expressible as a <see cref="SecondOrderResponse"/>.</summary>
public static class FirstOrderLag {
    /// <summary>Returns the blend fraction <c>α = 1 − e^(−rate·dt)</c> a caller lerps its lagged value toward its
    /// target by this step. A non-positive <paramref name="rate"/> or <paramref name="deltaSeconds"/> clamps to
    /// zero rather than reversing the ease.</summary>
    /// <param name="rate">The exponential closing rate, per second.</param>
    /// <param name="deltaSeconds">The elapsed time this step, in seconds.</param>
    /// <returns>The blend fraction, in <c>[0, 1]</c>.</returns>
    /// <remarks>Non-finite inputs stay inside the contract rather than propagating: a <see cref="float.NaN"/>
    /// <paramref name="rate"/> or <paramref name="deltaSeconds"/> is treated as non-positive and returns exactly
    /// <c>0</c>, the branch decided before either value ever reaches the product — the previous
    /// <c>MathF.Max(rate, 0f) * MathF.Max(deltaSeconds, 0f)</c> formulation clamped the SIGN but not this case, so a
    /// positive-infinity <paramref name="rate"/> with a zero <paramref name="deltaSeconds"/> (or the mirror) reached
    /// <c>Infinity·0</c> and returned <see cref="float.NaN"/>. A positive-infinity <paramref name="rate"/> or
    /// <paramref name="deltaSeconds"/> paired with the other strictly positive returns exactly <c>1</c> — immediate
    /// full catch-up — which is why the contract is the CLOSED interval <c>[0, 1]</c> rather than the half-open one
    /// every finite input alone produces.</remarks>
    public static float Alpha(float rate, float deltaSeconds) =>
        (((rate > 0f) && (deltaSeconds > 0f))
            ? (1f - MathF.Exp(x: -(rate * deltaSeconds)))
            : 0f
        );
}
