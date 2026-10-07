namespace Puck.Maths.Tests;

internal static partial class Subjects {
    /// <summary>Checks sustained large-prime transfers against independent native interval counts.</summary>
    public static string? PrimeWideSieveNativeCounts() {
        ReadOnlySpan<(ulong Low, ulong Count)> windows = [(1_000_000_000_000, 3_618_282), (100_000_000_000_000, 3_102_679)];
        ReadOnlySpan<int> segments = [32768, 65537, 1048576];

        foreach (var (low, expected) in windows) {
            foreach (var size in segments) {
                var actual = PrimeExploration.Count(low: low, high: (low + 99_999_999), mode: PrimeSieveMode.Eratosthenes, segmentBytes: size);

                if (actual != expected) { return $"wide native count at {low}, {size} bytes: expected {expected}, got {actual}"; }
            }
        }
        return null;
    }
    /// <summary>Forces full sieving through the final uint base prime and checks the final partial ulong byte.</summary>
    public static string? PrimeWideSieveFinalWindow() {
        var low = (ulong.MaxValue - 256);
        var expected = Oracles.PrimeExplorationPrimes(high: ulong.MaxValue, low: low);
        var count = PrimeExploration.Count(low: low, high: ulong.MaxValue, mode: PrimeSieveMode.Eratosthenes, segmentBytes: PrimeExploration.CacheSegmentBytes);
        var actual = new List<ulong>();

        PrimeExploration.Enumerate(low: low, high: ulong.MaxValue, onPrime: actual.Add, mode: PrimeSieveMode.Eratosthenes, segmentBytes: 7);
        return (((count == ((ulong)expected.Count)) && actual.SequenceEqual(second: expected))
            ? null : $"full-width final window: expected {expected.Count}, count {count}, enumeration {actual.Count}");
    }
    /// <summary>Checks upper-base squares, partial buckets, cursor carry and future-bucket advancement with ordinary integer-index sieving.</summary>
    public static string? PrimeWideSieveIntegerWindows() {
        ReadOnlySpan<(ulong Low, ulong High, int Root)> windows = [
            (4_295_098_219, 4_295_098_519, 65540),
            (4_295_360_371, 4_295_360_671, 65540),
            (9_666_232_339, 9_666_232_639, 98320),
            (9_999_999_983, 10_004_999_983, 100025),
            (999_999_999_977, 1_000_000_999_999, 1000001),
        ];

        foreach (var (low, high, root) in windows) {
            var expected = Oracles.PrimeWideIntegerWindow(high: high, low: low, rootBound: root);
            ReadOnlySpan<int> segments = (((high - low) < 1000) ? [1, 31, 32768] : [32768, 65537]);

            foreach (var segment in segments) {
                var actual = new List<ulong>();

                PrimeExploration.Enumerate(high: high, low: low, mode: PrimeSieveMode.Eratosthenes, onPrime: actual.Add, segmentBytes: segment);
                var count = PrimeExploration.Count(high: high, low: low, mode: PrimeSieveMode.Eratosthenes, segmentBytes: segment);

                if (!actual.SequenceEqual(second: expected) || (count != ((ulong)expected.Count))) {
                    return $"wide sieve [{low},{high}], segment {segment}: expected {expected.Count} primes; enumeration {actual.Count}, count {count}";
                }
            }
        }
        return null;
    }
}
