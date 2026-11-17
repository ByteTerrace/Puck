namespace Puck.Maths;

/// <summary>
/// Order statistics of a sample, outside the determinism contract: the one quantile every measurement reading in the
/// tree reports (frame-time percentiles, benchmark metrics, mesh audit shares), so two readings of one sample never
/// differ by spelling.
/// </summary>
public static class OrderStatistics {
    /// <summary>Returns a quantile of ascending samples, interpolated linearly between the two neighbouring samples at
    /// position <c>(count - 1) * fraction</c>: the sample itself when that position is whole, so the 0 and 1 quantiles
    /// are the minimum and the maximum and the median of an odd count is its middle sample.</summary>
    /// <param name="ascending">The samples in ascending order; at least one.</param>
    /// <param name="fraction">The quantile, from 0 to 1.</param>
    /// <returns>The quantile.</returns>
    /// <exception cref="ArgumentException"><paramref name="ascending"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fraction"/> is not in 0 to 1.</exception>
    public static double Quantile(ReadOnlySpan<double> ascending, double fraction) {
        if (ascending.IsEmpty) {
            throw new ArgumentException(message: "A quantile needs at least one sample.", paramName: nameof(ascending));
        }
        if (!(fraction >= 0.0) || !(fraction <= 1.0)) {
            throw new ArgumentOutOfRangeException(actualValue: fraction, message: "A quantile fraction lies in 0 to 1.", paramName: nameof(fraction));
        }

        var position = ((ascending.Length - 1) * fraction);
        var lower = ((int)Math.Floor(d: position));
        var upper = ((int)Math.Ceiling(a: position));

        return (ascending[lower] + ((ascending[upper] - ascending[lower]) * (position - lower)));
    }
}
