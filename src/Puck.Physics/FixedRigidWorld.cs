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
/// unbiased relax (the only pass that solves friction). Restitution runs once at the end of the step, then each slot's
/// friction impulse is stored back as a world vector. The softness coefficients are formed at the substep width.</para>
/// <para><b>Order.</b> Bodies are integrated in the order the caller lists them and manifolds are solved in the order the
/// caller lists them, each manifold's slots in ascending index order. The caller supplies a canonical order; nothing here
/// reads a hash container.</para>
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

    /// <summary>Initializes a new instance of the <see cref="FixedRigidWorld"/> class bound to one set of options.</summary>
    /// <param name="options">The solver options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rate or substep count is not strictly positive, an iteration
    /// count, speed or tolerance is negative, or a carrier placement is outside <c>[0, 64]</c>.</exception>
    public FixedRigidWorld(FixedRigidSolverOptions options) {
        ArgumentNullException.ThrowIfNull(argument: options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: options.RateHz, paramName: nameof(FixedRigidSolverOptions.RateHz));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: options.SubstepCount, paramName: nameof(FixedRigidSolverOptions.SubstepCount));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.SolveIterations, paramName: nameof(FixedRigidSolverOptions.SolveIterations));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.RelaxIterations, paramName: nameof(FixedRigidSolverOptions.RelaxIterations));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.RestitutionIterations, paramName: nameof(FixedRigidSolverOptions.RestitutionIterations));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.ContactSpeed.Value, paramName: nameof(FixedRigidSolverOptions.ContactSpeed));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.RestitutionThreshold.Value, paramName: nameof(FixedRigidSolverOptions.RestitutionThreshold));
        ArgumentOutOfRangeException.ThrowIfNegative(value: options.ConvergenceToleranceRaw, paramName: nameof(FixedRigidSolverOptions.ConvergenceToleranceRaw));
        ValidateScale(fractionBitCount: options.Scales.InverseMass, paramName: nameof(FixedRigidScales.InverseMass));
        ValidateScale(fractionBitCount: options.Scales.InverseInertia, paramName: nameof(FixedRigidScales.InverseInertia));
        ValidateScale(fractionBitCount: options.Scales.EffectiveMass, paramName: nameof(FixedRigidScales.EffectiveMass));

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
    private void ApplyRestitution(ReadOnlySpan<FixedRigidManifold> manifolds) {
        for (var iteration = 0; (iteration < m_options.RestitutionIterations); ++iteration) {
            foreach (var manifold in manifolds) {
                if (manifold.Restitution <= FixedQ4816.Zero) {
                    continue;
                }

                for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
                    ref var slot = ref manifold.Slots[index];

                    if (
                        (slot.Disposition != FixedPairManifoldSlotDisposition.Constraint) ||
                        (slot.NormalMassRaw <= 0L) ||
                        (slot.TotalNormalImpulseRaw == 0L) ||
                        (slot.RelativeVelocity > (-m_options.RestitutionThreshold))
                    ) {
                        continue;
                    }

                    var normalVelocity = RelativeVelocity(
                        direction: slot.Normal,
                        manifold: manifold,
                        slot: in slot
                    );
                    var target = (normalVelocity + Scale(
                        value: manifold.Restitution,
                        factorRaw: slot.RelativeVelocity.Value,
                        factorBits: FixedQ4816.FractionBitCount
                    ));
                    var delta = -Scale(
                        value: target,
                        factorRaw: slot.NormalMassRaw,
                        factorBits: m_options.Scales.EffectiveMass
                    );
                    var accumulated = Math.Max(
                        val1: (slot.NormalImpulseRaw + delta.Value),
                        val2: 0L
                    );
                    var applied = (accumulated - slot.NormalImpulseRaw);

                    slot.NormalImpulseRaw = accumulated;
                    slot.TotalNormalImpulseRaw += applied;
                    ApplyImpulse(
                        direction: slot.Normal,
                        impulseRaw: applied,
                        manifold: manifold,
                        slot: in slot
                    );
                }
            }
        }
    }
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
            }
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
    private FixedQ4816 Scale(FixedQ4816 value, long factorRaw, int factorBits) {
        if (!FusedArithmetic.TryMixedScaleProduct(
            a: value.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            b: factorRaw,
            fractionBitsB: factorBits,
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
        var delta1 = -Scale(
            value: velocity1,
            factorRaw: slot.TangentMass1Raw,
            factorBits: m_options.Scales.EffectiveMass
        );
        var delta2 = -Scale(
            value: velocity2,
            factorRaw: slot.TangentMass2Raw,
            factorBits: m_options.Scales.EffectiveMass
        );
        var maximum = Scale(
            value: manifold.Friction,
            factorRaw: slot.NormalImpulseRaw,
            factorBits: FixedQ4816.FractionBitCount
        );
        var accumulated = new FixedVector2(
            X: FixedQ4816.FromRawBits(value: (slot.TangentImpulse1Raw + delta1.Value)),
            Y: FixedQ4816.FromRawBits(value: (slot.TangentImpulse2Raw + delta2.Value))
        ).ClampLength(maximum: maximum);
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
    // Every iteration of the budget runs unconditionally: an early break driven by a tolerance would make the
    // measurement change the trajectory it measures.
    private void SolveOnce(ReadOnlySpan<FixedRigidManifold> manifolds, bool useBias) {
        foreach (var manifold in manifolds) {
            var bodyA = manifold.BodyA!;
            var bodyB = manifold.BodyB!;
            var softness = ((bodyA.IsDynamic && bodyB.IsDynamic)
                ? m_dynamicSoftness
                : m_staticSoftness
            );

            for (var index = 0; (index < FixedPairManifoldSlotTable.Capacity); ++index) {
                ref var slot = ref manifold.Slots[index];

                if (
                    (slot.Disposition != FixedPairManifoldSlotDisposition.Constraint) ||
                    (slot.NormalMassRaw <= 0L)
                ) {
                    continue;
                }

                var rotatedA = bodyA.DeltaRotation.Rotate(vector: slot.AnchorA);
                var rotatedB = bodyB.DeltaRotation.Rotate(vector: slot.AnchorB);
                var relativeDisplacement = ((bodyB.DeltaPosition + rotatedB) - (bodyA.DeltaPosition + rotatedA));
                var separation = (slot.BaseSeparation + FixedVector3.Dot(
                    left: relativeDisplacement,
                    right: slot.Normal
                ));
                var velocityBias = FixedQ4816.Zero;
                // The two softness weights are read at their own finer scale rather than narrowed to Q48.16 first; a
                // mass scale as small as 0.0076 keeps only nine significant bits once narrowed.
                var massScaleRaw = FixedQ4816.One.Value;
                var massScaleBits = FixedQ4816.FractionBitCount;
                var impulseScaleRaw = 0L;
                var impulseScaleBits = FixedQ4816.FractionBitCount;

                if (separation > FixedQ4816.Zero) {
                    // Speculative: the constraint may remove exactly the closing speed that would carry the pair past
                    // contact within this substep, and no more, so a first-appearance contact neither tunnels nor stops
                    // short.
                    velocityBias = Scale(
                        value: separation,
                        factorRaw: m_substepRate.Value,
                        factorBits: FixedQ4816.FractionBitCount
                    );
                } else if (useBias) {
                    velocityBias = FixedQ4816.Max(
                        x: SoftScale(
                            softness: in softness,
                            value: separation
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
                var driven = (Scale(
                    factorBits: massScaleBits,
                    factorRaw: massScaleRaw,
                    value: normalVelocity
                ) + velocityBias);
                var delta = (-Scale(
                    value: driven,
                    factorRaw: slot.NormalMassRaw,
                    factorBits: m_options.Scales.EffectiveMass
                ) - Scale(
                    value: FixedQ4816.FromRawBits(value: slot.NormalImpulseRaw),
                    factorRaw: impulseScaleRaw,
                    factorBits: impulseScaleBits
                ));
                var accumulated = Math.Max(
                    val1: (slot.NormalImpulseRaw + delta.Value),
                    val2: 0L
                );
                var applied = (accumulated - slot.NormalImpulseRaw);

                slot.NormalImpulseRaw = accumulated;
                slot.TotalNormalImpulseRaw += applied;
                manifold.LastStepMovementRaw = Math.Max(
                    val1: manifold.LastStepMovementRaw,
                    val2: Math.Abs(value: applied)
                );

                if (applied != 0L) {
                    ApplyImpulse(
                        direction: slot.Normal,
                        impulseRaw: applied,
                        manifold: manifold,
                        slot: in slot
                    );
                }

                // Friction runs in the unbiased pass only: a biased push-out is a position correction, not a contact
                // force, and coupling friction to it would brake the body by how deep the correction reached.
                if (
                    !useBias &&
                    (slot.TangentMass1Raw > 0L)
                ) {
                    SolveFriction(
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
            }
        }
    }
}
