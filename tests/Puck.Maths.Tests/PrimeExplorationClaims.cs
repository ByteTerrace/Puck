namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly ulong[] PrimeExplorationBoundaryValues = [
        0, 1, 2, 3, 5, 7, 73, 193, 561, 1_105, 2_047, 3_215_031_751,
        341_550_071_728_321, 3_825_123_056_546_413_051,
        (4_294_967_291UL * 4_294_967_291UL),
        18_446_744_073_709_551_557, (ulong.MaxValue - 1), ulong.MaxValue,
    ];
    /// <summary>Decides the entire small sieve band and independently rechecks the full-width witness ladder.</summary>
    public static string? PrimeExplorationPrimality() {
        for (var value = 0; (value < Oracles.PrimeExplorationSieve.Length); ++value) {
            var expected = Oracles.PrimeExplorationSieve[value];
            var failure = PrimeExplorationPrimalityValue(expected: expected, value: ((ulong)value));

            if (failure is not null) { return failure; }
        }
        foreach (var value in PrimeExplorationBoundaryValues) {
            var failure = PrimeExplorationPrimalityValue(value, Oracles.ExactPrimality(value: value));

            if (failure is not null) { return failure; }
        }
        return null;
    }
    /// <summary>Rechecks full-width raw and forced-odd domain values against trial division and arbitrary-width rounds.</summary>
    public static string? PrimeExplorationPrimalitySweep(long[] left, long[] right) {
        foreach (var raw in ((ReadOnlySpan<long>)[left[0], right[0]])) {
            var drawn = unchecked((ulong)raw);

            foreach (var value in ((ReadOnlySpan<ulong>)[drawn, drawn | 1UL])) {
                var failure = PrimeExplorationPrimalityValue(value, Oracles.ExactPrimality(value: value));

                if (failure is not null) { return failure; }
            }
        }
        return null;
    }

    private static string? PrimeExplorationPrimalityValue(ulong value, bool expected) {
        if (PrimeExtensions.IsPrime(value: value) != expected) {
            return $"machineword primality disagrees with the independent decision at {value}, expected {expected}";
        }
        return null;
    }
}
