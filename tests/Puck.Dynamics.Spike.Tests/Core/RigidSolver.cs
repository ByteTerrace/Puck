using Puck.Maths;

namespace Puck.Dynamics.Spike.Tests.Core;

/// <summary>
/// The spike's sequential-impulse contact solver over temporal substeps. One dynamic body is solved against static
/// surfaces described only by contact candidates, so no absolute world position enters: every separation is re-derived
/// from the displacement the solver has itself accumulated within the step.
/// </summary>
/// <remarks>
/// <para><b>Order.</b> Candidates are canonicalized, associated into an ordered slot table, and then solved in slot
/// index order. Every loop in this type runs over an array or a list by ascending index; no hash container is read
/// anywhere.</para>
/// <para><b>Rounding.</b> Every mixed-scale product goes through the refusing face of the fused kernels, so a result
/// that leaves its carrier is DECLINED and counted (<see cref="RefusalCount"/>) rather than wrapped into an ordinary
/// answer.</para>
/// <para><b>Substep sequence.</b> Per substep: integrate velocities, warm start, biased solve, integrate positions,
/// unbiased relax. Restitution runs once at the end of the step. This is the reference sequence; the softness
/// coefficients are formed at the substep width, never at the step width.</para>
/// </remarks>
internal sealed class RigidSolver {
    private readonly SolverOptions m_options;
    private readonly SoftConstraint m_softness;
    private readonly FixedVector3 m_substepVelocityDelta;
    // The substep rate is exactly 1/h, so it serves both as the divisor every h-scaled integration uses and as the
    // multiplier the speculative bias applies; the reciprocal is never formed.
    private readonly FixedQ4816 m_substepRate;
    private readonly FixedQ4816 m_doubleSubstepRate;
    private readonly FixedQ4816 m_substepRecoveryDistance;
    private readonly long[] m_profile;

    /// <summary>Creates a solver bound to one set of options.</summary>
    /// <param name="options">The solver options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    internal RigidSolver(SolverOptions options) {
        ArgumentNullException.ThrowIfNull(argument: options);

        var substepRate = ((long)options.RateHz * options.SubstepCount);

        m_options = options;
        m_softness = SoftConstraint.Create(
            rateHz: options.RateHz,
            substepCount: options.SubstepCount,
            hertz: options.ContactHertz,
            dampingRatio: options.ContactDampingRatio,
            fractionBitCount: SoftConstraint.DefaultFractionBitCount
        );
        m_substepRate = FixedQ4816.FromInteger(value: substepRate);
        m_doubleSubstepRate = FixedQ4816.FromInteger(value: (2L * substepRate));
        m_substepVelocityDelta = ((options.Gravity + options.AppliedAcceleration) / m_substepRate);
        m_substepRecoveryDistance = (options.RecoverySpeed / m_substepRate);
        m_profile = new long[options.SolveIterations];
    }

    /// <summary>Gets the number of kernel refusals the solver has counted; a healthy fixture ends at zero.</summary>
    internal int RefusalCount { get; private set; }
    /// <summary>Gets the largest accumulated-impulse movement each biased solve iteration of the most recent substep
    /// left behind, in raw Q48.16 units. The solve always runs its whole budget, so this is an observation of the
    /// trajectory rather than a control over it.</summary>
    internal ReadOnlySpan<long> IterationProfile => m_profile;

    /// <summary>Gets the number of biased solve iterations the most recent substep needed to bring the accumulated
    /// impulses within a tolerance.</summary>
    /// <param name="toleranceRaw">The tolerance, in raw Q48.16 units.</param>
    /// <returns>The one-based iteration index, or the whole budget when the tolerance was never met.</returns>
    internal int IterationsToConverge(long toleranceRaw) {
        for (var index = 0; (index < m_profile.Length); ++index) {
            if (m_profile[index] <= toleranceRaw) {
                return (index + 1);
            }
        }

        return m_profile.Length;
    }

    /// <summary>Gets the number of biased solve iterations the most recent substep needed, read at the options'
    /// own tolerance.</summary>
    internal int LastStepIterationsToConverge => IterationsToConverge(toleranceRaw: m_options.ConvergenceToleranceRaw);
    /// <summary>Gets the smallest effective-mass raw seen while preparing the most recent step.</summary>
    internal long LastStepMinimumNormalMassRaw { get; private set; }
    /// <summary>Gets the largest accumulated normal impulse raw seen at the end of the most recent step.</summary>
    internal long LastStepMaximumImpulseRaw { get; private set; }
    /// <summary>Gets the soft-constraint coefficients this solver formed at its substep width.</summary>
    internal SoftConstraint Softness => m_softness;

    /// <summary>Advances one body by one step against one step's contact candidates.</summary>
    /// <param name="body">The dynamic body.</param>
    /// <param name="slots">The body's persistent manifold slots.</param>
    /// <param name="candidates">This step's candidates; canonicalized in place when the options ask for it.</param>
    /// <param name="step">The step ordinal.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    internal void Step(SpikeBody body, ManifoldSlotTable slots, List<ContactCandidate> candidates, int step) {
        ArgumentNullException.ThrowIfNull(argument: body);
        ArgumentNullException.ThrowIfNull(argument: slots);
        ArgumentNullException.ThrowIfNull(argument: candidates);

        if (m_options.CanonicalOrder) {
            ContactCandidate.Canonicalize(candidates: candidates);
        }

        slots.Associate(candidates: candidates, step: step, compositeIdentity: m_options.CompositeIdentity);
        body.ResetStepAccumulators();
        Prepare(body: body, slots: slots);

        for (var substep = 0; (substep < m_options.SubstepCount); ++substep) {
            IntegrateVelocity(body: body);
            WarmStart(body: body, slots: slots);
            RunIterations(body: body, slots: slots, iterations: m_options.SolveIterations, useBias: true, profile: m_profile);
            IntegratePosition(body: body, slots: slots);
            RunIterations(body: body, slots: slots, iterations: m_options.RelaxIterations, useBias: false, profile: null);
        }

        ApplyRestitution(body: body, slots: slots);
        RecordStepMaxima(slots: slots);
    }

    private void Prepare(SpikeBody body, ManifoldSlotTable slots) {
        var orientation = body.Orientation;

        LastStepMinimumNormalMassRaw = long.MaxValue;

        for (var index = 0; (index < ManifoldSlotTable.Capacity); ++index) {
            ref var slot = ref slots[index];

            if (slot.Disposition == SlotDisposition.Idle) {
                continue;
            }

            if (m_options.DeepRecovery && (slot.Separation < m_options.RecoveryThreshold)) {
                // A witness this far inside the solid cannot be trusted to name a side, so no constraint is formed and
                // no impulse survives: the bounded extraction below is the whole response.
                slot.Disposition = SlotDisposition.Recovery;
                slot.NormalImpulseRaw = 0L;
                slot.NormalMassRaw = 0L;

                continue;
            }

            slot.BaseSeparation = (slot.Separation - FixedVector3.Dot(left: slot.Anchor, right: slot.Normal));

            if (!m_options.WarmStart) {
                slot.NormalImpulseRaw = 0L;
            }

            slot.NormalMassRaw = EffectiveMass(body: body, orientation: orientation, anchor: slot.Anchor, normal: slot.Normal);
            slot.RelativeVelocity = RelativeNormalVelocity(body: body, anchor: slot.Anchor, normal: slot.Normal);

            if ((slot.NormalMassRaw > 0L) && (slot.NormalMassRaw < LastStepMinimumNormalMassRaw)) {
                LastStepMinimumNormalMassRaw = slot.NormalMassRaw;
            }
        }

        if (LastStepMinimumNormalMassRaw == long.MaxValue) {
            LastStepMinimumNormalMassRaw = 0L;
        }
    }

    private long EffectiveMass(SpikeBody body, FixedQuaternion orientation, FixedVector3 anchor, FixedVector3 normal) {
        var lever = FixedVector3.Cross(left: anchor, right: normal);
        var localLever = orientation.RotateInverse(vector: lever);

        if (!FixedSymmetricSolve.TryApplySymmetric3(
            a: body.InverseInertiaXX,
            b: body.InverseInertiaXY,
            c: body.InverseInertiaXZ,
            d: body.InverseInertiaYY,
            e: body.InverseInertiaYZ,
            f: body.InverseInertiaZZ,
            vX: localLever.X.Value,
            vY: localLever.Y.Value,
            vZ: localLever.Z.Value,
            fractionBitsMatrix: m_options.Scales.InverseInertia,
            fractionBitsVector: FixedQ4816.FractionBitCount,
            fractionBitsOut: m_options.Scales.InverseInertia,
            x: out var wx,
            y: out var wy,
            z: out var wz
        )) {
            ++RefusalCount;

            return 0L;
        }

        // A rotation preserves a dot product, so the angular term is taken in BODY axes and never needs the world
        // inverse inertia to be formed at all.
        if (!SpikeArithmetic.TryMixedDot(
            ax: localLever.X.Value,
            ay: localLever.Y.Value,
            az: localLever.Z.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            bx: wx,
            by: wy,
            bz: wz,
            fractionBitsB: m_options.Scales.InverseInertia,
            fractionBitsOut: m_options.Scales.InverseMass,
            result: out var angular
        )) {
            ++RefusalCount;

            return 0L;
        }

        var kNormal = (body.InverseMassRaw + angular);

        if (kNormal <= 0L) {
            return 0L;
        }

        if (!SpikeArithmetic.TryReciprocal(
            value: kNormal,
            fractionBitsIn: m_options.Scales.InverseMass,
            fractionBitsOut: m_options.Scales.EffectiveMass,
            result: out var normalMass
        )) {
            ++RefusalCount;

            return 0L;
        }

        return normalMass;
    }

    private static FixedQ4816 RelativeNormalVelocity(SpikeBody body, FixedVector3 anchor, FixedVector3 normal) =>
        FixedVector3.Dot(left: (body.LinearVelocity + FixedVector3.Cross(left: body.AngularVelocity, right: anchor)), right: normal);

    private void IntegrateVelocity(SpikeBody body) {
        body.LinearVelocity += m_substepVelocityDelta;
    }

    private void WarmStart(SpikeBody body, ManifoldSlotTable slots) {
        if (!m_options.WarmStart) {
            return;
        }

        for (var index = 0; (index < ManifoldSlotTable.Capacity); ++index) {
            ref var slot = ref slots[index];

            if ((slot.Disposition != SlotDisposition.Constraint) || (slot.NormalImpulseRaw == 0L)) {
                continue;
            }

            ApplyImpulse(body: body, anchor: slot.Anchor, normal: slot.Normal, impulseRaw: slot.NormalImpulseRaw);
        }
    }

    // Every iteration of the budget runs, unconditionally: an early break driven by a tolerance would make the
    // measurement change the trajectory it is measuring.
    private void RunIterations(SpikeBody body, ManifoldSlotTable slots, int iterations, bool useBias, long[]? profile) {
        for (var iteration = 0; (iteration < iterations); ++iteration) {
            var movement = SolveOnce(body: body, slots: slots, useBias: useBias);

            if (profile is not null) {
                profile[iteration] = movement;
            }
        }
    }

    private long SolveOnce(SpikeBody body, ManifoldSlotTable slots, bool useBias) {
        var movement = 0L;

        for (var index = 0; (index < ManifoldSlotTable.Capacity); ++index) {
            ref var slot = ref slots[index];

            if ((slot.Disposition != SlotDisposition.Constraint) || (slot.NormalMassRaw <= 0L)) {
                continue;
            }

            var rotated = body.DeltaRotation.Rotate(vector: slot.Anchor);
            var separation = (slot.BaseSeparation + FixedVector3.Dot(left: (body.DeltaPosition + rotated), right: slot.Normal));
            var velocityBias = FixedQ4816.Zero;
            // The two softness weights are read at their OWN finer scale rather than narrowed to Q48.16 first; a
            // mass scale as small as 0.0076 keeps only nine significant bits once narrowed.
            var massScaleRaw = FixedQ4816.One.Value;
            var massScaleBits = FixedQ4816.FractionBitCount;
            var impulseScaleRaw = 0L;
            var impulseScaleBits = FixedQ4816.FractionBitCount;

            if (separation > FixedQ4816.Zero) {
                // Speculative: the constraint is allowed to remove exactly the closing speed that would carry the body
                // past the surface within this substep, and no more, so a first-appearance contact neither tunnels nor
                // stops short.
                velocityBias = Scale(value: separation, factor: m_substepRate);
            } else if (useBias) {
                var soft = SoftScale(
                    first: m_softness.MassScaleRaw,
                    second: m_softness.BiasRateRaw,
                    value: separation
                );

                velocityBias = FixedQ4816.Max(x: soft, y: (-m_options.ContactSpeed));
                massScaleRaw = m_softness.MassScaleRaw;
                massScaleBits = m_softness.FractionBitCount;
                impulseScaleRaw = m_softness.ImpulseScaleRaw;
                impulseScaleBits = m_softness.FractionBitCount;
            }

            var normalVelocity = RelativeNormalVelocity(body: body, anchor: slot.Anchor, normal: slot.Normal);
            var driven = (Scale(value: normalVelocity, factor: FixedQ4816.FromRawBits(value: massScaleRaw), fractionBits: massScaleBits) + velocityBias);
            var delta = (-Scale(value: driven, factor: FixedQ4816.FromRawBits(value: slot.NormalMassRaw), fractionBits: m_options.Scales.EffectiveMass)
                - Scale(value: FixedQ4816.FromRawBits(value: slot.NormalImpulseRaw), factor: FixedQ4816.FromRawBits(value: impulseScaleRaw), fractionBits: impulseScaleBits));
            var accumulated = Math.Max(val1: (slot.NormalImpulseRaw + delta.Value), val2: 0L);
            var applied = (accumulated - slot.NormalImpulseRaw);

            slot.NormalImpulseRaw = accumulated;
            slot.TotalNormalImpulseRaw += applied;
            movement = Math.Max(val1: movement, val2: Math.Abs(value: applied));

            ApplyImpulse(body: body, anchor: slot.Anchor, normal: slot.Normal, impulseRaw: applied);
        }

        return movement;
    }

    private void IntegratePosition(SpikeBody body, ManifoldSlotTable slots) {
        body.DeltaPosition += (body.LinearVelocity / m_substepRate);
        body.DeltaRotation = (FixedQuaternion.Exp(bivector: (body.AngularVelocity / m_doubleSubstepRate)) * body.DeltaRotation).Normalize();
        Extract(body: body, slots: slots);
    }

    private void Extract(SpikeBody body, ManifoldSlotTable slots) {
        for (var index = 0; (index < ManifoldSlotTable.Capacity); ++index) {
            ref readonly var slot = ref slots[index];

            if (slot.Disposition != SlotDisposition.Recovery) {
                continue;
            }

            // Bounded extraction: one authored escape direction, at most RecoverySpeed·h of travel per substep, and no
            // impulse of any kind. The generator stops emitting a deep candidate once the body clears, so the loop
            // terminates on geometry rather than on a counter.
            body.DeltaPosition += (body.EscapeDirection * m_substepRecoveryDistance);

            var closing = FixedVector3.Dot(left: body.LinearVelocity, right: body.EscapeDirection);

            if (closing < FixedQ4816.Zero) {
                body.LinearVelocity -= (body.EscapeDirection * closing);
            }
        }
    }

    private void ApplyRestitution(SpikeBody body, ManifoldSlotTable slots) {
        if (m_options.Restitution == FixedQ4816.Zero) {
            return;
        }

        for (var iteration = 0; (iteration < m_options.RestitutionIterations); ++iteration) {
            for (var index = 0; (index < ManifoldSlotTable.Capacity); ++index) {
                ref var slot = ref slots[index];

                if ((slot.Disposition != SlotDisposition.Constraint) || (slot.NormalMassRaw <= 0L) ||
                    (slot.TotalNormalImpulseRaw == 0L) || (slot.RelativeVelocity > (-m_options.RestitutionThreshold))) {
                    continue;
                }

                var normalVelocity = RelativeNormalVelocity(body: body, anchor: slot.Anchor, normal: slot.Normal);
                var target = (normalVelocity + Multiply(left: m_options.Restitution, right: slot.RelativeVelocity));
                var delta = -Scale(value: target, factor: FixedQ4816.FromRawBits(value: slot.NormalMassRaw), fractionBits: m_options.Scales.EffectiveMass);
                var accumulated = Math.Max(val1: (slot.NormalImpulseRaw + delta.Value), val2: 0L);
                var applied = (accumulated - slot.NormalImpulseRaw);

                slot.NormalImpulseRaw = accumulated;
                slot.TotalNormalImpulseRaw += applied;

                ApplyImpulse(body: body, anchor: slot.Anchor, normal: slot.Normal, impulseRaw: applied);
            }
        }
    }

    private void RecordStepMaxima(ManifoldSlotTable slots) {
        LastStepMaximumImpulseRaw = 0L;

        for (var index = 0; (index < ManifoldSlotTable.Capacity); ++index) {
            ref readonly var slot = ref slots[index];

            if (slot.NormalImpulseRaw > LastStepMaximumImpulseRaw) {
                LastStepMaximumImpulseRaw = slot.NormalImpulseRaw;
            }
        }
    }

    private void ApplyImpulse(SpikeBody body, FixedVector3 anchor, FixedVector3 normal, long impulseRaw) {
        if (impulseRaw == 0L) {
            return;
        }

        var impulse = (normal * FixedQ4816.FromRawBits(value: impulseRaw));

        body.LinearVelocity += new FixedVector3(
            X: ScaleByInverseMass(body: body, value: impulse.X),
            Y: ScaleByInverseMass(body: body, value: impulse.Y),
            Z: ScaleByInverseMass(body: body, value: impulse.Z)
        );

        var orientation = body.CurrentOrientation;
        var torque = orientation.RotateInverse(vector: FixedVector3.Cross(left: anchor, right: impulse));

        if (!FixedSymmetricSolve.TryApplySymmetric3(
            a: body.InverseInertiaXX,
            b: body.InverseInertiaXY,
            c: body.InverseInertiaXZ,
            d: body.InverseInertiaYY,
            e: body.InverseInertiaYZ,
            f: body.InverseInertiaZZ,
            vX: torque.X.Value,
            vY: torque.Y.Value,
            vZ: torque.Z.Value,
            fractionBitsMatrix: m_options.Scales.InverseInertia,
            fractionBitsVector: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            x: out var wx,
            y: out var wy,
            z: out var wz
        )) {
            ++RefusalCount;

            return;
        }

        body.AngularVelocity += orientation.Rotate(vector: new FixedVector3(
            X: FixedQ4816.FromRawBits(value: wx),
            Y: FixedQ4816.FromRawBits(value: wy),
            Z: FixedQ4816.FromRawBits(value: wz)
        ));
    }

    private FixedQ4816 ScaleByInverseMass(SpikeBody body, FixedQ4816 value) {
        if (!FusedArithmetic.TryMixedScaleProduct(
            a: body.InverseMassRaw,
            fractionBitsA: m_options.Scales.InverseMass,
            b: value.Value,
            fractionBitsB: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var product
        )) {
            ++RefusalCount;

            return FixedQ4816.Zero;
        }

        return FixedQ4816.FromRawBits(value: product);
    }

    private FixedQ4816 Scale(FixedQ4816 value, FixedQ4816 factor) =>
        Scale(value: value, factor: factor, fractionBits: FixedQ4816.FractionBitCount);

    private FixedQ4816 Scale(FixedQ4816 value, FixedQ4816 factor, int fractionBits) {
        if (!FusedArithmetic.TryMixedScaleProduct(
            a: value.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            b: factor.Value,
            fractionBitsB: fractionBits,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var product
        )) {
            ++RefusalCount;

            return FixedQ4816.Zero;
        }

        return FixedQ4816.FromRawBits(value: product);
    }

    private FixedQ4816 Multiply(FixedQ4816 left, FixedQ4816 right) =>
        Scale(value: left, factor: right);

    // massScale · biasRate · separation, with the two softness operands read at their own finer scale and the whole
    // triple product rounded once.
    private FixedQ4816 SoftScale(long first, long second, FixedQ4816 value) {
        if (!FusedArithmetic.TryMixedScaleProduct(
            a: first,
            fractionBitsA: m_softness.FractionBitCount,
            b: second,
            fractionBitsB: m_softness.FractionBitCount,
            c: value.Value,
            fractionBitsC: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var product
        )) {
            ++RefusalCount;

            return FixedQ4816.Zero;
        }

        return FixedQ4816.FromRawBits(value: product);
    }
}
