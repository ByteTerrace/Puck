namespace Puck.Maths.Tests;

/// <summary>Claims over <see cref="OrderStatistics"/>: the quantile every measurement reading shares, pinned against
/// values computed by hand from its definition on fixed samples.</summary>
internal static class OrderStatisticsClaims {
    private const double Tolerance = 1e-9;

    public static string? Quantile() => (QuantileHandComputed() ?? QuantileRefusals());

    private static string? QuantileHandComputed() {
        var tens = Enumerable.Range(start: 1, count: 10).Select(selector: static value => ((double)value)).ToArray();
        var thousand = Enumerable.Range(start: 1, count: 1000).Select(selector: static value => ((double)value)).ToArray();
        (double[] Samples, double Fraction, double Expected, string Name)[] rows = [
            (tens, 0.0, 1.0, "1..10 minimum"),
            (tens, 0.5, 5.5, "1..10 median"),
            (tens, 0.95, 9.55, "1..10 95th"),
            (tens, 0.99, 9.91, "1..10 99th"),
            (tens, 1.0, 10.0, "1..10 maximum"),
            (thousand, 0.5, 500.5, "1..1000 median"),
            (thousand, 0.99, 990.01, "1..1000 99th"),
            (thousand, 0.999, 999.001, "1..1000 99.9th"),
            ([2.0, 4.0], 0.25, 2.5, "pair 25th"),
            ([7.0], 0.0, 7.0, "single minimum"),
            ([7.0], 0.73, 7.0, "single 73rd"),
            ([7.0], 1.0, 7.0, "single maximum"),
        ];

        foreach (var (samples, fraction, expected, name) in rows) {
            var actual = OrderStatistics.Quantile(ascending: samples, fraction: fraction);

            if (Math.Abs(value: (actual - expected)) > Tolerance) {
                return $"{name}: Quantile returned {actual:R}, but the definition gives {expected:R}";
            }
        }

        return null;
    }

    private static string? QuantileRefusals() {
        try {
            _ = OrderStatistics.Quantile(ascending: [], fraction: 0.5);

            return "an empty sample was accepted";
        } catch (ArgumentException exception) when (exception is not ArgumentOutOfRangeException) {
        }

        foreach (var fraction in ((ReadOnlySpan<double>)[-0.1, 1.1, double.NaN])) {
            try {
                _ = OrderStatistics.Quantile(ascending: [1.0, 2.0], fraction: fraction);

                return $"fraction {fraction} was accepted";
            } catch (ArgumentOutOfRangeException) {
            }
        }

        return null;
    }
}
