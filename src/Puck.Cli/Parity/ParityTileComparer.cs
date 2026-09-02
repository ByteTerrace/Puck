using Puck.Assets;

namespace Puck.Cli.Parity;

/// <summary>One tile's measured divergence: the pixel-grid coordinate and its channel-delta statistics.</summary>
/// <param name="TileX">The tile's column in the fixed grid.</param>
/// <param name="TileY">The tile's row in the fixed grid.</param>
/// <param name="MeanDelta">The mean absolute R/G/B channel delta over the tile's pixels, in LSB units.</param>
/// <param name="MaxDelta">The largest single-channel delta observed in the tile, in LSB units.</param>
internal readonly record struct ParityTileMetrics(int TileX, int TileY, double MeanDelta, int MaxDelta);
/// <summary>A frame pair's per-tile comparison result.</summary>
/// <param name="Passed">Whether every tile stayed within both thresholds.</param>
/// <param name="Worst">The tile with the largest mean delta (ties broken by max delta) — reported on failure
/// and, harmlessly, on success.</param>
internal readonly record struct ParityTileComparison(bool Passed, ParityTileMetrics Worst);
/// <summary>
/// Per-tile frame comparison — the replacement for the condemned whole-frame mean. A localized defect that a
/// million agreeing pixels would dilute under a global mean instead lands entirely inside a handful of tiles,
/// so it is those tiles' own mean and max that decide the verdict, never an average across the whole frame.
/// </summary>
internal static class ParityTileComparer {
    /// <summary>Compares two equal-extent frames over a fixed tile grid. A capture fails when any tile's mean
    /// or max delta exceeds the station's threshold.</summary>
    public static ParityTileComparison Compare(PngImage left, PngImage right, int tileSize, double tileMeanDeltaThreshold, int tileMaxDeltaThreshold) {
        var width = left.Width;
        var height = left.Height;
        var passed = true;
        var worst = default(ParityTileMetrics);
        var hasWorst = false;

        for (var tileY = 0; (tileY < height); tileY += tileSize) {
            var y1 = Math.Min(val1: (tileY + tileSize), val2: height);

            for (var tileX = 0; (tileX < width); tileX += tileSize) {
                var x1 = Math.Min(val1: (tileX + tileSize), val2: width);
                var metrics = MeasureTile(left: left, right: right, tileX: (tileX / tileSize), tileY: (tileY / tileSize), width: width, x0: tileX, x1: x1, y0: tileY, y1: y1);

                if (!hasWorst || IsWorse(candidate: metrics, current: worst)) {
                    worst = metrics;
                    hasWorst = true;
                }

                if ((metrics.MeanDelta > tileMeanDeltaThreshold) || (metrics.MaxDelta > tileMaxDeltaThreshold)) {
                    passed = false;
                }
            }
        }

        return new ParityTileComparison(Passed: passed, Worst: worst);
    }
    /// <summary>Builds a per-pixel delta heatmap: pure red, scaled so the frame's single worst-delta pixel is
    /// full red (255) and an identical pixel is black. Opaque throughout.</summary>
    public static byte[] BuildHeatmap(PngImage left, PngImage right) {
        var width = left.Width;
        var height = left.Height;
        var pixelCount = (width * height);
        var deltas = new int[pixelCount];
        var worstDelta = 0;

        for (int pixel = 0, index = 0; (pixel < pixelCount); pixel++, index += 4) {
            var delta = MaxChannelDelta(left: left.RgbaPixels, right: right.RgbaPixels, index: index);

            deltas[pixel] = delta;

            if (delta > worstDelta) {
                worstDelta = delta;
            }
        }

        var heatmap = new byte[(pixelCount * 4)];

        for (int pixel = 0, index = 0; (pixel < pixelCount); pixel++, index += 4) {
            heatmap[index] = ((worstDelta == 0) ? (byte)0 : (byte)((deltas[pixel] * 255) / worstDelta));
            heatmap[(index + 3)] = 255;
        }

        return heatmap;
    }

    private static ParityTileMetrics MeasureTile(PngImage left, PngImage right, int tileX, int tileY, int x0, int x1, int y0, int y1, int width) {
        var total = 0L;
        var count = 0;
        var maxDelta = 0;

        for (var y = y0; (y < y1); y++) {
            for (var x = x0; (x < x1); x++) {
                var index = (((y * width) + x) * 4);
                var delta = MaxChannelDelta(left: left.RgbaPixels, right: right.RgbaPixels, index: index);

                total += ChannelDeltaSum(left: left.RgbaPixels, right: right.RgbaPixels, index: index);
                count++;

                if (delta > maxDelta) {
                    maxDelta = delta;
                }
            }
        }

        var meanDelta = ((count == 0) ? 0.0 : (total / (count * 3.0)));

        return new ParityTileMetrics(MaxDelta: maxDelta, MeanDelta: meanDelta, TileX: tileX, TileY: tileY);
    }
    private static int MaxChannelDelta(byte[] left, byte[] right, int index) {
        var deltaR = Math.Abs(value: (left[index] - right[index]));
        var deltaG = Math.Abs(value: (left[(index + 1)] - right[(index + 1)]));
        var deltaB = Math.Abs(value: (left[(index + 2)] - right[(index + 2)]));

        return Math.Max(val1: deltaR, val2: Math.Max(val1: deltaG, val2: deltaB));
    }
    private static int ChannelDeltaSum(byte[] left, byte[] right, int index) =>
        ((Math.Abs(value: (left[index] - right[index])) + Math.Abs(value: (left[(index + 1)] - right[(index + 1)]))) + Math.Abs(value: (left[(index + 2)] - right[(index + 2)])));
    private static bool IsWorse(ParityTileMetrics candidate, ParityTileMetrics current) =>
        ((candidate.MeanDelta > current.MeanDelta) || ((candidate.MeanDelta == current.MeanDelta) && (candidate.MaxDelta > current.MaxDelta)));
}
