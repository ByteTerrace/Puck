namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly (uint Index, uint Prime)[] PrimeSelectionRanks = [
        (6541, 65521), (6542, 65537), (999999, 15485863), (99999999, 2038074743),
        (203280220, 4294967291), (203280221, 0), (uint.MaxValue, 0),
    ];

    public static string? PrimeSelectionRanksMatchSieve() {
        var expected = Oracles.PrimeSieve(inclusiveMaximum: 100_000);
        var index = 0U;

        for (var value = 2; (value < expected.Length); ++value) {
            if (!expected[value]) { continue; }
            var actual = ((uint)index).NthPrime();

            if (actual != value) { return $"rank {index}: expected {value}, got {actual}"; }
            ++index;
        }
        foreach (var row in PrimeSelectionRanks) {
            var actual = row.Index.NthPrime();

            if (actual != row.Prime) { return $"rank {row.Index}: expected {row.Prime}, got {actual}"; }
        }
        return null;
    }
}
