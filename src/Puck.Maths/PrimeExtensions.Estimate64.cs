namespace Puck.Maths;

public static partial class PrimeExtensions {
    // R(x) = 1 + sum(log(x)^k / (k * k! * zeta(k+1))). This Gram-series estimate is used in
    // primecount's nth-prime strategy: https://github.com/kimwalisch/primecount/blob/master/src/RiemannR.cpp.
    // It is only a work estimate: exact counting and integer rank selection determine every result.
    private static ulong EstimateNthPrime64(ulong ordinal) {
        var logarithm = Math.Log(d: ordinal);
        var logLogarithm = Math.Log(d: logarithm);
        var estimate = (ordinal * ((((logarithm + logLogarithm) - 1D) + ((logLogarithm - 2D) / logarithm))
            - ((((logLogarithm * logLogarithm) - (6D * logLogarithm)) + 11D) / ((2D * logarithm) * logarithm))));
        var previousCorrection = double.PositiveInfinity;

        for (var iteration = 0; (iteration < 12); ++iteration) {
            var (count, derivative) = EstimatePrimeCount64(value: estimate);
            var correction = ((count - ordinal) / derivative);

            if (!double.IsFinite(d: correction) || (Math.Abs(value: correction) >= previousCorrection)) { break; }
            var next = (estimate - correction);

            if (next <= uint.MaxValue) { break; }
            previousCorrection = Math.Abs(value: correction);
            estimate = next;
        }
        // ulong.MaxValue rounds to 2^64 as a double. Test that endpoint before narrowing.
        return ((estimate >= ulong.MaxValue) ? ulong.MaxValue : ((ulong)estimate));
    }
    private static (double Count, double Derivative) EstimatePrimeCount64(double value) {
        var logarithm = Math.Log(d: value);
        var factorialTerm = 1D;
        var count = 1D;
        var derivative = 0D;

        for (var exponent = 1; (exponent < 192); ++exponent) {
            factorialTerm *= (logarithm / exponent);
            var weightedTerm = (factorialTerm / EstimateIntegerZeta64(exponent: (exponent + 1)));
            var next = (count + (weightedTerm / exponent));

            derivative += (weightedTerm / (value * logarithm));
            if (next == count) { break; }
            count = next;
        }
        return (count, derivative);
    }
    private static double EstimateIntegerZeta64(int exponent) {
        ReadOnlySpan<double> small = [
            1.6449340668482264D, 1.2020569031595943D, 1.0823232337111382D,
            1.0369277551433699D, 1.0173430619844491D, 1.0083492773819228D,
            1.0040773561979443D, 1.0020083928260822D, 1.0009945751278181D,
            1.0004941886041195D, 1.0002460865533080D, 1.0001227133475785D,
            1.0000612481350587D, 1.0000305882363070D, 1.0000152822594087D,
        ];

        if (exponent <= 16) { return small[(exponent - 2)]; }
        // For exponent >= 17, the omitted tail is at most the integral from 16 to infinity,
        // 16^(1-exponent)/(exponent-1) <= 2^-68. This is below double rounding near one.
        var sum = 1D;

        for (var denominator = 2; (denominator <= 16); ++denominator) {
            sum += Math.Pow(x: (1D / denominator), y: exponent);
        }
        return sum;
    }
}
