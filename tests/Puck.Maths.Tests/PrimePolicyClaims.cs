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
        (0, 10_000_000_000_000_000, PrimeSieveMode.Eratosthenes),
        (0, 1_000_000_000_000_000_000, PrimeSieveMode.Windowed),
        (0, ulong.MaxValue, PrimeSieveMode.Windowed),
        (18_446_744_073_642_442_752, ulong.MaxValue, PrimeSieveMode.Windowed),
        (18_446_744_073_642_442_753, ulong.MaxValue, PrimeSieveMode.Presieve),
        (ulong.MaxValue, ulong.MaxValue, PrimeSieveMode.Presieve),
        (2, 1, PrimeSieveMode.Eratosthenes),
    ];
    // Automatic windowed intervals raise the request to 16 MiB less one 32-KiB chunk: 16777216 - 32768. Otherwise twice the
    // square root is clamped between min(request, 32768) and the request, then rounded down to a multiple of that minimum.
    private static readonly (ulong Low, ulong High, int Requested, PrimeSieveMode Mode, int Expected)[] PrimeIntervalBitmapCases = [
        (0, 0, 1, PrimeSieveMode.Automatic, 1),
        (0, 1000, 65536, PrimeSieveMode.Automatic, 32768),
        (0, uint.MaxValue, 1048576, PrimeSieveMode.Automatic, 98304),
        (0, 1_000_000_000_000, 1048576, PrimeSieveMode.Automatic, 1048576),
        (0, ulong.MaxValue, 32768, PrimeSieveMode.Automatic, 16_744_448),
        (0, ulong.MaxValue, 33_554_432, PrimeSieveMode.Automatic, 33_554_432),
        (0, ulong.MaxValue, 32768, PrimeSieveMode.Windowed, 32768),
        (0, 10_000_000_000_000_000, 32768, PrimeSieveMode.Automatic, 32768),
        (18_446_744_073_642_442_753, ulong.MaxValue, 32768, PrimeSieveMode.Automatic, 32768),
    ];

    public static string? PrimeSegmentCancellation() {
        var canceled = new CancellationToken(canceled: true);
        var calls = 0;

        foreach (var (low, high) in new[] { (7UL, 1000UL), (2UL, 1UL), (10_000_000_000UL, 10_000_010_000UL) }) {
            var failure = (Refuses(() => PrimeExploration.Count(cancellationToken: canceled, high: high, low: low), typeof(OperationCanceledException), null, "canceled interval count")
                ?? Refuses(() => PrimeExploration.Enumerate(low: low, high: high, cancellationToken: canceled, onPrime: _ => ++calls), typeof(OperationCanceledException), null, "canceled interval enumeration"));

            if (failure is not null) { return failure; }
        }
        if (calls != 0) { return "pre-canceled enumeration invoked a callback"; }
        foreach (var mode in new[] { PrimeSieveMode.Eratosthenes, PrimeSieveMode.Presieve, PrimeSieveMode.Windowed }) {
            foreach (var (low, high) in new[] { (2UL, 2UL), (7UL, 29UL), (7UL, 1000UL), (10_000_000_000UL, 10_000_010_000UL) }) {
                using var source = new CancellationTokenSource();
                var firstBlock = ulong.MaxValue;
                var nextSegmentVisited = false;

                try {
                    PrimeExploration.Enumerate(low: low, high: high, segmentBytes: 1, mode: mode,
                        cancellationToken: source.Token, onPrime: prime => {
                            if (firstBlock == ulong.MaxValue) { firstBlock = (prime / 30); }
                            nextSegmentVisited |= ((prime / 30) != firstBlock);
                            source.Cancel();
                        });
                    return "callback cancellation returned normally";
                } catch (OperationCanceledException exception) {
                    if (exception.CancellationToken != source.Token) { return "cancellation lost the caller's token"; }
                }
                if (nextSegmentVisited) { return "enumeration continued into a segment after cancellation"; }
            }
        }
        return ((PrimeExploration.Count(low: 7, high: 29) == 7) ? null : "fresh count failed after canceled enumeration");
    }
    public static string? PrimePolicyBoundaries() {
        foreach (var row in PrimePolicyCases) {
            var actual = PrimeExploration.ResolveMode(low: row.Low, high: row.High);

            if (actual != row.Expected) { return $"automatic [{row.Low},{row.High}] chose {actual}, expected {row.Expected}"; }
            foreach (var mode in new[] { PrimeSieveMode.Eratosthenes, PrimeSieveMode.Presieve, PrimeSieveMode.Windowed }) {
                if (PrimeExploration.ResolveMode(high: row.High, low: row.Low, mode: mode) != mode) { return "explicit sieve policy was changed"; }
            }
        }
        foreach (var row in PrimeIntervalBitmapCases) {
            var actual = PrimeExploration.ResolveSegmentBytes(high: row.High, low: row.Low, mode: row.Mode, segmentBytes: row.Requested);

            if (actual != row.Expected) { return $"bitmap for [{row.Low},{row.High}] {row.Mode}: expected {row.Expected}, got {actual}"; }
        }
        return (Refuses(() => PrimeExploration.ResolveMode(high: 0, low: 0, mode: ((PrimeSieveMode)99)), typeof(ArgumentOutOfRangeException), "mode", "undefined mode")
            ?? (Refuses(() => PrimeExploration.ResolveSegmentBytes(high: 0, low: 0, mode: PrimeSieveMode.Automatic, segmentBytes: 0), typeof(ArgumentOutOfRangeException), "segmentBytes", "zero segment")
            ?? (Refuses(() => PrimeExploration.ResolveSegmentBytes(high: 0, low: 0, mode: PrimeSieveMode.Automatic, segmentBytes: int.MaxValue), typeof(ArgumentOutOfRangeException), "segmentBytes", "oversized segment")
            ?? Refuses(() => PrimeExploration.ResolveSegmentBytes(high: 0, low: 0, mode: ((PrimeSieveMode)99), segmentBytes: 1), typeof(ArgumentOutOfRangeException), "mode", "undefined interval mode"))));
    }
    public static string? PrimePresieveProofBoundary() {
        var square = 4_295_098_369UL;
        var expected = new List<ulong>();

        for (var value = (square - 128); (value <= (square + 128)); ++value) {
            if (Oracles.ExactPrimality(value: value)) { expected.Add(item: value); }
        }
        var actual = new List<ulong>();

        PrimeExploration.Enumerate(low: (square - 128), high: (square + 128), onPrime: actual.Add, segmentBytes: 3, mode: PrimeSieveMode.Presieve);
        if (!actual.SequenceEqual(second: expected)) { return "presieve threshold crossed incorrectly"; }
        return ((PrimeExploration.Count(low: (square - 128), high: (square + 128), segmentBytes: 3, mode: PrimeSieveMode.Presieve) == ((ulong)expected.Count))
            ? null : "presieve threshold count differs");
    }
    public static string? PrimeConstellationEmptyValidation() {
        var pattern = new PrimeConstellation(offsets: [0, 1]);
        var count = 0;
        Action<ulong> visit = _ => ++count;

        pattern.Enumerate(low: 10, high: 9, visit: visit);
        if (count != 0) { return "reversed constellation interval is not empty"; }
        return (Refuses(() => pattern.Enumerate(low: 10, high: 9, visit: null!), typeof(ArgumentNullException), "visit", "empty interval with a null visitor")
            ?? Refuses(() => pattern.Enumerate(low: 10, high: 9, visit: visit, cancellationToken: new CancellationToken(canceled: true)), typeof(OperationCanceledException), null, "pre-canceled empty interval"));
    }
}
