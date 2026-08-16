using Puck.Assets;

namespace Puck.Cli.Parity;

/// <summary>One frame pair's measured divergence and its verdict under the relaxed parity envelope.</summary>
/// <param name="MeanDelta">The mean absolute channel delta in LSB units, averaged over every pixel's R, G, and B.</param>
/// <param name="DiffFraction">The fraction of pixels with any nonzero R, G, or B delta.</param>
/// <param name="MaxDelta">The largest single-channel delta observed, in LSB units.</param>
/// <param name="Passed">Whether the pair sits inside the envelope.</param>
internal readonly record struct ParityVerdict(
    double MeanDelta,
    double DiffFraction,
    int MaxDelta,
    bool Passed
);
/// <summary>
/// The relaxed cross-backend parity envelope. Benign SPIR-V-vs-DXIL codegen noise is ±1-LSB deltas clustered
/// along gradients (worst measured benign mean ~0.06 LSB); a genuinely missing, relocated, or recolored region
/// lands in multiples of 1.0, so the mean is the load-bearing guard. A failure under this envelope is a real
/// divergence, never noise — widening it hides bugs, and tightening it toward pixel-perfection re-rolls with
/// every shader-codegen change.
/// </summary>
internal static class ParityEnvelope {
    /// <summary>The largest admissible fraction of differing pixels (the spread guard).</summary>
    public const double MaxDiffFraction = 0.20;
    /// <summary>The largest admissible mean absolute channel delta, in LSB units (the load-bearing guard).</summary>
    public const double MaxMeanDelta = 0.35;

    /// <summary>Measures one frame pair. Alpha is ignored — composed screenshots are opaque and the channel carries
    /// no scene content. The caller refuses mismatched extents before measuring; this method requires them equal.</summary>
    /// <param name="left">One decoded frame.</param>
    /// <param name="right">The other decoded frame, with the same extent.</param>
    /// <returns>The measured divergence and its envelope verdict.</returns>
    /// <exception cref="ArgumentException">The frames disagree on extent.</exception>
    public static ParityVerdict Compare(PngImage left, PngImage right) {
        if ((left.Width != right.Width) || (left.Height != right.Height)) {
            throw new ArgumentException(
                message: $"Frame extents disagree: {left.Width}x{left.Height} vs {right.Width}x{right.Height}.",
                paramName: nameof(right)
            );
        }

        var leftPixels = left.RgbaPixels;
        var rightPixels = right.RgbaPixels;
        var pixelCount = (left.Width * left.Height);
        var differingPixels = 0L;
        var maxDelta = 0;
        var totalDelta = 0L;

        for (var index = 0; (index < (pixelCount * 4)); index += 4) {
            var deltaR = Math.Abs(value: (leftPixels[(index + 0)] - rightPixels[(index + 0)]));
            var deltaG = Math.Abs(value: (leftPixels[(index + 1)] - rightPixels[(index + 1)]));
            var deltaB = Math.Abs(value: (leftPixels[(index + 2)] - rightPixels[(index + 2)]));
            var pixelMax = Math.Max(val1: deltaR, val2: Math.Max(val1: deltaG, val2: deltaB));

            if (pixelMax > 0) {
                differingPixels++;
            }
            if (pixelMax > maxDelta) {
                maxDelta = pixelMax;
            }

            totalDelta += ((deltaR + deltaG) + deltaB);
        }

        var diffFraction = ((pixelCount == 0) ? 0.0 : (differingPixels / ((double)pixelCount)));
        var meanDelta = ((pixelCount == 0) ? 0.0 : (totalDelta / (pixelCount * 3.0)));

        return new ParityVerdict(
            DiffFraction: diffFraction,
            MaxDelta: maxDelta,
            MeanDelta: meanDelta,
            Passed: ((meanDelta <= MaxMeanDelta) && (diffFraction <= MaxDiffFraction))
        );
    }
}
