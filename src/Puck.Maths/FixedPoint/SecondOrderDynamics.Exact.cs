using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// The exact <see cref="BigInteger"/> derivation behind <see cref="SecondOrderDynamics.Create"/> and
/// <see cref="SecondOrderDynamics.Compile"/> — never on a per-tick or per-frame path. Every transcendental is formed
/// by scaling-and-squaring (<see cref="ExpNegative"/>) or a doubled-angle Taylor series after exact 2π reduction
/// against <see cref="FixedQ4816.PiQ61"/> (<see cref="SinCosExact"/>), at <see cref="GuardFractionBitCount"/>
/// fraction bits — far past what the one Q32 rounding each caller performs at the end can see.
/// </summary>
internal static class SecondOrderExactMath {
    /// <summary>The fraction bit count internal transcendental evaluation is carried at, well past the sixteen guard
    /// bits <see cref="SecondOrderDynamics.CoefficientFractionBitCount"/> itself carries over <see cref="FixedQ4816"/>.</summary>
    internal const int GuardFractionBitCount = 128;

    // Past this exponent, exp(-x) sits far below any Q32 rounding threshold; the series is skipped entirely rather
    // than range-reduced for nothing. Internal (rather than private) so the test suite's guard-scale tie-discipline
    // search can replicate ExpNegative's own early exit and reduction exactly, from the live constant rather than a
    // transcribed copy that could silently drift.
    internal const int ExpUnderflowExponent = 48;
    private const int ExpSeriesTermBudget = 40;
    private const int AngleSeriesTermBudget = 160;
    internal const int ResidualShift = 10; // range-reduce exp's argument below 2^-10 before the Taylor series runs.

    private static readonly BigInteger GuardOne = (BigInteger.One << GuardFractionBitCount);

    /// <summary>Compiles the four Q32 propagator entries for one fixed step width from a branch's derived Q32
    /// constants, via the exact matched Z-transform matrix exponential.</summary>
    internal static (long A11Raw, long A12Raw, long A21Raw, long A22Raw) CompilePropagator(
        SecondOrderDynamicsBranch branch,
        long dampingOverOscillationRaw,
        long decayRateRaw,
        long oscillationRateRaw,
        long stiffnessRaw,
        ulong stepTicks,
        ulong ticksPerSecond
    ) {
        var stepDenominator = (((BigInteger)ticksPerSecond) << SecondOrderDynamics.CoefficientFractionBitCount);
        var decayTimeNumerator = (((BigInteger)decayRateRaw) * stepTicks); // ζω·T (= ω·T at critical)
        var stiffness = FromRaw(raw: stiffnessRaw, fractionBitCount: SecondOrderDynamics.CoefficientFractionBitCount);

        switch (branch) {
            case SecondOrderDynamicsBranch.CriticallyDamped: {
                var e = FromRaw(
                    raw: ExpNegative(numerator: decayTimeNumerator, denominator: stepDenominator),
                    fractionBitCount: GuardFractionBitCount
                );
                var omegaT = new Rational(Numerator: decayTimeNumerator, Denominator: stepDenominator);
                var omega = FromRaw(raw: decayRateRaw, fractionBitCount: SecondOrderDynamics.CoefficientFractionBitCount);

                var phi11 = (e * (Rational.One + omegaT));
                var phi12 = ((e * omegaT) / omega);
                var phi21 = -(stiffness * phi12);
                var phi22 = (e * (Rational.One - omegaT));

                return (RoundQ32(value: phi11), RoundQ32(value: phi12), RoundQ32(value: phi21), RoundQ32(value: phi22));
            }
            case SecondOrderDynamicsBranch.Underdamped: {
                var e = FromRaw(
                    raw: ExpNegative(numerator: decayTimeNumerator, denominator: stepDenominator),
                    fractionBitCount: GuardFractionBitCount
                );
                var angleNumerator = (((BigInteger)oscillationRateRaw) * stepTicks);
                var (sinRaw, cosRaw) = SinCosExact(numerator: angleNumerator, denominator: stepDenominator);
                var sin = FromRaw(raw: sinRaw, fractionBitCount: GuardFractionBitCount);
                var cos = FromRaw(raw: cosRaw, fractionBitCount: GuardFractionBitCount);
                var ratio = FromRaw(raw: dampingOverOscillationRaw, fractionBitCount: SecondOrderDynamics.CoefficientFractionBitCount);
                var omegaD = FromRaw(raw: oscillationRateRaw, fractionBitCount: SecondOrderDynamics.CoefficientFractionBitCount);

                var ratioSin = (ratio * sin);
                var phi11 = (e * (cos + ratioSin));
                var phi12 = ((e * sin) / omegaD);
                var phi21 = -(stiffness * phi12);
                var phi22 = (e * (cos - ratioSin));

                return (RoundQ32(value: phi11), RoundQ32(value: phi12), RoundQ32(value: phi21), RoundQ32(value: phi22));
            }
            default: { // Overdamped.
                var p1Numerator = (((BigInteger)(decayRateRaw - oscillationRateRaw)) * stepTicks);
                var p2Numerator = (((BigInteger)(decayRateRaw + oscillationRateRaw)) * stepTicks);
                var lambda1 = FromRaw(
                    raw: ExpNegative(numerator: p1Numerator, denominator: stepDenominator),
                    fractionBitCount: GuardFractionBitCount
                );
                var lambda2 = FromRaw(
                    raw: ExpNegative(numerator: p2Numerator, denominator: stepDenominator),
                    fractionBitCount: GuardFractionBitCount
                );
                // p1 = ζω − σ, p2 = ζω + σ (both positive); the poles proper are −p1 and −p2.
                var p1 = FromRaw(raw: (decayRateRaw - oscillationRateRaw), fractionBitCount: SecondOrderDynamics.CoefficientFractionBitCount);
                var p2 = FromRaw(raw: (decayRateRaw + oscillationRateRaw), fractionBitCount: SecondOrderDynamics.CoefficientFractionBitCount);
                var twoSigma = (FromRaw(raw: oscillationRateRaw, fractionBitCount: SecondOrderDynamics.CoefficientFractionBitCount) * Rational.Two);

                var phi11 = (((p2 * lambda1) - (p1 * lambda2)) / twoSigma);
                var phi12 = ((lambda1 - lambda2) / twoSigma);
                var phi21 = -(stiffness * phi12);
                var phi22 = (((p2 * lambda2) - (p1 * lambda1)) / twoSigma);

                return (RoundQ32(value: phi11), RoundQ32(value: phi12), RoundQ32(value: phi21), RoundQ32(value: phi22));
            }
        }
    }

    /// <summary>Returns <c>round(exp(−numerator/denominator) · 2^GuardFractionBitCount)</c> for a non-negative
    /// exponent, by scaling-and-squaring: halve the argument until its Taylor series converges in a handful of
    /// terms, then square back up.</summary>
    private static BigInteger ExpNegative(BigInteger numerator, BigInteger denominator) {
        if (numerator.IsZero) {
            return GuardOne;
        }
        if (numerator > (ExpUnderflowExponent * denominator)) {
            return BigInteger.Zero;
        }

        var reducedDenominator = denominator;
        var halvings = 0;

        while ((numerator << ResidualShift) >= reducedDenominator) {
            reducedDenominator <<= 1;
            ++halvings;
        }

        var residualRaw = RoundToGuardScale(numerator: numerator, denominator: reducedDenominator);
        var sum = GuardOne;
        var term = GuardOne;

        for (var n = 1; ((n <= ExpSeriesTermBudget) && !term.IsZero); ++n) {
            term = (-(term * residualRaw) / (GuardOne * n));
            sum += term;
        }

        for (var i = 0; (i < halvings); ++i) {
            sum = ((sum * sum) / GuardOne);
        }

        return ((sum.Sign < 0) ? BigInteger.Zero : sum);
    }

    /// <summary>Returns <c>(sin, cos)</c> of <c>numerator/denominator</c> radians (any non-negative magnitude), each
    /// as a raw at <see cref="GuardFractionBitCount"/>, reduced modulo <c>2π</c> using
    /// <see cref="FixedQ4816.PiQ61"/> as the value of π (consistent with every other exact-π chain in this
    /// library) and evaluated by the standard even/odd Taylor series over the reduced angle. Internal (rather than
    /// private) so <c>CurvatureSplineExactMath</c> (<c>CurvatureSpline.Exact.cs</c>) can derive an authored tangent
    /// yaw's exact unit direction from the same series rather than a transcribed copy that could silently drift; not
    /// named in any <c>VerifiedCode.json</c> entry (checked at widening time).</summary>
    internal static (BigInteger Sin, BigInteger Cos) SinCosExact(BigInteger numerator, BigInteger denominator) {
        var twoPiNumerator = (2 * ((BigInteger)FixedQ4816.PiQ61));
        var twoPiDenominator = (BigInteger.One << FixedQ4816.PiQ61FractionBitCount);

        var reducedNumerator = ((numerator * twoPiDenominator) - (((numerator * twoPiDenominator) / (denominator * twoPiNumerator)) * denominator * twoPiNumerator));
        var reducedDenominator = (denominator * twoPiDenominator);

        var thetaRaw = RoundToGuardScale(numerator: reducedNumerator, denominator: reducedDenominator);
        var thetaSquaredRaw = ((thetaRaw * thetaRaw) / GuardOne);

        var cosSum = GuardOne;
        var cosTerm = GuardOne;
        var sinSum = thetaRaw;
        var sinTerm = thetaRaw;

        for (var k = 1; (k <= AngleSeriesTermBudget); ++k) {
            cosTerm = (-(cosTerm * thetaSquaredRaw) / (GuardOne * ((2L * k) - 1) * (2L * k)));
            cosSum += cosTerm;
            sinTerm = (-(sinTerm * thetaSquaredRaw) / (GuardOne * (2L * k) * ((2L * k) + 1)));
            sinSum += sinTerm;

            if (cosTerm.IsZero && sinTerm.IsZero) {
                break;
            }
        }

        return (sinSum, cosSum);
    }

    // round(numerator / denominator · 2^GuardFractionBitCount) for a non-negative rational, ties to even. This once
    // rounded ties up instead (matching every other tie in the library only by coincidence of never landing on one);
    // dynamics.guard-scale-ties-vs-half-up proves the two disciplines are REQUIRED to differ at an even-truncated
    // guard-scale tie, and dynamics.guard-scale-public-divergence-search proves that no reduction point CompilePropagator
    // reaches, across authored (frequency, damping) pairs straddling the critical boundary crossed with step widths
    // from one tick to a million and tick rates from one to a million, ever lands on one — so the correction is
    // unobservable at every public Q32 propagator entry that search reaches, not merely plausible from the guard
    // margin.
    private static BigInteger RoundToGuardScale(BigInteger numerator, BigInteger denominator) =>
        FixedPointRounding.RoundRational(
            denominator: denominator,
            fractionBitCount: GuardFractionBitCount,
            numerator: numerator
        );

    private static long RoundQ32(Rational value) {
        if (!FixedPointRounding.TryRoundRational(
            numerator: value.Numerator,
            denominator: value.Denominator,
            fractionBitCount: SecondOrderDynamics.CoefficientFractionBitCount,
            result: out var raw
        )) {
            throw new OverflowException(message: "A compiled propagator entry overflowed the Q32 raw carrier.");
        }

        return raw;
    }

    private static Rational FromRaw(BigInteger raw, int fractionBitCount) =>
        new(Numerator: raw, Denominator: (BigInteger.One << fractionBitCount));
}

/// <summary>An exact rational over <see cref="BigInteger"/> — never narrowed until a caller's own closing rounding.
/// Every BigInteger-exact authoring/compile-time derivation in this library, and <c>Puck.Physics</c>'s soft-constraint
/// chain, forms its intermediates in this type. The denominator is always nonzero: the constructor refuses an
/// explicit zero, and <c>/</c> refuses a zero right-hand numerator rather than manufacturing one.</summary>
public readonly record struct Rational {
    // The denominator AS GIVEN to the constructor, or zero for a zero-initialized default(Rational) — which never ran
    // the constructor and so could not be validated. Denominator's own getter is what turns that unvalidated zero
    // into the canonical 1: the constructor can and does refuse an explicit zero, but nothing can stop a value type
    // from being zero-initialized, so the invariant lives in the READ rather than in every possible zero-fill.
    private readonly BigInteger _denominatorOrDefaultZero;

    /// <summary>Constructs an exact rational.</summary>
    /// <param name="Numerator">The exact numerator.</param>
    /// <param name="Denominator">The exact denominator, which must be nonzero.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="Denominator"/> is zero.</exception>
    public Rational(BigInteger Numerator, BigInteger Denominator) {
        if (Denominator.IsZero) {
            throw new ArgumentOutOfRangeException(paramName: nameof(Denominator), message: "A Rational's denominator must be nonzero.");
        }

        this.Numerator = Numerator;
        _denominatorOrDefaultZero = Denominator;
    }

    /// <summary>Gets the exact numerator.</summary>
    public BigInteger Numerator { get; }
    /// <summary>Gets the exact, nonzero denominator. <see langword="default"/>(<see cref="Rational"/>) reads back as
    /// <c>1</c> here rather than the zero its own zero-initialized storage holds, so the all-zero default is the
    /// canonical zero <c>0/1</c> rather than the invalid, unrepresentable-by-any-other-path <c>0/0</c>.</summary>
    public BigInteger Denominator => (_denominatorOrDefaultZero.IsZero ? BigInteger.One : _denominatorOrDefaultZero);

    /// <summary>Gets the rational <c>1</c>.</summary>
    public static Rational One { get; } = new(Numerator: BigInteger.One, Denominator: BigInteger.One);
    /// <summary>Gets the rational <c>2</c>.</summary>
    public static Rational Two { get; } = new(Numerator: (2 * BigInteger.One), Denominator: BigInteger.One);

    public static Rational operator +(Rational left, Rational right) => new(
        Numerator: ((left.Numerator * right.Denominator) + (right.Numerator * left.Denominator)),
        Denominator: (left.Denominator * right.Denominator)
    );
    public static Rational operator -(Rational left, Rational right) => new(
        Numerator: ((left.Numerator * right.Denominator) - (right.Numerator * left.Denominator)),
        Denominator: (left.Denominator * right.Denominator)
    );
    public static Rational operator -(Rational value) => new(
        Numerator: -value.Numerator,
        Denominator: value.Denominator
    );
    public static Rational operator *(Rational left, Rational right) => new(
        Numerator: (left.Numerator * right.Numerator),
        Denominator: (left.Denominator * right.Denominator)
    );
    /// <summary>Divides one rational by another.</summary>
    /// <param name="left">The dividend.</param>
    /// <param name="right">The divisor.</param>
    /// <returns>The exact quotient.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> is the rational zero (a zero numerator),
    /// which would otherwise manufacture a zero-denominator result.</exception>
    public static Rational operator /(Rational left, Rational right) {
        if (right.Numerator.IsZero) {
            throw new DivideByZeroException(message: "Cannot divide a Rational by the rational zero.");
        }

        return new(
            Numerator: (left.Numerator * right.Denominator),
            Denominator: (left.Denominator * right.Numerator)
        );
    }
}
