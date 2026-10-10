namespace Puck.Maths;

public static partial class PrimeExtensions {
    // Gourdon's decomposition and integer x-star corrections follow primecount's
    // src/gourdon/pi_gourdon.cpp, Sigma.cpp and src/util.cpp (BSD-2-Clause).
    // z=y is an admissible choice and the upstream default. It lets ordinary
    // leaves and D reuse one compact square-free factor table without an MPF table.
    private const uint GourdonTableCapacityLimit = (1U << 27);

    public static uint ResolveGourdonCutoff(ulong value) {
        var root = CombinatorialCubeRoot(value: value);
        var logarithm = Math.Log(d: value);
        var alpha = ((((((0.007888745747017503D * logarithm) - 0.7610431057184933D) * logarithm)
            + 25.74106177417062D) * logarithm) - 288.9823576050215D);

        // Floating-point tuning proposes work only. Every admitted y satisfies
        // floor(cuberoot(x)) < y < floor(sqrt(x)); exact leaves decide the result.
        alpha = Math.Max(val1: 1D, val2: (Math.Floor(d: (alpha * 1000D)) / 1000D));
        var desired = ((ulong)(root * alpha));
        var maximum = Math.Min(val1: GourdonTableCapacityLimit, val2: (value.SquareRoot() - 1UL));

        return ((uint)Math.Clamp(max: maximum, min: (root + 1UL), value: desired));
    }

    private static uint ResolveGourdonXStar(ulong value, uint cutoff) {
        var square = (((ulong)cutoff) * cutoff);

        var (quotient, remainder) = Math.DivRem(left: value, right: square);
        var rounded = (quotient + ((remainder == 0) ? 0UL : 1UL));
        var fourthRoot = value.SquareRoot().SquareRoot();
        var rootQuotient = (value / cutoff).SquareRoot();

        return ((uint)Math.Max(val1: 1UL, val2: Math.Min(val1: Math.Max(val1: fourthRoot, val2: rounded),
            val2: Math.Min(val1: cutoff, val2: rootQuotient))));
    }
    private static Int128 CountGourdonSigma(ulong value, uint cutoff, uint xStar, CombinatorialFactorTables tables,
        CancellationToken cancellationToken) {
        var root = CombinatorialCubeRoot(value: value);
        var squareRootQuotient = ((uint)(value / cutoff).SquareRoot());
        var a = ((Int128)tables.CountPrimes(value: cutoff));
        var b = ((Int128)tables.CountPrimes(value: root));
        var c = ((Int128)tables.CountPrimes(value: squareRootQuotient));
        var d = ((Int128)tables.CountPrimes(value: xStar));
        var difference = (a - b);
        var sigma1 = ((difference * (difference - 1)) / 2);
        var sigma2 = (a * (((b - c) - ((c * (c - 3)) / 2)) + ((d * (d - 3)) / 2)));
        var sigma3 = ((((((b * (b - 1)) * ((2 * b) - 1)) / 6) - b)
            - (((d * (d - 1)) * ((2 * d) - 1)) / 6)) + d);
        var sigma4 = Int128.Zero;
        var sigma5 = Int128.Zero;
        var sigma6 = Int128.Zero;
        var primes = tables.Primes;
        var last = ((int)b);

        for (var index = (((int)d) + 1); (index <= last); ++index) {
            if ((index & 4095) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
            var prime = primes[index];
            var quotient = (value / prime);

            if (prime <= squareRootQuotient) {
                sigma4 += tables.CountPrimes(value: ((uint)(quotient / cutoff)));
            } else {
                sigma5 += tables.CountPrimes(value: ((uint)(quotient / prime)));
            }
            var count = ((Int128)tables.CountPrimes(value: ((uint)quotient.SquareRoot())));

            sigma6 -= (count * count);
        }

        // With a=pi(y), b0=pi(sqrt(x)), B=P2+b0(b0-1)/2-a(a-1)/2.
        // Sigma0=a-1+b0(b0-1)/2-a(a-1)/2. Their shared triangular part
        // cancels exactly, so the caller subtracts P2 and we retain only a-1.
        return (((((((a - 1) + sigma1) + sigma2) + sigma3) + (a * sigma4)) + sigma5) + sigma6);
    }
}
