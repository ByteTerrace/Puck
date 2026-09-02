using System.Numerics;

namespace Puck.Maths;

/// <summary>Which analytic branch a <see cref="SecondOrderDynamics"/> instance takes, decided once from the damping
/// ratio against <see cref="FixedQ4816.One"/>.</summary>
public enum SecondOrderDynamicsBranch : byte {
    /// <summary>Damping ratio below one — a decaying oscillation.</summary>
    Underdamped = 0,
    /// <summary>Damping ratio exactly one — the fastest approach that never overshoots.</summary>
    CriticallyDamped = 1,
    /// <summary>Damping ratio above one — a slower approach that never overshoots.</summary>
    Overdamped = 2,
}

/// <summary>
/// The derived constants of a t3ssel8r-style second-order follower: <c>y'' + 2ζω·y' + ω²·y = ω²·x + rζω·x'</c>, with
/// <c>ω = 2π·f</c>. Authors declare the natural frequency <c>f</c> (Hz), damping ratio <c>ζ</c> and initial response
/// <c>r</c> only — <see cref="Create"/> derives every raw this type and <see cref="SecondOrderStep"/> read. Reachable
/// forms are the per-step matched Z-transform propagator (<see cref="Compile"/> → <see cref="SecondOrderStep.Step(SecondOrderState,FixedQ4816,FixedQ4816)"/>)
/// and the closed form from initial conditions (<see cref="Evaluate"/>), both zero-allocation on the value path; only
/// <see cref="Create"/> and <see cref="Compile"/> allocate (exact <see cref="BigInteger"/> derivation, run once at
/// authoring/compile time, never on a per-tick or per-frame path).
/// </summary>
public readonly record struct SecondOrderDynamics {
    /// <summary>The fraction bit count every derived raw on this type is carried at (<c>32</c> — sixteen guard bits
    /// past <see cref="FixedQ4816"/>'s own Q16, so a follower's rest state is exact rather than dithering at the last
    /// Q16 bit).</summary>
    public const int CoefficientFractionBitCount = 32;

    private const long Log2EQ16Raw = 94548L; // round(log2(e) · 2^16)

    /// <summary>The authored natural frequency, in Hz.</summary>
    public required FixedQ4816 Frequency { get; init; }
    /// <summary>The authored damping ratio (dimensionless).</summary>
    public required FixedQ4816 DampingRatio { get; init; }
    /// <summary>The authored initial response (dimensionless).</summary>
    public required FixedQ4816 InitialResponse { get; init; }
    /// <summary>The analytic branch <see cref="DampingRatio"/> selected.</summary>
    public required SecondOrderDynamicsBranch Branch { get; init; }
    /// <summary>ζω, the exponential decay rate, in reciprocal seconds, at <see cref="CoefficientFractionBitCount"/>.</summary>
    public required long DecayRateRaw { get; init; }
    /// <summary>The damped oscillation rate ω_d (underdamped) or the real half-difference σ (overdamped), in radians
    /// per second, at <see cref="CoefficientFractionBitCount"/>. Exactly zero at <see cref="SecondOrderDynamicsBranch.CriticallyDamped"/>.</summary>
    public required long OscillationRateRaw { get; init; }
    /// <summary>ω², the system's stiffness, in reciprocal seconds squared, at <see cref="CoefficientFractionBitCount"/>.</summary>
    public required long StiffnessRaw { get; init; }
    /// <summary>k3 = rζ/ω, the target-velocity gain that shapes the initial response, in seconds, at
    /// <see cref="CoefficientFractionBitCount"/> (signed with r).</summary>
    public required long TargetVelocityGainRaw { get; init; }
    /// <summary>rζω, the velocity impulse <see cref="Retarget"/> applies per unit of target jump, in reciprocal
    /// seconds, at <see cref="CoefficientFractionBitCount"/> (signed with r).</summary>
    public required long RetargetGainRaw { get; init; }
    /// <summary>ζω/ρ (ρ = <see cref="OscillationRateRaw"/>), at <see cref="CoefficientFractionBitCount"/> — precomputed
    /// so <see cref="Evaluate"/> never divides. Meaningful only away from <see cref="SecondOrderDynamicsBranch.CriticallyDamped"/>.</summary>
    public required long DampingOverOscillationRaw { get; init; }

    /// <summary>Derives a follower's constants from its authored triple.</summary>
    /// <param name="frequencyHz">The natural frequency in Hz; must be finite and strictly positive.</param>
    /// <param name="dampingRatio">The damping ratio; must be finite and non-negative.</param>
    /// <param name="initialResponse">The initial response; must be finite.</param>
    /// <returns>The derived constants.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frequencyHz"/> is not positive, or its derived
    /// stiffness leaves the Q32 coefficient carrier (<c>ω² &lt; 2³¹</c>); <paramref name="dampingRatio"/> is negative,
    /// or so close to critical for this frequency that its derived oscillation rate rounds to zero at the Q32 coefficient
    /// scale.</exception>
    public static SecondOrderDynamics Create(FixedQ4816 frequencyHz, FixedQ4816 dampingRatio, FixedQ4816 initialResponse) {
        if (frequencyHz.Value <= 0L) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(frequencyHz),
                message: "The natural frequency must be finite and strictly positive."
            );
        }
        if (dampingRatio.Value < 0L) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(dampingRatio),
                message: "The damping ratio must be finite and non-negative."
            );
        }

        var (omegaNumerator, omegaDenominator) = FixedQ4816.AngularFrequency(frequencyHz: frequencyHz);

        if (!FixedPointRounding.TryRoundRational(
            numerator: (omegaNumerator * omegaNumerator),
            denominator: (omegaDenominator * omegaDenominator),
            fractionBitCount: CoefficientFractionBitCount,
            result: out var stiffnessRaw
        )) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(frequencyHz),
                message: "The derived stiffness ω² does not fit the Q32 coefficient carrier."
            );
        }

        var zetaRaw = ((BigInteger)dampingRatio.Value);
        var oneQ16 = ((BigInteger)(1L << FixedQ4816.FractionBitCount));

        if (!FixedPointRounding.TryRoundRational(
            numerator: (zetaRaw * omegaNumerator),
            denominator: (oneQ16 * omegaDenominator),
            fractionBitCount: CoefficientFractionBitCount,
            result: out var decayRateRaw
        )) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(frequencyHz),
                message: "The derived decay rate ζω does not fit the Q32 coefficient carrier."
            );
        }

        var branch = ((dampingRatio.Value < oneQ16)
            ? SecondOrderDynamicsBranch.Underdamped
            : ((dampingRatio.Value == oneQ16)
                ? SecondOrderDynamicsBranch.CriticallyDamped
                : SecondOrderDynamicsBranch.Overdamped));

        long oscillationRateRaw;
        long dampingOverOscillationRaw;

        if (branch == SecondOrderDynamicsBranch.CriticallyDamped) {
            oscillationRateRaw = 0L;
            dampingOverOscillationRaw = 0L;
        } else {
            // discriminant = |1 − ζ²|·2^32 (exact, since ζ is Q16). rootRaw = round(√|1 − ζ²| · 2^Guard) — the
            // 2·Guard − 2·16 shift accounts for discriminant already carrying ζ's own 2^32, so the square root
            // divides that exponent by two before rootRaw's target scale is added back.
            var discriminant = BigInteger.Abs((oneQ16 * oneQ16) - (zetaRaw * zetaRaw));
            var discriminantScaled = (discriminant << ((2 * SecondOrderExactMath.GuardFractionBitCount) - (2 * FixedQ4816.FractionBitCount)));
            var rootRaw = ((BigIntegerFunctions.SquareRoot(value: (4 * discriminantScaled)) + 1) / 2);

            if (!FixedPointRounding.TryRoundRational(
                numerator: (omegaNumerator * rootRaw),
                denominator: (omegaDenominator * (BigInteger.One << SecondOrderExactMath.GuardFractionBitCount)),
                fractionBitCount: CoefficientFractionBitCount,
                result: out oscillationRateRaw
            )) {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(frequencyHz),
                    message: "The derived oscillation rate does not fit the Q32 coefficient carrier."
                );
            }

            // Evaluate reads this rate narrowed to Q16 (OscillationRateRawAsQ16, an exact >> 16 — no rounding, so
            // anything below one Q16 unit at Q32 truncates to zero and would divide by it); refuse here rather than
            // admit a row whose closed form can only fault at read time.
            if (oscillationRateRaw < (1L << FixedQ4816.FractionBitCount)) {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(dampingRatio),
                    message: "The damping ratio is too close to critical for this frequency to resolve a Q16-representable oscillation rate."
                );
            }

            if (!FixedPointRounding.TryRoundRational(
                numerator: decayRateRaw,
                denominator: oscillationRateRaw,
                fractionBitCount: CoefficientFractionBitCount,
                result: out dampingOverOscillationRaw
            )) {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(dampingRatio),
                    message: "The derived damping-over-oscillation ratio does not fit the Q32 coefficient carrier."
                );
            }
        }

        var responseRaw = ((BigInteger)initialResponse.Value);
        var responseZeta = (responseRaw * zetaRaw); // r·ζ, Q32 exact

        if (!FixedPointRounding.TryRoundRational(
            numerator: (responseZeta * omegaDenominator),
            denominator: ((oneQ16 * oneQ16) * omegaNumerator),
            fractionBitCount: CoefficientFractionBitCount,
            result: out var targetVelocityGainRaw
        )) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(initialResponse),
                message: "The derived target-velocity gain k3 does not fit the Q32 coefficient carrier."
            );
        }

        if (!FixedPointRounding.TryRoundRational(
            numerator: (responseZeta * omegaNumerator),
            denominator: ((oneQ16 * oneQ16) * omegaDenominator),
            fractionBitCount: CoefficientFractionBitCount,
            result: out var retargetGainRaw
        )) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(initialResponse),
                message: "The derived retarget gain rζω does not fit the Q32 coefficient carrier."
            );
        }

        return new() {
            Branch = branch,
            DampingOverOscillationRaw = dampingOverOscillationRaw,
            DampingRatio = dampingRatio,
            DecayRateRaw = decayRateRaw,
            Frequency = frequencyHz,
            InitialResponse = initialResponse,
            OscillationRateRaw = oscillationRateRaw,
            RetargetGainRaw = retargetGainRaw,
            StiffnessRaw = stiffnessRaw,
            TargetVelocityGainRaw = targetVelocityGainRaw,
        };
    }

    /// <summary>Compiles the exact pole-matched (matched Z-transform) state-transition matrix for one fixed step
    /// width.</summary>
    /// <param name="stepTicks">The step width, in simulation ticks; must be strictly positive.</param>
    /// <param name="ticksPerSecond">The tick rate the step width is measured against; must be strictly positive.</param>
    /// <returns>The compiled step.</returns>
    /// <exception cref="InvalidOperationException">This instance is default-initialized (unbound).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepTicks"/> or <paramref name="ticksPerSecond"/>
    /// is zero.</exception>
    public SecondOrderStep Compile(ulong stepTicks, ulong ticksPerSecond) {
        ThrowIfUnbound();

        if (stepTicks == 0UL) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(stepTicks),
                message: "The step width must be strictly positive."
            );
        }
        if (ticksPerSecond == 0UL) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(ticksPerSecond),
                message: "The tick rate must be strictly positive."
            );
        }

        var (a11, a12, a21, a22) = SecondOrderExactMath.CompilePropagator(
            branch: Branch,
            dampingOverOscillationRaw: DampingOverOscillationRaw,
            decayRateRaw: DecayRateRaw,
            oscillationRateRaw: OscillationRateRaw,
            stepTicks: stepTicks,
            stiffnessRaw: StiffnessRaw,
            ticksPerSecond: ticksPerSecond
        );

        return new(
            A11Raw: a11,
            A12Raw: a12,
            A21Raw: a21,
            A22Raw: a22,
            StepTicks: stepTicks,
            TargetVelocityGainRaw: TargetVelocityGainRaw,
            TicksPerSecond: ticksPerSecond
        );
    }

    /// <summary>Evaluates the closed-form response at an elapsed duration from stated initial conditions — the
    /// no-per-tick-work form <c>WorldStateAdvance</c>-style epoch reads use.</summary>
    /// <param name="initialValue">The value at the epoch.</param>
    /// <param name="initialVelocity">The velocity at the epoch.</param>
    /// <param name="target">The (piecewise-constant) target held over the interval.</param>
    /// <param name="elapsedTicks">The elapsed duration, in ticks, since the epoch.</param>
    /// <param name="ticksPerSecond">The tick rate <paramref name="elapsedTicks"/> is measured against; must be
    /// strictly positive.</param>
    /// <returns>The value and velocity at the elapsed duration.</returns>
    /// <exception cref="InvalidOperationException">This instance is default-initialized (unbound).</exception>
    /// <remarks>An approximation, not the exact propagator <see cref="Compile"/> derives: built from the public
    /// <see cref="FixedQ4816.Exp2"/>/<see cref="FixedQ4816.SinCos"/> kernels at their own documented error, so the
    /// result agrees with a stepped <see cref="SecondOrderStep"/> walk within a few raw Q16 units, not to the bit.
    /// Settles to exactly <c>(target, Zero)</c> once the decay exponent clears the kernels' own underflow floor.</remarks>
    public SecondOrderSample Evaluate(FixedQ4816 initialValue, FixedQ4816 initialVelocity, FixedQ4816 target, ulong elapsedTicks, ulong ticksPerSecond) {
        ThrowIfUnbound();

        if (ticksPerSecond == 0UL) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(ticksPerSecond),
                message: "The tick rate must be strictly positive."
            );
        }
        if (elapsedTicks == 0UL) {
            return new(Value: initialValue, Velocity: initialVelocity);
        }

        if (!FusedArithmetic.TryDivideMagnitudeRounded(
            denominatorMagnitude: ticksPerSecond,
            fractionBitCount: FixedQ4816.FractionBitCount,
            numeratorMagnitude: elapsedTicks,
            quotient: out var tMagnitude
        ) || (tMagnitude > ((UInt128)long.MaxValue))) {
            // An elapsed duration too large to carry as a FixedQ4816 has, for every physically meaningful decay
            // rate, already settled — report the settled state rather than throwing on a read path.
            return new(Value: target, Velocity: FixedQ4816.Zero);
        }

        var t = FixedQ4816.FromRawBits(value: unchecked((long)tMagnitude));
        var e0 = (initialValue - target);
        var v0 = initialVelocity;
        var log2e = FixedQ4816.FromRawBits(value: Log2EQ16Raw);

        FixedQ4816 valueOffset;
        FixedQ4816 velocity;

        switch (Branch) {
            case SecondOrderDynamicsBranch.CriticallyDamped: {
                if (!TryDecayFactor(decayNumeratorRaw: DecayRateRaw, t: t, log2e: log2e, factor: out var e, timeProduct: out var decayTime)) {
                    return new(Value: target, Velocity: FixedQ4816.Zero);
                }

                var onePlus = (FixedQ4816.One + decayTime);
                var oneMinus = (FixedQ4816.One - decayTime);

                valueOffset = (e * ((e0 * onePlus) + (v0 * t)));
                velocity = (e * ((v0 * oneMinus) - (NarrowQ32(raw: StiffnessRaw) * e0 * t)));
                break;
            }
            case SecondOrderDynamicsBranch.Underdamped: {
                if (!TryDecayFactor(decayNumeratorRaw: DecayRateRaw, t: t, log2e: log2e, factor: out var e, timeProduct: out _)) {
                    return new(Value: target, Velocity: FixedQ4816.Zero);
                }
                if (!FusedArithmetic.TryMixedScaleProduct(
                    a: OscillationRateRaw,
                    b: t.Value,
                    fractionBitsA: CoefficientFractionBitCount,
                    fractionBitsB: FixedQ4816.FractionBitCount,
                    fractionBitsOut: FixedQ4816.FractionBitCount,
                    result: out var angleRaw
                )) {
                    return new(Value: target, Velocity: FixedQ4816.Zero);
                }

                var (sin, cos) = FixedQ4816.SinCos(angle: FixedQ4816.FromRawBits(value: angleRaw));
                var ratio = NarrowQ32(raw: DampingOverOscillationRaw);
                // The two divisions by ω_d take the Q32 rate as their divisor directly — v0·sin/ω_d and k·e0·sin/ω_d each
                // one rounding — instead of dividing by a Q16 narrowing of a rate that can sit within a few units of it.
                var velocityTerm = FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(
                    denominator: ((UInt128)((ulong)OscillationRateRaw)),
                    numerator: FusedArithmetic.Product(
                        left: v0.Value,
                        right: sin.Value
                    )
                ));
                var stiffnessTerm = FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(
                    denominator: (((UInt128)((ulong)OscillationRateRaw)) << (2 * FixedQ4816.FractionBitCount)),
                    numerator: ScaleSignedProduct(
                        product: FusedArithmetic.Product(
                            left: e0.Value,
                            right: sin.Value
                        ),
                        scale: StiffnessRaw
                    )
                ));

                valueOffset = (e * ((e0 * (cos + (ratio * sin))) + velocityTerm));
                velocity = (e * ((v0 * (cos - (ratio * sin))) - stiffnessTerm));
                break;
            }
            default: { // Overdamped — settling tracks the SLOWER pole p1 = ζω−σ, never the bare ζω.
                var sigma = NarrowQ32(raw: OscillationRateRaw);

                if (!TryDecayFactor(decayNumeratorRaw: (DecayRateRaw - OscillationRateRaw), t: t, log2e: log2e, factor: out var lambda1, timeProduct: out _)) {
                    return new(Value: target, Velocity: FixedQ4816.Zero);
                }
                if (!FusedArithmetic.TryMixedScaleProduct(
                    a: (DecayRateRaw + OscillationRateRaw),
                    b: t.Value,
                    fractionBitsA: CoefficientFractionBitCount,
                    fractionBitsB: FixedQ4816.FractionBitCount,
                    fractionBitsOut: FixedQ4816.FractionBitCount,
                    result: out var p2TimeRaw
                )) {
                    return new(Value: target, Velocity: FixedQ4816.Zero);
                }

                // lambda1/lambda2 decay at the positive rates p1 = ζω−σ, p2 = ζω+σ (the poles are −p1, −p2); p1·p2 =
                // ω² exactly, which is how the velocity term below reaches ω² without a separate stiffness read.
                var lambda2 = FixedQ4816.Exp2(value: -(FixedQ4816.FromRawBits(value: p2TimeRaw) * log2e));
                // The poles are narrowed from their exact Q32 difference and sum, not as differences of two narrowings.
                var p1 = NarrowQ32(raw: (DecayRateRaw - OscillationRateRaw));
                var p2 = NarrowQ32(raw: (DecayRateRaw + OscillationRateRaw));
                // The closing divisions by 2σ take the Q32 rate directly, each one rounding.
                var twoSigmaQ32 = (((UInt128)((ulong)OscillationRateRaw)) << 1);

                valueOffset = FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(
                    denominator: twoSigmaQ32,
                    numerator: FusedArithmetic.Product(
                        left: ((e0 * ((p2 * lambda1) - (p1 * lambda2))) + (v0 * (lambda1 - lambda2))).Value,
                        right: FixedQ4816.One.Value
                    )
                ));
                velocity = FixedQ4816.FromRawBits(value: FusedArithmetic.DivideProductSum(
                    denominator: twoSigmaQ32,
                    numerator: FusedArithmetic.Product(
                        left: ((e0 * p1 * p2 * (lambda2 - lambda1)) + (v0 * ((p2 * lambda2) - (p1 * lambda1)))).Value,
                        right: FixedQ4816.One.Value
                    )
                ));
                break;
            }
        }

        return new(Value: (target + valueOffset), Velocity: velocity);
    }

    // Forms e = exp(-decayNumeratorRaw/2^32 · t) and reports the raw ζω·t product alongside it; false when the
    // exponent's own decay factor has already rounded to zero (the caller reports the settled state) or the
    // intermediate product overflowed.
    private static bool TryDecayFactor(long decayNumeratorRaw, FixedQ4816 t, FixedQ4816 log2e, out FixedQ4816 factor, out FixedQ4816 timeProduct) {
        if (!FusedArithmetic.TryMixedScaleProduct(
            a: decayNumeratorRaw,
            b: t.Value,
            fractionBitsA: CoefficientFractionBitCount,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var timeProductRaw
        )) {
            factor = FixedQ4816.Zero;
            timeProduct = FixedQ4816.Zero;
            return false;
        }

        timeProduct = FixedQ4816.FromRawBits(value: timeProductRaw);
        factor = FixedQ4816.Exp2(value: -(timeProduct * log2e));

        return (factor != FixedQ4816.Zero);
    }

    /// <summary>Applies the velocity impulse a piecewise-constant target retarget carries: the closed-form sibling of
    /// re-seeding <see cref="TargetVelocityGainRaw"/>'s continuous term, for a target that jumps rather than
    /// moves.</summary>
    /// <param name="current">The sample at the moment the target changes.</param>
    /// <param name="oldTarget">The target before the change.</param>
    /// <param name="newTarget">The target after the change.</param>
    /// <returns><paramref name="current"/> with its velocity adjusted by <see cref="RetargetGainRaw"/> times the
    /// target delta; the value is unchanged (a target jump does not move the follower instantaneously).</returns>
    /// <exception cref="InvalidOperationException">This instance is default-initialized (unbound).</exception>
    public SecondOrderSample Retarget(SecondOrderSample current, FixedQ4816 oldTarget, FixedQ4816 newTarget) {
        ThrowIfUnbound();

        var delta = (newTarget - oldTarget);

        if (!FusedArithmetic.TryMixedScaleProduct(
            a: RetargetGainRaw,
            b: delta.Value,
            fractionBitsA: CoefficientFractionBitCount,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var kickRaw
        )) {
            return current;
        }

        return new(Value: current.Value, Velocity: (current.Velocity + FixedQ4816.FromRawBits(value: kickRaw)));
    }

    // Q32 → Q16 to nearest, ties to even — the same narrowing SecondOrderState's accessors use, never a truncating
    // shift, whose downward bias reaches a whole Q16 unit on a rate that then serves as a divisor.
    private static FixedQ4816 NarrowQ32(long raw) =>
        FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(
            fractionBitCount: FixedQ4816.FractionBitCount,
            product: raw
        ));
    // Multiplies a sign-magnitude Q32 product by a non-negative Q32 raw, exactly, for a divisor lifted by the same width.
    private static (bool Negative, UInt128 Magnitude) ScaleSignedProduct((bool Negative, UInt128 Magnitude) product, long scale) =>
        (product.Negative, (product.Magnitude * ((UInt128)((ulong)scale))));

    private void ThrowIfUnbound() {
        if (Frequency.Value <= 0L) {
            throw new InvalidOperationException(message: "The dynamics are default-initialized; construct them with Create before evaluating them.");
        }
    }
}

/// <summary>The exact pole-matched propagator for one fixed step width, produced by
/// <see cref="SecondOrderDynamics.Compile"/>.</summary>
/// <param name="A11Raw">Row 1, column 1 of the state-transition matrix, Q32 (dimensionless).</param>
/// <param name="A12Raw">Row 1, column 2, Q32 (seconds).</param>
/// <param name="A21Raw">Row 2, column 1, Q32 (reciprocal seconds).</param>
/// <param name="A22Raw">Row 2, column 2, Q32 (dimensionless).</param>
/// <param name="TargetVelocityGainRaw">k3, carried unchanged from <see cref="SecondOrderDynamics.TargetVelocityGainRaw"/>.</param>
/// <param name="StepTicks">The step width this propagator was compiled for, in ticks.</param>
/// <param name="TicksPerSecond">The tick rate the step width was measured against.</param>
public readonly record struct SecondOrderStep(
    long A11Raw,
    long A12Raw,
    long A21Raw,
    long A22Raw,
    long TargetVelocityGainRaw,
    ulong StepTicks,
    ulong TicksPerSecond
) {
    /// <summary>Half a Q16 unit at the Q32 coefficient scale — the snap threshold below which a settled step reports
    /// exactly the target.</summary>
    public const long SettleHalfUnitRaw = (1L << 15);

    /// <summary>Advances one scalar follower lane by one step.</summary>
    /// <param name="state">The follower's state before the step.</param>
    /// <param name="target">The target held over the step.</param>
    /// <param name="targetVelocity">The target's velocity, held constant over the step (the ZOH assumption).</param>
    /// <returns>The follower's state after the step.</returns>
    /// <exception cref="OverflowException"><paramref name="target"/> leaves the sixteen guard bits available at Q32
    /// (magnitude ≥ 2⁴⁷ — <c>&lt;&lt;</c> is not covered by a <c>checked</c> context), or an intermediate product
    /// leaves the carrier; <paramref name="state"/> is left unread — the caller's own copy is untouched.</exception>
    public SecondOrderState Step(SecondOrderState state, FixedQ4816 target, FixedQ4816 targetVelocity) {
        if ((target.Value >= (1L << 47)) || (target.Value < -(1L << 47))) {
            throw new OverflowException(message: "The target leaves the Q32 coefficient carrier's sixteen guard bits.");
        }

        var xShift = (target.Value << 16);

        if (!FusedArithmetic.TryMixedScaleProduct(
            a: TargetVelocityGainRaw,
            b: targetVelocity.Value,
            fractionBitsA: SecondOrderDynamics.CoefficientFractionBitCount,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsOut: SecondOrderDynamics.CoefficientFractionBitCount,
            result: out var gainRaw
        )) {
            throw new OverflowException(message: "The target-velocity gain term overflowed while forming x*.");
        }

        var xStar = checked(xShift + gainRaw);
        var e = (state.PositionRaw - xStar);
        var v = state.VelocityRaw;

        var eSum = FusedArithmetic.AddProducts(
            firstLeft: A11Raw,
            firstRight: e,
            secondLeft: A12Raw,
            secondRight: v
        );
        var vSum = FusedArithmetic.AddProducts(
            firstLeft: A21Raw,
            firstRight: e,
            secondLeft: A22Raw,
            secondRight: v
        );

        var eScaled = FusedArithmetic.ScaleMagnitudeToNearest(
            magnitude: eSum.Magnitude,
            shift: -SecondOrderDynamics.CoefficientFractionBitCount
        );
        var vScaled = FusedArithmetic.ScaleMagnitudeToNearest(
            magnitude: vSum.Magnitude,
            shift: -SecondOrderDynamics.CoefficientFractionBitCount
        );

        if (
            !FusedArithmetic.TryNarrowSignedMagnitude(negative: eSum.Negative, magnitude: eScaled.Magnitude, result: out var eNext) ||
            !FusedArithmetic.TryNarrowSignedMagnitude(negative: vSum.Negative, magnitude: vScaled.Magnitude, result: out var vNext)
        ) {
            throw new OverflowException(message: "The propagator step overflowed the Q32 raw carrier.");
        }

        if (
            (eNext >= -SettleHalfUnitRaw) && (eNext <= SettleHalfUnitRaw) &&
            (vNext >= -SettleHalfUnitRaw) && (vNext <= SettleHalfUnitRaw)
        ) {
            return new(PositionRaw: xStar, VelocityRaw: 0L);
        }

        return new(PositionRaw: checked(xStar + eNext), VelocityRaw: vNext);
    }

    /// <summary>Advances a three-lane planar follower by one step — three independent scalar
    /// <see cref="Step(SecondOrderState,FixedQ4816,FixedQ4816)"/> calls, X then Y then Z.</summary>
    /// <param name="state">The follower's state before the step.</param>
    /// <param name="target">The target held over the step.</param>
    /// <param name="targetVelocity">The target's velocity, held constant over the step.</param>
    /// <returns>The follower's state after the step.</returns>
    public SecondOrderState3 Step(SecondOrderState3 state, FixedVector3 target, FixedVector3 targetVelocity) => new(
        X: Step(
            state: state.X,
            target: target.X,
            targetVelocity: targetVelocity.X
        ),
        Y: Step(
            state: state.Y,
            target: target.Y,
            targetVelocity: targetVelocity.Y
        ),
        Z: Step(
            state: state.Z,
            target: target.Z,
            targetVelocity: targetVelocity.Z
        )
    );
}

/// <summary>One scalar follower lane's authoritative Q32 state.</summary>
/// <param name="PositionRaw">The position, Q32.</param>
/// <param name="VelocityRaw">The velocity, Q32.</param>
public readonly record struct SecondOrderState(long PositionRaw, long VelocityRaw) {
    /// <summary>The position, narrowed to Q16 (nearest, ties to even).</summary>
    public FixedQ4816 Position => FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(
        product: PositionRaw,
        fractionBitCount: 16
    ));
    /// <summary>The velocity, narrowed to Q16 (nearest, ties to even).</summary>
    public FixedQ4816 Velocity => FixedQ4816.FromRawBits(value: FixedQ4816.RoundProduct(
        product: VelocityRaw,
        fractionBitCount: 16
    ));

    /// <summary>Constructs a state exactly from Q16 position and velocity (an exact left shift; no rounding).</summary>
    /// <param name="position">The position.</param>
    /// <param name="velocity">The velocity.</param>
    /// <returns>The Q32 state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A raw would leave the sixteen guard bits available at Q32 (magnitude ≥ 2⁴⁷).</exception>
    public static SecondOrderState FromValue(FixedQ4816 position, FixedQ4816 velocity) {
        if ((position.Value >= (1L << 47)) || (position.Value < -(1L << 47))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(position));
        }
        if ((velocity.Value >= (1L << 47)) || (velocity.Value < -(1L << 47))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(velocity));
        }

        return new(PositionRaw: (position.Value << 16), VelocityRaw: (velocity.Value << 16));
    }
    /// <summary>Constructs a state at rest at a position, with zero velocity.</summary>
    /// <param name="position">The rest position.</param>
    /// <returns>The Q32 state.</returns>
    public static SecondOrderState AtRest(FixedQ4816 position) =>
        FromValue(position: position, velocity: FixedQ4816.Zero);
    /// <summary>Restores a state from its raw Q32 bits — the snapshot/checkpoint round trip.</summary>
    /// <param name="positionRaw">The position raw.</param>
    /// <param name="velocityRaw">The velocity raw.</param>
    /// <returns>The Q32 state.</returns>
    public static SecondOrderState FromRawBits(long positionRaw, long velocityRaw) =>
        new(PositionRaw: positionRaw, VelocityRaw: velocityRaw);
}

/// <summary>Three independent <see cref="SecondOrderState"/> lanes — a planar (X, Y, Z) follower's authoritative
/// state.</summary>
/// <param name="X">The first lane.</param>
/// <param name="Y">The second lane.</param>
/// <param name="Z">The third lane.</param>
public readonly record struct SecondOrderState3(SecondOrderState X, SecondOrderState Y, SecondOrderState Z) {
    /// <summary>The position vector.</summary>
    public FixedVector3 Position => new(X: X.Position, Y: Y.Position, Z: Z.Position);
    /// <summary>The velocity vector.</summary>
    public FixedVector3 Velocity => new(X: X.Velocity, Y: Y.Velocity, Z: Z.Velocity);

    /// <summary>Constructs a state exactly from Q16 position and velocity vectors.</summary>
    /// <param name="position">The position.</param>
    /// <param name="velocity">The velocity.</param>
    /// <returns>The Q32 state.</returns>
    public static SecondOrderState3 FromValue(FixedVector3 position, FixedVector3 velocity) => new(
        X: SecondOrderState.FromValue(position: position.X, velocity: velocity.X),
        Y: SecondOrderState.FromValue(position: position.Y, velocity: velocity.Y),
        Z: SecondOrderState.FromValue(position: position.Z, velocity: velocity.Z)
    );
    /// <summary>Constructs a state at rest at a position, with zero velocity.</summary>
    /// <param name="position">The rest position.</param>
    /// <returns>The Q32 state.</returns>
    public static SecondOrderState3 AtRest(FixedVector3 position) =>
        FromValue(position: position, velocity: FixedVector3.Zero);
}

/// <summary>One evaluated sample of a <see cref="SecondOrderDynamics"/> follower.</summary>
/// <param name="Value">The value at the sampled instant.</param>
/// <param name="Velocity">The velocity at the sampled instant.</param>
public readonly record struct SecondOrderSample(FixedQ4816 Value, FixedQ4816 Velocity);
