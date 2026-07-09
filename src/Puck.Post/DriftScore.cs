namespace Puck.Post;

/// <summary>
/// The composite DRIFT SCORE the fuzz HUNT ranks candidates by — a single number where HIGHER means more
/// cross-backend divergence that MATTERS (a genuine backend disagreement), not more benign ±1-LSB dither. The gate
/// (<see cref="FuzzStage"/> / <c>WorldFuzz</c>) asks the yes/no question "is this benign?"; the hunt instead needs an
/// ORDERING over candidates, so it folds the same <see cref="ParityMetrics"/> the gate reads into one explicit,
/// documented formula. The three signals and their weights (chosen so a purely-benign ±1 scene scores near zero while
/// a real region divergence dominates):
/// <list type="number">
///   <item><b>mean term</b> = <see cref="ParityMetrics.MeanAbsError"/> × 100. The load-bearing "real divergence"
///   signal: benign worst is ~0.06, a real relocated/recolored region lands in multiples of 1.0, so ×100 puts it at
///   O(1..30) — the dominant term when something genuinely diverges.</item>
///   <item><b>cluster term</b> = the NON-ISOLATED differing-pixel percentage
///   (<c>(1 − isolatedFraction) × percentDiffering</c>) × 3.0. Benign ±1 codegen noise is near-fully isolated
///   (isolation→1 ⇒ cluster→0); a real divergence CLUMPS into contiguous regions, so clustered mass is THE signature
///   that separates a genuine disagreement from FP dither — weighted highest per unit.</item>
///   <item><b>spread term</b> = <see cref="ParityMetrics.PercentDiffering"/> × 0.25. A mild reward for breadth (a
///   wrong layout spreads) kept small so benign gradient-band dither — which legitimately reaches ~6–8% at pure ±1 —
///   cannot dominate the ranking on its own.</item>
///   <item><b>magnitude term</b> = <c>min(maxChannelDelta, 96) / 96</c>. A capped nudge in [0, 1]: an isolated
///   material-winner flip is legitimately large, so the cap stops ONE Δ255 pixel from dominating — magnitude only
///   breaks ties between otherwise comparable candidates.</item>
/// </list>
/// Pure and allocation-free, so the child render process can score in-line and print the result.
/// </summary>
internal static class DriftScore {
    private const int MagnitudeCap = 96;

    /// <summary>The non-isolated differing-pixel percentage — the clustered mass the score weights highest.</summary>
    /// <param name="metrics">The measured parity metrics.</param>
    /// <returns>The clustered-differing percentage of the frame.</returns>
    public static double ClusterPercent(ParityMetrics metrics) {
        ArgumentNullException.ThrowIfNull(metrics);

        return ((1.0 - metrics.IsolatedFraction) * metrics.PercentDiffering);
    }

    /// <summary>Computes the composite drift score for one candidate's cross-backend metrics.</summary>
    /// <param name="metrics">The measured parity metrics.</param>
    /// <returns>The composite score (higher = more meaningful divergence).</returns>
    public static double Compute(ParityMetrics metrics) {
        ArgumentNullException.ThrowIfNull(metrics);

        var meanTerm = (metrics.MeanAbsError * 100.0);
        var clusterTerm = (ClusterPercent(metrics: metrics) * 3.0);
        var spreadTerm = (metrics.PercentDiffering * 0.25);
        var magnitudeTerm = (Math.Min(metrics.MaxChannelDelta, MagnitudeCap) / (double)MagnitudeCap);

        return (meanTerm + clusterTerm + spreadTerm + magnitudeTerm);
    }
}
