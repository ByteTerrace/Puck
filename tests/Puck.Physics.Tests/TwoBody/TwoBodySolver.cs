using Puck.Maths;

namespace Puck.Physics.Tests.TwoBody;

/// <summary>
/// Test-only measurement scaffolding, not production code. One persistent contact between two named bodies in a
/// <see cref="TwoBodySolver"/> chain — fixed topology (no candidate generation, no manifold-slot association), because
/// the precision-floor measurement needs a stable, reproducible two-dynamic-body geometry, not a re-proof of the
/// existing single-body candidate-order and warm-start laws.
/// </summary>
/// <param name="BodyA">Index into the solver's body array; the normal points away from this body.</param>
/// <param name="BodyB">Index into the solver's body array; the normal points toward this body.</param>
/// <param name="AnchorA">The contact point relative to body A's centre of mass, in A's REST orientation's world axes.</param>
/// <param name="AnchorB">The contact point relative to body B's centre of mass, in B's REST orientation's world axes.</param>
/// <param name="Normal">The unit contact normal, fixed for the fixture's lifetime (no re-generation).</param>
/// <param name="RestSeparation">The signed gap at zero displacement.</param>
internal sealed class TwoBodyContact(int BodyA, int BodyB, FixedVector3 AnchorA, FixedVector3 AnchorB, FixedVector3 Normal, FixedQ4816 RestSeparation) {
    internal int BodyAIndex { get; } = BodyA;
    internal int BodyBIndex { get; } = BodyB;
    internal FixedVector3 AnchorAValue { get; } = AnchorA;
    internal FixedVector3 AnchorBValue { get; } = AnchorB;
    internal FixedVector3 NormalValue { get; } = Normal;
    internal FixedQ4816 BaseSeparation { get; } =
        (RestSeparation - FixedVector3.Dot(
        left: (AnchorB - AnchorA),
        right: Normal
    ));

    internal long NormalImpulseRaw { get; set; }
    internal long NormalMassRaw { get; set; }
    internal FixedQ4816 RelativeVelocity { get; set; }
}
/// <summary>
/// Test-only measurement scaffolding, not production code. A sequential-impulse solver over a fixed, explicitly ordered list of
/// <see cref="TwoBodyContact"/>s among any number of bodies (one of which may be static), reusing
/// <see cref="FixedSoftConstraint"/>, <see cref="TwoBodyDynamics"/> and the same substep sequence
/// <see cref="FixedRigidSolver"/> uses (integrate, warm start, biased solve, integrate positions, relax).
/// </summary>
internal sealed class TwoBodySolver {
    private readonly FixedQ4816 m_doubleSubstepRate;
    private readonly FixedRigidSolverOptions m_options;
    private readonly FixedSoftConstraint m_softness;
    private readonly FixedQ4816 m_substepRate;
    private readonly FixedVector3 m_substepVelocityDelta;

    internal TwoBodySolver(FixedRigidSolverOptions options) {
        ArgumentNullException.ThrowIfNull(argument: options);

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
    }

    /// <summary>Gets the largest accumulated normal impulse raw seen at the end of the most recent step.</summary>
    internal long LastStepMaximumImpulseRaw { get; private set; }
    /// <summary>Gets the smallest positive effective-mass raw seen while preparing the most recent step; zero when no
    /// contact had a positive effective mass.</summary>
    internal long LastStepMinimumNormalMassRaw { get; private set; }
    /// <summary>Gets the number of kernel refusals counted since construction.</summary>
    internal int RefusalCount { get; private set; }

    /// <summary>Advances every dynamic body in <paramref name="bodies"/> by one step against the fixed
    /// <paramref name="contacts"/> list, in list order.</summary>
    internal void Step(FixedRigidBody[] bodies, IReadOnlyList<TwoBodyContact> contacts, int step) {
        ArgumentNullException.ThrowIfNull(argument: bodies);
        ArgumentNullException.ThrowIfNull(argument: contacts);

        foreach (var body in bodies) {
            body.ResetStepAccumulators();
        }

        Prepare(
            bodies: bodies,
            contacts: contacts
        );

        for (var substep = 0; (substep < m_options.SubstepCount); ++substep) {
            foreach (var body in bodies) {
                if (body.IsDynamic) {
                    body.LinearVelocity += m_substepVelocityDelta;
                }
            }

            WarmStart(
                bodies: bodies,
                contacts: contacts
            );
            RunIterations(
                bodies: bodies,
                contacts: contacts,
                iterations: m_options.SolveIterations,
                useBias: true
            );
            IntegratePositions(bodies: bodies);
            RunIterations(
                bodies: bodies,
                contacts: contacts,
                iterations: m_options.RelaxIterations,
                useBias: false
            );
        }

        RecordStepMaxima(contacts: contacts);
    }

    private void IntegratePositions(FixedRigidBody[] bodies) {
        foreach (var body in bodies) {
            if (!body.IsDynamic) {
                continue;
            }

            body.DeltaPosition += (body.LinearVelocity / m_substepRate);
            body.DeltaRotation = (FixedQuaternion.Exp(bivector: (body.AngularVelocity / m_doubleSubstepRate)) * body.DeltaRotation).Normalize();
        }
    }
    private void Prepare(FixedRigidBody[] bodies, IReadOnlyList<TwoBodyContact> contacts) {
        LastStepMinimumNormalMassRaw = long.MaxValue;

        foreach (var contact in contacts) {
            var bodyA = bodies[contact.BodyAIndex];
            var bodyB = bodies[contact.BodyBIndex];

            if (!m_options.WarmStart) {
                contact.NormalImpulseRaw = 0L;
            }

            var refusals = 0;

            _ = TwoBodyDynamics.TryEffectiveMass(
                bodyA: bodyA,
                anchorA: contact.AnchorAValue,
                bodyB: bodyB,
                anchorB: contact.AnchorBValue,
                normal: contact.NormalValue,
                scales: m_options.Scales,
                normalMassRaw: out var normalMass,
                refusals: ref refusals
            );
            RefusalCount += refusals;
            contact.NormalMassRaw = normalMass;
            contact.RelativeVelocity = TwoBodyDynamics.RelativeNormalVelocity(
                bodyA: bodyA,
                anchorA: contact.AnchorAValue,
                bodyB: bodyB,
                anchorB: contact.AnchorBValue,
                normal: contact.NormalValue
            );

            if (
                (contact.NormalMassRaw > 0L) &&
                (contact.NormalMassRaw < LastStepMinimumNormalMassRaw)
            ) {
                LastStepMinimumNormalMassRaw = contact.NormalMassRaw;
            }
        }

        if (LastStepMinimumNormalMassRaw == long.MaxValue) {
            LastStepMinimumNormalMassRaw = 0L;
        }
    }
    private void RecordStepMaxima(IReadOnlyList<TwoBodyContact> contacts) {
        LastStepMaximumImpulseRaw = 0L;

        foreach (var contact in contacts) {
            if (contact.NormalImpulseRaw > LastStepMaximumImpulseRaw) {
                LastStepMaximumImpulseRaw = contact.NormalImpulseRaw;
            }
        }
    }
    private void RunIterations(FixedRigidBody[] bodies, IReadOnlyList<TwoBodyContact> contacts, int iterations, bool useBias) {
        for (var iteration = 0; (iteration < iterations); ++iteration) {
            SolveOnce(
                bodies: bodies,
                contacts: contacts,
                useBias: useBias
            );
        }
    }
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
    private void SolveOnce(FixedRigidBody[] bodies, IReadOnlyList<TwoBodyContact> contacts, bool useBias) {
        foreach (var contact in contacts) {
            if (contact.NormalMassRaw <= 0L) {
                continue;
            }

            var bodyA = bodies[contact.BodyAIndex];
            var bodyB = bodies[contact.BodyBIndex];
            var rotatedA = bodyA.DeltaRotation.Rotate(vector: contact.AnchorAValue);
            var rotatedB = bodyB.DeltaRotation.Rotate(vector: contact.AnchorBValue);
            var relativeDisplacement = ((bodyB.DeltaPosition + rotatedB) - (bodyA.DeltaPosition + rotatedA));
            var separation = (contact.BaseSeparation + FixedVector3.Dot(
                left: relativeDisplacement,
                right: contact.NormalValue
            ));
            var velocityBias = FixedQ4816.Zero;
            var massScaleRaw = FixedQ4816.One.Value;
            var massScaleBits = FixedQ4816.FractionBitCount;
            var impulseScaleRaw = 0L;
            var impulseScaleBits = FixedQ4816.FractionBitCount;

            if (separation > FixedQ4816.Zero) {
                velocityBias = Scale(
                    value: separation,
                    factorRaw: m_substepRate.Value,
                    factorBits: FixedQ4816.FractionBitCount
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

            var normalVelocity = TwoBodyDynamics.RelativeNormalVelocity(
                bodyA: bodyA,
                anchorA: contact.AnchorAValue,
                bodyB: bodyB,
                anchorB: contact.AnchorBValue,
                normal: contact.NormalValue
            );
            var driven = (Scale(
                factorBits: massScaleBits,
                factorRaw: massScaleRaw,
                value: normalVelocity
            ) + velocityBias);
            var delta = (-Scale(
                value: driven,
                factorRaw: contact.NormalMassRaw,
                factorBits: m_options.Scales.EffectiveMass
            )
                - Scale(
                value: FixedQ4816.FromRawBits(value: contact.NormalImpulseRaw),
                factorRaw: impulseScaleRaw,
                factorBits: impulseScaleBits
            ));
            var accumulated = Math.Max(
                val1: (contact.NormalImpulseRaw + delta.Value),
                val2: 0L
            );
            var applied = (accumulated - contact.NormalImpulseRaw);

            contact.NormalImpulseRaw = accumulated;

            var refusals = 0;

            TwoBodyDynamics.ApplyImpulse(
                bodyA: bodyA,
                anchorA: contact.AnchorAValue,
                bodyB: bodyB,
                anchorB: contact.AnchorBValue,
                normal: contact.NormalValue,
                impulseRaw: applied,
                scales: m_options.Scales,
                refusals: ref refusals
            );
            RefusalCount += refusals;
        }
    }
    private void WarmStart(FixedRigidBody[] bodies, IReadOnlyList<TwoBodyContact> contacts) {
        if (!m_options.WarmStart) {
            return;
        }

        foreach (var contact in contacts) {
            if (contact.NormalImpulseRaw == 0L) {
                continue;
            }

            var refusals = 0;

            TwoBodyDynamics.ApplyImpulse(
                bodyA: bodies[contact.BodyAIndex],
                anchorA: contact.AnchorAValue,
                bodyB: bodies[contact.BodyBIndex],
                anchorB: contact.AnchorBValue,
                normal: contact.NormalValue,
                impulseRaw: contact.NormalImpulseRaw,
                scales: m_options.Scales,
                refusals: ref refusals
            );
            RefusalCount += refusals;
        }
    }
}
