namespace Puck.Bench;

/// <summary>
/// The versioned scoring model: the single owner of reference constants, formulas, and the formula-version
/// string live, so two reports are comparable if and only if their <see cref="ScoreFormula"/> strings match. Any edit
/// to a formula, reference constant, or scored scene list requires a new <see cref="ScoreFormula"/> value.
/// Per-scene throughput is the arithmetic mean of frame times inverted
/// (true delivered throughput); the overall is the geometric mean of per-scene ratios, so a regression in ANY scene
/// moves the composite the same relative amount regardless of that scene's absolute FPS — no scene hides behind
/// another.
/// </summary>
public static class BenchScoreModel {
    /// <summary>The scoring-formula version embedded in reports and printed with scores. Reports are comparable only
    /// when this value matches.</summary>
    public const string ScoreFormula = "puck.bench.score/2";

    /// <summary>The reference-configuration label the overall score is measured against.</summary>
    public const string ReferenceLabel = "rtx4070/vulkan/native/1280x800";

    /// <summary>The spike threshold multiplier over a scene's median interval (§6): an interval greater than this
    /// times the median is a spike, excluded from throughput scoring but kept in the tail.</summary>
    public const double SpikeFactor = 4.0;

    /// <summary>The DVFS-canary drift fraction (§6): a scored scene whose beam-pass LAST-THIRD median drifts more than
    /// this from its OWN FIRST-THIRD median (a within-scene, constant-workload clock-sag signal) is flagged
    /// <c>canaryDrift</c>.</summary>
    public const double CanaryDriftFraction = 0.10;

    // Calibrated reference FPS per v1 scene, frozen on the reference box (RTX 4070, Vulkan, native tier,
    // 1280×800, default switches, headless immediate-mode), each the MEDIAN of the scene's delivered throughput
    // (wall.meanFps) across three back-to-back suite runs, so the reference machine reports ≈ the memorable 10000
    // overall by construction. The three pace-capped synthetics (sdf.shapes, sdf.ops, and sdf.carves) run at
    // the display-tier ceiling (~162 fps) rather than a raw GPU rate: their sub-present-slot GPU frame (carves' baked
    // frame is 3.31 ms) measures the pacer's honest automatic-for-this-display cadence (stable to the LSB across runs).
    // display-tier ceiling (~162 fps). The sdf.carves workload bakes its analytic carve cluster into one brick and is
    // therefore pace-capped like the other inexpensive synthetics. Any edit to a value here, a weight, or the scored
    // scene list must change ScoreFormula so incompatible reports cannot be compared silently.
    private static readonly IReadOnlyDictionary<string, double> s_referenceFps = new Dictionary<string, double>(comparer: StringComparer.Ordinal) {
        ["room.flythrough"] = 56.5,
        ["room.active"] = 21.5,
        ["sdf.shapes"] = 162.1,
        ["sdf.ops"] = 162.1,
        ["sdf.carves"] = 162.1,
        ["sdf.storm"] = 3.4,
        ["sdf.instances"] = 48.7,
    };

    /// <summary>The reference machine's target overall score — the reference configuration reports this by
    /// construction (per-scene 1000s decompose it legibly).</summary>
    public const int ReferenceOverall = 10000;

    /// <summary>Per-scene delivered throughput: <c>1000 × N / Σ(intervalMs)</c> over the <paramref name="sampleCount"/>
    /// non-spike frames — the arithmetic mean of frame times, inverted (equivalently the harmonic mean of instantaneous
    /// FPS). This is what a score integrates; medians/percentiles are reported, never scored.</summary>
    /// <param name="sampleCount">The number of non-spike sampled frames.</param>
    /// <param name="sumIntervalMs">The sum of those frames' intervals, in milliseconds.</param>
    /// <returns>The scene's delivered FPS, or 0 when nothing valid was sampled.</returns>
    public static double SceneFps(int sampleCount, double sumIntervalMs) =>
        (((sampleCount > 0) && (sumIntervalMs > 0.0)) ? ((1000.0 * sampleCount) / sumIntervalMs) : 0.0);

    /// <summary>Looks up a scene's provisional reference FPS.</summary>
    /// <param name="sceneName">The scene's dotted name.</param>
    /// <param name="referenceFps">The reference FPS, or 0 when the scene has no calibration entry.</param>
    /// <returns>Whether a reference FPS is known for the scene (an unscored/warmup scene returns
    /// <see langword="false"/>).</returns>
    public static bool TryGetReferenceFps(string sceneName, out double referenceFps) =>
        s_referenceFps.TryGetValue(key: sceneName, value: out referenceFps);

    /// <summary>Per-scene score: <c>1000 × sceneFps / refFps</c> — the reference machine scores 1000 per scene, so a
    /// scene's score IS a percentage ×10 against reference, readable at a glance. Zero when no reference is known.</summary>
    /// <param name="sceneFps">The scene's measured delivered FPS.</param>
    /// <param name="referenceFps">The scene's reference FPS.</param>
    /// <returns>The rounded per-scene score.</returns>
    public static int SceneScore(double sceneFps, double referenceFps) =>
        ((referenceFps > 0.0) ? (int)Math.Round(a: ((1000.0 * sceneFps) / referenceFps)) : 0);

    /// <summary>The overall score: <c>round(10000 × Π (sceneFps_i / refFps_i)^w_i)</c> with <c>Σ w_i = 1</c> over the
    /// scored scenes (weight-0 warmup excluded). Geometric mean of per-scene ratios, weighted. The weights are the
    /// callers' (the scene descriptors'); this method only combines the already-normalized <paramref name="terms"/>.</summary>
    /// <param name="terms">Per scored scene: its measured FPS, its reference FPS, and its weight.</param>
    /// <returns>The rounded overall score, or 0 when there are no scored terms or the weights sum to zero.</returns>
    public static int Overall(IReadOnlyList<(double sceneFps, double referenceFps, double weight)> terms) {
        var weightSum = 0.0;

        for (var index = 0; (index < terms.Count); index++) {
            weightSum += terms[index].weight;
        }

        if (weightSum <= 0.0) {
            return 0;
        }

        // Σ w_i · ln(ratio_i) in log space, weights renormalized to sum to 1 (defensive: the v1 roster sums to 1
        // already, but a partial/failed suite must still combine what it has coherently).
        var logAccumulator = 0.0;

        for (var index = 0; (index < terms.Count); index++) {
            var (sceneFps, referenceFps, weight) = terms[index];

            if ((referenceFps <= 0.0) || (sceneFps <= 0.0) || (weight <= 0.0)) {
                continue;
            }

            logAccumulator += ((weight / weightSum) * Math.Log(d: (sceneFps / referenceFps)));
        }

        return (int)Math.Round(a: (10000.0 * Math.Exp(d: logAccumulator)));
    }
}
