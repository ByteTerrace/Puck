using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// Order statistics of a sample, outside the determinism contract: the two rules every measurement reading in the tree
/// reports (frame-time percentiles, benchmark metrics, mesh audit shares and medians, expected peaks), each named once,
/// so two readings of one sample never differ by spelling. <see cref="Quantile(ReadOnlySpan{double}, double)"/>
/// interpolates between samples and is the rule for a reading that describes a sample; <see cref="NearestRank"/> returns
/// a sample that was observed and is the rule for a number that must be one of them.
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
    public static double Quantile(ReadOnlySpan<double> ascending, double fraction) => Interpolate(ascending: ascending, fraction: fraction);
    /// <summary>Returns a quantile of ascending single-precision samples by the rule of
    /// <see cref="Quantile(ReadOnlySpan{double}, double)"/>, computed in single precision.</summary>
    /// <param name="ascending">The samples in ascending order; at least one.</param>
    /// <param name="fraction">The quantile, from 0 to 1.</param>
    /// <returns>The quantile.</returns>
    /// <exception cref="ArgumentException"><paramref name="ascending"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fraction"/> is not in 0 to 1.</exception>
    public static float Quantile(ReadOnlySpan<float> ascending, double fraction) => Interpolate(ascending: ascending, fraction: fraction);
    /// <summary>Returns the nearest-rank percentile of ascending samples: the sample at rank
    /// <c>ceil(numerator / denominator * count)</c>, counting from one, so the result is always a sample that was
    /// observed. The rank is computed in integers, since a product of floats can land a rounding step over an exact
    /// rank.</summary>
    /// <param name="ascending">The samples in ascending order; at least one.</param>
    /// <param name="numerator">The fraction's numerator, from 0 to <paramref name="denominator"/>.</param>
    /// <param name="denominator">The fraction's denominator, at least one.</param>
    /// <returns>The sample at the rank; the smallest one for a fraction of zero.</returns>
    /// <exception cref="ArgumentException"><paramref name="ascending"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The fraction is not in 0 to 1.</exception>
    public static double NearestRank(ReadOnlySpan<double> ascending, int numerator, int denominator) {
        if (ascending.IsEmpty) {
            throw new ArgumentException(message: "A percentile needs at least one sample.", paramName: nameof(ascending));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(value: denominator, other: 1);
        ArgumentOutOfRangeException.ThrowIfNegative(value: numerator);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: numerator, other: denominator);

        var rank = ((long)numerator * ascending.Length).CeilingDivide(divisor: denominator);

        return ascending[((int)Math.Max(val1: 1L, val2: rank) - 1)];
    }

    /// <summary>Selects the median in place. Equal values occupy a single partition.</summary>
    public static float SelectMedian(Span<float> samples) {
        if (samples.IsEmpty) { throw new ArgumentException("A median needs at least one sample.", nameof(samples)); }
        var middle = samples.Length / 2;
        var low = 0;
        var high = samples.Length;
        var depth = 2 * BitOperations.Log2((uint)samples.Length);
        while (high - low > 1) {
            if (depth-- == 0) { samples[low..high].Sort(); break; }
            var pivot = samples[low + ((high - low) / 2)];
            var left = low;
            var right = high;
            var cursor = low;
            while (cursor < right) {
                var order = samples[cursor].CompareTo(pivot);
                if (order < 0) {
                    (samples[left], samples[cursor]) = (samples[cursor], samples[left]);
                    left++;
                    cursor++;
                } else if (order > 0) {
                    right--;
                    (samples[right], samples[cursor]) = (samples[cursor], samples[right]);
                } else { cursor++; }
            }
            if (middle < left) { high = left; }
            else if (middle >= right) { low = right; }
            else { break; }
        }
        var upper = samples[middle];
        if ((samples.Length & 1) != 0) { return upper; }
        var lower = samples[0];
        foreach (var value in samples[1..middle]) { if (value.CompareTo(lower) > 0) { lower = value; } }
        return lower + ((upper - lower) * 0.5f);
    }

    private static T Interpolate<T>(ReadOnlySpan<T> ascending, double fraction) where T : IFloatingPointIeee754<T> {
        if (ascending.IsEmpty) {
            throw new ArgumentException(message: "A quantile needs at least one sample.", paramName: nameof(ascending));
        }
        if (!(fraction >= 0.0) || !(fraction <= 1.0)) {
            throw new ArgumentOutOfRangeException(actualValue: fraction, message: "A quantile fraction lies in 0 to 1.", paramName: nameof(fraction));
        }

        var position = ((ascending.Length - 1) * fraction);
        var lower = ((int)Math.Floor(d: position));
        var upper = ((int)Math.Ceiling(a: position));

        return (ascending[lower] + ((ascending[upper] - ascending[lower]) * T.CreateChecked(value: (position - lower))));
    }
}
