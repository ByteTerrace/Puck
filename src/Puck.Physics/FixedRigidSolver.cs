using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// A deterministic sequential-impulse contact solver over temporal substeps. One dynamic body is solved against static
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
public sealed class FixedRigidSolver {
    private readonly FixedQ4816 m_doubleSubstepRate;
    private readonly FixedRigidSolverOptions m_options;
    private readonly long[] m_profile;
    private readonly FixedSoftConstraint m_softness;
    // The substep rate is exactly 1/h, so it serves both as the divisor every h-scaled integration uses and as the
    // multiplier the speculative bias applies; the reciprocal is never formed.
    private readonly FixedQ4816 m_substepRate;
    private readonly FixedQ4816 m_substepRecoveryDistance;
    private readonly FixedVector3 m_substepVelocityDelta;

    /// <summary>Creates a solver bound to one set of options.</summary>
    /// <param name="options">The solver options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public FixedRigidSolver(FixedRigidSolverOptions options) {
        ArgumentNullException.ThrowIfNull(argument: options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            value: options.SolveIterations,
            paramName: nameof(FixedRigidSolverOptions.SolveIterations)
        );
        ArgumentOutOfRangeException.ThrowIfNegative(
            value: options.RelaxIterations,
            paramName: nameof(FixedRigidSolverOptions.RelaxIterations)
        );
        ArgumentOutOfRangeException.ThrowIfNegative(
            value: options.RestitutionIterations,
            paramName: nameof(FixedRigidSolverOptions.RestitutionIterations)
        );
        ArgumentOutOfRangeException.ThrowIfNegative(
            value: options.ContactSpeed.Value,
            paramName: nameof(FixedRigidSolverOptions.ContactSpeed)
        );
        ArgumentOutOfRangeException.ThrowIfNegative(
            value: options.Restitution.Value,
            paramName: nameof(FixedRigidSolverOptions.Restitution)
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: options.Restitution,
            other: FixedQ4816.One,
            paramName: nameof(FixedRigidSolverOptions.Restitution)
        );
        ArgumentOutOfRangeException.ThrowIfNegative(
            value: options.RestitutionThreshold.Value,
            paramName: nameof(FixedRigidSolverOptions.RestitutionThreshold)
        );
        ArgumentOutOfRangeException.ThrowIfNegative(
            value: options.Friction.Value,
            paramName: nameof(FixedRigidSolverOptions.Friction)
        );
        ArgumentOutOfRangeException.ThrowIfNegative(
            value: options.ContactMargin.Value,
            paramName: nameof(FixedRigidSolverOptions.ContactMargin)
        );
        ArgumentOutOfRangeException.ThrowIfNegative(
            value: options.RecoverySpeed.Value,
            paramName: nameof(FixedRigidSolverOptions.RecoverySpeed)
        );
        ArgumentOutOfRangeException.ThrowIfNegative(
            value: options.ConvergenceToleranceRaw,
            paramName: nameof(FixedRigidSolverOptions.ConvergenceToleranceRaw)
        );
        ValidateScale(
            fractionBitCount: options.Scales.InverseMass,
            paramName: nameof(FixedRigidScales.InverseMass)
        );
        ValidateScale(
            fractionBitCount: options.Scales.InverseInertia,
            paramName: nameof(FixedRigidScales.InverseInertia)
        );
        ValidateScale(
            fractionBitCount: options.Scales.EffectiveMass,
            paramName: nameof(FixedRigidScales.EffectiveMass)
        );

        var substepRate = (((long)options.RateHz) * options.SubstepCount);

        m_options = options;
        m_softness = FixedSoftConstraint.Create(
            rateHz: options.RateHz,
            substepCount: options.SubstepCount,
            hertz: options.ContactHertz,
            dampingRatio: options.ContactDampingRatio,
            fractionBitCount: FixedSoftConstraint.DefaultFractionBitCount
        );
        m_substepRate = FixedQ4816.FromInteger(value: substepRate);
        m_doubleSubstepRate = FixedQ4816.FromInteger(value: (2L * substepRate));
        m_substepVelocityDelta = ((options.Gravity + options.AppliedAcceleration) / m_substepRate);
        m_substepRecoveryDistance = (options.RecoverySpeed / m_substepRate);
        m_profile = new long[options.SolveIterations];
    }

    private void ApplyImpulse(FixedRigidBody body, FixedVector3 anchor, FixedVector3 normal, long impulseRaw) {
        if (impulseRaw == 0L) {
            return;
        }

        var impulse = (normal * FixedQ4816.FromRawBits(value: impulseRaw));

        body.LinearVelocity += new FixedVector3(
            X: ScaleByInverseMass(
                body: body,
                value: impulse.X
            ),
            Y: ScaleByInverseMass(
                body: body,
                value: impulse.Y
            ),
            Z: ScaleByInverseMass(
                body: body,
                value: impulse.Z
            )
        );

        var orientation = body.CurrentOrientation;
        var torque = orientation.RotateInverse(vector: FixedVector3.Cross(
            left: anchor,
            right: impulse
        ));

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
    private void ApplyRestitution(FixedRigidBody body, FixedManifoldSlotTable slots) {
        if (m_options.Restitution == FixedQ4816.Zero) {
            return;
        }

        for (var iteration = 0; (iteration < m_options.RestitutionIterations); ++iteration) {
            for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
                ref var slot = ref slots[index];

                if (
                    (slot.Disposition != FixedManifoldSlotDisposition.Constraint) ||
                    (slot.NormalMassRaw <= 0L) ||
                    (slot.TotalNormalImpulseRaw == 0L) ||
                    (slot.RelativeVelocity > (-m_options.RestitutionThreshold))
                ) {
                    continue;
                }

                var normalVelocity = RelativeNormalVelocity(
                    anchor: slot.Anchor,
                    body: body,
                    normal: slot.Normal
                );
                var target = (normalVelocity + Multiply(
                    left: m_options.Restitution,
                    right: slot.RelativeVelocity
                ));
                var delta = -Scale(
                    value: target,
                    factor: FixedQ4816.FromRawBits(value: slot.NormalMassRaw),
                    fractionBits: m_options.Scales.EffectiveMass
                );
                var accumulated = Math.Max(
                    val1: (slot.NormalImpulseRaw + delta.Value),
                    val2: 0L
                );
                var applied = (accumulated - slot.NormalImpulseRaw);

                slot.NormalImpulseRaw = accumulated;
                slot.TotalNormalImpulseRaw += applied;

                ApplyImpulse(
                    anchor: slot.Anchor,
                    body: body,
                    impulseRaw: applied,
                    normal: slot.Normal
                );
            }
        }
    }
    private long EffectiveMass(FixedRigidBody body, FixedQuaternion orientation, FixedVector3 anchor, FixedVector3 normal) {
        var lever = FixedVector3.Cross(
            left: anchor,
            right: normal
        );
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
        if (!FusedArithmetic.TryMixedScaleDotProduct(
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

        if (!FusedArithmetic.TryScaledReciprocal(
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
    private void Extract(FixedRigidBody body, FixedManifoldSlotTable slots) {
        for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
            ref readonly var slot = ref slots[index];

            if (slot.Disposition != FixedManifoldSlotDisposition.Recovery) {
                continue;
            }

            // Bounded extraction: one authored escape direction, at most RecoverySpeed·h of travel per substep, and no
            // impulse of any kind. The generator stops emitting a deep candidate once the body clears, so the loop
            // terminates on geometry rather than on a counter.
            body.DeltaPosition += (body.EscapeDirection * m_substepRecoveryDistance);

            var closing = FixedVector3.Dot(
                left: body.LinearVelocity,
                right: body.EscapeDirection
            );

            if (closing < FixedQ4816.Zero) {
                body.LinearVelocity -= (body.EscapeDirection * closing);
            }
        }
    }
    private void IntegratePosition(FixedRigidBody body, FixedManifoldSlotTable slots) {
        body.DeltaPosition += (body.LinearVelocity / m_substepRate);
        body.DeltaRotation = (FixedQuaternion.Exp(bivector: (body.AngularVelocity / m_doubleSubstepRate)) * body.DeltaRotation).Normalize();
        Extract(
            body: body,
            slots: slots
        );
    }
    private void IntegrateVelocity(FixedRigidBody body) {
        body.LinearVelocity += m_substepVelocityDelta;
    }
    private FixedQ4816 Multiply(FixedQ4816 left, FixedQ4816 right) =>
        Scale(
            factor: right,
            value: left
        );
    private void Prepare(FixedRigidBody body, FixedManifoldSlotTable slots) {
        var orientation = body.Orientation;

        LastStepMinimumNormalMassRaw = long.MaxValue;

        for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
            ref var slot = ref slots[index];

            if (slot.Disposition == FixedManifoldSlotDisposition.Idle) {
                continue;
            }

            if (
                m_options.DeepRecovery &&
                (slot.Separation < m_options.RecoveryThreshold)
            ) {
                // A witness this far inside the solid cannot be trusted to name a side, so no constraint is formed and
                // no impulse survives: the bounded extraction below is the whole response.
                slot.Disposition = FixedManifoldSlotDisposition.Recovery;
                slot.NormalImpulseRaw = 0L;
                slot.NormalMassRaw = 0L;
                slot.FrictionImpulse = FixedVector3.Zero;

                continue;
            }

            slot.BaseSeparation = (slot.Separation - FixedVector3.Dot(
                left: slot.Anchor,
                right: slot.Normal
            ));

            if (!m_options.WarmStart) {
                slot.NormalImpulseRaw = 0L;
                slot.FrictionImpulse = FixedVector3.Zero;
            }

            slot.NormalMassRaw = EffectiveMass(
                anchor: slot.Anchor,
                body: body,
                normal: slot.Normal,
                orientation: orientation
            );
            slot.RelativeVelocity = RelativeNormalVelocity(
                anchor: slot.Anchor,
                body: body,
                normal: slot.Normal
            );

            if (
                (slot.NormalMassRaw > 0L) &&
                (slot.NormalMassRaw < LastStepMinimumNormalMassRaw)
            ) {
                LastStepMinimumNormalMassRaw = slot.NormalMassRaw;
            }

            FixedVector3.OrthonormalBasis(
                normal: slot.Normal,
                tangent1: out var tangent1,
                tangent2: out var tangent2
            );

            slot.Tangent1 = tangent1;
            slot.Tangent2 = tangent2;

            if (!TryTangentMass(
                anchor: slot.Anchor,
                body: body,
                orientation: orientation,
                tangent1: tangent1,
                tangent2: tangent2,
                xx: out var tangentXX,
                xy: out var tangentXY,
                yy: out var tangentYY
            )) {
                slot.TangentMassXXRaw = 0L;
                slot.TangentMassXYRaw = 0L;
                slot.TangentMassYYRaw = 0L;
                slot.TangentImpulseXRaw = 0L;
                slot.TangentImpulseYRaw = 0L;

                continue;
            }

            slot.TangentMassXXRaw = tangentXX;
            slot.TangentMassXYRaw = tangentXY;
            slot.TangentMassYYRaw = tangentYY;
            slot.TangentImpulseXRaw = FixedVector3.Dot(
                left: slot.FrictionImpulse,
                right: tangent1
            ).Value;
            slot.TangentImpulseYRaw = FixedVector3.Dot(
                left: slot.FrictionImpulse,
                right: tangent2
            ).Value;
        }

        if (LastStepMinimumNormalMassRaw == long.MaxValue) {
            LastStepMinimumNormalMassRaw = 0L;
        }
    }
    // Recomposes each Constraint slot's working tangential impulse back onto the world-space carrier that
    // survives to the next step's Prepare — the tangent basis itself is rebuilt from Normal every step, so the
    // scalar pair only means anything against THIS step's basis.
    private static void RecomposeFriction(FixedManifoldSlotTable slots) {
        for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
            ref var slot = ref slots[index];

            if (slot.Disposition != FixedManifoldSlotDisposition.Constraint) {
                continue;
            }

            slot.FrictionImpulse = ((slot.Tangent1 * FixedQ4816.FromRawBits(value: slot.TangentImpulseXRaw)) +
                                     (slot.Tangent2 * FixedQ4816.FromRawBits(value: slot.TangentImpulseYRaw)));
        }
    }
    private void RecordStepMaxima(FixedManifoldSlotTable slots) {
        LastStepMaximumImpulseRaw = 0L;

        for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
            ref readonly var slot = ref slots[index];

            if (slot.NormalImpulseRaw > LastStepMaximumImpulseRaw) {
                LastStepMaximumImpulseRaw = slot.NormalImpulseRaw;
            }
        }
    }
    private static FixedQ4816 RelativeNormalVelocity(FixedRigidBody body, FixedVector3 anchor, FixedVector3 normal) =>
        FixedVector3.Dot(
            left: (body.LinearVelocity + FixedVector3.Cross(
                left: body.AngularVelocity,
                right: anchor
            )),
            right: normal
        );
    // Every iteration of the budget runs, unconditionally: an early break driven by a tolerance would make the
    // measurement change the trajectory it is measuring.
    private void RunIterations(FixedRigidBody body, FixedManifoldSlotTable slots, int iterations, bool useBias, long[]? profile) {
        for (var iteration = 0; (iteration < iterations); ++iteration) {
            var movement = SolveOnce(
                body: body,
                slots: slots,
                useBias: useBias
            );

            if (profile is not null) {
                profile[iteration] = movement;
            }
        }
    }
    private FixedQ4816 Scale(FixedQ4816 value, FixedQ4816 factor) =>
        Scale(
            factor: factor,
            fractionBits: FixedQ4816.FractionBitCount,
            value: value
        );
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
    private FixedQ4816 ScaleByInverseMass(FixedRigidBody body, FixedQ4816 value) {
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
    // The coupled 2x2 tangential impulse: relative tangential velocity, driven through the precomputed tangent mass,
    // clamped to the friction cone friction·NormalImpulseRaw (the SAME NormalImpulseRaw this call's own normal block
    // just updated), then applied along both tangents. Cone membership is decided by a squared-magnitude compare —
    // no square root needed there — and FixedQ4816.Sqrt runs only on the rescale branch, the one place this cannot
    // be avoided.
    private void SolveFriction(FixedRigidBody body, ref FixedManifoldSlot slot) {
        var tangentVelocityX = RelativeNormalVelocity(
            anchor: slot.Anchor,
            body: body,
            normal: slot.Tangent1
        ).Value;
        var tangentVelocityY = RelativeNormalVelocity(
            anchor: slot.Anchor,
            body: body,
            normal: slot.Tangent2
        ).Value;

        if (!FixedSymmetricSolve.TryApplySymmetric2(
            a: slot.TangentMassXXRaw,
            b: slot.TangentMassXYRaw,
            d: slot.TangentMassYYRaw,
            vX: -tangentVelocityX,
            vY: -tangentVelocityY,
            fractionBitsMatrix: m_options.Scales.EffectiveMass,
            fractionBitsVector: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            x: out var deltaXRaw,
            y: out var deltaYRaw
        )) {
            ++RefusalCount;

            return;
        }

        var accumulatedXRaw = (slot.TangentImpulseXRaw + deltaXRaw);
        var accumulatedYRaw = (slot.TangentImpulseYRaw + deltaYRaw);
        var maxImpulseRaw = Scale(
            factor: m_options.Friction,
            value: FixedQ4816.FromRawBits(value: slot.NormalImpulseRaw)
        ).Value;
        var lengthSquared = ((((Int128)accumulatedXRaw) * accumulatedXRaw) + (((Int128)accumulatedYRaw) * accumulatedYRaw));
        var maxSquared = (((Int128)maxImpulseRaw) * maxImpulseRaw);

        if (lengthSquared > maxSquared) {
            var lengthRaw = ((long)((UInt128)lengthSquared).SquareRoot());

            if (lengthRaw <= 0L) {
                accumulatedXRaw = 0L;
                accumulatedYRaw = 0L;
            } else {
                accumulatedXRaw = ((long)((((Int128)accumulatedXRaw) * maxImpulseRaw) / lengthRaw));
                accumulatedYRaw = ((long)((((Int128)accumulatedYRaw) * maxImpulseRaw) / lengthRaw));
            }
        }

        var appliedXRaw = (accumulatedXRaw - slot.TangentImpulseXRaw);
        var appliedYRaw = (accumulatedYRaw - slot.TangentImpulseYRaw);

        slot.TangentImpulseXRaw = accumulatedXRaw;
        slot.TangentImpulseYRaw = accumulatedYRaw;

        ApplyImpulse(
            anchor: slot.Anchor,
            body: body,
            impulseRaw: appliedXRaw,
            normal: slot.Tangent1
        );
        ApplyImpulse(
            anchor: slot.Anchor,
            body: body,
            impulseRaw: appliedYRaw,
            normal: slot.Tangent2
        );
    }
    private long SolveOnce(FixedRigidBody body, FixedManifoldSlotTable slots, bool useBias) {
        var movement = 0L;

        for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
            ref var slot = ref slots[index];

            if (
                (slot.Disposition != FixedManifoldSlotDisposition.Constraint) ||
                (slot.NormalMassRaw <= 0L)
            ) {
                continue;
            }

            var rotated = body.DeltaRotation.Rotate(vector: slot.Anchor);
            var separation = (slot.BaseSeparation + FixedVector3.Dot(
                left: (body.DeltaPosition + rotated),
                right: slot.Normal
            ));
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
                velocityBias = Scale(
                    factor: m_substepRate,
                    value: separation
                );
            } else if (useBias) {
                var soft = SoftScale(
                    first: m_softness.MassScaleRaw,
                    second: m_softness.BiasRateRaw,
                    value: separation
                );

                velocityBias = FixedQ4816.Max(
                    x: soft,
                    y: (-m_options.ContactSpeed)
                );
                massScaleRaw = m_softness.MassScaleRaw;
                massScaleBits = m_softness.FractionBitCount;
                impulseScaleRaw = m_softness.ImpulseScaleRaw;
                impulseScaleBits = m_softness.FractionBitCount;
            }

            var normalVelocity = RelativeNormalVelocity(
                anchor: slot.Anchor,
                body: body,
                normal: slot.Normal
            );
            var driven = (Scale(
                value: normalVelocity,
                factor: FixedQ4816.FromRawBits(value: massScaleRaw),
                fractionBits: massScaleBits
            ) + velocityBias);
            var delta = (-Scale(
                value: driven,
                factor: FixedQ4816.FromRawBits(value: slot.NormalMassRaw),
                fractionBits: m_options.Scales.EffectiveMass
            )
                - Scale(
                value: FixedQ4816.FromRawBits(value: slot.NormalImpulseRaw),
                factor: FixedQ4816.FromRawBits(value: impulseScaleRaw),
                fractionBits: impulseScaleBits
            ));
            var accumulated = Math.Max(
                val1: (slot.NormalImpulseRaw + delta.Value),
                val2: 0L
            );
            var applied = (accumulated - slot.NormalImpulseRaw);

            slot.NormalImpulseRaw = accumulated;
            slot.TotalNormalImpulseRaw += applied;
            movement = Math.Max(
                val1: movement,
                val2: Math.Abs(value: applied)
            );

            ApplyImpulse(
                anchor: slot.Anchor,
                body: body,
                impulseRaw: applied,
                normal: slot.Normal
            );

            // Friction: unbiased pass only, matching the "no friction when applying bias" reference shape — a
            // biased push-out is a position correction, not a physical contact force, and coupling friction to it
            // would brake or accelerate the body based on how deep the correction reached rather than how it moves.
            if (
                !useBias &&
                (slot.TangentMassXXRaw > 0L)
            ) {
                SolveFriction(
                    body: body,
                    slot: ref slot
                );
            }
        }

        return movement;
    }
    // The coupled tangent effective-mass tensor: K = [[a,b],[b,d]], where a and d are each InverseMassRaw plus the
    // angular contribution of their OWN tangent's lever arm, and b is the cross term between the two tangents'
    // lever arms — nonzero whenever the tangent directions are not aligned with the body's principal inertia axes.
    // Inverted once here (precompute-once-apply-per-iteration, mirroring EffectiveMass's own scalar shape) rather
    // than solved fresh every relax iteration.
    private bool TryTangentMass(FixedRigidBody body, FixedQuaternion orientation, FixedVector3 anchor, FixedVector3 tangent1, FixedVector3 tangent2, out long xx, out long xy, out long yy) {
        var localLever1 = orientation.RotateInverse(vector: FixedVector3.Cross(
            left: anchor,
            right: tangent1
        ));
        var localLever2 = orientation.RotateInverse(vector: FixedVector3.Cross(
            left: anchor,
            right: tangent2
        ));

        if (
            !FixedSymmetricSolve.TryApplySymmetric3(
            a: body.InverseInertiaXX,
            b: body.InverseInertiaXY,
            c: body.InverseInertiaXZ,
            d: body.InverseInertiaYY,
            e: body.InverseInertiaYZ,
            f: body.InverseInertiaZZ,
            fractionBitsMatrix: m_options.Scales.InverseInertia,
            fractionBitsOut: m_options.Scales.InverseInertia,
            fractionBitsVector: FixedQ4816.FractionBitCount,
            vX: localLever1.X.Value,
            vY: localLever1.Y.Value,
            vZ: localLever1.Z.Value,
            x: out var w1x,
            y: out var w1y,
            z: out var w1z
        ) ||
            !FixedSymmetricSolve.TryApplySymmetric3(
            a: body.InverseInertiaXX,
            b: body.InverseInertiaXY,
            c: body.InverseInertiaXZ,
            d: body.InverseInertiaYY,
            e: body.InverseInertiaYZ,
            f: body.InverseInertiaZZ,
            fractionBitsMatrix: m_options.Scales.InverseInertia,
            fractionBitsOut: m_options.Scales.InverseInertia,
            fractionBitsVector: FixedQ4816.FractionBitCount,
            vX: localLever2.X.Value,
            vY: localLever2.Y.Value,
            vZ: localLever2.Z.Value,
            x: out var w2x,
            y: out var w2y,
            z: out var w2z
        )
        ) {
            ++RefusalCount;

            xx = 0L;
            xy = 0L;
            yy = 0L;

            return false;
        }

        if (
            !FusedArithmetic.TryMixedScaleDotProduct(
            ax: localLever1.X.Value,
            ay: localLever1.Y.Value,
            az: localLever1.Z.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            bx: w1x,
            by: w1y,
            bz: w1z,
            fractionBitsB: m_options.Scales.InverseInertia,
            fractionBitsOut: m_options.Scales.InverseMass,
            result: out var angularXX
        ) ||
            !FusedArithmetic.TryMixedScaleDotProduct(
            ax: localLever2.X.Value,
            ay: localLever2.Y.Value,
            az: localLever2.Z.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            bx: w2x,
            by: w2y,
            bz: w2z,
            fractionBitsB: m_options.Scales.InverseInertia,
            fractionBitsOut: m_options.Scales.InverseMass,
            result: out var angularYY
        ) ||
            !FusedArithmetic.TryMixedScaleDotProduct(
            ax: localLever1.X.Value,
            ay: localLever1.Y.Value,
            az: localLever1.Z.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            bx: w2x,
            by: w2y,
            bz: w2z,
            fractionBitsB: m_options.Scales.InverseInertia,
            fractionBitsOut: m_options.Scales.InverseMass,
            result: out var angularXY
        )
        ) {
            ++RefusalCount;

            xx = 0L;
            xy = 0L;
            yy = 0L;

            return false;
        }

        var kxx = (body.InverseMassRaw + angularXX);
        var kyy = (body.InverseMassRaw + angularYY);

        if (
            (kxx <= 0L) ||
            (kyy <= 0L)
        ) {
            xx = 0L;
            xy = 0L;
            yy = 0L;

            return false;
        }

        if (!FixedSymmetricSolve.TryInvertSymmetric2(
            a: kxx,
            b: angularXY,
            d: kyy,
            outputFractionShift: (m_options.Scales.InverseMass + m_options.Scales.EffectiveMass),
            invA: out xx,
            invB: out xy,
            invD: out yy
        )) {
            ++RefusalCount;

            xx = 0L;
            xy = 0L;
            yy = 0L;

            return false;
        }

        return true;
    }
    private static void ValidateScale(int fractionBitCount, string paramName) {
        ArgumentOutOfRangeException.ThrowIfNegative(
            paramName: paramName,
            value: fractionBitCount
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            other: 64,
            paramName: paramName,
            value: fractionBitCount
        );
    }
    private void WarmStart(FixedRigidBody body, FixedManifoldSlotTable slots) {
        if (!m_options.WarmStart) {
            return;
        }

        for (var index = 0; (index < FixedManifoldSlotTable.Capacity); ++index) {
            ref var slot = ref slots[index];

            if (
                (slot.Disposition != FixedManifoldSlotDisposition.Constraint) ||
                (slot.NormalImpulseRaw == 0L)
            ) {
                continue;
            }

            ApplyImpulse(
                anchor: slot.Anchor,
                body: body,
                impulseRaw: slot.NormalImpulseRaw,
                normal: slot.Normal
            );
            ApplyImpulse(
                anchor: slot.Anchor,
                body: body,
                impulseRaw: slot.TangentImpulseXRaw,
                normal: slot.Tangent1
            );
            ApplyImpulse(
                anchor: slot.Anchor,
                body: body,
                impulseRaw: slot.TangentImpulseYRaw,
                normal: slot.Tangent2
            );
        }
    }

    /// <summary>Gets the number of biased solve iterations the most recent substep needed to bring the accumulated
    /// impulses within a tolerance.</summary>
    /// <param name="toleranceRaw">The tolerance, in raw Q48.16 units.</param>
    /// <returns>The one-based iteration index, or the whole budget when the tolerance was never met.</returns>
    public int IterationsToConverge(long toleranceRaw) {
        for (var index = 0; (index < m_profile.Length); ++index) {
            if (m_profile[index] <= toleranceRaw) {
                return (index + 1);
            }
        }

        return m_profile.Length;
    }
    /// <summary>Advances one body by one step against one step's contact candidates.</summary>
    /// <param name="body">The dynamic body.</param>
    /// <param name="slots">The body's persistent manifold slots.</param>
    /// <param name="candidates">This step's candidates; canonicalized in place when the options ask for it.</param>
    /// <param name="step">The step ordinal.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public void Step(FixedRigidBody body, FixedManifoldSlotTable slots, List<FixedContactCandidate> candidates, int step) {
        ArgumentNullException.ThrowIfNull(argument: body);
        ArgumentNullException.ThrowIfNull(argument: slots);
        ArgumentNullException.ThrowIfNull(argument: candidates);

        if (m_options.CanonicalOrder) {
            FixedContactCandidate.Canonicalize(candidates: candidates);
        }

        slots.Associate(
            candidates: candidates,
            step: step,
            compositeIdentity: m_options.CompositeIdentity
        );
        body.ResetStepAccumulators();
        Prepare(
            body: body,
            slots: slots
        );

        for (var substep = 0; (substep < m_options.SubstepCount); ++substep) {
            IntegrateVelocity(body: body);
            WarmStart(
                body: body,
                slots: slots
            );
            RunIterations(
                body: body,
                slots: slots,
                iterations: m_options.SolveIterations,
                useBias: true,
                profile: m_profile
            );
            IntegratePosition(
                body: body,
                slots: slots
            );
            RunIterations(
                body: body,
                slots: slots,
                iterations: m_options.RelaxIterations,
                useBias: false,
                profile: null
            );
        }

        ApplyRestitution(
            body: body,
            slots: slots
        );
        RecomposeFriction(slots: slots);
        RecordStepMaxima(slots: slots);
    }

    /// <summary>Gets the largest accumulated-impulse movement each biased solve iteration of the most recent substep
    /// left behind, in raw Q48.16 units. The solve always runs its whole budget, so this is an observation of the
    /// trajectory rather than a control over it.</summary>
    public ReadOnlySpan<long> IterationProfile => m_profile;
    /// <summary>Gets the number of biased solve iterations the most recent substep needed, read at the options'
    /// own tolerance.</summary>
    public int LastStepIterationsToConverge => IterationsToConverge(toleranceRaw: m_options.ConvergenceToleranceRaw);
    /// <summary>Gets the largest accumulated normal impulse raw seen at the end of the most recent step.</summary>
    public long LastStepMaximumImpulseRaw { get; private set; }
    /// <summary>Gets the smallest effective-mass raw seen while preparing the most recent step.</summary>
    public long LastStepMinimumNormalMassRaw { get; private set; }
    /// <summary>Gets the number of kernel refusals the solver has counted; a healthy fixture ends at zero.</summary>
    public int RefusalCount { get; private set; }
    /// <summary>Gets the soft-constraint coefficients this solver formed at its substep width.</summary>
    public FixedSoftConstraint Softness => m_softness;
}
