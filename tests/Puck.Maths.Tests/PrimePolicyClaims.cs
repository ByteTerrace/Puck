namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly (ulong Low, ulong High, PrimeSieveMode Expected)[] PrimePolicyCases = [
        (0, uint.MaxValue, PrimeSieveMode.Eratosthenes),
        (4_295_098_368, 4_295_098_368, PrimeSieveMode.Eratosthenes),
        (4_295_098_369, 4_295_098_369, PrimeSieveMode.Presieve),
        (999_999_984_376, 1_000_000_000_000, PrimeSieveMode.Eratosthenes),
        (999_999_984_377, 1_000_000_000_000, PrimeSieveMode.Presieve),
        (99_999_999_843_751, 100_000_000_000_000, PrimeSieveMode.Eratosthenes),
        (99_999_999_843_752, 100_000_000_000_000, PrimeSieveMode.Presieve),
        (0, 10_000_000_000_000_000, PrimeSieveMode.Presieve),
        (0, ulong.MaxValue, PrimeSieveMode.Presieve),
        (ulong.MaxValue, ulong.MaxValue, PrimeSieveMode.Presieve),
        (2, 1, PrimeSieveMode.Eratosthenes),
    ];
    private static readonly (ulong High, int Requested, int Expected)[] PrimeBitmapCases = [
        (0, 1, 1), (1000, 65536, 32768), (uint.MaxValue, 1048576, 98304),
        (1_000_000_000_000, 1048576, 1048576), (ulong.MaxValue, 32768, 32768),
    ];

    public static string? PrimePolicyBoundaries() {
        foreach (var row in PrimePolicyCases) {
            var actual = PrimeExploration.ResolveMode(low: row.Low, high: row.High);

            if (actual != row.Expected) { return $"automatic [{row.Low},{row.High}] chose {actual}, expected {row.Expected}"; }
            foreach (var mode in new[] { PrimeSieveMode.Eratosthenes, PrimeSieveMode.Presieve }) {
                if (PrimeExploration.ResolveMode(high: row.High, low: row.Low, mode: mode) != mode) { return "explicit sieve policy was changed"; }
            }
        }
        foreach (var row in PrimeBitmapCases) {
            var actual = PrimeExploration.ResolveSegmentBytes(high: row.High, segmentBytes: row.Requested);

            if (actual != row.Expected) { return $"bitmap for {row.High}: expected {row.Expected}, got {actual}"; }
            if (PrimeExploration.ResolveSegmentBytes(high: row.High, segmentBytes: row.Requested, strategy: PrimeSieveStrategy.EightStreams) != row.Requested) { return "nonadaptive bitmap request was changed"; }
        }
        return (Refuses(() => PrimeExploration.ResolveMode(high: 0, low: 0, mode: ((PrimeSieveMode)99)), typeof(ArgumentOutOfRangeException), "mode", "undefined mode")
            ?? (Refuses(() => PrimeExploration.ResolveSegmentBytes(high: 0, segmentBytes: 0), typeof(ArgumentOutOfRangeException), "segmentBytes", "zero segment")
            ?? (Refuses(() => PrimeExploration.ResolveSegmentBytes(high: 0, segmentBytes: int.MaxValue), typeof(ArgumentOutOfRangeException), "segmentBytes", "oversized segment")
            ?? Refuses(() => PrimeExploration.ResolveSegmentBytes(high: 0, strategy: ((PrimeSieveStrategy)99)), typeof(ArgumentOutOfRangeException), "strategy", "undefined strategy"))));
    }
    public static string? PrimePresieveProofBoundary() {
        var square = 4_295_098_369UL;
        var expected = new List<ulong>();

        for (var value = (square - 128); (value <= (square + 128)); ++value) {
            if (Oracles.ExactPrimality(value: value)) { expected.Add(item: value); }
        }
        foreach (var layout in new[] { PrimeByteLayout.Numeric, PrimeByteLayout.Algebraic }) {
            foreach (var patterns in new[] { false, true }) {
                var actual = new List<ulong>();

                PrimeExploration.Enumerate(low: (square - 128), high: (square + 128), onPrime: actual.Add, segmentBytes: 3,
                    layout: layout, mode: PrimeSieveMode.Presieve, usePreSieve: patterns);
                if (!actual.SequenceEqual(second: expected)) { return $"presieve threshold crossed incorrectly for {layout}, patterns={patterns}"; }
                if (PrimeExploration.Count(low: (square - 128), high: (square + 128), segmentBytes: 3,
                    layout: layout, mode: PrimeSieveMode.Presieve, usePreSieve: patterns) != ((ulong)expected.Count)) { return "presieve threshold count differs"; }
            }
        }
        return null;
    }
    public static string? PrimeConstellationEmptyValidation() {
        var pattern = new PrimeConstellation(offsets: [0, 1]);
        var count = 0;
        Action<ulong> visit = _ => ++count;

        pattern.Enumerate(low: 10, high: 9, visit: visit);
        if (count != 0) { return "reversed constellation interval is not empty"; }
        return (Refuses(() => pattern.Enumerate(low: 10, high: 9, visit: visit, segmentBytes: 0), typeof(ArgumentOutOfRangeException), "segmentBytes", "empty invalid segment")
            ?? (Refuses(() => pattern.Enumerate(low: 10, high: 9, visit: visit, segmentBytes: int.MaxValue), typeof(ArgumentOutOfRangeException), "segmentBytes", "empty oversized segment")
            ?? (Refuses(() => pattern.Enumerate(low: 10, high: 9, visit: visit, strategy: ((PrimeSieveStrategy)99)), typeof(ArgumentOutOfRangeException), "strategy", "empty invalid strategy")
            ?? (Refuses(() => pattern.Enumerate(low: 10, high: 9, visit: visit, layout: ((PrimeByteLayout)99)), typeof(ArgumentOutOfRangeException), "layout", "empty invalid layout")
            ?? Refuses(() => pattern.Enumerate(low: 10, high: 9, visit: visit, mode: ((PrimeSieveMode)99)), typeof(ArgumentOutOfRangeException), "mode", "empty invalid mode")))));
    }
}
