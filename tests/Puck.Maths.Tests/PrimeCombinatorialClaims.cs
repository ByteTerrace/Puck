namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly (ulong Bound, ulong Count)[] PrimeBitmapCheckpoints = [
        ((1UL << 40), 41_203_088_796),
        ((1UL << 63), 216_289_611_853_439_384),
        (ulong.MaxValue, 425_656_284_035_217_743),
    ];
    private static readonly int[] PrimeBitmapDistances = [17, 32, 64, 128];

    public static string? PrimeCombinatorialCountsMatchPublished() {
        const ulong Bound = 100_000_000_000_000;
        const ulong Expected = 3_204_941_750_802;
        var profile = PrimeExtensions.ProfilePrimeCountingFunction(value: Bound);

        if (profile.Result != Expected) { return $"combinatorial pi({Bound}): published {Expected}, got {profile.Result}"; }
        if ((profile.Route != "CountOnly") || (profile.Counts.Count != 1) || PrimeProfileHasSelectionWork(profile: profile)) {
            return "profiled combinatorial count has the wrong request route or dispatch count";
        }
        var work = profile.Counts[0];

        if ((work.Bound != Bound) || (work.Route != "Gourdon") || (work.Cutoff == 0) ||
            (work.TableCapacity < work.Cutoff) || !work.TableBuilt) { return "profiled combinatorial count lacks its cutoff/table record"; }
        if ((work.OrdinaryFactorCoordinates == 0) || (work.SpecialFactorCoordinates == 0) || (work.CachedQuotientDivisions == 0) ||
            (work.LeafSegments == 0) || (work.LeafBitmapBytes == 0) || (work.LeafFrontier == 0) ||
            (work.EasyLeafSegments == 0) || (work.EasyLeafBitmapBytes == 0) ||
            (work.SemiprimeForwardFrontier == 0) || (work.SemiprimeForwardSegments == 0) || (work.SemiprimeForwardBitmapBytes == 0) ||
            (work.SemiprimeReverseWindows == 0) || (work.SemiprimeReverseBitmapBytes == 0)) { return "profiled combinatorial count omitted positive Gourdon work"; }
        return (((work.CheckpointSegments == 0) && (work.CheckpointBitmapBytes == 0))
            ? null : "profiled combinatorial count recorded checkpoint sieve work");
    }
    public static string? PrimeCombinatorialRanksMatchPublished() {
        const ulong Bound = 100_000_000_000_000;
        const ulong Count = 3_204_941_750_802;
        var lower = PrimeCombinatorialPublishedRank(bound: Bound, count: Count);

        if (lower is not null) { return lower; }
        var upper = Oracles.PrimeExplorationPrimes(high: (Bound + 1024), low: (Bound + 1));

        if (upper.Count == 0) { return "independent rank fixture has no prime above 10^14"; }
        var next = Count.NthPrime();

        return ((next == upper[0]) ? null : $"combinatorial successor rank: expected {upper[0]}, got {next}");
    }
    public static string? PrimeCombinatorialBoundaryDifferences() =>
        (PrimeCombinatorialCubeBoundary(root: 27_001)
        ?? (PrimeCombinatorialSquareBoundary(prime: 6_700_417)
        ?? (PrimeCombinatorialEasySquareBoundary(otherPrime: 65521, prime: 8191)
        ?? (PrimeCombinatorialEasySquareBoundary(otherPrime: 262139, prime: 1031)
        ?? PrimeCombinatorialHardEasyBoundary(otherPrime: 8191, prime: 4093)))));
    public static string? PrimeCombinatorialCountAt1E16() =>
        PrimeCombinatorialPublishedCount(bound: 10_000_000_000_000_000, expected: 279_238_341_033_925);
    public static string? PrimeCombinatorialCountAt1E19() =>
        PrimeCombinatorialPublishedCount(bound: 10_000_000_000_000_000_000, expected: 234_057_667_276_344_607);
    public static string? PrimeCombinatorialRankAt1E16() {
        const ulong Bound = 10_000_000_000_000_000;
        const ulong Count = 279_238_341_033_925;
        var expected = Oracles.PrimeExplorationPrimes(high: Bound, low: (Bound - 1023));
        var profile = PrimeExtensions.ProfileNthPrime(value: (Count - 1));

        if ((expected.Count == 0) || (profile.Result != expected[^1])) { return $"profiled rank near 10^16 differs from independent primes: {profile.Result}"; }
        return (((profile.Route == "CountedSelection") && (profile.Counts.Count == 1) && (profile.Counts[0].Route == "Gourdon") &&
            (profile.SelectionWindows >= 2) && (profile.SelectionSegments > 0) && (profile.SelectionBitmapBytes > 0))
            ? null : "rank near 10^16 did not use one Gourdon count and continue beyond its first local bitmap window");
    }
    public static string? PrimeCombinatorialRankAt1E19() =>
        PrimeCombinatorialPublishedRank(bound: 10_000_000_000_000_000_000, count: 234_057_667_276_344_607);
    public static string? PrimeCombinatorialCubeNear1E16() => PrimeCombinatorialCubeBoundary(root: 215_443);
    public static string? PrimeCombinatorialCubeNear1E19() => PrimeCombinatorialCubeBoundary(root: 2_154_435);
    public static string? PrimeCombinatorialHighASquare() =>
        PrimeCombinatorialEasySquareBoundary(otherPrime: 4_194_301, prime: 1_048_573);
    public static string? PrimeBitmapSelectionDirections() {
        foreach (var (bound, count) in PrimeBitmapCheckpoints) {
            var below = Oracles.PrimeExplorationPrimes(high: bound, low: (bound - 8191));

            if (below.Count < PrimeBitmapDistances[^1]) { return $"independent bitmap fixture has too few primes below {bound}"; }
            foreach (var distance in PrimeBitmapDistances) {
                var expected = below[^distance];
                var index = (count - ((uint)distance));
                var actual = index.NthPrime();

                if (actual != expected) { return $"reverse bitmap rank {index}: expected {expected}, got {actual}"; }
            }
            if (bound == ulong.MaxValue) { continue; }
            var above = Oracles.PrimeExplorationPrimes(high: (bound + 8192), low: (bound + 1));

            if (above.Count < PrimeBitmapDistances[^1]) { return $"independent bitmap fixture has too few primes above {bound}"; }
            foreach (var distance in PrimeBitmapDistances) {
                var expected = above[(distance - 1)];
                var index = ((count + ((uint)distance)) - 1);
                var actual = index.NthPrime();

                if (actual != expected) { return $"forward bitmap rank {index}: expected {expected}, got {actual}"; }
            }
        }
        return null;
    }

    private static string? PrimeCombinatorialPublishedCount(ulong bound, ulong expected) {
        // Table IV supplies the expected count; it is not computed by another production path.
        var actual = bound.PrimeCountingFunction();

        return ((actual == expected) ? null : $"combinatorial pi({bound}): published {expected}, got {actual}");
    }
    private static string? PrimeCombinatorialPublishedRank(ulong bound, ulong count) {
        var expected = Oracles.PrimeExplorationPrimes(high: bound, low: (bound - 1023));

        if (expected.Count == 0) { return $"independent rank fixture has no prime below {bound}"; }
        var actual = (count - 1).NthPrime();

        return ((actual == expected[^1]) ? null : $"combinatorial rank {(count - 1)}: expected {expected[^1]}, got {actual}");
    }
    private static string? PrimeCombinatorialCubeBoundary(uint root) {
        var center = checked(((((ulong)root) * root) * root));

        return PrimeCombinatorialCountDifferences(boundaries: [(center - 128), (center - 1), center, (center + 1), (center + 128)]);
    }
    private static string? PrimeCombinatorialSquareBoundary(uint prime) {
        if (!Oracles.ExactPrimality(value: prime)) { return $"square fixture factor {prime} is composite"; }
        var center = (((ulong)prime) * prime);

        return PrimeCombinatorialCountDifferences(boundaries: [(center - 1), center, (center + 1)]);
    }
    private static string? PrimeCombinatorialEasySquareBoundary(uint prime, uint otherPrime) {
        if (!Oracles.ExactPrimality(value: prime) || !Oracles.ExactPrimality(value: otherPrime)) {
            return $"easy square-root fixture factors {prime}/{otherPrime} are not both prime";
        }
        // At x=p*q^2 both floor(x/(p*q)) and floor(sqrt(x/p)) reach q. The fixtures
        // qualify A or reflected C2 endpoints according to their named declarations.
        var center = checked(((((ulong)prime) * otherPrime) * otherPrime));

        return PrimeCombinatorialCountDifferences(boundaries: [(center - 1), center, (center + 1)]);
    }
    private static string? PrimeCombinatorialHardEasyBoundary(uint prime, uint otherPrime) {
        if (!Oracles.ExactPrimality(value: prime) || !Oracles.ExactPrimality(value: otherPrime)) {
            return $"hard/easy fixture factors {prime}/{otherPrime} are not both prime";
        }
        // At x=p^3*q the leaf quotient floor(x/(p*q)) first reaches p^2.
        // This equality moves the pair from C2 to D; adjacent counts must neither lose nor duplicate it.
        var center = checked((((((ulong)prime) * prime) * prime) * otherPrime));

        return PrimeCombinatorialCountDifferences(boundaries: [(center - 1), center, (center + 1)]);
    }
    private static string? PrimeCombinatorialCountDifferences(ReadOnlySpan<ulong> boundaries) {
        var expected = Oracles.PrimeExplorationPrimes(low: (boundaries[0] + 1), high: boundaries[^1]);
        var previousBound = boundaries[0];
        var previousCount = previousBound.PrimeCountingFunction();
        var oracleIndex = 0;

        // Sorted endpoints share their full counts: k endpoints require exactly k calls,
        // not two calls for each of the k-1 adjacent differences.
        for (var index = 1; (index < boundaries.Length); ++index) {
            var bound = boundaries[index];
            var first = oracleIndex;

            while ((oracleIndex < expected.Count) && (expected[oracleIndex] <= bound)) { ++oracleIndex; }
            var increment = ((ulong)(oracleIndex - first));
            var actual = bound.PrimeCountingFunction();
            var expectedCount = (previousCount + increment);

            if (actual != expectedCount) {
                return $"combinatorial delta ({previousBound},{bound}]: expected {increment} from independent primes, got pi endpoints {previousCount}/{actual}";
            }
            previousBound = bound;
            previousCount = actual;
        }
        return null;
    }
}
