namespace Puck.Assets.Textures;

/// <summary>The endpoint fitting the block encoders share. Every result is integer arithmetic or scalar double addition,
/// multiplication and division in a written order, so a fit is the same on every machine.</summary>
internal static class EndpointFit {
    // The block's bounding box over the first `channels` channels of sixteen texels `stride` values apart, as a line
    // from `low` to `high`: the widest channel runs from its minimum to its maximum, and each other channel runs the
    // same way when its covariance with the widest is non-negative and the opposite way otherwise, so a block of two
    // colors gets exactly those two colors as its endpoints.
    public static void BoundingBox(ReadOnlySpan<int> texels, int stride, int channels, Span<double> low, Span<double> high) {
        Span<int> minimum = stackalloc int[channels];
        Span<int> maximum = stackalloc int[channels];
        Span<long> sums = stackalloc long[channels];
        var widest = 0;

        minimum.Fill(value: int.MaxValue);
        maximum.Fill(value: int.MinValue);

        for (var texel = 0; (texel < 16); texel++) {
            for (var channel = 0; (channel < channels); channel++) {
                var value = texels[((texel * stride) + channel)];

                minimum[channel] = Math.Min(val1: minimum[channel], val2: value);
                maximum[channel] = Math.Max(val1: maximum[channel], val2: value);
                sums[channel] += value;
            }
        }

        for (var channel = 1; (channel < channels); channel++) {
            if ((maximum[channel] - minimum[channel]) > (maximum[widest] - minimum[widest])) {
                widest = channel;
            }
        }

        for (var channel = 0; (channel < channels); channel++) {
            // 16 Σ x y - Σ x Σ y, sixteen times the covariance, exactly.
            var products = 0L;

            for (var texel = 0; (texel < 16); texel++) {
                products += (((long)texels[((texel * stride) + channel)]) * texels[((texel * stride) + widest)]);
            }

            var ascending = (((16L * products) - (sums[channel] * sums[widest])) >= 0L);

            low[channel] = (ascending ? minimum[channel] : maximum[channel]);
            high[channel] = (ascending ? maximum[channel] : minimum[channel]);
        }
    }
    // Refits `low` and `high` by least squares to the texels given each texel's weight (weights[indices[texel]] / 64
    // along the line from low to high). Returns false, leaving them unchanged, when every texel has the same weight.
    public static bool LeastSquares(ReadOnlySpan<int> texels, int stride, int channels, ReadOnlySpan<int> indices, ReadOnlySpan<byte> weights, Span<double> low, Span<double> high) {
        var aa = 0.0;
        var ab = 0.0;
        var bb = 0.0;

        for (var texel = 0; (texel < 16); texel++) {
            var t = (weights[indices[texel]] / 64.0);
            var s = (1.0 - t);

            aa += (s * s);
            ab += (s * t);
            bb += (t * t);
        }

        var determinant = ((aa * bb) - (ab * ab));

        if (!(determinant > 1e-9)) {
            return false;
        }

        for (var channel = 0; (channel < channels); channel++) {
            var ax = 0.0;
            var bx = 0.0;

            for (var texel = 0; (texel < 16); texel++) {
                var t = (weights[indices[texel]] / 64.0);
                var x = ((double)texels[((texel * stride) + channel)]);

                ax += ((1.0 - t) * x);
                bx += (t * x);
            }

            low[channel] = (((bb * ax) - (ab * bx)) / determinant);
            high[channel] = (((aa * bx) - (ab * ax)) / determinant);
        }

        return true;
    }
}
