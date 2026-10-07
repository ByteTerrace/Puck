namespace Puck.Maths.Tests;

internal static partial class Oracles {
    /// <summary>Marks each ordinary integer offset with independent prime divisors through the supplied root bound.</summary>
    /// <remarks>The selected laws stay below 2e13 and span at most 2^26 integers. Thus square, rounded
    /// first multiple, stride and endpoint arithmetic fit ulong; the caller's explicit root bound is checked.</remarks>
    public static List<ulong> PrimeWideIntegerWindow(ulong low, ulong high, int rootBound) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: high, other: (((ulong)rootBound) * ((ulong)rootBound)), paramName: nameof(rootBound));
        var primes = PrimeSieve(inclusiveMaximum: rootBound);
        var composite = new bool[checked((int)((high - low) + 1))];

        for (var prime = 2; (prime < primes.Length); ++prime) {
            if (!primes[prime]) { continue; }
            var p = ((ulong)prime);
            var square = (p * p);

            if (square > high) { break; }
            var multiple = Math.Max(val1: square, val2: ((((low + p) - 1) / p) * p));

            for (; (multiple <= high); multiple += p) { composite[((int)(multiple - low))] = true; }
        }
        var result = new List<ulong>();

        for (var index = 0; (index < composite.Length); ++index) {
            if (!composite[index] && ((low + ((uint)index)) >= 2)) { result.Add(item: (low + ((uint)index))); }
        }
        return result;
    }
}
