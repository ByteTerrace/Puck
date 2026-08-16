using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// SHAPE ONLY — not wired into any world tick. A multi-body orchestrator over <see cref="FixedTwoBodyKernel"/> and
/// per-pair <see cref="FixedPairManifoldSlotTable"/>s: one <see cref="FixedRigidSolver"/> generalizes to a body, this
/// generalizes to any number of bodies and the pairs among them. It acquires no absolute position, broadphase, or
/// world-schema dependency — candidates arrive caller-supplied, exactly as <see cref="FixedRigidSolver"/>'s already do.
/// </summary>
/// <remarks>
/// <para><b>Body identity.</b> A body's id is assigned at <see cref="AddBody"/> as the NEXT ordinal of an
/// ever-increasing counter, and doubles as its dense-array storage index — there is no separate id-to-index map, and
/// no hash container anywhere in this type. An id is never reused within one instance's lifetime, including after its
/// body is removed: <see cref="RemoveBody"/> tombstones the array slot (sets it to <see langword="null"/>) rather than
/// swap-removing, so removing one body never changes another's storage index mid-tick. The id space is an
/// <see cref="int"/> ordinal — up to <see cref="int.MaxValue"/> bodies over the world's lifetime, effectively
/// unbounded for a real session; growth cost lives in the backing array, never in the id itself.</para>
/// <para><b>Pair storage.</b> Each active body pair owns its OWN <see cref="FixedPairManifoldSlotTable"/> — the same
/// per-body 16-slot cap <see cref="FixedRigidSolver"/> already uses, now read as a per-PAIR cap — rather than one
/// global flat array. The pair registry itself is a dense, canonically-ordered array with a DECLARED
/// <see cref="MaxActivePairs"/> budget: at capacity, a new pair evicts the least-recently-active existing pair, and
/// once every slot is claimed for the step, the overflow pair's candidates are dropped rather than destroying a
/// claimed contact — the same policy <see cref="FixedManifoldSlotTable.Associate"/> already applies at the slot
/// level, one level up.</para>
/// <para><b>Order.</b> Every candidate is canonicalized on the world's own (bodyIdMin, bodyIdMax, source, feature,
/// normal, separation, anchorA, anchorB) key before association. Bodies are integrated in ascending id order; pairs
/// are solved in ascending registry-index order, and each pair's own slots in ascending slot-index order.</para>
/// </remarks>
public sealed class FixedRigidWorld {
    /// <summary>The default declared budget on simultaneously active body pairs.</summary>
    public const int DefaultMaxActivePairs = 64;

    private readonly FixedQ4816 m_doubleSubstepRate;
    private readonly FixedSoftConstraint m_dynamicSoftness;
    private readonly FixedRigidSolverOptions m_options;
    private readonly long[] m_pairStepMovement;
    private readonly PairRecord[] m_pairs;
    private readonly FixedSoftConstraint m_staticSoftness;
    private readonly FixedQ4816 m_substepRate;
    private readonly FixedVector3 m_substepVelocityDelta;

    private FixedRigidBody?[] m_bodies = new FixedRigidBody?[16];
    private int m_bodyCount;

    /// <summary>Creates a world bound to one set of options.</summary>
    /// <param name="options">The solver options, shared with the pair kernels.</param>
    /// <param name="maxActivePairs">The declared budget on simultaneously active body pairs, strictly positive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxActivePairs"/> is not strictly positive.</exception>
    public FixedRigidWorld(FixedRigidSolverOptions options, int maxActivePairs = DefaultMaxActivePairs) {
        ArgumentNullException.ThrowIfNull(argument: options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: maxActivePairs);

        var substepRate = (((long)options.RateHz) * options.SubstepCount);

        m_options = options;
        // The static-side softness is stiffer and less damped than the ordinary contact softness — 2x the contact
        // hertz, 0.5x the damping ratio — matching a contact touching an immovable body needing to converge faster
        // since it carries none of the give a dynamic-dynamic pair's shared compliance provides.
        m_dynamicSoftness = FixedSoftConstraint.Create(
            rateHz: options.RateHz,
            substepCount: options.SubstepCount,
            hertz: options.ContactHertz,
            dampingRatio: options.ContactDampingRatio,
            fractionBitCount: FixedSoftConstraint.DefaultFractionBitCount
        );
        m_staticSoftness = FixedSoftConstraint.Create(
            rateHz: options.RateHz,
            substepCount: options.SubstepCount,
            hertz: (options.ContactHertz * FixedQ4816.FromInteger(value: 2L)),
            dampingRatio: (options.ContactDampingRatio * FixedQ4816.FromDouble(value: 0.5d)),
            fractionBitCount: FixedSoftConstraint.DefaultFractionBitCount
        );
        m_substepRate = FixedQ4816.FromInteger(value: substepRate);
        m_doubleSubstepRate = FixedQ4816.FromInteger(value: (2L * substepRate));
        m_substepVelocityDelta = ((options.Gravity + options.AppliedAcceleration) / m_substepRate);
        m_pairs = new PairRecord[maxActivePairs];
        m_pairStepMovement = new long[maxActivePairs];

        for (var index = 0; (index < maxActivePairs); ++index) {
            m_pairs[index] = new();
        }
    }

    /// <summary>Gets the number of ids assigned so far, including tombstoned ones — the exclusive upper bound on a
    /// live id.</summary>
    public int BodyCount => m_bodyCount;
    /// <summary>Gets the number of pair candidates the most recent step dropped because every pair slot was already
    /// claimed — a measured overflow count, never a silent one.</summary>
    public int LastStepDroppedPairCount { get; private set; }
    /// <summary>Gets the number of active pair-manifolds whose largest applied-impulse movement the most recent step
    /// left at or below <see cref="FixedRigidSolverOptions.ConvergenceToleranceRaw"/>. A READING toward a future
    /// sleeping/islands decision — never a control; every pair still runs its full iteration budget regardless of
    /// this count.</summary>
    public int LastStepQuiescentPairCount { get; private set; }
    /// <summary>Gets the declared budget on simultaneously active body pairs.</summary>
    public int MaxActivePairs => m_pairs.Length;
    /// <summary>Gets the number of kernel refusals the world has counted; a healthy fixture ends at zero.</summary>
    public int RefusalCount { get; private set; }

    private void ApplyRestitution() {
        if (m_options.Restitution == FixedQ4816.Zero) {
            return;
        }

        for (var iteration = 0; (iteration < m_options.RestitutionIterations); ++iteration) {
            for (var index = 0; (index < m_pairs.Length); ++index) {
                var pair = m_pairs[index];

                if (!pair.Occupied) {
                    continue;
                }

                var bodyA = m_bodies[pair.BodyIdMin];
                var bodyB = m_bodies[pair.BodyIdMax];

                if (
                    (bodyA is null) ||
                    (bodyB is null)
                ) {
                    continue;
                }

                for (var slotIndex = 0; (slotIndex < FixedPairManifoldSlotTable.Capacity); ++slotIndex) {
                    ref var slot = ref pair.Slots[slotIndex];

                    if (
                        (slot.Disposition != FixedPairManifoldSlotDisposition.Constraint) ||
                        (slot.NormalMassRaw <= 0L) ||
                        (slot.TotalNormalImpulseRaw == 0L) ||
                        (slot.RelativeVelocity > (-m_options.RestitutionThreshold))
                    ) {
                        continue;
                    }

                    var normalVelocity = FixedTwoBodyKernel.RelativeNormalVelocity(
                        anchorA: slot.AnchorA,
                        anchorB: slot.AnchorB,
                        bodyA: bodyA,
                        bodyB: bodyB,
                        normal: slot.Normal
                    );
                    var target = (normalVelocity + Scale(
                        value: m_options.Restitution,
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

                    var refusals = 0;

                    FixedTwoBodyKernel.ApplyImpulse(
                        bodyA: bodyA,
                        anchorA: slot.AnchorA,
                        bodyB: bodyB,
                        anchorB: slot.AnchorB,
                        normal: slot.Normal,
                        impulseRaw: applied,
                        scales: m_options.Scales,
                        refusals: ref refusals
                    );
                    RefusalCount += refusals;
                }
            }
        }
    }
    private void AssociatePairs(List<FixedTwoBodyContact> candidates, int step) {
        LastStepDroppedPairCount = 0;

        var bucket = new List<FixedTwoBodyContact>();
        var index = 0;

        while (index < candidates.Count) {
            var min = candidates[index].BodyIdA;
            var max = candidates[index].BodyIdB;

            bucket.Clear();

            while (
                (index < candidates.Count) &&
                (candidates[index].BodyIdA == min) &&
                (candidates[index].BodyIdB == max)
            ) {
                bucket.Add(item: candidates[index]);
                ++index;
            }

            var pair = FindPair(
                max: max,
                min: min
            );

            if (pair is null) {
                pair = FindFreePair();
            }

            if (pair is null) {
                pair = EvictPair();
            }

            if (pair is null) {
                LastStepDroppedPairCount += bucket.Count;

                continue;
            }

            pair.Occupied = true;
            pair.BodyIdMin = min;
            pair.BodyIdMax = max;
            pair.LastActiveStep = step;
            pair.Slots.Associate(
                candidates: bucket,
                step: step
            );
        }
    }
    private PairRecord? EvictPair() {
        PairRecord? victim = null;

        for (var index = 0; (index < m_pairs.Length); ++index) {
            var candidate = m_pairs[index];

            if (
                (victim is null) ||
                (candidate.LastActiveStep < victim.LastActiveStep)
            ) {
                victim = candidate;
            }
        }

        return victim;
    }
    private PairRecord? FindFreePair() {
        for (var index = 0; (index < m_pairs.Length); ++index) {
            if (!m_pairs[index].Occupied) {
                return m_pairs[index];
            }
        }

        return null;
    }
    private PairRecord? FindPair(int min, int max) {
        for (var index = 0; (index < m_pairs.Length); ++index) {
            var pair = m_pairs[index];

            if (
                pair.Occupied &&
                (pair.BodyIdMin == min) &&
                (pair.BodyIdMax == max)
            ) {
                return pair;
            }
        }

        return null;
    }
    // Every candidate is re-expressed with BodyIdA == the pair's canonical minimum id, so every candidate landing in
    // the same pair's bucket agrees on which anchor is A and which is B. Swapping which body is A flips which body
    // the normal points FROM, so the normal is negated along with the swap to preserve its physical meaning.
    private static void NormalizeRoles(List<FixedTwoBodyContact> candidates) {
        for (var index = 0; (index < candidates.Count); ++index) {
            var candidate = candidates[index];

            if (candidate.BodyIdA > candidate.BodyIdB) {
                candidates[index] = candidate with {
                    BodyIdA = candidate.BodyIdB,
                    BodyIdB = candidate.BodyIdA,
                    AnchorA = candidate.AnchorB,
                    AnchorB = candidate.AnchorA,
                    Normal = -candidate.Normal,
                };
            }
        }
    }
    private void Prepare() {
        for (var index = 0; (index < m_pairs.Length); ++index) {
            var pair = m_pairs[index];

            if (!pair.Occupied) {
                continue;
            }

            var bodyA = m_bodies[pair.BodyIdMin];
            var bodyB = m_bodies[pair.BodyIdMax];

            if (
                (bodyA is null) ||
                (bodyB is null)
            ) {
                continue;
            }

            for (var slotIndex = 0; (slotIndex < FixedPairManifoldSlotTable.Capacity); ++slotIndex) {
                ref var slot = ref pair.Slots[slotIndex];

                if (slot.Disposition != FixedPairManifoldSlotDisposition.Constraint) {
                    continue;
                }

                slot.BaseSeparation = (slot.Separation - FixedVector3.Dot(
                    left: (slot.AnchorB - slot.AnchorA),
                    right: slot.Normal
                ));

                if (!m_options.WarmStart) {
                    slot.NormalImpulseRaw = 0L;
                }

                var refusals = 0;

                _ = FixedTwoBodyKernel.TryEffectiveMass(
                    bodyA: bodyA,
                    anchorA: slot.AnchorA,
                    bodyB: bodyB,
                    anchorB: slot.AnchorB,
                    normal: slot.Normal,
                    scales: m_options.Scales,
                    normalMassRaw: out var normalMass,
                    refusals: ref refusals
                );
                RefusalCount += refusals;
                slot.NormalMassRaw = normalMass;
                slot.RelativeVelocity = FixedTwoBodyKernel.RelativeNormalVelocity(
                    anchorA: slot.AnchorA,
                    anchorB: slot.AnchorB,
                    bodyA: bodyA,
                    bodyB: bodyB,
                    normal: slot.Normal
                );
            }
        }
    }
    private void RecordQuiescence() {
        var quiescent = 0;

        for (var index = 0; (index < m_pairs.Length); ++index) {
            if (
                m_pairs[index].Occupied &&
                (m_pairStepMovement[index] <= m_options.ConvergenceToleranceRaw)
            ) {
                ++quiescent;
            }
        }

        LastStepQuiescentPairCount = quiescent;
    }
    private void RunIterations(int iterations, bool useBias) {
        for (var iteration = 0; (iteration < iterations); ++iteration) {
            SolveOnce(useBias: useBias);
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
    private FixedQ4816 SoftScale(long first, long second, FixedQ4816 value, int fractionBitCount) {
        if (!FusedArithmetic.TryMixedScaleProduct(
            a: first,
            fractionBitsA: fractionBitCount,
            b: second,
            fractionBitsB: fractionBitCount,
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
    private void SolveOnce(bool useBias) {
        for (var index = 0; (index < m_pairs.Length); ++index) {
            var pair = m_pairs[index];

            if (!pair.Occupied) {
                continue;
            }

            var bodyA = m_bodies[pair.BodyIdMin];
            var bodyB = m_bodies[pair.BodyIdMax];

            if (
                (bodyA is null) ||
                (bodyB is null)
            ) {
                continue;
            }

            var softness = ((bodyA.IsDynamic && bodyB.IsDynamic)
                ? m_dynamicSoftness
                : m_staticSoftness
            );

            for (var slotIndex = 0; (slotIndex < FixedPairManifoldSlotTable.Capacity); ++slotIndex) {
                ref var slot = ref pair.Slots[slotIndex];

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
                        first: softness.MassScaleRaw,
                        second: softness.BiasRateRaw,
                        value: separation,
                        fractionBitCount: softness.FractionBitCount
                    );

                    velocityBias = FixedQ4816.Max(
                        x: soft,
                        y: (-m_options.ContactSpeed)
                    );
                    massScaleRaw = softness.MassScaleRaw;
                    massScaleBits = softness.FractionBitCount;
                    impulseScaleRaw = softness.ImpulseScaleRaw;
                    impulseScaleBits = softness.FractionBitCount;
                }

                var normalVelocity = FixedTwoBodyKernel.RelativeNormalVelocity(
                    anchorA: slot.AnchorA,
                    anchorB: slot.AnchorB,
                    bodyA: bodyA,
                    bodyB: bodyB,
                    normal: slot.Normal
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
                )
                    - Scale(
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

                var movement = Math.Abs(value: applied);

                if (movement > m_pairStepMovement[index]) {
                    m_pairStepMovement[index] = movement;
                }

                var refusals = 0;

                FixedTwoBodyKernel.ApplyImpulse(
                    bodyA: bodyA,
                    anchorA: slot.AnchorA,
                    bodyB: bodyB,
                    anchorB: slot.AnchorB,
                    normal: slot.Normal,
                    impulseRaw: applied,
                    scales: m_options.Scales,
                    refusals: ref refusals
                );
                RefusalCount += refusals;
            }
        }
    }
    private void WarmStart() {
        if (!m_options.WarmStart) {
            return;
        }

        for (var index = 0; (index < m_pairs.Length); ++index) {
            var pair = m_pairs[index];

            if (!pair.Occupied) {
                continue;
            }

            var bodyA = m_bodies[pair.BodyIdMin];
            var bodyB = m_bodies[pair.BodyIdMax];

            if (
                (bodyA is null) ||
                (bodyB is null)
            ) {
                continue;
            }

            for (var slotIndex = 0; (slotIndex < FixedPairManifoldSlotTable.Capacity); ++slotIndex) {
                ref var slot = ref pair.Slots[slotIndex];

                if (
                    (slot.Disposition != FixedPairManifoldSlotDisposition.Constraint) ||
                    (slot.NormalImpulseRaw == 0L)
                ) {
                    continue;
                }

                var refusals = 0;

                FixedTwoBodyKernel.ApplyImpulse(
                    bodyA: bodyA,
                    anchorA: slot.AnchorA,
                    bodyB: bodyB,
                    anchorB: slot.AnchorB,
                    normal: slot.Normal,
                    impulseRaw: slot.NormalImpulseRaw,
                    scales: m_options.Scales,
                    refusals: ref refusals
                );
                RefusalCount += refusals;
            }
        }
    }

    /// <summary>Adds a body and returns its id.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The assigned id — the same value for the whole lifetime of this world instance, never reused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
    public int AddBody(FixedRigidBody body) {
        ArgumentNullException.ThrowIfNull(argument: body);

        var id = m_bodyCount;

        if (id >= m_bodies.Length) {
            Array.Resize(
                array: ref m_bodies,
                newSize: (m_bodies.Length * 2)
            );
        }

        m_bodies[id] = body;
        ++m_bodyCount;

        return id;
    }
    /// <summary>Gets the body at an id, or <see langword="null"/> when the id is out of range or tombstoned.</summary>
    /// <param name="id">The body id.</param>
    /// <returns>The body, or <see langword="null"/>.</returns>
    public FixedRigidBody? GetBody(int id) =>
        (((id >= 0) && (id < m_bodyCount))
            ? m_bodies[id]
            : null
        );
    /// <summary>Gets the pair-manifold slot table for an active pair, or <see langword="null"/> when the pair is not
    /// currently tracked.</summary>
    /// <param name="bodyIdA">One body id.</param>
    /// <param name="bodyIdB">The other body id.</param>
    /// <returns>The pair's slot table, or <see langword="null"/>.</returns>
    public FixedPairManifoldSlotTable? GetPairSlots(int bodyIdA, int bodyIdB) {
        var min = Math.Min(
            val1: bodyIdA,
            val2: bodyIdB
        );
        var max = Math.Max(
            val1: bodyIdA,
            val2: bodyIdB
        );

        for (var index = 0; (index < m_pairs.Length); ++index) {
            var pair = m_pairs[index];

            if (
                pair.Occupied &&
                (pair.BodyIdMin == min) &&
                (pair.BodyIdMax == max)
            ) {
                return pair.Slots;
            }
        }

        return null;
    }
    /// <summary>Tombstones a body's storage slot. The id is never reassigned, and no other body's id or index moves.</summary>
    /// <param name="id">The body id.</param>
    public void RemoveBody(int id) {
        if (
            (id >= 0) &&
            (id < m_bodyCount)
        ) {
            m_bodies[id] = null;
        }
    }
    /// <summary>Advances every dynamic body by one step against one step's contact candidates.</summary>
    /// <param name="candidates">This step's candidates, canonicalized in place.</param>
    /// <param name="step">The step ordinal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> is <see langword="null"/>.</exception>
    public void Step(List<FixedTwoBodyContact> candidates, int step) {
        ArgumentNullException.ThrowIfNull(argument: candidates);

        FixedTwoBodyContact.Canonicalize(candidates: candidates);
        NormalizeRoles(candidates: candidates);
        AssociatePairs(
            candidates: candidates,
            step: step
        );

        for (var id = 0; (id < m_bodyCount); ++id) {
            m_bodies[id]?.ResetStepAccumulators();
        }

        Array.Clear(array: m_pairStepMovement);
        Prepare();

        for (var substep = 0; (substep < m_options.SubstepCount); ++substep) {
            for (var id = 0; (id < m_bodyCount); ++id) {
                var body = m_bodies[id];

                if (
                    (body is not null) &&
                    body.IsDynamic
                ) {
                    body.LinearVelocity += m_substepVelocityDelta;
                }
            }

            WarmStart();
            RunIterations(
                iterations: m_options.SolveIterations,
                useBias: true
            );

            for (var id = 0; (id < m_bodyCount); ++id) {
                var body = m_bodies[id];

                if (
                    (body is not null) &&
                    body.IsDynamic
                ) {
                    body.DeltaPosition += (body.LinearVelocity / m_substepRate);
                    body.DeltaRotation = (FixedQuaternion.Exp(bivector: (body.AngularVelocity / m_doubleSubstepRate)) * body.DeltaRotation).Normalize();
                }
            }

            RunIterations(
                iterations: m_options.RelaxIterations,
                useBias: false
            );
        }

        ApplyRestitution();
        RecordQuiescence();
    }

    // One table per active pair — never a slice of one flat global array — with the same 16-slot cap
    // FixedManifoldSlotTable already uses per body, now read as a per-pair cap.
    private sealed class PairRecord {
        internal int BodyIdMax;
        internal int BodyIdMin;
        internal bool Occupied;

        internal int LastActiveStep = int.MinValue;
        internal FixedPairManifoldSlotTable Slots { get; } = new();
    }
}
