using System.Numerics;

namespace Puck.Maths;

public static partial class PrimeExtensions {
    // Gourdon's decomposition and integer x-star corrections follow primecount's
    // src/gourdon/pi_gourdon.cpp, Sigma.cpp and src/util.cpp (BSD-2-Clause).
    // z=y is an admissible choice and the upstream default. It lets ordinary
    // leaves and D reuse one compact square-free factor table without an MPF table.
    private const uint GourdonTableCapacityLimit = (1U << 27);
    // Evaluate the logarithm at the integer square root's square: see ResolveGourdonCutoff.
    private const double GourdonCutoffLogarithmScale = 0.42D;
    private const double GourdonCutoffDivisorFloor = 14D;

    /// <summary>Chooses the Gourdon cutoff y for <paramref name="value"/>: <c>r / max(14, 0.84 ln r)</c>, where
    /// <c>r = floor(sqrt(x))</c>, held
    /// strictly between the integer cube root and the integer square root and at most 2^27, the factor-table capacity.
    /// <para>The count's cost is a + s*x/y + l(x)*y. The hard-leaf sieve and the semiprime sieve each cover [0, x/y), together
    /// about s = 1.3 cycles per integer; the factor and prime tables, the easy-leaf terms and the hard-leaf special-factor
    /// coordinates grow with y at l cycles per unit of y, and the coordinates per unit of y grow like x^(1/6). That cost is
    /// least at y = sqrt(s*x/l(x)): sqrt(x)/y is 14 from 10^13 to 10^15, 15 to 16 near 10^16 and 10^17, and 17 to 18 near
    /// 10^18, on the AVX-512 and AVX2 kernels and on the Zen 2 and Alder Lake loop models alike, which is 0.42 ln x above
    /// 14. The minimum is wide: a divisor within 2 of the best costs about 1% at any size, so the rule does not depend on one
    /// core's per-unit costs.</para>
    /// <para>The divisor uses the deterministic fixed-point logarithm at r, so it stays constant between square
    /// boundaries. This approximates r / max(14, 0.42 ln x) within one cutoff unit without decreasing between squares.
    /// Binary64 arithmetic only scales that logarithm and proposes y; every admitted y satisfies floor(cuberoot(x)) &lt; y &lt;
    /// floor(sqrt(x)), and the exact leaves decide the count at any admitted y.</para></summary>
    /// <param name="value">The bound, in the Gourdon regime.</param>
    /// <returns>The cutoff y, with floor(cuberoot(x)) &lt; y &lt; floor(sqrt(x)) and y at most 2^27.</returns>
    public static uint ResolveGourdonCutoff(ulong value) {
        var root = CombinatorialCubeRoot(value: value);
        var squareRoot = value.SquareRoot();
        var exponent = BitOperations.Log2(value: squareRoot);
        var fraction = FixedQ4816.Log2FractionQ61(mantissaQ62: (squareRoot << (62 - exponent)));
        var logarithm = ((exponent + (((double)fraction) / (1UL << 61))) / (((double)FixedQ4816.Log2EQ62) / (1UL << 62)));
        var divisor = Math.Max(val1: GourdonCutoffDivisorFloor, val2: ((2D * GourdonCutoffLogarithmScale) * logarithm));
        // Across square boundaries r/max(14, .84 ln r) rises by more than .04 in this domain. The fixed logarithm's
        // error (about 2^-36 in log2) moves the quotient by less than .001, including at table-interval boundaries.
        // Between squares it is constant; the increasing integer clamps preserve monotonicity in both cases.
        var desired = ((ulong)(squareRoot / divisor));
        var maximum = Math.Min(val1: GourdonTableCapacityLimit, val2: (squareRoot - 1UL));

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
