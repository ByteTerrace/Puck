namespace Puck.Maths.Tests;

internal static partial class Subjects {
    private static readonly (ulong Value, ulong Count)[] Prime64CountFixtures = [
        (10_000_000_000, 455_052_511), (100_000_000_000, 4_118_054_813),
        (1_000_000_000_000, 37_607_912_018),
    ];
    private static readonly (ulong Index, ulong Prime)[] Prime64RankFixtures = [
        (0, 2), (1, 3), (6541, 65521), (6542, 65537),
        (203_280_220, 4_294_967_291), (203_280_221, 4_294_967_311),
        (999_999_999, 22_801_763_489), (9_999_999_999, 252_097_800_623),
        (425_656_284_035_217_742, 18_446_744_073_709_551_557),
        (425_656_284_035_217_743, 0), (ulong.MaxValue, 0),
    ];
    private static readonly (ulong Bound, ulong Count)[] Prime64Checkpoints = [
        ((1UL << 32), 203_280_221), ((1UL << 40), 41_203_088_796),
        ((1UL << 44), 597_116_381_732), ((1UL << 48), 8_731_188_863_470),
        ((1UL << 63), 216_289_611_853_439_384), (ulong.MaxValue, 425_656_284_035_217_743),
    ];

    public static string? Prime64CountsMatchIndependent() {
        var sieve = Oracles.PrimeSieve(inclusiveMaximum: 2048);
        var expected = 0UL;

        for (var value = 0; (value < sieve.Length); ++value) {
            if (sieve[value]) { ++expected; }
            var actual = ((ulong)value).PrimeCountingFunction();

            if (actual != expected) { return $"ulong pi({value}): expected {expected}, got {actual}"; }
        }
        foreach (var (value, count) in Prime64CountFixtures) {
            var actual = value.PrimeCountingFunction();

            if (actual != count) { return $"ulong pi({value}): published {count}, got {actual}"; }
        }
        foreach (var (bound, count) in Prime64Checkpoints) {
            var below = count;
            var above = count;

            for (var offset = 0UL; (offset < 256); ++offset) {
                if (Oracles.ExactPrimality(value: (bound - offset))) { --below; }
                if ((offset & 63) == 63) {
                    var lower = ((bound - offset) - 1);
                    var actual = lower.PrimeCountingFunction();

                    if (actual != below) { return $"ulong pi({lower}) below checkpoint {bound}: expected {below}, got {actual}"; }
                }
                if (bound == ulong.MaxValue) { continue; }
                if (Oracles.ExactPrimality(value: ((bound + offset) + 1))) { ++above; }
                if ((offset & 63) == 63) {
                    var upper = ((bound + offset) + 1);
                    var actual = upper.PrimeCountingFunction();

                    if (actual != above) { return $"ulong pi({upper}) above checkpoint {bound}: expected {above}, got {actual}"; }
                }
            }
        }
        return null;
    }
    public static string? Prime64RanksMatchIndependent() {
        foreach (var (index, prime) in Prime64RankFixtures) {
            var actual = index.NthPrime();

            if (actual != prime) { return $"ulong rank {index}: expected {prime}, got {actual}"; }
        }
        foreach (var (bound, count) in Prime64Checkpoints) {
            var below = count;
            var above = count;

            for (var offset = 0UL; (offset < 256); ++offset) {
                var lower = (bound - offset);

                if (Oracles.ExactPrimality(value: lower)) {
                    var actual = (--below).NthPrime();

                    if (actual != lower) { return $"ulong checkpoint rank {below}: expected {lower}, got {actual}"; }
                }
                if (bound == ulong.MaxValue) { continue; }
                var upper = ((bound + offset) + 1);

                if (Oracles.ExactPrimality(value: upper)) {
                    var actual = above.NthPrime();

                    if (actual != upper) { return $"ulong checkpoint rank {above}: expected {upper}, got {actual}"; }
                    ++above;
                }
            }
        }
        var canceled = new CancellationToken(canceled: true);

        return (Refuses(() => 1_000_000_000UL.NthPrime(cancellationToken: canceled), typeof(OperationCanceledException), null, "canceled ulong rank")
            ?? Refuses(() => (1UL << 60).PrimeCountingFunction(cancellationToken: canceled), typeof(OperationCanceledException), null, "canceled ulong count"));
    }
    public static string? Prime64CheckpointWalksMatchNative() {
        // Native primesieve counts 31,645 primes in [2^48+1, 2^48+1,048,832], and 31,426 in
        // [2^48-1,048,832, 2^48]. Each interval exceeds the production chunk width in a different direction.
        var checkpointCount = 8_731_188_863_470UL;
        var upperCount = (281_474_977_759_488UL).PrimeCountingFunction();
        var lowerCount = (281_474_975_661_824UL).PrimeCountingFunction();

        if (upperCount != (checkpointCount + 31_645)) { return $"upper chunk walk: expected {(checkpointCount + 31_645)}, got {upperCount}"; }
        if (lowerCount != (checkpointCount - 31_426)) { return $"lower chunk walk: expected {(checkpointCount - 31_426)}, got {lowerCount}"; }
        var upperPrime = (checkpointCount + 31_644).NthPrime();
        var lowerPrime = (checkpointCount - 31_426).NthPrime();

        if (upperPrime != 281_474_977_759_477) { return $"forward rank chunks: expected 281474977759477, got {upperPrime}"; }
        if (lowerPrime != 281_474_975_661_833) { return $"backward rank chunks: expected 281474975661833, got {lowerPrime}"; }
        // The root cap is an odd square and its successor is even. Native sieving finds 274,879 primes
        // above that square through 2^44, independently fixing both sides of the counting policy boundary.
        var atCap = (17_592_177_655_809UL).PrimeCountingFunction();
        var aboveCap = (17_592_177_655_810UL).PrimeCountingFunction();

        if ((atCap != 597_116_106_853) || (aboveCap != 597_116_106_853)) {
            return $"counting storage boundary: expected 597116106853 twice, got {atCap}/{aboveCap}";
        }
        return null;
    }
}
