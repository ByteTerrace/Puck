namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly (ulong Bound, ulong Count, string Route)[] PrimeProfileCounts = [
        (0, 0, "Narrow32"),
        (1000, 168, "Narrow32"),
        (10_000_000_000, 455_052_511, "Quotient64"),
        ((1UL << 40), 41_203_088_796, "Checkpoint"),
    ];

    public static string? PrimeProfileCountResults() {
        foreach (var (bound, expected, route) in PrimeProfileCounts) {
            var profile = PrimeExtensions.ProfilePrimeCountingFunction(value: bound);

            if (profile.Result != expected) { return $"profile pi({bound}): expected {expected}, got {profile.Result}"; }
            if (profile.Result != bound.PrimeCountingFunction()) { return $"profiling changed pi({bound})"; }
            var failure = PrimeProfileCountHeader(bound: bound, profile: profile, route: route);

            if (failure is not null) { return failure; }
            if ((profile.Counts[0].CheckpointSegments != 0) || (profile.Counts[0].CheckpointBitmapBytes != 0)) {
                return $"non-interval {route} count recorded checkpoint sieve work";
            }
        }
        const ulong Checkpoint = (1UL << 40);
        const ulong CheckpointCount = 41_203_088_796;
        var primes = Oracles.PrimeExplorationPrimes(high: (Checkpoint + 64), low: (Checkpoint + 1));
        var delta = PrimeExtensions.ProfilePrimeCountingFunction(value: (Checkpoint + 64));
        var header = PrimeProfileCountHeader(bound: (Checkpoint + 64), profile: delta, route: "CheckpointInterval");

        if (header is not null) { return header; }
        if (delta.Result != (CheckpointCount + ((ulong)primes.Count))) { return "profile checkpoint delta differs from independent primes"; }
        if (delta.Result != (Checkpoint + 64).PrimeCountingFunction()) { return "profiling changed the checkpoint interval count"; }
        // 2^40 has residue 16 modulo thirty; the 64 following integers touch three wheel bytes.
        return (((delta.Counts[0].CheckpointSegments == 1) && (delta.Counts[0].CheckpointBitmapBytes == 3))
            ? null : "profile checkpoint interval did not record one logical three-byte segment");
    }
    public static string? PrimeProfileSelectionResults() {
        var narrow = PrimeExtensions.ProfileNthPrime(value: 10);

        if ((narrow.Result != 31) || (narrow.Result != 10UL.NthPrime()) || (narrow.Route != "Narrow32") ||
            (narrow.Counts.Count != 0) || PrimeProfileHasSelectionWork(profile: narrow)) { return "narrow profile rank or work record differs"; }
        var outside = PrimeExtensions.ProfileNthPrime(value: ulong.MaxValue);

        if ((outside.Result != 0) || (outside.Result != ulong.MaxValue.NthPrime()) || (outside.Route != "OutOfRange") ||
            (outside.Counts.Count != 0) || PrimeProfileHasSelectionWork(profile: outside)) { return "out-of-range profile rank or work record differs"; }
        const ulong Checkpoint = (1UL << 40);
        const ulong CheckpointCount = 41_203_088_796;
        var below = Oracles.PrimeExplorationPrimes(high: Checkpoint, low: (Checkpoint - 4095));
        var above = Oracles.PrimeExplorationPrimes(high: (Checkpoint + 4096), low: (Checkpoint + 1));

        if ((below.Count < 32) || (above.Count < 32)) { return "independent profile selection window has fewer than 32 primes"; }
        var failure = (PrimeProfileBitmapSelection(index: (CheckpointCount - 32), expected: below[^32])
            ?? PrimeProfileBitmapSelection(index: (CheckpointCount + 31), expected: above[31]));

        if (failure is not null) { return failure; }
        const ulong LastIndex = 425_656_284_035_217_742;
        var last = PrimeExtensions.ProfileNthPrime(value: LastIndex);
        var tail = Oracles.PrimeExplorationPrimes(high: ulong.MaxValue, low: (ulong.MaxValue - 63));

        if ((tail.Count == 0) || (last.Result != tail[^1]) || (last.Result != LastIndex.NthPrime())) { return "final profiled rank differs from the independent ulong tail"; }
        if ((last.Route != "CheckpointSelection") || (last.Counts.Count != 0) || (last.SelectionWindows != 0) ||
            (last.SelectionSegments != 0) || (last.SelectionBitmapBytes != 0) || (last.DirectPrimalityRequests != 30)) {
            return "final prime profile did not record exactly 30 odd-candidate requests and no bitmap/count work";
        }
        return null;
    }
    public static string? PrimeProfileSnapshotsAndCancellation() {
        var first = PrimeExtensions.ProfilePrimeCountingFunction(value: 1000);
        var second = PrimeExtensions.ProfilePrimeCountingFunction(value: 0);

        if ((first.Result != 168) || (second.Result != 0) || (first.Counts.Count != 1) || (second.Counts.Count != 1)) {
            return "profile snapshots lost their request results or records";
        }
        if ((first.Counts[0].Bound != 1000) || (second.Counts[0].Bound != 0) || (first.Counts[0].Route != "Narrow32")) {
            return "a later request changed the earlier profile count record";
        }
        if (first.Counts is IList<PrimeCountWork> list) {
            if (!list.IsReadOnly) { return "profile exposes a writable count list"; }
            var failure = Refuses(() => list[0] = second.Counts[0], typeof(NotSupportedException), null, "frozen prime profile list");

            if (failure is not null) { return failure; }
        }
        var canceled = new CancellationToken(canceled: true);

        return (Refuses(() => PrimeExtensions.ProfilePrimeCountingFunction(cancellationToken: canceled, value: 0), typeof(OperationCanceledException), null, "pre-canceled count profile")
            ?? Refuses(() => PrimeExtensions.ProfileNthPrime(cancellationToken: canceled, value: ulong.MaxValue), typeof(OperationCanceledException), null, "pre-canceled rank profile"));
    }

    private static string? PrimeProfileBitmapSelection(ulong index, ulong expected) {
        var profile = PrimeExtensions.ProfileNthPrime(value: index);

        if ((profile.Result != expected) || (profile.Result != index.NthPrime())) { return $"profile bitmap rank {index} differs from independent/ordinary selection"; }
        return (((profile.Route == "CheckpointSelection") && (profile.Counts.Count == 0) && (profile.SelectionWindows == 1) &&
            (profile.SelectionSegments == 1) && (profile.SelectionBitmapBytes > 0) && (profile.DirectPrimalityRequests == 0))
            ? null : $"profile bitmap rank {index} did not record exactly one window and segment without count dispatch");
    }
    private static bool PrimeProfileHasSelectionWork(PrimeRequestProfile profile) =>
        ((profile.SelectionWindows != 0) || (profile.SelectionSegments != 0) ||
            (profile.SelectionBitmapBytes != 0) || (profile.DirectPrimalityRequests != 0));
    private static string? PrimeProfileCountHeader(PrimeRequestProfile profile, ulong bound, string route) {
        if ((profile.Route != "CountOnly") || (profile.Counts.Count != 1) || PrimeProfileHasSelectionWork(profile: profile)) {
            return $"profile pi({bound}) has the wrong request route or dispatch count";
        }
        var work = profile.Counts[0];

        if ((work.Bound != bound) || (work.Route != route)) { return $"profile pi({bound}) has the wrong count bound/route"; }
        return (((work.Cutoff == 0) && (work.TableCapacity == 0) && !work.TableBuilt &&
            (work.OrdinaryFactorCoordinates == 0) && (work.SpecialFactorCoordinates == 0) && (work.CachedQuotientDivisions == 0) &&
            (work.LeafSegments == 0) && (work.LeafBitmapBytes == 0) && (work.LeafFrontier == 0) &&
            (work.EasyLeafSegments == 0) && (work.EasyLeafBitmapBytes == 0) &&
            (work.SemiprimeForwardFrontier == 0) && (work.SemiprimeForwardSegments == 0) && (work.SemiprimeForwardBitmapBytes == 0) &&
            (work.SemiprimeReverseWindows == 0) && (work.SemiprimeReverseBitmapBytes == 0))
            ? null : $"non-Gourdon route {route} recorded Gourdon work");
    }
}
