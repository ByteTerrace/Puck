namespace Puck.Demo;

/// <summary>
/// A conjunctive set of PASS thresholds for one parity comparison. Every active threshold must hold;
/// <see cref="Evaluate"/> returns the ones that tripped (empty = pass). A threshold is disabled by a sentinel:
/// <see cref="MaxChannelDelta"/> at 255 or <see cref="MinUnitDeltaFraction"/> at 0 never trips.
/// </summary>
internal sealed record ParityThresholdSet {
    /// <summary>Gets the largest allowed single-channel delta (255 disables the check).</summary>
    public required int MaxChannelDelta { get; init; }
    /// <summary>Gets the largest allowed differing-pixel percentage.</summary>
    public required double MaxPercentDiffering { get; init; }
    /// <summary>Gets the smallest allowed fraction of differing pixels whose delta is exactly 1 (0 disables the check).</summary>
    public required double MinUnitDeltaFraction { get; init; }
    /// <summary>Gets the smallest allowed isolated fraction.</summary>
    public required double MinIsolatedFraction { get; init; }
    /// <summary>Gets the largest allowed mean max-channel delta over all pixels.</summary>
    public required double MaxMeanAbsError { get; init; }

    /// <summary>Evaluates the metrics against every active threshold and returns a description of each that
    /// tripped. An empty list means this comparison passed.</summary>
    /// <param name="metrics">The measured parity metrics.</param>
    /// <returns>The tripped thresholds, or an empty list on pass.</returns>
    public IReadOnlyList<string> Evaluate(ParityMetrics metrics) {
        ArgumentNullException.ThrowIfNull(metrics);

        var failures = new List<string>();

        if (metrics.MaxChannelDelta > MaxChannelDelta) {
            failures.Add(item: $"maxChannelDelta {metrics.MaxChannelDelta} > {MaxChannelDelta}");
        }

        if (metrics.PercentDiffering > MaxPercentDiffering) {
            failures.Add(item: $"percentDiffering {metrics.PercentDiffering:0.####}% > {MaxPercentDiffering}%");
        }

        if (metrics.UnitDeltaFraction < MinUnitDeltaFraction) {
            failures.Add(item: $"unitDeltaFraction {metrics.UnitDeltaFraction:0.####} < {MinUnitDeltaFraction}");
        }

        if (metrics.IsolatedFraction < MinIsolatedFraction) {
            failures.Add(item: $"isolatedFraction {metrics.IsolatedFraction:0.####} < {MinIsolatedFraction}");
        }

        if (metrics.MeanAbsError > MaxMeanAbsError) {
            failures.Add(item: $"meanAbsError {metrics.MeanAbsError:0.######} > {MaxMeanAbsError}");
        }

        return failures;
    }
}

/// <summary>
/// The PASS thresholds for the cross-backend parity gate, by view-mode flavour. Calibrated against the measured
/// RTX 4070 baseline — <c>~0.13%</c> of pixels differ by exactly <c>±1/255</c>, ~99% of those spatially isolated
/// single-pixel flips (driver-level FP codegen that cannot be forced equal across APIs). GPU-specific (tuned on
/// one box) but overridable; ~4× headroom over the baseline so benign noise passes while a real divergence —
/// which spreads into contiguous regions or shows larger deltas — trips at least one threshold.
/// </summary>
internal static class ParityThresholds {
    /// <summary>The strict thresholds for continuous-shading views (final, depth, normals, raydir), where the
    /// only cross-backend residual is ±1-LSB quantization noise.</summary>
    public static readonly ParityThresholdSet Continuous = new() {
        MaxChannelDelta = 1, // ±1-LSB noise; above 1 is a real divergence.
        MaxMeanAbsError = 0.05, // ±1 on a fraction of pixels keeps this far below 0.05.
        MaxPercentDiffering = 0.5, // ~4× over the measured 0.13%.
        MinIsolatedFraction = 0.90, // benign noise is ~99% isolated; a bug clumps.
        MinUnitDeltaFraction = 0.99, // benign noise is entirely ±1.
    };

    /// <summary>The thresholds for the full compute SDF <em>world composite</em> (the <c>--validate-world</c> gate).
    /// Same continuous-shading flavour as <see cref="Continuous"/> — every residual is an isolated ±1-LSB flip — but
    /// the richer scene (up to four cameras, fog/sky gradients, the child surface) puts measurably more pixels in
    /// the 1/255 transition bands: the RTX 4070 baseline is ~0.37% (single/child) to ~0.57% (4-camera split)
    /// differing, ~93–96% isolated, all ±1. So only the spread cap is relaxed (~3.5× over the worst measured split);
    /// the max-delta, isolation, unit-delta, and mean guards — which a real divergence (contiguous, multi-LSB, or
    /// clustered) trips — stay strict.</summary>
    public static readonly ParityThresholdSet WorldComposite = new() {
        MaxChannelDelta = 1, // ±1-LSB noise; above 1 is a real divergence.
        MaxMeanAbsError = 0.05, // ±1 on <1% of pixels keeps this far below 0.05.
        MaxPercentDiffering = 2.0, // ~3.5× over the measured 0.57% split baseline; benign noise scales with scene richness.
        MinIsolatedFraction = 0.85, // measured 93–96% isolated; a clustered bug collapses well below this.
        MinUnitDeltaFraction = 0.99, // benign noise is entirely ±1.
    };

    /// <summary>The thresholds for cross-backend DIFFERENTIAL FUZZING of the SDF world (<c>--validate-world
    /// --fuzz-seed</c>). Fuzz-generated scenes span the whole input space, so the benign ±1-LSB codegen residual is
    /// legitimately MORE clustered and widespread than the hand-tuned showcase: it follows the large smooth
    /// ground-plane gradients and the cone-march banding (thin contiguous ±1 bands), which collapses the
    /// isolated-fraction and lifts the spread far below what those showcase-calibrated guards expect. So the fuzz
    /// oracle leans on the DEFINITIVE benign signature instead — every difference is exactly ±1 LSB
    /// (<c>MaxChannelDelta</c> = 1 and <c>MinUnitDeltaFraction</c> = 0.99 stay strict) — and disables
    /// the gradient-structure-dependent isolation guard while widening the spread cap. A real divergence (a wrong
    /// shape/blend/material renders a region with multi-LSB deltas) trips the max-delta and unit-delta guards
    /// regardless of how the benign noise happens to cluster.</summary>
    public static readonly ParityThresholdSet WorldFuzz = new() {
        MaxChannelDelta = 1, // the key guard: any ≥2 delta is a real divergence (benign codegen is exactly ±1).
        MaxMeanAbsError = 0.05, // ±1 on a minority of pixels stays far below this; a real bug lifts the mean.
        MaxPercentDiffering = 8.0, // wide: gradient/banding-heavy fuzz scenes put many pixels in ±1 transition bands.
        MinIsolatedFraction = 0.0, // disabled: benign ±1 noise follows gradient bands and is legitimately clustered.
        MinUnitDeltaFraction = 0.99, // the co-guard: benign codegen noise is entirely ±1, so a real bug drops this.
    };

    /// <summary>The relaxed thresholds for discrete views (material-id palette, iteration-count ramp), where a
    /// single benign step- or material-boundary flip is one isolated pixel but a multi-LSB delta — so the
    /// ±1-specific checks are dropped and the gate leans on spread (percent), clustering (isolated), and
    /// magnitude (mean), which still catch a real bug (it spreads into contiguous regions).</summary>
    public static readonly ParityThresholdSet Discrete = new() {
        MaxChannelDelta = 255, // disabled: a discrete flip is a legitimately large isolated delta.
        MaxMeanAbsError = 0.05, // a real bug spreads, lifting the mean well past this.
        MaxPercentDiffering = 0.5, // a real bug spreads past ~4× the benign baseline.
        MinIsolatedFraction = 0.90, // benign flips are isolated; a bug clumps.
        MinUnitDeltaFraction = 0.0, // disabled: discrete deltas are not ±1.
    };

    /// <summary>Returns the threshold set for a debug view mode: relaxed for the discrete modes (material-id,
    /// iteration-count), strict otherwise.</summary>
    /// <param name="mode">The debug view mode value.</param>
    /// <returns>The threshold set to apply.</returns>
    public static ParityThresholdSet ForMode(int mode) {
        // 4 = material-id, 5 = iteration-count (see DebugViewModes).
        return (((mode == 4) || (mode == 5))
            ? Discrete
            : Continuous);
    }
}
