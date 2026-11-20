namespace Puck.Maths.Tests;

/// <summary>Claims over <see cref="OrderStatistics"/>: the quantile every measurement reading shares, pinned against
/// values computed by hand from its definition on fixed samples.</summary>
internal static class OrderStatisticsClaims {
    private const double Tolerance = 1e-9;

    public static string? Quantile() => (QuantileHandComputed() ?? QuantileRefusals());

    public static string? Select() {
        // Shapes a naive quickselect degrades on (sorted, reversed, all equal, organ pipe, sawtooth, few distinct
        // values) beside a seeded shuffle, each selected at every rank and read against the sorted copy.
        const int Length = 257;
        var random = new Random(Seed: 20240611);
        Func<int, int>[] shapes = [
            static index => index,
            static index => (Length - index),
            static index => 7,
            static index => ((index < (Length / 2)) ? index : (Length - index)),
            static index => (index % 16),
            static index => (index % 3),
            index => random.Next(maxValue: 1000),
        ];

        foreach (var shape in shapes) {
            var original = Enumerable.Range(start: 0, count: Length).Select(selector: shape).ToArray();
            var sorted = original.Order().ToArray();

            for (var rank = 0; (rank < Length); rank++) {
                var values = ((int[])original.Clone());
                var selected = OrderStatistics.Select(values: values.AsSpan(), rank: rank);

                if (selected != sorted[rank]) {
                    return $"Select of rank {rank} returned {selected}, but the sorted copy holds {sorted[rank]}";
                }
                for (var index = 0; (index < Length); index++) {
                    if ((index < rank) && (values[index] > selected)) { return $"rank {rank}: a value after the pivot sits before the rank at {index}"; }
                    if ((index > rank) && (values[index] < selected)) { return $"rank {rank}: a value before the pivot sits after the rank at {index}"; }
                }
            }
        }

        var single = new[] { 5 };

        if (OrderStatistics.Select(values: single.AsSpan(), rank: 0) != 5) { return "a single value is its own rank 0"; }
        if (!Throws<ArgumentOutOfRangeException>(action: static () => OrderStatistics.Select(values: new[] { 1, 2 }.AsSpan(), rank: 2))) { return "a rank at the length is not refused"; }
        if (!Throws<ArgumentOutOfRangeException>(action: static () => OrderStatistics.Select(values: new[] { 1, 2 }.AsSpan(), rank: -1))) { return "a negative rank is not refused"; }

        return null;
    }

    private static bool Throws<TException>(Action action) where TException : Exception {
        try {
            action();
        } catch (TException) {
            return true;
        }

        return false;
    }

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

        // The single-precision overload follows the same rule on values a float holds exactly.
        if (OrderStatistics.Quantile(ascending: ((ReadOnlySpan<float>)[1f, 2f, 3f, 4f]), fraction: 0.5) != 2.5f) {
            return "the single-precision median of 1, 2, 3, 4 is not 2.5";
        }
        if (OrderStatistics.Quantile(ascending: ((ReadOnlySpan<float>)[2f, 4f, 6f]), fraction: 0.75) != 5f) {
            return "the single-precision 75th percentile of 2, 4, 6 is not 5";
        }

        // Nearest rank is the sample at rank ceil(numerator / denominator * count), counting from one.
        (double[] Samples, int Numerator, int Denominator, double Expected, string Name)[] ranks = [
            (tens, 4, 5, 8.0, "1..10 at 4/5 (rank 8)"),
            (tens, 1, 2, 5.0, "1..10 at 1/2 (rank 5)"),
            (tens, 1, 1, 10.0, "1..10 at 1/1 (rank 10)"),
            (tens, 0, 1, 1.0, "1..10 at 0/1 (the smallest)"),
            ([1.0, 2.0, 3.0], 4, 5, 3.0, "1..3 at 4/5 (rank ceil(2.4) = 3)"),
            ([1.0, 2.0, 3.0, 4.0, 5.0], 4, 5, 4.0, "1..5 at 4/5 (rank 4)"),
            ([9.0], 4, 5, 9.0, "a single sample"),
        ];

        foreach (var (samples, numerator, denominator, expected, name) in ranks) {
            var actual = OrderStatistics.NearestRank(ascending: samples, numerator: numerator, denominator: denominator);

            if (actual != expected) {
                return $"{name}: NearestRank returned {actual:R}, but the definition gives {expected:R}";
            }
        }

        return null;
    }

    private static string? QuantileRefusals() {
        try {
            _ = OrderStatistics.Quantile(ascending: ((ReadOnlySpan<double>)[]), fraction: 0.5);

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
        try {
            _ = OrderStatistics.NearestRank(ascending: [], numerator: 1, denominator: 2);

            return "an empty sample was accepted by NearestRank";
        } catch (ArgumentException exception) when (exception is not ArgumentOutOfRangeException) {
        }
        foreach (var (numerator, denominator) in ((ReadOnlySpan<(int, int)>)[(-1, 2), (3, 2), (1, 0), (1, -1)])) {
            try {
                _ = OrderStatistics.NearestRank(ascending: [1.0, 2.0], numerator: numerator, denominator: denominator);

                return $"the fraction {numerator}/{denominator} was accepted by NearestRank";
            } catch (ArgumentOutOfRangeException) {
            }
        }

        return null;
    }
}
