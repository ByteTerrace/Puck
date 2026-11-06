using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// The rigid contact solver: a deterministic soft-step sequential-impulse solve over temporal substeps, for any number
/// of bodies and the contact manifolds among them and against static surfaces. The contact geometry enters once a step,
/// as caller-generated candidates associated into each <see cref="FixedRigidManifold"/>; the substeps and solver passes
/// then re-derive every separation from the displacement the solver has itself accumulated, so no geometry query runs
/// inside the solve.
/// </summary>
/// <remarks>
/// <para><b>Substep sequence.</b> Per substep: integrate velocities, warm start, biased solve, integrate positions,
/// unbiased relax (the only pass that solves friction and rolling resistance). Restitution runs once at the end of the
/// step, then each slot's friction impulse is stored back as a world vector. The softness coefficients are formed at the
/// substep width.</para>
/// <para><b>Rest.</b> A contact within twice <see cref="FixedRigidSolverOptions.RestOffset"/> of zero separation is a
/// plain non-penetration constraint: no bias and no softness, so a resting body neither sinks under its load nor creeps.
/// A speculative contact approaches, and a deeper contact's soft push returns, to the rest offset, so either ends in
/// that band.</para>
/// <para><b>Order.</b> Bodies are integrated in the order the caller lists them and manifolds are solved in the order the
/// caller lists them. Within a manifold, the points' common mode (equal increments at every point, one impulse at their
/// centroid) is solved exactly first, then each point in turn, in ascending slot order in the biased pass and descending
/// order in the relax pass. The caller supplies a canonical order; nothing here reads a hash container.</para>
/// <para><b>Rounding.</b> Every mixed-scale product goes through the refusing face of the fused kernels, so a result that
/// leaves its carrier is declined and counted (<see cref="RefusalCount"/>) rather than wrapped into an ordinary
/// answer.</para>
/// </remarks>
public sealed class FixedRigidWorld {
    private readonly FixedQ4816 m_doubleSubstepRate;
    private readonly FixedSoftConstraint m_dynamicSoftness;
    private readonly FixedRigidSolverOptions m_options;
    private readonly FixedSoftConstraint m_staticSoftness;
    // The substep rate is exactly 1/h, so it serves both as the divisor every h-scaled integration uses and as the
    // multiplier the speculative bias applies; the reciprocal is never formed.
    private readonly FixedQ4816 m_substepRate;
    // The top of the rest band, twice the rest offset.
    private readonly FixedQ4816 m_restBand;

    /// <summary>Initializes a new instance of the <see cref="FixedRigidWorld"/> class bound to one set of options.</summary>
    /// <param name="options">The solver options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rate or substep count is not strictly positive, an iteration
    /// count, speed, rest offset or tolerance is negative, or a carrier placement is outside <c>[0, 64]</c>.</exception>
    public FixedRigidWorld(FixedRigidSolverOptions options) {
        ArgumentNullException.ThrowIfNull(argument: options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: options.RateHz, paramName: nameof(FixedRigidSolverOptions.RateHz));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: options.SubstepCount, paramName: nameof(FixedRigidSolverOptions.SubstepCount));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.SolveIterations, paramName: nameof(FixedRigidSolverOptions.SolveIterations));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.RelaxIterations, paramName: nameof(FixedRigidSolverOptions.RelaxIterations));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.RestitutionIterations, paramName: nameof(FixedRigidSolverOptions.RestitutionIterations));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.ContactSpeed.Value, paramName: nameof(FixedRigidSolverOptions.ContactSpeed));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.RestitutionThreshold.Value, paramName: nameof(FixedRigidSolverOptions.RestitutionThreshold));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.RestOffset.Value, paramName: nameof(FixedRigidSolverOptions.RestOffset));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.ConvergenceToleranceRaw, paramName: nameof(FixedRigidSolverOptions.ConvergenceToleranceRaw));
        ValidateScale(fractionBitCount: options.Scales.InverseMass, paramName: nameof(FixedRigidScales.InverseMass));
        ValidateScale(fractionBitCount: options.Scales.InverseInertia, paramName: nameof(FixedRigidScales.InverseInertia));
        ValidateScale(fractionBitCount: options.Scales.EffectiveMass, paramName: nameof(FixedRigidScales.EffectiveMass));
        ValidateScale(fractionBitCount: options.Scales.Impulse, paramName: nameof(FixedRigidScales.Impulse));

        var substepRate = (((long)options.RateHz) * options.SubstepCount);

        m_options = options;
        m_dynamicSoftness = FixedSoftConstraint.Create(
            rateHz: options.RateHz,
            substepCount: options.SubstepCount,
            hertz: options.ContactHertz,
            dampingRatio: options.ContactDampingRatio,
            fractionBitCount: FixedSoftConstraint.DefaultFractionBitCount
        );
        // A contact against an immovable side carries none of the give a dynamic pair's shared compliance provides, so
        // it is formed stiffer and less damped: twice the contact hertz (before the substep clamp), half the damping.
        m_staticSoftness = FixedSoftConstraint.Create(
            rateHz: options.RateHz,
            substepCount: options.SubstepCount,
            hertz: (options.ContactHertz * FixedQ4816.FromInteger(value: 2L)),
            dampingRatio: (options.ContactDampingRatio * FixedQ4816.FromDouble(value: 0.5d)),
            fractionBitCount: FixedSoftConstraint.DefaultFractionBitCount
        );
        m_substepRate = FixedQ4816.FromInteger(value: substepRate);
        m_restBand = (options.RestOffset + options.RestOffset);
        m_doubleSubstepRate = FixedQ4816.FromInteger(value: (2L * substepRate));
    }

    /// <summary>Gets the number of constraint slots the most recent step solved.</summary>
    public int LastStepConstraintCount { get; private set; }
    /// <summary>Gets the number of manifolds whose largest applied-impulse change the most recent step left at or below
    /// <see cref="FixedRigidSolverOptions.ConvergenceToleranceRaw"/> — a reading, never a control.</summary>
    public int LastStepQuiescentManifoldCount { get; private set; }
    /// <summary>Gets the options the world was formed with.</summary>
    public FixedRigidSolverOptions Options => m_options;
    /// <summary>Gets the number of kernel refusals the world has counted; a healthy world stays at zero.</summary>
    public int RefusalCount { get; private set; }
    /// <summary>Gets the soft-constraint coefficients formed at the substep width for a contact between two dynamic
    /// bodies.</summary>
    public FixedSoftConstraint Softness => m_dynamicSoftness;

    /// <summary>Advances the dynamic bodies by one step against the manifolds, which already hold this step's associated
    /// candidates. Static bodies in the list or in a manifold are read and never written.</summary>
    /// <param name="bodies">The bodies to integrate, in canonical order; each one's step accumulators are reset first.</param>
    /// <param name="manifolds">The manifolds to solve, in canonical order; each names both its bodies.</param>
    /// <exception cref="ArgumentException">A manifold is missing a body.</exception>
    public void Step(ReadOnlySpan<FixedRigidBody> bodies, ReadOnlySpan<FixedRigidManifold> manifolds) {
        foreach (var manifold in manifolds) {
            if ((manifold.BodyA is null) || (manifold.BodyB is null)) {
                throw new ArgumentException(message: "every manifold names both of its bodies.", paramName: nameof(manifolds));
            }

            manifold.LastStepMovementRaw = 0L;
        }

        foreach (var body in bodies) {
            body.ResetStepAccumulators();
        }

        Prepare(manifolds: manifolds);

        for (var substep = 0; (substep < m_options.SubstepCount); ++substep) {
            foreach (var body in bodies) {
                if (body.IsDynamic) {
                    body.LinearVelocity += (body.Acceleration / m_substepRate);
                }
            }

            WarmStart(manifolds: manifolds);

            for (var iteration = 0; (iteration < m_options.SolveIterations); ++iteration) {
                SolveOnce(
                    manifolds: manifolds,
                    useBias: true
                );
            }

            foreach (var body in bodies) {
                if (body.IsDynamic) {
                    body.DeltaPosition += (body.LinearVelocity / m_substepRate);
                    body.DeltaRotation = (FixedQuaternion.Exp(bivector: (body.AngularVelocity / m_doubleSubstepRate)) * body.DeltaRotation).Normalize();
                }
            }

            for (var iteration = 0; (iteration < m_options.RelaxIterations); ++iteration) {
                SolveOnce(
                    manifolds: manifolds,
                    useBias: false
                );
            }
        }

        ApplyRestitution(manifolds: manifolds);
        StoreFriction(manifolds: manifolds);

        var quiescent = 0;

        foreach (var manifold in manifolds) {
            if (manifold.LastStepMovementRaw <= m_options.ConvergenceToleranceRaw) {
                ++quiescent;
            }
        }

        LastStepQuiescentManifoldCount = quiescent;
    }

    private void ApplyImpulse(FixedRigidManifold manifold, in FixedPairManifoldSlot slot, FixedVector3 direction, long impulseRaw) {
        var refusals = 0;

        FixedTwoBodyKernel.ApplyImpulse(
            bodyA: manifold.BodyA!,
            anchorA: slot.AnchorA,
            bodyB: manifold.BodyB!,
            anchorB: slot.AnchorB,
            normal: direction,
            impulseRaw: impulseRaw,
            scales: m_options.Scales,
            refusals: ref refusals
        );
        RefusalCount += refusals;
    }
    private void ApplyAngularImpulse(FixedRigidManifold manifold, FixedVector3 axis, long impulseRaw) {
        var refusals = 0;

        FixedTwoBodyKernel.ApplyAngularImpulse(
            axis: axis,
            bodyA: manifold.BodyA!,
            bodyB: manifold.BodyB!,
            impulseRaw: impulseRaw,
            refusals: ref refusals,
            scales: m_options.Scales
        );
        RefusalCount += refusals;
    }
    // Restitution reflects each closing slot's pre-solve normal speed, solved like the normal rows (SolveOnce): the
    // common mode of a face whose every point restitutes first, then each point in turn.
    private void ApplyRestitution(ReadOnlySpan<FixedRigidManifold> manifolds) {
        for (var iteration = 0; (iteration < m_options.RestitutionIterations); ++iteration) {
            foreach (var manifold in manifolds) {
                if (manifold.Restitution <= FixedQ4816.Zero) {
                    continue;
                }

                var targetSum = 0L;
                var count = 0;
                var everyPoint = (manifold.CommonModeMassRaw > 0L);

                for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
                    ref var slot = ref manifold.Slots[index];

                    if ((slot.Disposition != FixedPairManifoldSlotDisposition.Constraint) || (slot.NormalMassRaw <= 0L)) {
                        continue;
                    }

                    if (!Restitutes(slot: in slot)) {
                        everyPoint = false;

                        continue;
                    }

                    targetSum += RestitutionTarget(manifold: manifold, slot: in slot).Value;
                    ++count;
                }

                if (count == 0) {
                    continue;
                }

                if (everyPoint) {
                    var share = (-ToImpulse(value: FixedQ4816.FromRawBits(value: (targetSum / count)), factorRaw: manifold.CommonModeMassRaw) / count);

                    for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
                        ref var slot = ref manifold.Slots[index];

                        if (Restitutes(slot: in slot)) {
                            AccumulateNormal(deltaRaw: share, manifold: manifold, slot: ref slot);
                        }
                    }
                }

                for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
                    ref var slot = ref manifold.Slots[index];

                    if (Restitutes(slot: in slot)) {
                        AccumulateNormal(
                            deltaRaw: -ToImpulse(value: RestitutionTarget(manifold: manifold, slot: in slot), factorRaw: slot.NormalMassRaw),
                            manifold: manifold,
                            slot: ref slot
                        );
                    }
                }
            }
        }
    }
    // The normal velocity a restituting slot must still gain: its current normal velocity less the coefficient times its
    // pre-solve closing speed.
    private FixedQ4816 RestitutionTarget(FixedRigidManifold manifold, in FixedPairManifoldSlot slot) =>
        (RelativeVelocity(direction: slot.Normal, manifold: manifold, slot: in slot) + (manifold.Restitution * slot.RelativeVelocity));
    private bool Restitutes(in FixedPairManifoldSlot slot) =>
        (slot.Disposition == FixedPairManifoldSlotDisposition.Constraint) &&
        (slot.NormalMassRaw > 0L) &&
        (slot.MaxNormalImpulseRaw > 0L) &&
        (slot.RelativeVelocity <= (-m_options.RestitutionThreshold));
    private long EffectiveMass(FixedRigidManifold manifold, in FixedPairManifoldSlot slot, FixedVector3 direction) {
        var refusals = 0;

        _ = FixedTwoBodyKernel.TryEffectiveMass(
            bodyA: manifold.BodyA!,
            anchorA: slot.AnchorA,
            bodyB: manifold.BodyB!,
            anchorB: slot.AnchorB,
            normal: direction,
            scales: m_options.Scales,
            normalMassRaw: out var mass,
            refusals: ref refusals
        );
        RefusalCount += refusals;

        return mass;
    }
    private long AngularMass(FixedRigidManifold manifold, FixedVector3 axis) {
        var refusals = 0;

        _ = FixedTwoBodyKernel.TryAngularMass(
            angularMassRaw: out var mass,
            axis: axis,
            bodyA: manifold.BodyA!,
            bodyB: manifold.BodyB!,
            refusals: ref refusals,
            scales: m_options.Scales
        );
        RefusalCount += refusals;

        return mass;
    }
    // The effective mass at the solved points' centroid along their shared normal, or zero when fewer than two points
    // are solved or any two normals differ.
    private long CommonModeMass(FixedRigidManifold manifold) {
        var anchorA = FixedVector3.Zero;
        var anchorB = FixedVector3.Zero;
        var normal = FixedVector3.Zero;
        var count = 0;

        for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
            ref readonly var slot = ref manifold.Slots[index];

            if (
                (slot.Disposition != FixedPairManifoldSlotDisposition.Constraint) ||
                (slot.NormalMassRaw <= 0L)
            ) {
                continue;
            }

            if ((count > 0) && (slot.Normal != normal)) {
                return 0L;
            }

            normal = slot.Normal;
            anchorA += slot.AnchorA;
            anchorB += slot.AnchorB;
            ++count;
        }

        if (count < 2) {
            return 0L;
        }

        var divisor = FixedQ4816.FromInteger(value: count);
        var centroid = new FixedPairManifoldSlot { AnchorA = (anchorA / divisor), AnchorB = (anchorB / divisor) };

        return EffectiveMass(direction: normal, manifold: manifold, slot: in centroid);
    }
    private void Prepare(ReadOnlySpan<FixedRigidManifold> manifolds) {
        var constraints = 0;

        foreach (var manifold in manifolds) {
            for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
                ref var slot = ref manifold.Slots[index];

                if (slot.Disposition != FixedPairManifoldSlotDisposition.Constraint) {
                    continue;
                }

                ++constraints;
                slot.BaseSeparation = (slot.Separation - FixedVector3.Dot(
                    left: (slot.AnchorB - slot.AnchorA),
                    right: slot.Normal
                ));

                if (!m_options.WarmStart) {
                    slot.NormalImpulseRaw = 0L;
                    slot.FrictionImpulse = FixedVector3.Zero;
                }

                slot.NormalMassRaw = EffectiveMass(
                    direction: slot.Normal,
                    manifold: manifold,
                    slot: in slot
                );
                slot.RelativeVelocity = RelativeVelocity(
                    direction: slot.Normal,
                    manifold: manifold,
                    slot: in slot
                );
                FixedVector3.OrthonormalBasis(
                    normal: slot.Normal,
                    tangent1: out slot.Tangent1,
                    tangent2: out slot.Tangent2
                );

                if (manifold.Friction > FixedQ4816.Zero) {
                    slot.TangentMass1Raw = EffectiveMass(
                        direction: slot.Tangent1,
                        manifold: manifold,
                        slot: in slot
                    );
                    slot.TangentMass2Raw = EffectiveMass(
                        direction: slot.Tangent2,
                        manifold: manifold,
                        slot: in slot
                    );
                } else {
                    slot.TangentMass1Raw = 0L;
                    slot.TangentMass2Raw = 0L;
                }

                if (
                    (slot.TangentMass1Raw > 0L) &&
                    (slot.TangentMass2Raw > 0L)
                ) {
                    slot.TangentImpulse1Raw = FixedVector3.Dot(
                        left: slot.FrictionImpulse,
                        right: slot.Tangent1
                    ).Value;
                    slot.TangentImpulse2Raw = FixedVector3.Dot(
                        left: slot.FrictionImpulse,
                        right: slot.Tangent2
                    ).Value;
                } else {
                    slot.TangentMass1Raw = 0L;
                    slot.TangentMass2Raw = 0L;
                    slot.TangentImpulse1Raw = 0L;
                    slot.TangentImpulse2Raw = 0L;
                }

                slot.RollingImpulse1Raw = 0L;
                slot.RollingImpulse2Raw = 0L;

                if (manifold.RollingResistance > FixedQ4816.Zero) {
                    slot.RollingMass1Raw = AngularMass(axis: slot.Tangent1, manifold: manifold);
                    slot.RollingMass2Raw = AngularMass(axis: slot.Tangent2, manifold: manifold);
                }

                if ((manifold.RollingResistance <= FixedQ4816.Zero) || (slot.RollingMass1Raw <= 0L) || (slot.RollingMass2Raw <= 0L)) {
                    slot.RollingMass1Raw = 0L;
                    slot.RollingMass2Raw = 0L;
                }
            }

            manifold.CommonModeMassRaw = CommonModeMass(manifold: manifold);
        }

        LastStepConstraintCount = constraints;
    }
    private static FixedQ4816 RelativeVelocity(FixedRigidManifold manifold, in FixedPairManifoldSlot slot, FixedVector3 direction) =>
        FixedTwoBodyKernel.RelativeNormalVelocity(
            anchorA: slot.AnchorA,
            anchorB: slot.AnchorB,
            bodyA: manifold.BodyA!,
            bodyB: manifold.BodyB!,
            normal: direction
        );
    // a·b, each read at its own placement, rounded once to the requested one; a product past the carrier is refused and
    // counted, reading zero.
    private long Product(long aRaw, int aBits, long bRaw, int bBits, int outBits) {
        if (!FusedArithmetic.TryMixedScaleProduct(
            a: aRaw,
            fractionBitsA: aBits,
            b: bRaw,
            fractionBitsB: bBits,
            fractionBitsOut: outBits,
            result: out var product
        )) {
            ++RefusalCount;

            return 0L;
        }

        return product;
    }
    // A velocity times an effective mass: the impulse raw that changes the velocity by that much.
    private long ToImpulse(FixedQ4816 value, long factorRaw) => Product(
        aBits: FixedQ4816.FractionBitCount,
        aRaw: value.Value,
        bBits: m_options.Scales.EffectiveMass,
        bRaw: factorRaw,
        outBits: m_options.Scales.Impulse
    );
    // A Q48.16 coefficient times an impulse raw, at the impulse placement.
    private long ScaleImpulse(FixedQ4816 coefficient, long impulseRaw) => Product(
        aBits: FixedQ4816.FractionBitCount,
        aRaw: coefficient.Value,
        bBits: m_options.Scales.Impulse,
        bRaw: impulseRaw,
        outBits: m_options.Scales.Impulse
    );
    // massScale · biasRate · separation, with the two softness operands read at their own finer scale and the whole
    // triple product rounded once.
    private FixedQ4816 SoftScale(in FixedSoftConstraint softness, FixedQ4816 value) {
        if (!FusedArithmetic.TryMixedScaleProduct(
            a: softness.MassScaleRaw,
            fractionBitsA: softness.FractionBitCount,
            b: softness.BiasRateRaw,
            fractionBitsB: softness.FractionBitCount,
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
    // The tangential impulse along both friction directions, each driven through its own effective mass, then the
    // accumulated pair shortened toward zero onto the Coulomb limit friction · (the normal impulse this pass just left).
    private void SolveFriction(FixedRigidManifold manifold, ref FixedPairManifoldSlot slot) {
        var velocity1 = RelativeVelocity(
            direction: slot.Tangent1,
            manifold: manifold,
            slot: in slot
        );
        var velocity2 = RelativeVelocity(
            direction: slot.Tangent2,
            manifold: manifold,
            slot: in slot
        );
        var delta1 = -ToImpulse(value: velocity1, factorRaw: slot.TangentMass1Raw);
        var delta2 = -ToImpulse(value: velocity2, factorRaw: slot.TangentMass2Raw);
        var maximum = ScaleImpulse(coefficient: manifold.Friction, impulseRaw: slot.NormalImpulseRaw);
        // The pair's raws sit at the impulse placement; the clamp compares and rescales raws exactly, so it reads them
        // at any placement.
        var accumulated = new FixedVector2(
            X: FixedQ4816.FromRawBits(value: (slot.TangentImpulse1Raw + delta1)),
            Y: FixedQ4816.FromRawBits(value: (slot.TangentImpulse2Raw + delta2))
        ).ClampLength(maximum: FixedQ4816.FromRawBits(value: maximum));
        var applied1 = (accumulated.X.Value - slot.TangentImpulse1Raw);
        var applied2 = (accumulated.Y.Value - slot.TangentImpulse2Raw);

        slot.TangentImpulse1Raw = accumulated.X.Value;
        slot.TangentImpulse2Raw = accumulated.Y.Value;

        if (applied1 != 0L) {
            ApplyImpulse(
                direction: slot.Tangent1,
                impulseRaw: applied1,
                manifold: manifold,
                slot: in slot
            );
        }

        if (applied2 != 0L) {
            ApplyImpulse(
                direction: slot.Tangent2,
                impulseRaw: applied2,
                manifold: manifold,
                slot: in slot
            );
        }
    }
    // The angular impulse about both tangent axes that stops the bodies' relative rolling at this point, the
    // accumulated pair shortened toward zero onto rollingResistance · (the normal impulse this pass just left): a
    // rolling contact does no work on its own, so without this a body rocking on a curved point never loses energy.
    private void SolveRolling(FixedRigidManifold manifold, ref FixedPairManifoldSlot slot) {
        var delta1 = -ToImpulse(
            factorRaw: slot.RollingMass1Raw,
            value: FixedTwoBodyKernel.RelativeAngularVelocity(axis: slot.Tangent1, bodyA: manifold.BodyA!, bodyB: manifold.BodyB!)
        );
        var delta2 = -ToImpulse(
            factorRaw: slot.RollingMass2Raw,
            value: FixedTwoBodyKernel.RelativeAngularVelocity(axis: slot.Tangent2, bodyA: manifold.BodyA!, bodyB: manifold.BodyB!)
        );
        var maximum = ScaleImpulse(coefficient: manifold.RollingResistance, impulseRaw: slot.NormalImpulseRaw);
        var accumulated = new FixedVector2(
            X: FixedQ4816.FromRawBits(value: (slot.RollingImpulse1Raw + delta1)),
            Y: FixedQ4816.FromRawBits(value: (slot.RollingImpulse2Raw + delta2))
        ).ClampLength(maximum: FixedQ4816.FromRawBits(value: maximum));
        var applied1 = (accumulated.X.Value - slot.RollingImpulse1Raw);
        var applied2 = (accumulated.Y.Value - slot.RollingImpulse2Raw);

        slot.RollingImpulse1Raw = accumulated.X.Value;
        slot.RollingImpulse2Raw = accumulated.Y.Value;
        ApplyAngularImpulse(axis: slot.Tangent1, impulseRaw: applied1, manifold: manifold);
        ApplyAngularImpulse(axis: slot.Tangent2, impulseRaw: applied2, manifold: manifold);
    }
    // What a slot asks for this pass, from the bodies' current velocities and displacement: the velocity its impulse
    // must remove (the speculative bias while the slot lies above the rest band, the soft bias in the biased pass once
    // it overlaps, each aiming at the rest offset, a plain non-penetration constraint inside the band) and the impulse
    // raw the softness relaxes from what the slot has accumulated.
    private (FixedQ4816 Driven, long Relaxed) NormalAsk(FixedRigidManifold manifold, in FixedPairManifoldSlot slot, in FixedSoftConstraint softness, bool useBias) {
        var bodyA = manifold.BodyA!;
        var bodyB = manifold.BodyB!;
        var rotatedA = bodyA.DeltaRotation.Rotate(vector: slot.AnchorA);
        var rotatedB = bodyB.DeltaRotation.Rotate(vector: slot.AnchorB);
        var relativeDisplacement = ((bodyB.DeltaPosition + rotatedB) - (bodyA.DeltaPosition + rotatedA));
        var separation = (slot.BaseSeparation + FixedVector3.Dot(
            left: relativeDisplacement,
            right: slot.Normal
        ));
        var velocityBias = FixedQ4816.Zero;
        // The two softness weights are read at their own finer scale rather than narrowed to Q48.16 first; a mass
        // scale as small as 0.0076 keeps only nine significant bits once narrowed.
        var massScaleRaw = FixedQ4816.One.Value;
        var massScaleBits = FixedQ4816.FractionBitCount;
        var impulseScaleRaw = 0L;
        var impulseScaleBits = FixedQ4816.FractionBitCount;

        if (separation > m_restBand) {
            // Speculative: the constraint may remove exactly the closing speed that would carry the pair past the rest
            // offset within this substep, and no more, so a first-appearance contact neither tunnels nor stops short.
            velocityBias = ((separation - m_options.RestOffset) * m_substepRate);
        } else if (useBias && (separation < FixedQ4816.Zero)) {
            velocityBias = FixedQ4816.Max(
                x: SoftScale(
                    softness: in softness,
                    value: (separation - m_options.RestOffset)
                ),
                y: (-m_options.ContactSpeed)
            );
            massScaleRaw = softness.MassScaleRaw;
            massScaleBits = softness.FractionBitCount;
            impulseScaleRaw = softness.ImpulseScaleRaw;
            impulseScaleBits = softness.FractionBitCount;
        }

        var normalVelocity = RelativeVelocity(
            direction: slot.Normal,
            manifold: manifold,
            slot: in slot
        );
        var driven = (FixedQ4816.FromRawBits(value: Product(
            aBits: FixedQ4816.FractionBitCount,
            aRaw: normalVelocity.Value,
            bBits: massScaleBits,
            bRaw: massScaleRaw,
            outBits: FixedQ4816.FractionBitCount
        )) + velocityBias);

        return (driven, Product(
            aBits: m_options.Scales.Impulse,
            aRaw: slot.NormalImpulseRaw,
            bBits: impulseScaleBits,
            bRaw: impulseScaleRaw,
            outBits: m_options.Scales.Impulse
        ));
    }
    // The normal impulse raw a slot asks for this pass on its own.
    private long NormalDelta(FixedRigidManifold manifold, in FixedPairManifoldSlot slot, in FixedSoftConstraint softness, bool useBias) {
        var (driven, relaxed) = NormalAsk(manifold: manifold, slot: in slot, softness: in softness, useBias: useBias);

        return (-ToImpulse(value: driven, factorRaw: slot.NormalMassRaw) - relaxed);
    }
    // Adds a normal impulse raw to a slot's accumulated impulse, clamped at zero, and applies what the clamp let through.
    private void AccumulateNormal(FixedRigidManifold manifold, ref FixedPairManifoldSlot slot, long deltaRaw) {
        var accumulated = Math.Max(val1: (slot.NormalImpulseRaw + deltaRaw), val2: 0L);
        var applied = (accumulated - slot.NormalImpulseRaw);

        slot.NormalImpulseRaw = accumulated;
        slot.MaxNormalImpulseRaw = Math.Max(val1: slot.MaxNormalImpulseRaw, val2: accumulated);
        manifold.LastStepMovementRaw = Math.Max(val1: manifold.LastStepMovementRaw, val2: Math.Abs(value: applied));

        if (applied != 0L) {
            ApplyImpulse(
                direction: slot.Normal,
                impulseRaw: applied,
                manifold: manifold,
                slot: in slot
            );
        }
    }
    // The common mode of a manifold's points: equal increments at every point, which act as one impulse at their
    // centroid, sized by the centroid's own effective mass from the points' mean ask. It is the exact correction along
    // that direction, so an impact or a warm start's excess shared by a face is removed in one pass without the torque a
    // point-by-point sweep would hand the body, and a body at rest (every ask zero) asks for nothing. It runs only while
    // every point takes part (carries load or asks to push); a point still apart leaves the points to the sweep alone.
    private void SolveCommonMode(FixedRigidManifold manifold, in FixedSoftConstraint softness, bool useBias) {
        if (manifold.CommonModeMassRaw <= 0L) {
            return;
        }

        var drivenSum = 0L;
        var relaxedSum = 0L;
        var count = 0;

        for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
            ref var slot = ref manifold.Slots[index];

            if (
                (slot.Disposition != FixedPairManifoldSlotDisposition.Constraint) ||
                (slot.NormalMassRaw <= 0L)
            ) {
                continue;
            }

            var (driven, relaxed) = NormalAsk(manifold: manifold, slot: in slot, softness: in softness, useBias: useBias);

            if ((slot.NormalImpulseRaw <= 0L) && ((-ToImpulse(value: driven, factorRaw: slot.NormalMassRaw) - relaxed) <= 0L)) {
                return;
            }

            drivenSum += driven.Value;
            relaxedSum += relaxed;
            ++count;
        }

        var share = ((-ToImpulse(value: FixedQ4816.FromRawBits(value: (drivenSum / count)), factorRaw: manifold.CommonModeMassRaw) - relaxedSum) / count);

        for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
            ref var slot = ref manifold.Slots[index];

            if (
                (slot.Disposition == FixedPairManifoldSlotDisposition.Constraint) &&
                (slot.NormalMassRaw > 0L)
            ) {
                AccumulateNormal(deltaRaw: share, manifold: manifold, slot: ref slot);
            }
        }
    }
    // Every iteration of the budget runs unconditionally: an early break driven by a tolerance would make the
    // measurement change the trajectory it measures.
    private void SolveOnce(ReadOnlySpan<FixedRigidManifold> manifolds, bool useBias) {
        foreach (var manifold in manifolds) {
            var softness = ((manifold.BodyA!.IsDynamic && manifold.BodyB!.IsDynamic)
                ? m_dynamicSoftness
                : m_staticSoftness
            );

            SolveCommonMode(manifold: manifold, softness: in softness, useBias: useBias);

            // Each point then reads the velocities the points before it left and applies its impulse at once. The
            // biased pass visits a manifold's points in ascending slot order and the relax pass in descending order, so
            // the first point visited does not keep the larger share of what the common mode left.
            for (var ordinal = 0; (ordinal < FixedPairManifoldSlotTable.Capacity); ++ordinal) {
                var index = (useBias ? ordinal : ((FixedPairManifoldSlotTable.Capacity - 1) - ordinal));
                ref var slot = ref manifold.Slots[index];

                if (
                    (slot.Disposition != FixedPairManifoldSlotDisposition.Constraint) ||
                    (slot.NormalMassRaw <= 0L)
                ) {
                    continue;
                }

                AccumulateNormal(
                    deltaRaw: NormalDelta(manifold: manifold, slot: in slot, softness: in softness, useBias: useBias),
                    manifold: manifold,
                    slot: ref slot
                );
            }
            // Friction and rolling resistance run in the unbiased pass only: a biased push-out is a position correction,
            // not a contact force, and coupling either to it would brake the body by how deep the correction reached.
            if (useBias) {
                continue;
            }

            for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
                ref var slot = ref manifold.Slots[index];

                if (
                    (slot.Disposition == FixedPairManifoldSlotDisposition.Constraint) &&
                    (slot.NormalMassRaw > 0L) &&
                    (slot.TangentMass1Raw > 0L)
                ) {
                    SolveFriction(
                        manifold: manifold,
                        slot: ref slot
                    );
                }

                if (
                    (slot.Disposition == FixedPairManifoldSlotDisposition.Constraint) &&
                    (slot.NormalMassRaw > 0L) &&
                    (slot.RollingMass1Raw > 0L)
                ) {
                    SolveRolling(
                        manifold: manifold,
                        slot: ref slot
                    );
                }
            }
        }
    }
    // Recomposes each constraint slot's tangent impulses onto the world-space carrier that survives to the next step's
    // Prepare — the tangent basis itself is rebuilt from the normal every step.
    private static void StoreFriction(ReadOnlySpan<FixedRigidManifold> manifolds) {
        foreach (var manifold in manifolds) {
            for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
                ref var slot = ref manifold.Slots[index];

                if (slot.Disposition != FixedPairManifoldSlotDisposition.Constraint) {
                    continue;
                }

                slot.FrictionImpulse = ((slot.Tangent1 * FixedQ4816.FromRawBits(value: slot.TangentImpulse1Raw)) +
                                        (slot.Tangent2 * FixedQ4816.FromRawBits(value: slot.TangentImpulse2Raw)));
            }
        }
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
    private void WarmStart(ReadOnlySpan<FixedRigidManifold> manifolds) {
        if (!m_options.WarmStart) {
            return;
        }

        foreach (var manifold in manifolds) {
            for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
                ref var slot = ref manifold.Slots[index];

                if (slot.Disposition != FixedPairManifoldSlotDisposition.Constraint) {
                    continue;
                }

                if (slot.NormalImpulseRaw != 0L) {
                    ApplyImpulse(
                        direction: slot.Normal,
                        impulseRaw: slot.NormalImpulseRaw,
                        manifold: manifold,
                        slot: in slot
                    );
                }

                if (slot.TangentImpulse1Raw != 0L) {
                    ApplyImpulse(
                        direction: slot.Tangent1,
                        impulseRaw: slot.TangentImpulse1Raw,
                        manifold: manifold,
                        slot: in slot
                    );
                }

                if (slot.TangentImpulse2Raw != 0L) {
                    ApplyImpulse(
                        direction: slot.Tangent2,
                        impulseRaw: slot.TangentImpulse2Raw,
                        manifold: manifold,
                        slot: in slot
                    );
                }

                ApplyAngularImpulse(axis: slot.Tangent1, impulseRaw: slot.RollingImpulse1Raw, manifold: manifold);
                ApplyAngularImpulse(axis: slot.Tangent2, impulseRaw: slot.RollingImpulse2Raw, manifold: manifold);
            }
        }
    }
}
