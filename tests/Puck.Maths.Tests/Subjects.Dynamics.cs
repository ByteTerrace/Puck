using System.Globalization;

namespace Puck.Maths.Tests;

/// <summary>Subject closures binding <see cref="SecondOrderDynamics"/> and its stepped/closed forms to the law
/// suite.</summary>
internal static partial class Subjects {
    // The magnitude of a raw long as an unsigned value, safe at long.MinValue (unlike Math.Abs).
    private static ulong DynamicsMagnitude(long value) {
        var sign = (value >> 63);

        return unchecked((ulong)((value ^ sign) - sign));
    }
    // Folds an arbitrary raw long onto the admitted authoring band a world declares: f in (0, 100] Hz.
    private static long DynamicsFrequencyRaw(long raw) =>
        (1L + ((long)(DynamicsMagnitude(value: raw) % (100UL << 16))));
    // zeta in [0, 16].
    private static long DynamicsDampingRaw(long raw) =>
        ((long)(DynamicsMagnitude(value: raw) % ((16UL << 16) + 1UL)));
    // r in [-4, 4].
    private static long DynamicsResponseRaw(long raw) =>
        (((long)(DynamicsMagnitude(value: raw) % ((8UL << 16) + 1UL))) - (4L << 16));

    /// <summary>Every raw <see cref="SecondOrderDynamics.Create"/> derives, folded to the admitted authoring band,
    /// against <see cref="Oracles.DynamicsConstants"/>.</summary>
    public static string? DynamicsCreateConstantsVsOracle(long[] left, long[] right) {
        var frequencyRaw = DynamicsFrequencyRaw(raw: left[0]);
        var dampingRaw = DynamicsDampingRaw(raw: left[1]);
        var responseRaw = DynamicsResponseRaw(raw: left[2]);
        var oracle = Oracles.DynamicsConstants(
            dampingRaw: dampingRaw,
            frequencyRaw: frequencyRaw,
            responseRaw: responseRaw
        );
        SecondOrderDynamics dynamics;

        try {
            dynamics = SecondOrderDynamics.Create(
                frequencyHz: FixedQ4816.FromRawBits(value: frequencyRaw),
                dampingRatio: FixedQ4816.FromRawBits(value: dampingRaw),
                initialResponse: FixedQ4816.FromRawBits(value: responseRaw)
            );
        } catch (ArgumentOutOfRangeException) when ((dampingRaw != (1L << 16))) {
            // Off-critical, Create refuses an oscillation rate that would truncate to zero at the Q16 scale
            // Evaluate reads it at — legitimate only when the INDEPENDENT oracle rate agrees it is that small; any
            // other refusal here is a genuine mismatch, so it is re-thrown rather than swallowed.
            return ((Math.Abs(value: oracle.OscillationRate) < (1L << 16))
                ? null
                : throw new InvalidOperationException(message: $"Create refused (f={frequencyRaw} zeta={dampingRaw}) but the oracle's own oscillation rate {oracle.OscillationRate} does not corroborate a Q16-representable-rate refusal."));
        }

        if (dynamics.StiffnessRaw != oracle.Stiffness) {
            return $"stiffness mismatch: subject={dynamics.StiffnessRaw} oracle={oracle.Stiffness} (f={frequencyRaw} zeta={dampingRaw})";
        }
        if (dynamics.DecayRateRaw != oracle.DecayRate) {
            return $"decay rate mismatch: subject={dynamics.DecayRateRaw} oracle={oracle.DecayRate} (f={frequencyRaw} zeta={dampingRaw})";
        }
        if (Math.Abs(value: (dynamics.OscillationRateRaw - oracle.OscillationRate)) > 1L) {
            return $"oscillation rate mismatch: subject={dynamics.OscillationRateRaw} oracle={oracle.OscillationRate} (f={frequencyRaw} zeta={dampingRaw})";
        }
        if ((dynamics.Branch != SecondOrderDynamicsBranch.CriticallyDamped) && (Math.Abs(value: (dynamics.DampingOverOscillationRaw - oracle.DampingOverOscillation)) > 2L)) {
            return $"damping-over-oscillation mismatch: subject={dynamics.DampingOverOscillationRaw} oracle={oracle.DampingOverOscillation}";
        }
        if (dynamics.TargetVelocityGainRaw != oracle.TargetVelocityGain) {
            return $"target-velocity gain mismatch: subject={dynamics.TargetVelocityGainRaw} oracle={oracle.TargetVelocityGain}";
        }
        if (dynamics.RetargetGainRaw != oracle.RetargetGain) {
            return $"retarget gain mismatch: subject={dynamics.RetargetGainRaw} oracle={oracle.RetargetGain}";
        }

        return (dynamics.Branch switch {
            SecondOrderDynamicsBranch.Underdamped => ((dampingRaw < (1L << 16)) ? null : "branch should be Underdamped"),
            SecondOrderDynamicsBranch.CriticallyDamped => ((dampingRaw == (1L << 16)) ? null : "branch should be CriticallyDamped"),
            _ => ((dampingRaw > (1L << 16)) ? null : "branch should be Overdamped"),
        });
    }
    /// <summary>Walks <see cref="SecondOrderStep.Step(SecondOrderState,FixedQ4816,FixedQ4816)"/> from rest toward a
    /// folded target and compares the walked state against <see cref="SecondOrderDynamics.Evaluate"/> at the same
    /// elapsed duration — two independent code paths over the same physical system (exact BigInteger
    /// scaling-and-squaring for the compiled propagator versus the public <see cref="FixedQ4816.Exp2"/>/
    /// <see cref="FixedQ4816.SinCos"/> kernels for the closed form), agreeing within Evaluate's own documented
    /// approximation envelope.</summary>
    public static string? DynamicsStepVsEvaluate(long[] left, long[] right) {
        const ulong ticksPerSecond = 240UL;
        const int steps = 240;

        // Bounded to [0.5, 8] Hz rather than DynamicsFrequencyRaw's full (0, 100] Hz band: the upper bound avoids
        // accumulating enough oscillation cycles in one second that Step's discrete recurrence and Evaluate's single
        // large-angle SinCos reduction diverge in HOW they round the same phase (a property of comparing two schemes
        // over many periods, not a defect either side owns); the lower bound avoids the opposite extreme, where a
        // near-zero decay rate makes both sides' Q16/Q32 rounding noise dominate a genuinely tiny signal.
        var frequencyRaw = ((1L << 15) + ((long)(DynamicsMagnitude(value: left[0]) % 491521UL)));
        var dampingRaw = DynamicsDampingRaw(raw: left[1]);

        // Nudged off the near-critical band: within it, the overdamped closed form's (p2*lambda1 - p1*lambda2)/(2sigma)
        // divides two nearly-equal decay rates by a near-zero sigma, and Step's independently rounded exact propagator
        // is free to disagree with Evaluate's kernel-built one well past this law's generic envelope — a genuinely
        // different numerical regime this generic bound is not shaped to cover.
        if (Math.Abs(value: (dampingRaw - (1L << 16))) < (65536L / 6L)) {
            dampingRaw += (65536L / 3L);
        }

        var responseRaw = 0L; // r is inert in Evaluate; isolate the propagator/closed-form agreement from it.
        var targetRaw = (((long)(DynamicsMagnitude(value: left[2]) % (20UL << 16))) - (10L << 16));

        var dynamics = SecondOrderDynamics.Create(
            frequencyHz: FixedQ4816.FromRawBits(value: frequencyRaw),
            dampingRatio: FixedQ4816.FromRawBits(value: dampingRaw),
            initialResponse: FixedQ4816.FromRawBits(value: responseRaw)
        );
        var step = dynamics.Compile(stepTicks: 1UL, ticksPerSecond: ticksPerSecond);
        var target = FixedQ4816.FromRawBits(value: targetRaw);
        var state = SecondOrderState.AtRest(position: FixedQ4816.Zero);

        for (var i = 0; (i < steps); ++i) {
            state = step.Step(state: state, target: target, targetVelocity: FixedQ4816.Zero);
        }

        var sample = dynamics.Evaluate(
            initialValue: FixedQ4816.Zero,
            initialVelocity: FixedQ4816.Zero,
            target: target,
            elapsedTicks: ((ulong)steps),
            ticksPerSecond: ticksPerSecond
        );

        var deltaValue = Math.Abs(value: (state.Position.Value - sample.Value.Value));
        var deltaVelocity = Math.Abs(value: (state.Velocity.Value - sample.Velocity.Value));

        // Aₘ ≈ |target| here (e0 = target, v0 = 0); the value envelope is Aₘ·2⁻¹⁵ plus float-kernel slack. Velocity
        // carries an extra factor of the natural frequency (Φ21 scales by ω²), so its bound widens with it.
        var valueBound = (8L + (2L * ((Math.Abs(value: targetRaw) >> 15) + 1L)));
        var velocityBound = (valueBound * (2L + (frequencyRaw >> 13)));

        return (((deltaValue <= valueBound) && (deltaVelocity <= velocityBound))
            ? null
            : $"step vs evaluate diverged: dValue={deltaValue} (bound {valueBound}) dVelocity={deltaVelocity} (bound {velocityBound}) (f={frequencyRaw} zeta={dampingRaw} target={targetRaw})");
    }
    /// <summary>ζ ≥ 1 from rest never overshoots a step target; a light-damping control (ζ = ¼) does.</summary>
    public static string? DynamicsCriticalAndOverdampedNeverOvershoot() {
        foreach (var zeta in new[] { 1.0, 1.5, 3.0, 8.0 }) {
            var dynamics = SecondOrderDynamics.Create(
                frequencyHz: FixedQ4816.FromDouble(value: 2.0),
                dampingRatio: FixedQ4816.FromDouble(value: zeta),
                initialResponse: FixedQ4816.Zero
            );
            var step = dynamics.Compile(stepTicks: 1UL, ticksPerSecond: 240UL);
            var state = SecondOrderState.AtRest(position: FixedQ4816.Zero);
            var target = FixedQ4816.FromDouble(value: 10.0);

            for (var i = 0; (i < (240 * 5)); ++i) {
                state = step.Step(state: state, target: target, targetVelocity: FixedQ4816.Zero);

                if (state.PositionRaw > (target.Value << 16)) {
                    return $"zeta={zeta} overshot at tick {i}: position raw {state.PositionRaw} exceeds target raw {(target.Value << 16)}";
                }
            }
        }

        // Control: light damping DOES overshoot, so the check above is discriminating rather than vacuous.
        {
            var dynamics = SecondOrderDynamics.Create(
                frequencyHz: FixedQ4816.FromDouble(value: 2.0),
                dampingRatio: FixedQ4816.FromDouble(value: 0.25),
                initialResponse: FixedQ4816.Zero
            );
            var step = dynamics.Compile(stepTicks: 1UL, ticksPerSecond: 240UL);
            var state = SecondOrderState.AtRest(position: FixedQ4816.Zero);
            var target = FixedQ4816.FromDouble(value: 10.0);
            var overshot = false;

            for (var i = 0; (i < (240 * 5)); ++i) {
                state = step.Step(state: state, target: target, targetVelocity: FixedQ4816.Zero);

                if (state.PositionRaw > (target.Value << 16)) {
                    overshot = true;

                    break;
                }
            }

            if (!overshot) {
                return "control (zeta=0.25) did not overshoot — the no-overshoot check above is not discriminating";
            }
        }

        return null;
    }
    /// <summary>A stepped follower reaches EXACTLY the target with zero velocity — <see cref="SecondOrderState.AtRest"/>
    /// — and stays there; the settle threshold does not merely approach it.</summary>
    public static string? DynamicsSteadyStateExact() {
        foreach (var zeta in new[] { 0.4, 1.0, 2.0 }) {
            var dynamics = SecondOrderDynamics.Create(
                frequencyHz: FixedQ4816.FromDouble(value: 3.0),
                dampingRatio: FixedQ4816.FromDouble(value: zeta),
                initialResponse: FixedQ4816.Zero
            );
            var step = dynamics.Compile(stepTicks: 1UL, ticksPerSecond: 240UL);
            var state = SecondOrderState.AtRest(position: FixedQ4816.Zero);
            var target = FixedQ4816.FromDouble(value: 7.5);

            for (var i = 0; (i < (240 * 10)); ++i) {
                state = step.Step(state: state, target: target, targetVelocity: FixedQ4816.Zero);
            }

            var atRest = SecondOrderState.AtRest(position: target);

            if ((state.PositionRaw != atRest.PositionRaw) || (state.VelocityRaw != 0L)) {
                return $"zeta={zeta} did not settle exactly: position={state.PositionRaw} velocity={state.VelocityRaw}";
            }

            var next = step.Step(state: state, target: target, targetVelocity: FixedQ4816.Zero);

            if ((next.PositionRaw != state.PositionRaw) || (next.VelocityRaw != 0L)) {
                return $"zeta={zeta} settled state is not a fixed point of Step";
            }
        }

        return null;
    }
    /// <summary>From rest, a step target advancing by one step's worth per tick: y1 orders strictly with r's sign,
    /// Evaluate is bit-identical for r = 0 regardless of target velocity, and <see cref="SecondOrderDynamics.Retarget"/>
    /// applies exactly the derived kick.</summary>
    public static string? DynamicsInitialResponseSign() {
        static FixedQ4816 OneStep(double r) {
            var dynamics = SecondOrderDynamics.Create(
                frequencyHz: FixedQ4816.FromDouble(value: 2.0),
                dampingRatio: FixedQ4816.FromDouble(value: 1.0),
                initialResponse: FixedQ4816.FromDouble(value: r)
            );
            var step = dynamics.Compile(stepTicks: 1UL, ticksPerSecond: 240UL);
            var state = SecondOrderState.AtRest(position: FixedQ4816.Zero);

            return step.Step(
                state: state,
                target: FixedQ4816.FromDouble(value: (1.0 / 240.0)),
                targetVelocity: FixedQ4816.FromDouble(value: 1.0)
            ).Position;
        }

        var positive = OneStep(r: 1.0);
        var zero = OneStep(r: 0.0);
        var negative = OneStep(r: -1.0);

        if (!((positive.Value > zero.Value) && (zero.Value > negative.Value))) {
            return $"initial-response ordering failed: r=+1 -> {positive.Value}, r=0 -> {zero.Value}, r=-1 -> {negative.Value}";
        }

        var noResponse = SecondOrderDynamics.Create(
            frequencyHz: FixedQ4816.FromDouble(value: 2.0),
            dampingRatio: FixedQ4816.FromDouble(value: 1.0),
            initialResponse: FixedQ4816.Zero
        );

        if (noResponse.TargetVelocityGainRaw != 0L) {
            return "r=0 must derive an exactly-zero target-velocity gain";
        }
        if (noResponse.RetargetGainRaw != 0L) {
            return "r=0 must derive an exactly-zero retarget gain";
        }

        var positiveResponse = SecondOrderDynamics.Create(
            frequencyHz: FixedQ4816.FromDouble(value: 2.0),
            dampingRatio: FixedQ4816.FromDouble(value: 1.0),
            initialResponse: FixedQ4816.FromDouble(value: 1.0)
        );
        var before = new SecondOrderSample(Value: FixedQ4816.FromDouble(value: 5.0), Velocity: FixedQ4816.Zero);
        var after = positiveResponse.Retarget(
            current: before,
            oldTarget: FixedQ4816.FromDouble(value: 5.0),
            newTarget: FixedQ4816.FromDouble(value: 6.0)
        );
        var kick = Oracles.WrapToRaw(value: Oracles.RoundRationalTiesToEven(
            numerator: (((System.Numerics.BigInteger)positiveResponse.RetargetGainRaw) * (1L << 16)),
            denominator: (System.Numerics.BigInteger.One << 32)
        ));
        var expectedVelocity = FixedQ4816.FromRawBits(value: (before.Velocity.Value + kick));

        if (after.Velocity.Value != expectedVelocity.Value) {
            return $"retarget kick mismatch: got={after.Velocity.Value} expected={expectedVelocity.Value}";
        }
        if (after.Value.Value != before.Value.Value) {
            return "retarget must not move the value, only the velocity";
        }

        return null;
    }
    /// <summary>The refusal ladder, by parameter name, and the atomic-on-overflow contract of
    /// <see cref="SecondOrderStep.Step(SecondOrderState,FixedQ4816,FixedQ4816)"/>.</summary>
    public static string? DynamicsRefusalsAndOverflow() {
        string? ExpectArgumentOutOfRange(Action action, string paramName, string label) {
            try {
                action();
            } catch (ArgumentOutOfRangeException ex) {
                return ((ex.ParamName == paramName)
                    ? null
                    : $"{label}: expected paramName '{paramName}', got '{ex.ParamName}'");
            } catch (Exception ex) {
                return $"{label}: expected ArgumentOutOfRangeException, got {ex.GetType().Name}";
            }

            return $"{label}: expected a refusal, none was thrown";
        }

        var frequencyFailure = ExpectArgumentOutOfRange(
            action: () => SecondOrderDynamics.Create(FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero),
            paramName: "frequencyHz",
            label: "f<=0"
        );

        if (frequencyFailure is not null) {
            return frequencyFailure;
        }

        var dampingFailure = ExpectArgumentOutOfRange(
            action: () => SecondOrderDynamics.Create(FixedQ4816.One, FixedQ4816.FromRawBits(value: -1L), FixedQ4816.Zero),
            paramName: "dampingRatio",
            label: "zeta<0"
        );

        if (dampingFailure is not null) {
            return dampingFailure;
        }

        var dynamics = SecondOrderDynamics.Create(FixedQ4816.One, FixedQ4816.One, FixedQ4816.Zero);

        var stepTicksFailure = ExpectArgumentOutOfRange(
            action: () => dynamics.Compile(stepTicks: 0UL, ticksPerSecond: 240UL),
            paramName: "stepTicks",
            label: "stepTicks=0"
        );

        if (stepTicksFailure is not null) {
            return stepTicksFailure;
        }

        var ticksPerSecondFailure = ExpectArgumentOutOfRange(
            action: () => dynamics.Compile(stepTicks: 1UL, ticksPerSecond: 0UL),
            paramName: "ticksPerSecond",
            label: "ticksPerSecond=0"
        );

        if (ticksPerSecondFailure is not null) {
            return ticksPerSecondFailure;
        }

        var unboundCompile = false;

        try {
            default(SecondOrderDynamics).Compile(stepTicks: 1UL, ticksPerSecond: 240UL);
        } catch (InvalidOperationException) {
            unboundCompile = true;
        }

        if (!unboundCompile) {
            return "an unbound (default) SecondOrderDynamics.Compile must throw InvalidOperationException";
        }

        var fromValueRefused = false;

        try {
            SecondOrderState.FromValue(position: FixedQ4816.FromRawBits(value: (1L << 47)), velocity: FixedQ4816.Zero);
        } catch (ArgumentOutOfRangeException ex) when ((ex.ParamName == "position")) {
            fromValueRefused = true;
        }

        if (!fromValueRefused) {
            return "SecondOrderState.FromValue must refuse a position at or past the sixteen guard bits";
        }

        var step = dynamics.Compile(stepTicks: 1UL, ticksPerSecond: 240UL);
        var overflowed = false;

        try {
            step.Step(
                state: SecondOrderState.FromRawBits(positionRaw: long.MaxValue, velocityRaw: long.MaxValue),
                target: FixedQ4816.MaxValue,
                targetVelocity: FixedQ4816.MaxValue
            );
        } catch (OverflowException) {
            overflowed = true;
        }

        return (overflowed
            ? null
            : "an out-of-range Step must throw OverflowException rather than silently wrap");
    }
    /// <summary>A three-lane <see cref="SecondOrderStep.Step(SecondOrderState3,FixedVector3,FixedVector3)"/> equals
    /// three independent scalar steps, bit for bit, lane by lane.</summary>
    public static string? DynamicsVectorLanesIndependent(long[] left, long[] right) {
        var dynamics = SecondOrderDynamics.Create(
            frequencyHz: FixedQ4816.FromDouble(value: 3.0),
            dampingRatio: FixedQ4816.FromDouble(value: 0.6),
            initialResponse: FixedQ4816.FromDouble(value: 0.2)
        );
        var step = dynamics.Compile(stepTicks: 1UL, ticksPerSecond: 240UL);

        FixedVector3 Fold(long[] source) => new(
            X: FixedQ4816.FromRawBits(value: (((long)(DynamicsMagnitude(value: source[0]) % (20UL << 16))) - (10L << 16))),
            Y: FixedQ4816.FromRawBits(value: (((long)(DynamicsMagnitude(value: source[1]) % (20UL << 16))) - (10L << 16))),
            Z: FixedQ4816.FromRawBits(value: (((long)(DynamicsMagnitude(value: source[2]) % (20UL << 16))) - (10L << 16)))
        );

        var target = Fold(source: left);
        var targetVelocity = Fold(source: right);
        var state3 = SecondOrderState3.AtRest(position: FixedVector3.Zero);
        var stateX = SecondOrderState.AtRest(position: FixedQ4816.Zero);
        var stateY = SecondOrderState.AtRest(position: FixedQ4816.Zero);
        var stateZ = SecondOrderState.AtRest(position: FixedQ4816.Zero);

        for (var i = 0; (i < 32); ++i) {
            state3 = step.Step(state: state3, target: target, targetVelocity: targetVelocity);
            stateX = step.Step(state: stateX, target: target.X, targetVelocity: targetVelocity.X);
            stateY = step.Step(state: stateY, target: target.Y, targetVelocity: targetVelocity.Y);
            stateZ = step.Step(state: stateZ, target: target.Z, targetVelocity: targetVelocity.Z);
        }

        return (((state3.X == stateX) && (state3.Y == stateY) && (state3.Z == stateZ))
            ? null
            : $"vector lanes diverged from scalar steps at (target={target}, targetVelocity={targetVelocity})".ToString(provider: CultureInfo.InvariantCulture));
    }
}
