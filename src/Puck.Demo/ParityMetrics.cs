namespace Puck.Demo;

/// <summary>
/// Tolerance-aware difference metrics between two RGBA images of equal extent — the quantitative core of the
/// cross-backend parity gate. Pure and GPU-free: it takes two pixel buffers and produces the numbers the
/// thresholds judge. Per pixel the difference is <c>d = max(|ΔR|, |ΔG|, |ΔB|)</c> (alpha is ignored — both
/// backends emit fully opaque pixels).
/// <para>
/// The "smart" signal is <see cref="IsolatedFraction"/>. Benign cross-backend divergence is driver-level FP
/// codegen: a sprinkle of isolated ±1-LSB quantization flips wherever the continuous shading slowly crosses a
/// 1/255 boundary (measured: ~99% of differing pixels have no differing neighbour). A real bug instead
/// <em>spreads into contiguous regions</em> — a shifted silhouette or a recoloured surface — so its differing
/// pixels clump together and the isolated fraction collapses. (A luminance-gradient "edge" test was tried and
/// rejected: the benign flips sit on gentle fog/sky gradients with sub-LSB slope, so ~74% register as flat.)
/// </para>
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
    /// <summary>Gets the count of pixels at each max-channel delta value, indexed 0..255.</summary>
    public required int[] DeltaHistogram { get; init; }
    /// <summary>Gets the fraction of differing pixels that are spatially isolated — at most one of their eight
    /// neighbours also differs (1.0 when nothing differs). Benign ±1 noise is near-1; a clustered bug drops it.</summary>
    public required double IsolatedFraction { get; init; }
    /// <summary>Gets the fraction of differing pixels whose delta is exactly 1 (1.0 when nothing differs).</summary>
    public required double UnitDeltaFraction { get; init; }

    /// <summary>Computes the parity metrics between two tightly packed RGBA8 images of the same extent.</summary>
    /// <param name="reference">The reference backend's RGBA pixels.</param>
    /// <param name="comparand">The other backend's RGBA pixels.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <returns>The computed metrics.</returns>
    /// <exception cref="ArgumentException">A buffer is not exactly <c>width * height * 4</c> bytes, or the two differ in length.</exception>
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
        var histogram = new int[256];
        var differingPixels = 0;
        var maxChannelDelta = 0;
        long deltaSum = 0;

        for (var pixel = 0; (pixel < totalPixels); pixel++) {
            var offset = (pixel * 4);
            var deltaR = Math.Abs(reference[offset] - comparand[offset]);
            var deltaG = Math.Abs(reference[offset + 1] - comparand[offset + 1]);
            var deltaB = Math.Abs(reference[offset + 2] - comparand[offset + 2]);
            var delta = Math.Max(deltaR, Math.Max(deltaG, deltaB));

            histogram[delta]++;
            deltaSum += delta;

            if (delta > 0) {
                differs[pixel] = true;
                differingPixels++;
                maxChannelDelta = Math.Max(maxChannelDelta, delta);
            }
        }

        var isolatedPixels = CountIsolated(differs: differs, width: width, height: height);

        return new ParityMetrics {
            DeltaHistogram = histogram,
            DifferingPixels = differingPixels,
            IsolatedFraction = ((differingPixels == 0) ? 1.0 : ((double)isolatedPixels / differingPixels)),
            MaxChannelDelta = maxChannelDelta,
            MeanAbsError = ((double)deltaSum / totalPixels),
            PercentDiffering = ((totalPixels == 0) ? 0.0 : (((double)differingPixels / totalPixels) * 100.0)),
            TotalPixels = totalPixels,
            UnitDeltaFraction = ((differingPixels == 0) ? 1.0 : ((double)histogram[1] / differingPixels)),
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
