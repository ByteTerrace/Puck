namespace Puck.World;

// Compile-time Bernstein operations. Every piece uses its own [0, 1] coordinate; no power of an absolute tick is
// formed. Composition retains convex combinations instead of the cancelling monomial coefficients of a degree-81
// smooth curve. Evaluation uses the same basis and bounded stack storage.
internal static class WorldRatePolynomial {
    internal const int MaximumDegree = 81;

    private static readonly double[,] Binomials = BuildBinomials();

    internal static double Evaluate(ReadOnlySpan<double> points, double at) {
        Span<double> work = stackalloc double[points.Length];

        points.CopyTo(destination: work);
        for (var remaining = (points.Length - 1); (remaining > 0); remaining--) {
            for (var index = 0; (index < remaining); index++) {
                work[index] = (((1d - at) * work[index]) + (at * work[(index + 1)]));
            }
        }
        return work[0];
    }
    internal static double[] Slice(double[] points, double lower, double upper) {
        var first = Split(points, upper, takeLeft: true);

        return ((lower == 0d) ? first : Split(first, (lower / upper), takeLeft: false));
    }

    private static double[] Split(double[] points, double at, bool takeLeft) {
        var result = new double[points.Length];
        var work = ((double[])points.Clone());

        result[(takeLeft ? 0 : ^1)] = work[(takeLeft ? 0 : ^1)];
        for (var remaining = (points.Length - 1); (remaining > 0); remaining--) {
            for (var index = 0; (index < remaining); index++) {
                work[index] = (((1d - at) * work[index]) + (at * work[(index + 1)]));
            }
            result[(takeLeft ? (points.Length - remaining) : (remaining - 1))] = work[(takeLeft ? 0 : (remaining - 1))];
        }
        return result;
    }

    internal static double[] Compose(ReadOnlySpan<double> outer, double[] inner) {
        var level = new double[outer.Length][];

        for (var index = 0; (index < outer.Length); index++) { level[index] = [outer[index]]; }
        var complement = new double[inner.Length];

        for (var index = 0; (index < inner.Length); index++) { complement[index] = (1d - inner[index]); }
        for (var remaining = (outer.Length - 1); (remaining > 0); remaining--) {
            for (var index = 0; (index < remaining); index++) {
                var left = Multiply(left: level[index], right: complement);
                var right = Multiply(left: level[(index + 1)], right: inner);

                for (var point = 0; (point < left.Length); point++) { left[point] += right[point]; }
                level[index] = left;
            }
        }
        return level[0];
    }

    private static double[] Multiply(double[] left, double[] right) {
        var m = (left.Length - 1);
        var n = (right.Length - 1);
        var result = new double[((m + n) + 1)];

        for (var at = 0; (at < result.Length); at++) {
            for (var index = Math.Max(val1: 0, val2: (at - n)); (index <= Math.Min(val1: m, val2: at)); index++) {
                var weight = ((Binomials[m, index] * Binomials[n, (at - index)]) / Binomials[(m + n), at]);

                result[at] += ((weight * left[index]) * right[(at - index)]);
            }
        }
        return result;
    }
    private static double[,] BuildBinomials() {
        var result = new double[(MaximumDegree + 1), (MaximumDegree + 1)];

        for (var row = 0; (row <= MaximumDegree); row++) {
            result[row, 0] = result[row, row] = 1d;
            for (var column = 1; (column < row); column++) { result[row, column] = (result[(row - 1), (column - 1)] + result[(row - 1), column]); }
        }
        return result;
    }
}
