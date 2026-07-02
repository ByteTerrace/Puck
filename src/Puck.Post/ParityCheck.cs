namespace Puck.Post;

/// <summary>
/// Tolerance-aware difference metrics between two RGBA images of equal extent — the quantitative core the parity
/// stages share, metric shapes ported from the demo's <c>ParityMetrics</c> (the worked reference). Pure and GPU-free.
/// Per pixel the difference is <c>d = max(|ΔR|, |ΔG|, |ΔB|)</c> (alpha is ignored — every kernel writes opaque).
/// The "smart" signal is <see cref="IsolatedFraction"/>: benign cross-backend divergence is driver-level FP codegen —
/// a sprinkle of isolated ±1-LSB quantization flips — while a real bug spreads into contiguous regions, so its
/// differing pixels clump and the isolated fraction collapses.
/// </summary>
internal sealed record ParityMetrics {
    // A differing pixel is "isolated" (benign-like) when at most this many of its 8 neighbours also differ.
    private const int IsolationNeighbourLimit = 1;

    /// <summary>Gets the total number of compared pixels.</summary>
    public required int TotalPixels { get; init; }
    /// <summary>Gets the number of pixels whose max-channel delta is non-zero.</summary>
    public required int DifferingPixels { get; init; }
    /// <summary>Gets the differing pixels as a percentage of the total.</summary>
    public required double PercentDiffering { get; init; }
    /// <summary>Gets the largest single-channel absolute delta over all pixels.</summary>
    public required int MaxChannelDelta { get; init; }
    /// <summary>Gets the mean max-channel delta over <em>all</em> pixels (so ±1 noise on a fraction of pixels stays tiny).</summary>
    public required double MeanAbsError { get; init; }
    /// <summary>Gets the fraction of differing pixels that are spatially isolated — at most one of their eight
    /// neighbours also differs (1.0 when nothing differs). Benign ±1 noise is near-1; a clustered bug drops it.</summary>
    public required double IsolatedFraction { get; init; }
    /// <summary>Gets the fraction of differing pixels whose delta is exactly 1 (1.0 when nothing differs).</summary>
    public required double UnitDeltaFraction { get; init; }

    /// <summary>Computes the parity metrics between two tightly packed RGBA8 images of the same extent.</summary>
    /// <param name="reference">The reference image's RGBA pixels.</param>
    /// <param name="comparand">The comparand image's RGBA pixels.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The computed metrics.</returns>
    /// <exception cref="ArgumentException">A buffer is not exactly <c>width * height * 4</c> bytes.</exception>
    public static ParityMetrics Compute(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> comparand, int width, int height) {
        var expected = ((width * height) * 4);

        if (
            (reference.Length != expected) ||
            (comparand.Length != expected)
        ) {
            throw new ArgumentException(message: $"Expected two {width}x{height} RGBA buffers of {expected} bytes; got {reference.Length} and {comparand.Length}.");
        }

        var totalPixels = (width * height);
        var differs = new bool[totalPixels];
        var differingPixels = 0;
        var maxChannelDelta = 0;
        var unitDeltaPixels = 0;
        var deltaSum = 0L;

        for (var pixel = 0; (pixel < totalPixels); pixel++) {
            var offset = (pixel * 4);
            var delta = Math.Max(
                Math.Abs(value: (reference[offset] - comparand[offset])),
                Math.Max(
                    Math.Abs(value: (reference[offset + 1] - comparand[offset + 1])),
                    Math.Abs(value: (reference[offset + 2] - comparand[offset + 2]))
                )
            );

            deltaSum += delta;

            if (delta > 0) {
                differs[pixel] = true;
                differingPixels++;
                maxChannelDelta = Math.Max(maxChannelDelta, delta);

                if (delta == 1) {
                    unitDeltaPixels++;
                }
            }
        }

        return new ParityMetrics {
            DifferingPixels = differingPixels,
            IsolatedFraction = ((differingPixels == 0) ? 1.0 : ((double)CountIsolated(differs: differs, width: width, height: height) / differingPixels)),
            MaxChannelDelta = maxChannelDelta,
            MeanAbsError = ((double)deltaSum / totalPixels),
            PercentDiffering = ((totalPixels == 0) ? 0.0 : (((double)differingPixels / totalPixels) * 100.0)),
            TotalPixels = totalPixels,
            UnitDeltaFraction = ((differingPixels == 0) ? 1.0 : ((double)unitDeltaPixels / differingPixels)),
        };
    }

    private static int CountIsolated(bool[] differs, int width, int height) {
        var isolated = 0;

        for (var pixel = 0; (pixel < differs.Length); pixel++) {
            if (!differs[pixel]) {
                continue;
            }

            var x = (pixel % width);
            var y = (pixel / width);
            var neighbours = 0;

            for (var dy = -1; (dy <= 1); dy++) {
                for (var dx = -1; (dx <= 1); dx++) {
                    if ((dx == 0) && (dy == 0)) {
                        continue;
                    }

                    var nx = (x + dx);
                    var ny = (y + dy);

                    if (
                        (nx >= 0) &&
                        (nx < width) &&
                        (ny >= 0) &&
                        (ny < height) &&
                        differs[((ny * width) + nx)]
                    ) {
                        neighbours++;
                    }
                }
            }

            if (neighbours <= IsolationNeighbourLimit) {
                isolated++;
            }
        }

        return isolated;
    }
}

/// <summary>
/// A conjunctive set of PASS thresholds for one parity comparison. Every active threshold must hold;
/// <see cref="Evaluate"/> returns the ones that tripped (empty = pass). A threshold is disabled by a sentinel:
/// <see cref="MaxChannelDelta"/> at 255 or <see cref="MinUnitDeltaFraction"/> at 0 never trips. Shape ported from
/// the demo's <c>ParityThresholdSet</c>.
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
/// The calibrated PASS thresholds the parity stages apply — constant VALUES copied from the demo's
/// <c>ParityThresholds.cs</c> (calibrated against the measured RTX 4070 baseline), never code-shared: the POST is a
/// from-scratch reimplementation and the demo gate is its standing cross-check.
/// </summary>
internal static class ParityThresholds {
    /// <summary>The strict thresholds for continuous-shading views, where the only cross-backend (or
    /// dynamic-vs-baked) residual is ±1-LSB quantization noise.</summary>
    public static readonly ParityThresholdSet Continuous = new() {
        MaxChannelDelta = 1, // ±1-LSB noise; above 1 is a real divergence.
        MaxMeanAbsError = 0.05, // ±1 on a fraction of pixels keeps this far below 0.05.
        MaxPercentDiffering = 0.5, // ~4x over the measured 0.13%.
        MinIsolatedFraction = 0.90, // benign noise is ~99% isolated; a bug clumps.
        MinUnitDeltaFraction = 0.99, // benign noise is entirely ±1.
    };

    /// <summary>The thresholds for the full compute SDF world composite: the same continuous-shading flavour as
    /// <see cref="Continuous"/>, but the richer scene puts measurably more pixels in the 1/255 transition bands, so
    /// only the spread cap is relaxed; the max-delta, isolation, unit-delta, and mean guards stay strict.</summary>
    public static readonly ParityThresholdSet WorldComposite = new() {
        MaxChannelDelta = 1, // ±1-LSB noise; above 1 is a real divergence.
        MaxMeanAbsError = 0.05, // ±1 on <1% of pixels keeps this far below 0.05.
        MaxPercentDiffering = 2.0, // ~3.5x over the measured 0.57% split baseline; benign noise scales with scene richness.
        MinIsolatedFraction = 0.85, // measured 93-96% isolated; a clustered bug collapses well below this.
        MinUnitDeltaFraction = 0.99, // benign noise is entirely ±1.
    };

    /// <summary>The thresholds for cross-backend DIFFERENTIAL FUZZING of the SDF world. Fuzz-generated scenes span
    /// the whole input space, so the benign ±1-LSB codegen residual is legitimately MORE clustered and widespread
    /// than the hand-tuned showcase: it follows the large smooth ground-plane gradients and the cone-march banding
    /// (thin contiguous ±1 bands), which collapses the isolated-fraction and lifts the spread far below what those
    /// showcase-calibrated guards expect. So the fuzz oracle leans on the DEFINITIVE benign signature instead —
    /// every difference is exactly ±1 LSB (<see cref="ParityThresholdSet.MaxChannelDelta"/> = 1 and
    /// <see cref="ParityThresholdSet.MinUnitDeltaFraction"/> = 0.99 stay strict) — and disables the
    /// gradient-structure-dependent isolation guard while widening the spread cap. A real divergence (a wrong
    /// shape/blend/material renders a region with multi-LSB deltas) trips the max-delta and unit-delta guards
    /// regardless of how the benign noise happens to cluster.</summary>
    public static readonly ParityThresholdSet WorldFuzz = new() {
        MaxChannelDelta = 1, // the key guard: any ≥2 delta is a real divergence (benign codegen is exactly ±1).
        MaxMeanAbsError = 0.05, // ±1 on a minority of pixels stays far below this; a real bug lifts the mean.
        MaxPercentDiffering = 8.0, // wide: gradient/banding-heavy fuzz scenes put many pixels in ±1 transition bands.
        MinIsolatedFraction = 0.0, // disabled: benign ±1 noise follows gradient bands and is legitimately clustered.
        MinUnitDeltaFraction = 0.99, // the co-guard: benign codegen noise is entirely ±1, so a real bug drops this.
    };
}

/// <summary>Shared parity plumbing for the cross-backend stages: the amplified diff heatmap and a one-line
/// metrics digest for stage details.</summary>
internal static class ParityCheck {
    /// <summary>Writes a grayscale max-channel-delta heatmap, amplified so divergences glow without a 1-LSB image
    /// looking alarming: <c>value = min(255, d * 64)</c>. Shape ported from the demo's diff writer.</summary>
    /// <param name="path">The output PNG path.</param>
    /// <param name="reference">The reference image's RGBA pixels.</param>
    /// <param name="comparand">The comparand image's RGBA pixels.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    public static void WriteDiffImage(string path, ReadOnlySpan<byte> reference, ReadOnlySpan<byte> comparand, int width, int height) {
        var pixelCount = (width * height);
        var diff = new byte[(pixelCount * 4)];

        for (var pixel = 0; (pixel < pixelCount); pixel++) {
            var offset = (pixel * 4);
            var delta = Math.Max(
                Math.Abs(value: (reference[offset] - comparand[offset])),
                Math.Max(
                    Math.Abs(value: (reference[offset + 1] - comparand[offset + 1])),
                    Math.Abs(value: (reference[offset + 2] - comparand[offset + 2]))
                )
            );
            var value = (byte)Math.Min(255, (delta * 64));

            diff[offset] = value;
            diff[offset + 1] = value;
            diff[offset + 2] = value;
            diff[offset + 3] = byte.MaxValue;
        }

        PngImage.Write(height: height, path: path, rgba: diff, width: width);
    }

    /// <summary>Formats the one-line metrics digest the parity stages put in their outcome detail.</summary>
    /// <param name="metrics">The measured parity metrics.</param>
    /// <returns>The digest, e.g. <c>diff 0.37% (2130px) | maxΔ1 | isolated 95% | unitΔ 1.00</c>.</returns>
    public static string Describe(ParityMetrics metrics) {
        ArgumentNullException.ThrowIfNull(metrics);

        return $"diff {metrics.PercentDiffering:0.##}% ({metrics.DifferingPixels}px) | maxΔ{metrics.MaxChannelDelta} | isolated {(metrics.IsolatedFraction * 100.0):0}% | unitΔ {metrics.UnitDeltaFraction:0.##}";
    }
}
