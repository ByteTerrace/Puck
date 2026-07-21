using System.Numerics;

namespace Puck.Maths.Research;

/// <summary>The outcome of bounded maximal-right-return analysis.</summary>
public enum FibonacciReturnAnalysisStatus {
    /// <summary>The two requested factors already differ.</summary>
    NotAReturn,
    /// <summary>The factors still agree at the supplied search limit.</summary>
    SearchLimitReached,
    /// <summary>A maximal right return was found, but no Lean-shaped decomposition was found.</summary>
    NoDecomposition,
    /// <summary>A maximal right return and at least one exact period decomposition were found.</summary>
    Classified
}

/// <summary>
/// An exact computational witness for <c>FibonacciPeriodDecomposition</c> in the Lean development.
/// </summary>
/// <remarks>
/// <paramref name="ShortBlockCount"/> and <paramref name="LongBlockCount"/> are the Lean coordinates
/// <c>l</c> and <c>k</c>. The certificate uses arbitrary-width integers and an exact quadratic-surd
/// comparison for the open strip; it is suitable for falsification and proof-shape discovery, not as
/// a substitute for the unbounded Lean theorem.
/// </remarks>
public readonly record struct FibonacciPeriodDecompositionCertificate(
    BigInteger Start,
    BigInteger Overlap,
    BigInteger Root,
    int Phase,
    BigInteger ShortBlockCount,
    BigInteger LongBlockCount,
    FibonacciFactorCounts RootCounts,
    BigInteger CentralFactorLength
) {
    /// <summary>Gets the exact strip quantity <c>|l·τ−k|</c>.</summary>
    public QuadraticSurd StripError =>
        ((QuadraticSurd.Rational(ShortBlockCount) * FibonacciResearch.GoldenRatio) -
            QuadraticSurd.Rational(LongBlockCount)).Abs();

    /// <summary>Gets the exact strip cutoff <c>τ²</c>.</summary>
    public static QuadraticSurd StripCutoff =>
        (FibonacciResearch.GoldenRatio * FibonacciResearch.GoldenRatio);

    /// <summary>Rechecks every clause of the corresponding Lean predicate.</summary>
    public bool Verify(FibonacciRulerWordIndex word) {
        ArgumentNullException.ThrowIfNull(word);
        if ((Start.Sign < 0) || (Overlap.Sign < 0) || (Root.Sign <= 0) ||
            (Phase <= 0) || (ShortBlockCount.Sign < 0) || (LongBlockCount.Sign < 0) ||
            (ShortBlockCount.IsZero && LongBlockCount.IsZero)) {
            return false;
        }
        if (!FibonacciReturnResearch.AreEqualBaseFactors(word, Start, Start + Root, Overlap)) {
            return false;
        }

        var expectedCounts = word.FactorCounts(Start, Root);
        var (phaseMinusOne, phaseValue) = FibonacciResearch.FibonacciPair(Phase - 1);
        var phasePlusOne = (phaseMinusOne + phaseValue);
        var phasePlusTwo = (phaseValue + phasePlusOne);
        var expectedCentralLength = FibonacciReturnResearch.CentralFactorLength(Phase);

        return (RootCounts == expectedCounts) &&
            (CentralFactorLength == expectedCentralLength) &&
            (Overlap <= CentralFactorLength) &&
            (Root == ((ShortBlockCount * phasePlusOne) + (LongBlockCount * phasePlusTwo))) &&
            (RootCounts.FalseCount ==
                ((ShortBlockCount * phaseValue) + (LongBlockCount * phasePlusOne))) &&
            (RootCounts.TrueCount ==
                ((ShortBlockCount * phaseMinusOne) + (LongBlockCount * phaseValue))) &&
            (StripError < StripCutoff);
    }
}

/// <summary>
/// The canonical-phase data of one maximal right return, including the two signed coordinates
/// that remain to be proved non-negative in the Lean development.
/// </summary>
/// <remarks>
/// Unlike <see cref="FibonacciPeriodDecompositionCertificate"/>, this profile is intentionally
/// meaningful when a coordinate is negative. That makes it a counterexample witness for the exact
/// residual conjecture instead of silently discarding the case. All quantities are exact.
/// </remarks>
public readonly record struct FibonacciCanonicalReturnProfile(
    BigInteger Start,
    BigInteger Root,
    BigInteger MaximalOverlap,
    int Phase,
    FibonacciFactorCounts RootCounts,
    BigInteger ShortCoordinate,
    BigInteger LongCoordinate,
    BigInteger CentralFactorLength
) {
    private static QuadraticSurd BaseSlope =>
        QuadraticSurd.One - (QuadraticSurd.One / FibonacciResearch.GoldenRatio);

    /// <summary>Gets whether this is in the overlap range used by the Lean maximal-return theorem.</summary>
    public bool HasRichOverlap => MaximalOverlap >= 3;

    /// <summary>Gets whether both canonical Cassini-inverse coordinates are natural numbers.</summary>
    public bool CoordinatesAreNonnegative =>
        (ShortCoordinate.Sign >= 0) && (LongCoordinate.Sign >= 0);

    /// <summary>Gets the signed mechanical discrepancy <c>root·τ⁻² − trueCount</c>.</summary>
    public QuadraticSurd SignedMechanicalError =>
        (QuadraticSurd.Rational(Root) * BaseSlope) -
        QuadraticSurd.Rational(RootCounts.TrueCount);

    /// <summary>Gets the absolute mechanical discrepancy.</summary>
    public QuadraticSurd MechanicalError => SignedMechanicalError.Abs();

    /// <summary>Gets the canonical cutoff <c>τ^(-phase)</c>.</summary>
    public QuadraticSurd MechanicalCutoff =>
        QuadraticSurd.One / FibonacciResearch.GoldenPower(Phase);

    /// <summary>Gets whether the exact mechanical-error inequality proved in Lean holds.</summary>
    public bool MechanicalBoundHolds => MechanicalError < MechanicalCutoff;

    /// <summary>Gets the exact coordinate-strip quantity <c>|l·τ−k|</c>.</summary>
    public QuadraticSurd CoordinateStripError =>
        ((QuadraticSurd.Rational(ShortCoordinate) * FibonacciResearch.GoldenRatio) -
            QuadraticSurd.Rational(LongCoordinate)).Abs();

    /// <summary>Gets whether the coordinates lie in the open strip <c>|l·τ−k| &lt; τ²</c>.</summary>
    public bool CoordinateStripHolds =>
        CoordinateStripError < FibonacciPeriodDecompositionCertificate.StripCutoff;

    /// <summary>
    /// Gets the error of the short neighboring convergent
    /// <c>F_(phase+1)·τ⁻² − F_(phase−1)</c>.
    /// </summary>
    public QuadraticSurd ShortApproximantError {
        get {
            var (phaseMinusOne, phaseValue) = FibonacciResearch.FibonacciPair(Phase - 1);
            var phasePlusOne = (phaseMinusOne + phaseValue);
            return (QuadraticSurd.Rational(phasePlusOne) * BaseSlope) -
                QuadraticSurd.Rational(phaseMinusOne);
        }
    }

    /// <summary>
    /// Gets the error of the long neighboring convergent
    /// <c>F_(phase+2)·τ⁻² − F_phase</c>.
    /// </summary>
    public QuadraticSurd LongApproximantError {
        get {
            var (phaseValue, phasePlusOne) = FibonacciResearch.FibonacciPair(Phase);
            var phasePlusTwo = (phaseValue + phasePlusOne);
            return (QuadraticSurd.Rational(phasePlusTwo) * BaseSlope) -
                QuadraticSurd.Rational(phaseValue);
        }
    }

    /// <summary>Gets whether the two consecutive convergent errors have opposite signs.</summary>
    public bool ApproximantsBracketSlope =>
        ((ShortApproximantError.Sign < 0) && (LongApproximantError.Sign > 0)) ||
        ((ShortApproximantError.Sign > 0) && (LongApproximantError.Sign < 0));

    /// <summary>Gets the sum of the two exact convergent-error magnitudes.</summary>
    public QuadraticSurd ApproximantErrorRadius =>
        ShortApproximantError.Abs() + LongApproximantError.Abs();

    /// <summary>
    /// Gets whether the neighboring-error radius is exactly the canonical mechanical cutoff.
    /// This is the executable form of the Fibonacci power identity used by the Lean proof.
    /// </summary>
    public bool ApproximantRadiusMatchesCutoff => ApproximantErrorRadius == MechanicalCutoff;

    /// <summary>
    /// Gets whether the two convergent denominators used by the Lean bracket fit inside
    /// <c>maximalOverlap+1</c>.
    /// </summary>
    public bool ApproximantDenominatorsFit {
        get {
            var (_, phasePlusTwo) = FibonacciResearch.FibonacciPair(Phase + 1);
            return phasePlusTwo <= (MaximalOverlap + BigInteger.One);
        }
    }

    /// <summary>Rechecks maximality and every stored exact field, without assuming the conjecture.</summary>
    public bool Verify(FibonacciRulerWordIndex word) {
        ArgumentNullException.ThrowIfNull(word);
        if ((Start.Sign < 0) || (Root.Sign <= 0) || (MaximalOverlap.Sign < 0) || (Phase <= 0)) {
            return false;
        }
        if (!FibonacciReturnResearch.AreEqualBaseFactors(
            word,
            Start,
            Start + Root,
            MaximalOverlap
        )) {
            return false;
        }
        if (word.BaseLetterAt(Start + MaximalOverlap) ==
            word.BaseLetterAt(Start + Root + MaximalOverlap)) {
            return false;
        }

        var expectedCounts = word.FactorCounts(Start, Root);
        var expectedPhase = FibonacciReturnResearch.LeastCentralPhaseCovering(MaximalOverlap);
        var expectedCoordinates = FibonacciReturnResearch.ReturnCoordinates(expectedPhase, expectedCounts);
        return (RootCounts == expectedCounts) &&
            (Phase == expectedPhase) &&
            (ShortCoordinate == expectedCoordinates.ShortCoordinate) &&
            (LongCoordinate == expectedCoordinates.LongCoordinate) &&
            (CentralFactorLength == FibonacciReturnResearch.CentralFactorLength(Phase)) &&
            (MaximalOverlap <= CentralFactorLength);
    }

    /// <summary>
    /// Converts the profile to the exact Lean period-decomposition certificate when the two
    /// residual sign conditions and the already-proved strip condition hold.
    /// </summary>
    public bool TryGetPeriodDecomposition(
        out FibonacciPeriodDecompositionCertificate certificate
    ) {
        if (!CoordinatesAreNonnegative ||
            (ShortCoordinate.IsZero && LongCoordinate.IsZero) ||
            !CoordinateStripHolds) {
            certificate = default;
            return false;
        }

        certificate = new FibonacciPeriodDecompositionCertificate(
            Start,
            MaximalOverlap,
            Root,
            Phase,
            ShortCoordinate,
            LongCoordinate,
            RootCounts,
            CentralFactorLength
        );
        return true;
    }
}

/// <summary>A bounded analysis of one concrete repeated Fibonacci factor.</summary>
public sealed record FibonacciMaximalReturnAnalysis(
    FibonacciReturnAnalysisStatus Status,
    BigInteger Start,
    BigInteger Root,
    BigInteger RequestedOverlap,
    BigInteger? MaximalOverlap,
    IReadOnlyList<FibonacciPeriodDecompositionCertificate> Decompositions
) {
    /// <summary>
    /// Gets the canonical signed-coordinate profile when a first right mismatch was found.
    /// This remains populated when the conjectured coordinate non-negativity fails.
    /// </summary>
    public FibonacciCanonicalReturnProfile? CanonicalProfile { get; init; }
}

/// <summary>
/// Exact executable probes for maximal right returns of the characteristic Fibonacci word.
/// </summary>
/// <remarks>
/// This API mirrors the remaining combinatorial interface in the Lean proof. It can exhaustively
/// search finite boxes for a counterexample, expose all admissible phases rather than choosing one
/// heuristically, and distinguish a disproved return from an inconclusive bounded extension search.
/// </remarks>
public static class FibonacciReturnResearch {
    /// <summary>Returns the central-factor length <c>F_(phase+3)−2</c> at a positive phase.</summary>
    public static BigInteger CentralFactorLength(int phase) {
        ArgumentOutOfRangeException.ThrowIfLessThan(phase, 1);
        var (phasePlusOne, phasePlusTwo) = FibonacciResearch.FibonacciPair(phase + 1);
        return (phasePlusOne + phasePlusTwo - 2);
    }

    /// <summary>Returns the least positive phase whose central factor covers an overlap.</summary>
    public static int LeastCentralPhaseCovering(BigInteger overlap) {
        if (overlap.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(overlap)); }
        for (var phase = 1; ; phase = checked(phase + 1)) {
            if (CentralFactorLength(phase) >= overlap) { return phase; }
        }
    }

    /// <summary>
    /// Applies the signed Cassini inverse at one positive phase to a Fibonacci Parikh vector.
    /// The result is Lean's <c>fibonacciShortCoordinate</c> and
    /// <c>fibonacciLongCoordinate</c>, before either sign is assumed.
    /// </summary>
    public static (BigInteger ShortCoordinate, BigInteger LongCoordinate) ReturnCoordinates(
        int phase,
        FibonacciFactorCounts counts
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThan(phase, 1);
        if ((counts.FalseCount.Sign < 0) || (counts.TrueCount.Sign < 0)) {
            throw new ArgumentOutOfRangeException(nameof(counts));
        }

        var (phaseMinusOne, phaseValue) = FibonacciResearch.FibonacciPair(phase - 1);
        var phasePlusOne = (phaseMinusOne + phaseValue);
        var determinant = ((phase & 1) == 1) ? BigInteger.One : -BigInteger.One;
        return (
            determinant * ((counts.FalseCount * phaseValue) - (counts.TrueCount * phasePlusOne)),
            determinant * ((counts.TrueCount * phaseValue) - (counts.FalseCount * phaseMinusOne))
        );
    }

    /// <summary>
    /// Creates the canonical profile of a known maximal right return and rejects a non-maximal claim.
    /// </summary>
    public static FibonacciCanonicalReturnProfile ProfileCanonicalMaximalReturn(
        FibonacciRulerWordIndex word,
        BigInteger start,
        BigInteger root,
        BigInteger maximalOverlap
    ) {
        ArgumentNullException.ThrowIfNull(word);
        if (start.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(start)); }
        if (root.Sign <= 0) { throw new ArgumentOutOfRangeException(nameof(root)); }
        if (maximalOverlap.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(maximalOverlap)); }
        if (!AreEqualBaseFactors(word, start, start + root, maximalOverlap) ||
            (word.BaseLetterAt(start + maximalOverlap) ==
                word.BaseLetterAt(start + root + maximalOverlap))) {
            throw new ArgumentException(
                "the supplied overlap is not the first right mismatch",
                nameof(maximalOverlap)
            );
        }

        var phase = LeastCentralPhaseCovering(maximalOverlap);
        var counts = word.FactorCounts(start, root);
        var coordinates = ReturnCoordinates(phase, counts);
        return new FibonacciCanonicalReturnProfile(
            start,
            root,
            maximalOverlap,
            phase,
            counts,
            coordinates.ShortCoordinate,
            coordinates.LongCoordinate,
            CentralFactorLength(phase)
        );
    }

    /// <summary>Checks equality of two finite base-word factors without materializing either prefix.</summary>
    public static bool AreEqualBaseFactors(
        FibonacciRulerWordIndex word,
        BigInteger leftStart,
        BigInteger rightStart,
        BigInteger length
    ) {
        ArgumentNullException.ThrowIfNull(word);
        if (leftStart.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(leftStart)); }
        if (rightStart.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(rightStart)); }
        if (length.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(length)); }

        for (var offset = BigInteger.Zero; offset < length; ++offset) {
            if (word.BaseLetterAt(leftStart + offset) != word.BaseLetterAt(rightStart + offset)) {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Extends a known return to its first right mismatch and finds every exact Lean-shaped decomposition.
    /// </summary>
    /// <param name="word">The random-access characteristic Fibonacci word.</param>
    /// <param name="start">The start of the first occurrence.</param>
    /// <param name="root">The positive distance to the second occurrence.</param>
    /// <param name="requestedOverlap">The already-known common-factor length.</param>
    /// <param name="searchLimit">
    /// The exclusive upper bound on offsets inspected for the first mismatch. A result of
    /// <see cref="FibonacciReturnAnalysisStatus.SearchLimitReached"/> is deliberately inconclusive.
    /// </param>
    public static FibonacciMaximalReturnAnalysis Analyze(
        FibonacciRulerWordIndex word,
        BigInteger start,
        BigInteger root,
        BigInteger requestedOverlap,
        BigInteger searchLimit
    ) {
        ArgumentNullException.ThrowIfNull(word);
        if (start.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(start)); }
        if (root.Sign <= 0) { throw new ArgumentOutOfRangeException(nameof(root)); }
        if (requestedOverlap.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(requestedOverlap)); }
        if (searchLimit < requestedOverlap) {
            throw new ArgumentOutOfRangeException(nameof(searchLimit), "the search limit must cover the requested overlap");
        }

        if (!AreEqualBaseFactors(word, start, start + root, requestedOverlap)) {
            return new FibonacciMaximalReturnAnalysis(
                FibonacciReturnAnalysisStatus.NotAReturn,
                start,
                root,
                requestedOverlap,
                MaximalOverlap: null,
                Decompositions: []
            );
        }

        BigInteger? maximalOverlap = null;
        for (var offset = requestedOverlap; offset < searchLimit; ++offset) {
            if (word.BaseLetterAt(start + offset) != word.BaseLetterAt(start + root + offset)) {
                maximalOverlap = offset;
                break;
            }
        }
        if (maximalOverlap is null) {
            return new FibonacciMaximalReturnAnalysis(
                FibonacciReturnAnalysisStatus.SearchLimitReached,
                start,
                root,
                requestedOverlap,
                MaximalOverlap: null,
                Decompositions: []
            );
        }

        // Certify the strongest overlap discovered. The same phase and coordinates
        // then witness every shorter overlap, including requestedOverlap.
        var canonicalProfile = ProfileCanonicalMaximalReturn(
            word,
            start,
            root,
            maximalOverlap.Value
        );
        var decompositions = FindPeriodDecompositions(word, start, maximalOverlap.Value, root);
        var status = (decompositions.Count == 0)
            ? FibonacciReturnAnalysisStatus.NoDecomposition
            : FibonacciReturnAnalysisStatus.Classified;
        return new FibonacciMaximalReturnAnalysis(
            status,
            start,
            root,
            requestedOverlap,
            maximalOverlap,
            decompositions
        ) { CanonicalProfile = canonicalProfile };
    }

    /// <summary>
    /// Finds every positive phase satisfying the exact clauses of Lean's
    /// <c>FibonacciPeriodDecomposition start overlap root</c>.
    /// </summary>
    public static IReadOnlyList<FibonacciPeriodDecompositionCertificate> FindPeriodDecompositions(
        FibonacciRulerWordIndex word,
        BigInteger start,
        BigInteger overlap,
        BigInteger root
    ) {
        ArgumentNullException.ThrowIfNull(word);
        if (start.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(start)); }
        if (overlap.Sign < 0) { throw new ArgumentOutOfRangeException(nameof(overlap)); }
        if (root.Sign <= 0) { throw new ArgumentOutOfRangeException(nameof(root)); }
        if (!AreEqualBaseFactors(word, start, start + root, overlap)) { return []; }

        var result = new List<FibonacciPeriodDecompositionCertificate>();
        var rootCounts = word.FactorCounts(start, root);
        var previous = BigInteger.Zero;
        var current = BigInteger.One;

        for (var phase = 1; ; ++phase) {
            var next = (previous + current);
            if (next > root) { break; }
            var (shortBlockCount, longBlockCount) = ReturnCoordinates(phase, rootCounts);
            var centralLength = FibonacciReturnResearch.CentralFactorLength(phase);

            if ((shortBlockCount.Sign >= 0) && (longBlockCount.Sign >= 0) &&
                !(shortBlockCount.IsZero && longBlockCount.IsZero) && (overlap <= centralLength)) {
                var certificate = new FibonacciPeriodDecompositionCertificate(
                    start,
                    overlap,
                    root,
                    phase,
                    shortBlockCount,
                    longBlockCount,
                    rootCounts,
                    centralLength
                );
                if (certificate.StripError < FibonacciPeriodDecompositionCertificate.StripCutoff) {
                    result.Add(certificate);
                }
            }

            (previous, current) = (current, next);
        }

        return result;
    }
}
