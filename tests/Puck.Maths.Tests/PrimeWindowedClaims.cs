namespace Puck.Maths.Tests;

internal static partial class Subjects {
    // Oliveira e Silva's pi(2^55); 2^55 is the first power-of-two checkpoint whose square-root-wide
    // interval exceeds the carried-state workspace bound, so its local selections are windowed.
    private const ulong WindowedCheckpoint = (1UL << 55);
    private const ulong WindowedCheckpointCount = 971_269_945_245_201;
    private const ulong WindowedCheckpointDistance = 250_000;

    /// <summary>Checks windowed complete sieving against ordinary integer-index sieving across many stateless segments.</summary>
    public static string? PrimeWindowedIntegerWindows() {
        ReadOnlySpan<(ulong Low, ulong High, int Root)> windows = [
            (4_295_098_219, 4_295_098_519, 65540),
            (4_295_360_371, 4_295_360_671, 65540),
            (9_666_232_339, 9_666_232_639, 98320),
            (9_999_999_983, 10_004_999_983, 100025),
            (999_999_999_977, 1_000_000_999_999, 1000001),
        ];

        foreach (var (low, high, root) in windows) {
            var expected = Oracles.PrimeWideIntegerWindow(high: high, low: low, rootBound: root);
            ReadOnlySpan<int> segments = (((high - low) < 1000) ? [1, 2, 31, 32768] : [4096, 32768]);

            foreach (var segment in segments) {
                var actual = new List<ulong>();

                PrimeExploration.Enumerate(high: high, low: low, mode: PrimeSieveMode.Windowed, onPrime: actual.Add, segmentBytes: segment);
                var count = PrimeExploration.Count(high: high, low: low, mode: PrimeSieveMode.Windowed, segmentBytes: segment);

                if (!actual.SequenceEqual(second: expected) || (count != ((ulong)expected.Count))) {
                    return $"windowed sieve [{low},{high}], segment {segment}: expected {expected.Count} primes; enumeration {actual.Count}, count {count}";
                }
            }
        }
        return null;
    }
    /// <summary>Forces windowed sieving through the final uint base prime and checks the final partial ulong byte.</summary>
    public static string? PrimeWindowedFinalWindow() {
        var low = (ulong.MaxValue - 256);
        var expected = Oracles.PrimeExplorationPrimes(high: ulong.MaxValue, low: low);
        var count = PrimeExploration.Count(low: low, high: ulong.MaxValue, mode: PrimeSieveMode.Windowed, segmentBytes: PrimeExploration.CacheSegmentBytes);
        var actual = new List<ulong>();

        PrimeExploration.Enumerate(low: low, high: ulong.MaxValue, onPrime: actual.Add, mode: PrimeSieveMode.Windowed, segmentBytes: 7);
        return (((count == ((ulong)expected.Count)) && actual.SequenceEqual(second: expected))
            ? null : $"windowed final window: expected {expected.Count}, count {count}, enumeration {actual.Count}");
    }
    /// <summary>Selects and counts beside 2^55 with one windowed segment each and no global count.</summary>
    public static string? PrimeWindowedCheckpointSelection() {
        var forward = PrimeExtensions.ProfileNthPrime(value: ((WindowedCheckpointCount + WindowedCheckpointDistance) - 1));
        var backward = PrimeExtensions.ProfileNthPrime(value: (WindowedCheckpointCount - WindowedCheckpointDistance));

        foreach (var (profile, direction) in new[] { (forward, "forward"), (backward, "backward") }) {
            if ((profile.Route != PrimeRequestRoute.CheckpointSelection) || (profile.Counts.Count != 0) || (profile.SelectionWindows != 1) ||
                (profile.SelectionSegments != 1) || (profile.SelectionWindowedSegments != 1) || (profile.DirectPrimalityRequests != 0)) {
                return $"{direction} windowed checkpoint rank: route {profile.Route}, {profile.Counts.Count} counts, {profile.SelectionWindows} windows, {profile.SelectionSegments} segments, {profile.SelectionWindowedSegments} windowed segments";
            }
            if (!Oracles.ExactPrimality(value: profile.Result)) { return $"{direction} windowed checkpoint rank returned composite {profile.Result}"; }
        }
        // Survivor decisions replace every upper-base mark in these presieved counts.
        var above = PrimeExploration.Count(low: (WindowedCheckpoint + 1), high: forward.Result, mode: PrimeSieveMode.Presieve, segmentBytes: PrimeExploration.CacheSegmentBytes);
        var below = PrimeExploration.Count(low: backward.Result, high: WindowedCheckpoint, mode: PrimeSieveMode.Presieve, segmentBytes: PrimeExploration.CacheSegmentBytes);

        if ((above != WindowedCheckpointDistance) || (below != WindowedCheckpointDistance)) {
            return $"windowed checkpoint ranks: presieved counts {above} above and {below} below, expected {WindowedCheckpointDistance}";
        }
        var count = PrimeExtensions.ProfilePrimeCountingFunction(value: forward.Result);
        var work = count.Counts[0];

        if (count.Result != (WindowedCheckpointCount + WindowedCheckpointDistance)) { return $"windowed checkpoint count: expected {(WindowedCheckpointCount + WindowedCheckpointDistance)}, got {count.Result}"; }
        return (((count.Counts.Count == 1) && (work.Route == PrimeCountRoute.CheckpointInterval) && (work.CheckpointSegments == 1) && (work.CheckpointWindowedSegments == 1))
            ? null : $"windowed checkpoint count used route {work.Route} with {work.CheckpointWindowedSegments} windowed segments");
    }
    /// <summary>Pins the local sieve budget's cost model against an independent search over its cost predicate.</summary>
    public static string? PrimeLocalSieveBudgets() {
        // Quotient bounds straddle the first upper prime and the counting limit; Gourdon bounds include carried and
        // windowed heights, budgets of whole windows only and of whole windows plus a partial window.
        ReadOnlySpan<ulong> bounds = [
            4_295_662_345, 8_589_934_592, 1_000_000_000_000, 17_592_177_655_809, 17_592_177_655_810,
            1_000_000_000_000_000, 10_000_000_000_000_000, WindowedCheckpoint, 394_906_913_798_226_432, ulong.MaxValue,
        ];
        const ulong CountingValueLimit = 17_592_177_655_809;
        const ulong Span = 502_333_410;
        var whole = false;
        var partial = false;

        foreach (var bound in bounds) {
            uint? cutoff = ((bound <= CountingValueLimit) ? null : PrimeExtensions.ResolveGourdonCutoff(value: bound));
            var expected = Oracles.PrimeLocalSieveBudget(gourdonCutoff: cutoff, value: bound);
            var actual = PrimeExtensions.LocalSieveBudget64(high: bound);

            if (actual != expected) { return $"local sieve budget at {bound}: expected {expected}, got {actual}"; }
            whole |= ((actual >= Span) && ((actual % Span) == 0));
            partial |= ((actual >= Span) && ((actual % Span) != 0));
        }
        // Hand derivations: at 2^32+695049 the root is 65541, pass 65541+30*7418 and floor 30*32771; at 2^33 the root
        // is 92681, pass 92681+30*10489 and floor 30*46341. Neither floor reaches a whole window.
        var first = PrimeExtensions.LocalSieveBudget64(high: 4_295_662_345);
        var second = PrimeExtensions.LocalSieveBudget64(high: 8_589_934_592);

        if ((first != 695_049) || (second != 982_879)) { return $"hand-derived quotient budgets: expected 695049 and 982879, got {first} and {second}"; }
        return ((whole && partial) ? null : $"budget fixtures miss a branch: whole windows {whole}, partial window {partial}");
    }
    public static string? PrimeWindowedRankAt1E15() =>
        PrimeWindowedPublishedRank(expected: 37_124_508_045_065_437, index: (1_000_000_000_000_000 - 1));
    public static string? PrimeWindowedRankAt1E16() =>
        PrimeWindowedPublishedRank(expected: 394_906_913_903_735_329, index: (10_000_000_000_000_000 - 1));

    private static string? PrimeWindowedPublishedRank(ulong index, ulong expected) {
        var profile = PrimeExtensions.ProfileNthPrime(value: index);

        if (profile.Result != expected) { return $"rank {index}: published {expected}, got {profile.Result}"; }
        return (((profile.Route == PrimeRequestRoute.CountedSelection) && (profile.Counts.Count == 1) && (profile.Counts[0].Route == PrimeCountRoute.Gourdon) &&
            (profile.SelectionWindows == 1) && (profile.SelectionWindowedSegments == 1))
            ? null : $"rank {index}: {profile.Counts.Count} global counts, {profile.SelectionWindows} windows, {profile.SelectionWindowedSegments} windowed segments");
    }
}
