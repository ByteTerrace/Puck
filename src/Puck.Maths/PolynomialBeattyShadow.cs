using System.Numerics;

namespace Puck.Maths;

/// <summary>An exact remainder certificate for the integer-boundary branch of one generalized Pell norm.</summary>
/// <param name="Norm">The cleared norm <c>h</c>.</param>
/// <param name="CoefficientCount">The number <c>k</c> of boundary-gap coefficients retained.</param>
/// <param name="Cutoff">The first index at which the certificate applies.</param>
/// <param name="RadiusNumerator">The integer <c>K</c> in the remainder bound <c>K/n^(k+1)</c>.</param>
/// <param name="DeltaLowerNumerator">The numerator of a rational lower bound for the conjugate gap divided by <c>n</c>.</param>
/// <param name="DenominatorLowerNumerator">The numerator of the lower bound used for the root-separation denominator.</param>
/// <param name="LowerBoundDenominator">The common positive denominator of the two lower bounds.</param>
/// <param name="ResidualMagnitudeCeiling">An integer upper bound for the cleared residual coefficient magnitude.</param>
public readonly record struct PolynomialBeattyBoundaryAsymptoticCertificate(
    BigInteger Norm,
    int CoefficientCount,
    BigInteger Cutoff,
    BigInteger RadiusNumerator,
    BigInteger DeltaLowerNumerator,
    BigInteger DenominatorLowerNumerator,
    BigInteger LowerBoundDenominator,
    BigInteger ResidualMagnitudeCeiling
);

/// <summary>
/// A finite proof that the boundary associated with one norm is eventually on a fixed side of the positive tail.
/// </summary>
/// <param name="Norm">The cleared generalized-Pell norm.</param>
/// <param name="ComparisonOrder">Zero for exact affine equality, otherwise the first unequal inverse-power order.</param>
/// <param name="Cutoff">The first index at which <see cref="BoundaryMinusTailSign"/> is certified.</param>
/// <param name="BoundaryMinusTailSign">The eventual sign of <c>m-s_n</c> on the near-center boundary branch.</param>
/// <param name="EventualFloorDiscrepancy">
/// The resulting discrepancy when that branch is the adjacent integer boundary: <c>+1</c>, <c>0</c>, or <c>-1</c>.
/// </param>
public readonly record struct PolynomialBeattyShadowNormDecisionCertificate(
    BigInteger Norm,
    int ComparisonOrder,
    BigInteger Cutoff,
    int BoundaryMinusTailSign,
    int EventualFloorDiscrepancy
);

/// <summary>An exact eventual floor decision for the integral-slope (square-discriminant) case.</summary>
/// <param name="Cutoff">The first index at which the discrepancy is constant.</param>
/// <param name="EventualFloorDiscrepancy">The constant discrepancy, necessarily zero or minus one.</param>
public readonly record struct PolynomialBeattyShadowRationalDecisionCertificate(
    BigInteger Cutoff,
    int EventualFloorDiscrepancy
);

/// <summary>One active generalized-Pell channel in an eventual Beatty-shadow presentation.</summary>
public readonly record struct PolynomialBeattyShadowEventualChannel(
    PolynomialBeattyShadowNormDecisionCertificate Decision,
    PolynomialBeattyShadowPellChannel Channel
);

/// <summary>
/// A finite presentation of every sufficiently large nonzero floor discrepancy as a union of congruence-periodic
/// generalized-Pell channels.
/// </summary>
public sealed class PolynomialBeattyShadowEventualCertificate {
    internal PolynomialBeattyShadowEventualCertificate(
        BigInteger cutoff,
        IReadOnlyList<PolynomialBeattyShadowNormDecisionCertificate> normDecisions,
        IReadOnlyList<PolynomialBeattyShadowEventualChannel> activeChannels) {
        Cutoff = cutoff;
        NormDecisions = normDecisions;
        ActiveChannels = activeChannels;
    }

    /// <summary>Gets the first index beyond which the channel presentation is complete.</summary>
    public BigInteger Cutoff { get; }
    /// <summary>Gets one sign-stabilization decision for every norm in the finite envelope.</summary>
    public IReadOnlyList<PolynomialBeattyShadowNormDecisionCertificate> NormDecisions { get; }
    /// <summary>Gets the channels whose adjacent near-center boundary is eventually crossed.</summary>
    public IReadOnlyList<PolynomialBeattyShadowEventualChannel> ActiveChannels { get; }
    /// <summary>Gets whether the discrepancy is certified zero at every index beyond <see cref="Cutoff"/>.</summary>
    public bool EventuallyIdenticallyZero => (ActiveChannels.Count == 0);
}

/// <summary>An explicit Ostrowski DFAO certificate for the sufficiently large discrepancy sequence.</summary>
public sealed class PolynomialBeattyShadowOstrowskiCertificate {
    internal PolynomialBeattyShadowOstrowskiCertificate(
        BigInteger cutoff,
        PolynomialBeattyShadowEventualCertificate eventualCertificate,
        IReadOnlyList<OstrowskiPellChannelCertificate> channelLanguages,
        OstrowskiOutputAutomaton automaton) {
        Cutoff = cutoff;
        EventualCertificate = eventualCertificate;
        ChannelLanguages = channelLanguages;
        Automaton = automaton;
    }

    public BigInteger Cutoff { get; }
    public PolynomialBeattyShadowEventualCertificate EventualCertificate { get; }
    public IReadOnlyList<OstrowskiPellChannelCertificate> ChannelLanguages { get; }
    public OstrowskiOutputAutomaton Automaton { get; }

    /// <summary>Evaluates the certified discrepancy at or beyond <see cref="Cutoff"/>.</summary>
    public BigInteger Output(BigInteger tailIndex) {
        if (tailIndex < Cutoff) {
            throw new ArgumentOutOfRangeException(nameof(tailIndex), "the index precedes the certified automaton cutoff");
        }
        return Automaton.Output(tailIndex);
    }
}

/// <summary>An all-index Ostrowski DFAO together with the exact finite-prefix floors used to construct it.</summary>
public sealed class PolynomialBeattyShadowTotalOstrowskiCertificate {
    internal PolynomialBeattyShadowTotalOstrowskiCertificate(
        PolynomialBeattyShadowOstrowskiCertificate eventualCertificate,
        IReadOnlyDictionary<BigInteger, BigInteger> finitePrefix,
        OstrowskiOutputAutomaton automaton) {
        EventualCertificate = eventualCertificate;
        FinitePrefix = finitePrefix;
        Automaton = automaton;
    }

    public PolynomialBeattyShadowOstrowskiCertificate EventualCertificate { get; }
    public IReadOnlyDictionary<BigInteger, BigInteger> FinitePrefix { get; }
    public OstrowskiOutputAutomaton Automaton { get; }
    public bool IdenticallyZero =>
        FinitePrefix.Values.All(value => value.IsZero) &&
        EventualCertificate.EventualCertificate.EventuallyIdenticallyZero;
    public BigInteger? FirstCounterexample {
        get {
            var finite = FinitePrefix.Where(pair => !pair.Value.IsZero).Select(pair => (BigInteger?)pair.Key).Min();
            var eventual = EventualCertificate.ChannelLanguages
                .Select(language => (BigInteger?)language.Channel.Decode(language.StartingExponent).TailIndex)
                .Min();
            if (finite is null) { return eventual; }
            if (eventual is null) { return finite; }
            return BigInteger.Min(finite.Value, eventual.Value);
        }
    }
    public BigInteger Output(BigInteger tailIndex) {
        if (tailIndex <= 0) {
            throw new ArgumentOutOfRangeException(nameof(tailIndex), "the tail index must be positive");
        }
        return Automaton.Output(tailIndex);
    }
}

/// <summary>An all-index positional DFAO for the integral-slope case.</summary>
public sealed class PolynomialBeattyShadowTotalPositionalCertificate {
    internal PolynomialBeattyShadowTotalPositionalCertificate(
        PolynomialBeattyShadowRationalDecisionCertificate eventualCertificate,
        IReadOnlyDictionary<BigInteger, BigInteger> finitePrefix,
        PositionalOutputAutomaton automaton) {
        EventualCertificate = eventualCertificate;
        FinitePrefix = finitePrefix;
        Automaton = automaton;
    }

    public PolynomialBeattyShadowRationalDecisionCertificate EventualCertificate { get; }
    public IReadOnlyDictionary<BigInteger, BigInteger> FinitePrefix { get; }
    public PositionalOutputAutomaton Automaton { get; }
    public bool IdenticallyZero =>
        FinitePrefix.Values.All(value => value.IsZero) &&
        (EventualCertificate.EventualFloorDiscrepancy == 0);
    public BigInteger? FirstCounterexample {
        get {
            var finite = FinitePrefix.Where(pair => !pair.Value.IsZero).Select(pair => (BigInteger?)pair.Key).Min();
            return finite ?? ((EventualCertificate.EventualFloorDiscrepancy == 0)
                ? null
                : EventualCertificate.Cutoff);
        }
    }
    public BigInteger Output(BigInteger tailIndex) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tailIndex);
        return Automaton.Output(tailIndex);
    }
}

/// <summary>
/// A bi-infinite congruence channel of integer boundaries for an affine quadratic center. Every point is obtained from
/// <c>BaseX+BaseY*sqrt(D)</c> by a power of <see cref="PeriodUnit"/>; every point has the same cleared norm.
/// </summary>
public readonly record struct PolynomialBeattyShadowPellChannel {
    internal PolynomialBeattyShadowPellChannel(
        BigInteger norm,
        BigInteger baseX,
        BigInteger baseY,
        PellUnit periodUnit,
        PolynomialBeattyShadowNormCertificate certificate) {
        Norm = norm;
        BaseX = baseX;
        BaseY = baseY;
        PeriodUnit = periodUnit;
        Certificate = certificate;
    }

    /// <summary>Gets the common cleared norm of the channel.</summary>
    public BigInteger Norm { get; }
    /// <summary>Gets the rational coefficient of the channel's exponent-zero Pell point.</summary>
    public BigInteger BaseX { get; }
    /// <summary>Gets the square-root coefficient of the channel's exponent-zero Pell point.</summary>
    public BigInteger BaseY { get; }
    /// <summary>Gets the norm-one unit advancing the channel by one congruence period.</summary>
    public PellUnit PeriodUnit { get; }
    /// <summary>Gets the affine-center certificate whose congruences define the channel.</summary>
    public PolynomialBeattyShadowNormCertificate Certificate { get; }

    /// <summary>Returns the Pell point at a signed channel exponent.</summary>
    public (BigInteger X, BigInteger Y) Point(int exponent) {
        var power = PeriodUnit.Power(checked((int)Math.Abs((long)exponent)));

        return (exponent < 0)
            ? power.Divide(x: BaseX, y: BaseY)
            : power.Multiply(x: BaseX, y: BaseY);
    }

    /// <summary>
    /// Decodes the point at a signed channel exponent into its tail index and integer boundary. Construction guarantees
    /// divisibility; the returned index can be non-positive on the finite side of a bi-infinite channel.
    /// </summary>
    public (BigInteger TailIndex, BigInteger Boundary) Decode(int exponent) {
        var point = Point(exponent: exponent);
        var slopeSurd = Certificate.SlopeSurdNumerator;
        var tailIndexNumerator = (point.Y - Certificate.OffsetSurdNumerator);
        var tailIndex = BigInteger.DivRem(
            dividend: tailIndexNumerator,
            divisor: slopeSurd,
            remainder: out var tailIndexRemainder
        );

        if (!tailIndexRemainder.IsZero) {
            throw new InvalidOperationException(message: "the Pell channel violated its tail-index congruence");
        }

        var centerRationalNumerator = (
            (Certificate.SlopeRationalNumerator * tailIndex) +
            Certificate.OffsetRationalNumerator
        );
        var boundary = BigInteger.DivRem(
            dividend: (point.X + centerRationalNumerator),
            divisor: Certificate.CommonDenominator,
            remainder: out var boundaryRemainder
        );

        if (!boundaryRemainder.IsZero) {
            throw new InvalidOperationException(message: "the Pell channel violated its integer-boundary congruence");
        }

        return (tailIndex, boundary);
    }
}

/// <summary>Finite quadratic-norm and generalized-Pell reductions for polynomial continued-fraction Beatty shadows.</summary>
public static class PolynomialBeattyShadow {
    /// <summary>
    /// Attempts to compile the complete integral-slope discrepancy sequence into a radix-2 DFAO. As in the
    /// irrational branch, an unresolved finite exact-integer comparison is reported rather than guessed.
    /// </summary>
    public static bool TryTotalPositionalAutomaton(
        PolynomialContinuedFractionAnalysis analysis,
        int refinementRounds,
        out PolynomialBeattyShadowTotalPositionalCertificate? certificate,
        out BigInteger unresolvedTailIndex) {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentOutOfRangeException.ThrowIfNegative(refinementRounds);
        if (!analysis.Slope.IsRational) {
            throw new ArgumentOutOfRangeException(nameof(analysis), "use the Ostrowski construction for irrational slope");
        }

        var eventual = RationalSlopeDecisionCertificate(analysis);
        var system = new PositionalNumerationSystem(radix: 2);
        var components = new List<(PositionalDigitAutomaton Automaton, BigInteger Output)>();
        if (eventual.EventualFloorDiscrepancy != 0) {
            components.Add((
                PositionalDigitAutomaton.AtLeast(system, eventual.Cutoff),
                eventual.EventualFloorDiscrepancy
            ));
        }

        var prefix = new Dictionary<BigInteger, BigInteger>();
        for (var tailIndex = BigInteger.One; tailIndex < eventual.Cutoff; ++tailIndex) {
            if (!analysis.TryCertifiedFloor(tailIndex, refinementRounds, out var tailFloor)) {
                certificate = null;
                unresolvedTailIndex = tailIndex;
                return false;
            }
            var discrepancy = tailFloor - analysis.AffineCenter(tailIndex).Floor();
            prefix[tailIndex] = discrepancy;
            if (!discrepancy.IsZero) {
                components.Add((
                    PositionalDigitAutomaton.FromLiteral(system.Radix, system.Represent(tailIndex)),
                    discrepancy
                ));
            }
        }

        certificate = new PolynomialBeattyShadowTotalPositionalCertificate(
            eventual,
            prefix,
            PositionalOutputAutomaton.Build(system, components)
        );
        unresolvedTailIndex = BigInteger.Zero;
        return true;
    }

    /// <summary>
    /// Attempts to compile the complete discrepancy sequence into one Ostrowski DFAO. The method is exact: when a
    /// finite tail enclosure still straddles an integer after <paramref name="refinementRounds"/>, it returns false and
    /// identifies that index instead of assuming equality or inequality.
    /// </summary>
    public static bool TryTotalOstrowskiAutomaton(
        PolynomialContinuedFractionAnalysis analysis,
        int refinementRounds,
        out PolynomialBeattyShadowTotalOstrowskiCertificate? certificate,
        out BigInteger unresolvedTailIndex) {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentOutOfRangeException.ThrowIfNegative(refinementRounds);
        if (analysis.Slope.IsRational) {
            throw new ArgumentOutOfRangeException(nameof(analysis), "use the positional rational-slope construction");
        }

        var eventual = EventualOstrowskiAutomaton(analysis);
        var components = new List<(OstrowskiDigitAutomaton Automaton, BigInteger Output)>();
        for (var index = 0; index < eventual.ChannelLanguages.Count; ++index) {
            components.Add((
                eventual.ChannelLanguages[index].CompileAutomaton(),
                eventual.EventualCertificate.ActiveChannels[index].Decision.EventualFloorDiscrepancy
            ));
        }

        var prefix = new Dictionary<BigInteger, BigInteger>();
        for (var tailIndex = BigInteger.One; tailIndex < eventual.Cutoff; ++tailIndex) {
            if (!analysis.TryCertifiedFloor(tailIndex, refinementRounds, out var tailFloor)) {
                certificate = null;
                unresolvedTailIndex = tailIndex;
                return false;
            }

            var discrepancy = (tailFloor - analysis.AffineCenter(tailIndex).Floor());
            prefix[tailIndex] = discrepancy;
            if (discrepancy.IsZero) { continue; }
            var word = eventual.Automaton.System.Represent(tailIndex);
            components.Add((OstrowskiDigitAutomaton.FromLiteral(word), discrepancy));
        }

        var automaton = OstrowskiOutputAutomaton.Build(eventual.Automaton.System, components);
        certificate = new PolynomialBeattyShadowTotalOstrowskiCertificate(eventual, prefix, automaton);
        unresolvedTailIndex = BigInteger.Zero;
        return true;
    }

    /// <summary>
    /// Compiles the eventual generalized-Pell presentation into an explicit deterministic output automaton over the
    /// canonical Ostrowski representations of the quadratic slope.
    /// </summary>
    public static PolynomialBeattyShadowOstrowskiCertificate EventualOstrowskiAutomaton(
        PolynomialContinuedFractionAnalysis analysis) {
        ArgumentNullException.ThrowIfNull(analysis);
        var eventual = EventualCertificate(analysis);
        var system = QuadraticOstrowskiSystem.Create(analysis.Slope);
        var languages = new List<OstrowskiPellChannelCertificate>();
        var cutoff = eventual.Cutoff;

        foreach (var active in eventual.ActiveChannels) {
            var language = OstrowskiPellChannel.Build(
                analysis,
                active.Channel,
                eventual.Cutoff
            );
            languages.Add(language);
            var first = active.Channel.Decode(language.StartingExponent).TailIndex;
            cutoff = BigInteger.Max(cutoff, first);
        }

        var components = new List<(OstrowskiDigitAutomaton Automaton, BigInteger Output)>();
        for (var index = 0; index < languages.Count; ++index) {
            var language = languages[index];
            var advance = 0;
            while (language.Channel.Decode(checked(language.StartingExponent + advance)).TailIndex < cutoff) {
                advance = checked(advance + 1);
            }
            language = language.Advance(advance);
            languages[index] = language;
            components.Add((
                language.CompileAutomaton(),
                BigInteger.One * eventual.ActiveChannels[index].Decision.EventualFloorDiscrepancy
            ));
        }

        var automaton = OstrowskiOutputAutomaton.Build(system, components);
        return new PolynomialBeattyShadowOstrowskiCertificate(
            cutoff,
            eventual,
            languages,
            automaton
        );
    }

    /// <summary>
    /// Decides the eventual discrepancy when the characteristic slope is rational (and therefore integral).
    /// </summary>
    public static PolynomialBeattyShadowRationalDecisionCertificate RationalSlopeDecisionCertificate(
        PolynomialContinuedFractionAnalysis analysis) {
        ArgumentNullException.ThrowIfNull(analysis);
        if (!analysis.Slope.IsRational || !analysis.Offset.IsRational) {
            throw new ArgumentOutOfRangeException(nameof(analysis), "the affine slope and offset must be rational");
        }

        var offsetFloor = analysis.Offset.Floor();
        var offsetFraction = (analysis.Offset - QuadraticSurd.Rational(offsetFloor));
        if (offsetFraction != QuadraticSurd.Zero) {
            var certificate = analysis.AsymptoticIntervalCertificate(termCount: 1);
            var upperDistance = (QuadraticSurd.One - offsetFraction);
            var boundaryDistance = (offsetFraction <= upperDistance) ? offsetFraction : upperDistance;
            var cutoff = BigInteger.Max(
                certificate.Cutoff,
                ((QuadraticSurd.Rational(certificate.RadiusNumerator) / boundaryDistance).Floor() + 1)
            );

            return new PolynomialBeattyShadowRationalDecisionCertificate(
                Cutoff: cutoff,
                EventualFloorDiscrepancy: 0
            );
        }

        var coefficients = analysis.AsymptoticCoefficients(termCount: 2);
        var leadingError = coefficients[1];
        if (leadingError == QuadraticSurd.Zero) {
            if (analysis.AffineResidual != QuadraticSurd.Zero) {
                throw new InvalidOperationException("zero leading error did not reduce to the exact affine case");
            }

            return new PolynomialBeattyShadowRationalDecisionCertificate(
                Cutoff: analysis.IntervalCertificate.Cutoff,
                EventualFloorDiscrepancy: 0
            );
        }

        var errorCertificate = analysis.AsymptoticIntervalCertificate(termCount: 2);
        var signCutoff = ((QuadraticSurd.Rational(errorCertificate.RadiusNumerator) /
            leadingError.Abs()).Floor() + 1);
        var unitCutoff = ((leadingError.Abs() +
            QuadraticSurd.Rational(errorCertificate.RadiusNumerator)).Floor() + 1);
        var finalCutoff = BigInteger.Max(
            errorCertificate.Cutoff,
            BigInteger.Max(signCutoff, unitCutoff)
        );

        return new PolynomialBeattyShadowRationalDecisionCertificate(
            Cutoff: finalCutoff,
            EventualFloorDiscrepancy: (leadingError.Sign < 0) ? -1 : 0
        );
    }

    /// <summary>
    /// Constructs a finite, exact generalized-Pell channel presentation of every nonzero discrepancy beyond one
    /// computable cutoff.
    /// </summary>
    /// <remarks>
    /// This method is intentionally proof-transparent rather than complexity-optimized: it enumerates every integer
    /// norm in the certified envelope and the bounded representative box for each generalized Pell equation.
    /// </remarks>
    public static PolynomialBeattyShadowEventualCertificate EventualCertificate(
        PolynomialContinuedFractionAnalysis analysis) {
        ArgumentNullException.ThrowIfNull(analysis);
        var envelope = analysis.BeattyShadowNormCertificate();
        if (envelope.SlopeSurdNumerator <= 0) {
            throw new ArgumentOutOfRangeException(nameof(analysis), "the affine slope must be irrational");
        }

        var decisions = new List<PolynomialBeattyShadowNormDecisionCertificate>();
        var activeChannels = new List<PolynomialBeattyShadowEventualChannel>();
        var cutoff = envelope.Cutoff;

        for (var norm = -envelope.NormMagnitudeBound;
            norm <= envelope.NormMagnitudeBound;
            ++norm) {
            var decision = NormDecisionCertificate(analysis, norm);
            decisions.Add(decision);
            cutoff = BigInteger.Max(cutoff, decision.Cutoff);
            if (decision.EventualFloorDiscrepancy == 0) { continue; }

            foreach (var channel in CandidatePellChannels(analysis, norm)) {
                var positiveEmbedding = QuadraticSurd.Create(
                    channel.BaseX,
                    channel.BaseY,
                    envelope.Radicand,
                    BigInteger.One
                );
                if (positiveEmbedding.Sign <= 0) { continue; }

                activeChannels.Add(new PolynomialBeattyShadowEventualChannel(
                    Decision: decision,
                    Channel: channel
                ));
            }
        }

        return new PolynomialBeattyShadowEventualCertificate(
            cutoff: cutoff,
            normDecisions: decisions,
            activeChannels: activeChannels
        );
    }

    /// <summary>
    /// Returns the first <paramref name="termCount"/> exact coefficients <c>a_1,...,a_k</c> of the near-center
    /// integer-boundary gap <c>m_h(n)-(lambda*n+beta)</c> for cleared norm <paramref name="norm"/>.
    /// </summary>
    public static IReadOnlyList<QuadraticSurd> BoundaryAsymptoticCoefficients(
        PolynomialContinuedFractionAnalysis analysis,
        BigInteger norm,
        int termCount) {
        ArgumentNullException.ThrowIfNull(analysis);
        if (termCount < 0) {
            throw new ArgumentOutOfRangeException(nameof(termCount), "the boundary term count must be non-negative");
        }
        if (termCount == 0) { return []; }

        var certificate = analysis.BeattyShadowNormCertificate();
        ValidateNorm(certificate: certificate, norm: norm);
        var scale = certificate.CommonDenominator;
        var normValue = QuadraticSurd.Rational(norm, scale * scale);
        var leadingConjugateGap = QuadraticSurd.Create(
            rationalNumerator: BigInteger.Zero,
            surdNumerator: (2 * certificate.SlopeSurdNumerator),
            radicand: certificate.Radicand,
            denominator: scale
        );
        var constantConjugateGap = QuadraticSurd.Create(
            rationalNumerator: BigInteger.Zero,
            surdNumerator: (2 * certificate.OffsetSurdNumerator),
            radicand: certificate.Radicand,
            denominator: scale
        );
        var coefficients = new QuadraticSurd[termCount];
        coefficients[0] = (normValue / leadingConjugateGap);

        for (var coefficientIndex = 1; coefficientIndex < termCount; ++coefficientIndex) {
            var convolution = QuadraticSurd.Zero;
            for (var leftIndex = 0; leftIndex <= (coefficientIndex - 2); ++leftIndex) {
                convolution += (coefficients[leftIndex] *
                    coefficients[coefficientIndex - 2 - leftIndex]);
            }

            coefficients[coefficientIndex] = -(
                (constantConjugateGap * coefficients[coefficientIndex - 1]) + convolution
            ) / leadingConjugateGap;
        }

        return coefficients;
    }

    /// <summary>
    /// Certifies the truncated boundary-gap expansion with an explicit <c>K/n^(k+1)</c> remainder.
    /// </summary>
    public static PolynomialBeattyBoundaryAsymptoticCertificate BoundaryAsymptoticIntervalCertificate(
        PolynomialContinuedFractionAnalysis analysis,
        BigInteger norm,
        int termCount) {
        ArgumentNullException.ThrowIfNull(analysis);
        if (termCount <= 0) {
            throw new ArgumentOutOfRangeException(nameof(termCount), "the certified boundary term count must be positive");
        }

        var normCertificate = analysis.BeattyShadowNormCertificate();
        ValidateNorm(certificate: normCertificate, norm: norm);
        var coefficients = BoundaryAsymptoticCoefficients(analysis, norm, termCount);
        var scaleZ = normCertificate.CommonDenominator;
        var normValue = QuadraticSurd.Rational(norm, scaleZ * scaleZ);
        var leadingConjugateGap = QuadraticSurd.Create(
            BigInteger.Zero,
            2 * normCertificate.SlopeSurdNumerator,
            normCertificate.Radicand,
            scaleZ
        );
        var constantConjugateGap = QuadraticSurd.Create(
            BigInteger.Zero,
            2 * normCertificate.OffsetSurdNumerator,
            normCertificate.Radicand,
            scaleZ
        );
        var residualCoefficients = BoundaryResidualCoefficients(
            coefficients,
            leadingConjugateGap,
            constantConjugateGap,
            normValue
        );
        var residualMagnitude = residualCoefficients.Aggregate(
            QuadraticSurd.Zero,
            (sum, coefficient) => sum + coefficient.Abs()
        );
        var coefficientMagnitude = coefficients.Aggregate(
            QuadraticSurd.Zero,
            (sum, coefficient) => sum + coefficient.Abs()
        );

        ChoosePositiveBounds(
            value: leadingConjugateGap,
            out var lowerScale,
            out var deltaLowerNumerator,
            out var denominatorLowerNumerator
        );
        var denominatorLower = QuadraticSurd.Rational(denominatorLowerNumerator, lowerScale);
        var residualMagnitudeCeiling = residualMagnitude.Ceiling();
        var radius = (QuadraticSurd.Rational(residualMagnitudeCeiling) / denominatorLower).Ceiling();
        var deltaLower = QuadraticSurd.Rational(deltaLowerNumerator, lowerScale);
        var cutoff = BigInteger.One;

        while (true) {
            var deltaAtCutoff = (leadingConjugateGap +
                (constantConjugateGap / QuadraticSurd.Rational(cutoff)));
            var discriminantLower = (deltaLower * deltaLower * QuadraticSurd.Rational(cutoff * cutoff));
            var denominatorLoss = (coefficientMagnitude +
                ((QuadraticSurd.Rational(2) * normValue.Abs()) / deltaLower));
            var denominatorMargin = ((deltaLower - denominatorLower) *
                QuadraticSurd.Rational(cutoff * cutoff));

            if ((deltaAtCutoff >= deltaLower) &&
                (discriminantLower >= (QuadraticSurd.Rational(4) * normValue.Abs())) &&
                (denominatorMargin >= denominatorLoss)) {
                var certificate = new PolynomialBeattyBoundaryAsymptoticCertificate(
                    Norm: norm,
                    CoefficientCount: termCount,
                    Cutoff: cutoff,
                    RadiusNumerator: radius,
                    DeltaLowerNumerator: deltaLowerNumerator,
                    DenominatorLowerNumerator: denominatorLowerNumerator,
                    LowerBoundDenominator: lowerScale,
                    ResidualMagnitudeCeiling: residualMagnitudeCeiling
                );

                if (!VerifyBoundaryAsymptoticIntervalCertificate(analysis, certificate)) {
                    throw new InvalidOperationException("the constructed boundary asymptotic certificate did not verify");
                }

                return certificate;
            }

            cutoff *= 2;
        }
    }

    /// <summary>Rechecks the exact inequalities in a boundary asymptotic certificate.</summary>
    public static bool VerifyBoundaryAsymptoticIntervalCertificate(
        PolynomialContinuedFractionAnalysis analysis,
        PolynomialBeattyBoundaryAsymptoticCertificate certificate) {
        ArgumentNullException.ThrowIfNull(analysis);
        if ((certificate.CoefficientCount <= 0) || (certificate.Cutoff < 1) ||
            (certificate.RadiusNumerator < 0) || (certificate.DeltaLowerNumerator <= 0) ||
            (certificate.DenominatorLowerNumerator <= 0) ||
            (certificate.DeltaLowerNumerator <= certificate.DenominatorLowerNumerator) ||
            (certificate.LowerBoundDenominator <= 0) ||
            (certificate.ResidualMagnitudeCeiling < 0)) {
            return false;
        }

        var normCertificate = analysis.BeattyShadowNormCertificate();
        if (BigInteger.Abs(certificate.Norm) > normCertificate.NormMagnitudeBound) { return false; }
        var coefficients = BoundaryAsymptoticCoefficients(
            analysis,
            certificate.Norm,
            certificate.CoefficientCount
        );
        var scaleZ = normCertificate.CommonDenominator;
        var normValue = QuadraticSurd.Rational(certificate.Norm, scaleZ * scaleZ);
        var leadingConjugateGap = QuadraticSurd.Create(
            BigInteger.Zero,
            2 * normCertificate.SlopeSurdNumerator,
            normCertificate.Radicand,
            scaleZ
        );
        var constantConjugateGap = QuadraticSurd.Create(
            BigInteger.Zero,
            2 * normCertificate.OffsetSurdNumerator,
            normCertificate.Radicand,
            scaleZ
        );
        var residualCoefficients = BoundaryResidualCoefficients(
            coefficients,
            leadingConjugateGap,
            constantConjugateGap,
            normValue
        );
        var residualMagnitude = residualCoefficients.Aggregate(
            QuadraticSurd.Zero,
            (sum, coefficient) => sum + coefficient.Abs()
        );
        if (QuadraticSurd.Rational(certificate.ResidualMagnitudeCeiling) < residualMagnitude) {
            return false;
        }

        var lowerScale = certificate.LowerBoundDenominator;
        var deltaLower = QuadraticSurd.Rational(certificate.DeltaLowerNumerator, lowerScale);
        var denominatorLower = QuadraticSurd.Rational(certificate.DenominatorLowerNumerator, lowerScale);
        if ((denominatorLower >= deltaLower) || (deltaLower >= leadingConjugateGap) ||
            (QuadraticSurd.Rational(certificate.RadiusNumerator) <
                (QuadraticSurd.Rational(certificate.ResidualMagnitudeCeiling) / denominatorLower))) {
            return false;
        }

        var coefficientMagnitude = coefficients.Aggregate(
            QuadraticSurd.Zero,
            (sum, coefficient) => sum + coefficient.Abs()
        );
        var cutoff = certificate.Cutoff;
        var deltaAtCutoff = (leadingConjugateGap +
            (constantConjugateGap / QuadraticSurd.Rational(cutoff)));
        var discriminantLower = (deltaLower * deltaLower * QuadraticSurd.Rational(cutoff * cutoff));
        var denominatorLoss = (coefficientMagnitude +
            ((QuadraticSurd.Rational(2) * normValue.Abs()) / deltaLower));
        var denominatorMargin = ((deltaLower - denominatorLower) *
            QuadraticSurd.Rational(cutoff * cutoff));

        return (deltaAtCutoff >= deltaLower) &&
            (discriminantLower >= (QuadraticSurd.Rational(4) * normValue.Abs())) &&
            (denominatorMargin >= denominatorLoss);
    }

    /// <summary>
    /// Decides the eventual crossing behavior for one norm. A first-order collision is resolved at second order by
    /// <c>c_2-a_2=-(p*lambda/(lambda^2+r))*c_1</c>; if <c>c_1=0</c>, norm zero is the exact affine tail.
    /// </summary>
    public static PolynomialBeattyShadowNormDecisionCertificate NormDecisionCertificate(
        PolynomialContinuedFractionAnalysis analysis,
        BigInteger norm) {
        ArgumentNullException.ThrowIfNull(analysis);
        var normCertificate = analysis.BeattyShadowNormCertificate();
        ValidateNorm(certificate: normCertificate, norm: norm);
        var tailCoefficients = analysis.AsymptoticCoefficients(termCount: 3);
        var boundaryCoefficients = BoundaryAsymptoticCoefficients(analysis, norm, termCount: 2);
        var comparisonOrder = 1;
        var comparison = (boundaryCoefficients[0] - tailCoefficients[1]);

        if (comparison == QuadraticSurd.Zero) {
            if (tailCoefficients[1] == QuadraticSurd.Zero) {
                if (!norm.IsZero || (analysis.AffineResidual != QuadraticSurd.Zero)) {
                    throw new InvalidOperationException("zero leading comparison did not reduce to the exact affine case");
                }

                return new PolynomialBeattyShadowNormDecisionCertificate(
                    Norm: norm,
                    ComparisonOrder: 0,
                    Cutoff: normCertificate.Cutoff,
                    BoundaryMinusTailSign: 0,
                    EventualFloorDiscrepancy: 0
                );
            }

            comparisonOrder = 2;
            comparison = (boundaryCoefficients[1] - tailCoefficients[2]);
            var forcedDifference = ((QuadraticSurd.Rational(analysis.Parameters.Linear) * analysis.Slope *
                tailCoefficients[1]) /
                ((analysis.Slope * analysis.Slope) +
                    QuadraticSurd.Rational(analysis.Parameters.NumeratorQuadratic)));
            if ((tailCoefficients[2] - boundaryCoefficients[1]) != -forcedDifference) {
                throw new InvalidOperationException("the second-order collision identity failed");
            }
            if (comparison == QuadraticSurd.Zero) {
                throw new InvalidOperationException("a nonzero first-order collision persisted to second order");
            }
        }

        var tailCertificate = analysis.AsymptoticIntervalCertificate(termCount: comparisonOrder + 1);
        var boundaryCertificate = BoundaryAsymptoticIntervalCertificate(
            analysis,
            norm,
            termCount: comparisonOrder
        );
        var errorRadius = (tailCertificate.RadiusNumerator + boundaryCertificate.RadiusNumerator);
        var dominanceCutoff = ((QuadraticSurd.Rational(errorRadius) / comparison.Abs()).Floor() + 1);
        var orientationCutoff = BigInteger.One;
        var slopeSurd = normCertificate.SlopeSurdNumerator;
        var offsetSurd = normCertificate.OffsetSurdNumerator;
        if ((slopeSurd * orientationCutoff + offsetSurd) <= 0) {
            orientationCutoff = (BigIntegerMath.FloorDivide(-offsetSurd, slopeSurd) + 1);
        }
        var cutoff = BigInteger.Max(
            normCertificate.Cutoff,
            BigInteger.Max(
                tailCertificate.Cutoff,
                BigInteger.Max(boundaryCertificate.Cutoff, BigInteger.Max(dominanceCutoff, orientationCutoff))
            )
        );
        var comparisonSign = comparison.Sign;
        var discrepancy = norm.Sign switch {
            > 0 when comparisonSign < 0 => 1,
            < 0 when comparisonSign > 0 => -1,
            _ => 0
        };

        return new PolynomialBeattyShadowNormDecisionCertificate(
            Norm: norm,
            ComparisonOrder: comparisonOrder,
            Cutoff: cutoff,
            BoundaryMinusTailSign: comparisonSign,
            EventualFloorDiscrepancy: discrepancy
        );
    }

    /// <summary>
    /// Constructs the finite set of bi-infinite Pell channels containing every integer boundary of a specified cleared
    /// norm for the analysis's affine center.
    /// </summary>
    /// <remarks>
    /// The result can contain duplicate channels because the transparent bounded Pell cover is deliberately not
    /// quotient-deduplicated. Every returned point satisfies both affine congruences, and every integer boundary with
    /// the requested norm occurs on at least one returned channel.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The affine slope is rational, the radicand is square, or <paramref name="norm"/> lies outside the certified
    /// discrepancy envelope.
    /// </exception>
    public static IReadOnlyList<PolynomialBeattyShadowPellChannel> CandidatePellChannels(
        PolynomialContinuedFractionAnalysis analysis,
        BigInteger norm) {
        ArgumentNullException.ThrowIfNull(analysis);

        var certificate = analysis.BeattyShadowNormCertificate();
        if (BigInteger.Abs(norm) > certificate.NormMagnitudeBound) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(norm),
                message: "the norm lies outside the certified discrepancy envelope"
            );
        }
        if (certificate.SlopeSurdNumerator.IsZero) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(analysis),
                message: "a rational affine slope has no quadratic Pell channels"
            );
        }

        var root = BigIntegerMath.SquareRoot(value: certificate.Radicand);
        if ((root * root) == certificate.Radicand) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(analysis),
                message: "the affine-center radicand must not be a perfect square"
            );
        }

        var unit = PellEquation.FundamentalUnit(radicand: certificate.Radicand);
        var representatives = PellEquation.OrbitRepresentatives(
            radicand: certificate.Radicand,
            norm: norm
        );
        var modulus = (BigInteger.Abs(certificate.SlopeSurdNumerator) * certificate.CommonDenominator);
        var channels = new List<PolynomialBeattyShadowPellChannel>();

        foreach (var representative in representatives) {
            var residues = PellEquation.ResidueCycle(
                unit: unit,
                x: representative.X,
                y: representative.Y,
                modulus: modulus
            );
            var periodUnit = unit.Power(exponent: residues.Count);
            var pointX = representative.X;
            var pointY = representative.Y;

            for (var exponent = 0; exponent < residues.Count; ++exponent) {
                if (SatisfiesAffineCongruences(
                    certificate: certificate,
                    x: pointX,
                    y: pointY)) {
                    channels.Add(new PolynomialBeattyShadowPellChannel(
                        norm: norm,
                        baseX: pointX,
                        baseY: pointY,
                        periodUnit: periodUnit,
                        certificate: certificate
                    ));
                }

                (pointX, pointY) = unit.Multiply(x: pointX, y: pointY);
            }
        }

        return channels;
    }

    private static QuadraticSurd[] BoundaryResidualCoefficients(
        IReadOnlyList<QuadraticSurd> coefficients,
        QuadraticSurd leadingConjugateGap,
        QuadraticSurd constantConjugateGap,
        QuadraticSurd normValue) {
        var termCount = coefficients.Count;
        var residual = new QuadraticSurd[(2 * termCount) + 1];
        Array.Fill(residual, QuadraticSurd.Zero);
        residual[0] = -normValue;

        for (var index = 0; index < termCount; ++index) {
            var inversePower = (index + 1);
            residual[inversePower - 1] += (leadingConjugateGap * coefficients[index]);
            residual[inversePower] += (constantConjugateGap * coefficients[index]);
        }
        for (var leftIndex = 0; leftIndex < termCount; ++leftIndex) {
            for (var rightIndex = 0; rightIndex < termCount; ++rightIndex) {
                residual[leftIndex + rightIndex + 2] +=
                    (coefficients[leftIndex] * coefficients[rightIndex]);
            }
        }

        for (var inversePower = 0; inversePower < termCount; ++inversePower) {
            if (residual[inversePower] != QuadraticSurd.Zero) {
                throw new InvalidOperationException("the boundary coefficients did not cancel the required residual orders");
            }
        }

        return residual[termCount..];
    }

    private static void ChoosePositiveBounds(
        QuadraticSurd value,
        out BigInteger scale,
        out BigInteger upperLowerNumerator,
        out BigInteger lowerLowerNumerator) {
        if (value.Sign <= 0) {
            throw new ArgumentOutOfRangeException(nameof(value), "the value requiring positive lower bounds must be positive");
        }

        var precisionBits = 8;
        while (true) {
            scale = (BigInteger.One << precisionBits);
            upperLowerNumerator = ((value * QuadraticSurd.Rational(scale)).Floor() - 1);
            lowerLowerNumerator = (upperLowerNumerator / 2);
            if (lowerLowerNumerator > 0) { return; }
            precisionBits = checked(precisionBits * 2);
        }
    }

    private static void ValidateNorm(
        PolynomialBeattyShadowNormCertificate certificate,
        BigInteger norm) {
        if (BigInteger.Abs(norm) > certificate.NormMagnitudeBound) {
            throw new ArgumentOutOfRangeException(nameof(norm), "the norm lies outside the certified discrepancy envelope");
        }
        if (certificate.SlopeSurdNumerator <= 0) {
            throw new ArgumentOutOfRangeException(nameof(certificate), "the affine slope must have a positive irrational part");
        }
    }

    private static bool SatisfiesAffineCongruences(
        PolynomialBeattyShadowNormCertificate certificate,
        BigInteger x,
        BigInteger y) {
        var tailIndex = BigInteger.DivRem(
            dividend: (y - certificate.OffsetSurdNumerator),
            divisor: certificate.SlopeSurdNumerator,
            remainder: out var tailIndexRemainder
        );
        if (!tailIndexRemainder.IsZero) { return false; }

        var centerRationalNumerator = (
            (certificate.SlopeRationalNumerator * tailIndex) +
            certificate.OffsetRationalNumerator
        );

        return ((x + centerRationalNumerator) % certificate.CommonDenominator).IsZero;
    }
}
