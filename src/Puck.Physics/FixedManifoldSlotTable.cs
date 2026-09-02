using Puck.Maths;

namespace Puck.Physics;

/// <summary>What a slot is doing this step.</summary>
internal enum FixedManifoldSlotDisposition {
    /// <summary>Not associated with a candidate this step.</summary>
    Idle,
    /// <summary>Solved as an ordinary or speculative contact.</summary>
    Constraint,
    /// <summary>Routed to the bounded extraction path; no impulse is accumulated and none is warm started.</summary>
    Recovery,
}
/// <summary>One persistent manifold slot: the geometry a candidate wrote into it, and the impulse it accumulates
/// across steps.</summary>
internal struct FixedManifoldSlot : IManifoldSlot<FixedContactCandidate> {
    /// <summary>Whether the slot holds a live association.</summary>
    internal bool Occupied;
    /// <summary>The associated candidate's source identity.</summary>
    internal int SourceId;
    /// <summary>The associated candidate's feature identity.</summary>
    internal int FeatureId;
    /// <summary>The contact point relative to the body's centre of mass, in world axes.</summary>
    internal FixedVector3 Anchor;
    /// <summary>The unit contact normal, in world axes.</summary>
    internal FixedVector3 Normal;
    /// <summary>The separation the candidate reported this step.</summary>
    internal FixedQ4816 Separation;
    /// <summary>The separation with the anchor's own normal component removed, so a substep can re-derive the current
    /// separation from the displacement it has accumulated.</summary>
    internal FixedQ4816 BaseSeparation;
    /// <summary>The accumulated normal impulse raw, at Q48.16; this is the warm-start carrier.</summary>
    internal long NormalImpulseRaw;
    /// <summary>The constraint's effective mass raw, at <see cref="FixedRigidScales.EffectiveMass"/>.</summary>
    internal long NormalMassRaw;
    /// <summary>The relative normal velocity captured before the step's first solve, for restitution.</summary>
    internal FixedQ4816 RelativeVelocity;
    /// <summary>The total normal impulse applied within the current step.</summary>
    internal long TotalNormalImpulseRaw;
    /// <summary>The step ordinal the slot was last associated on.</summary>
    internal int LastTouchedStep;
    /// <summary>What the slot is doing this step.</summary>
    internal FixedManifoldSlotDisposition Disposition;
    /// <summary>The accumulated tangential impulse, world-space, at Q48.16 per component — this is the friction
    /// warm-start carrier. Persisted as a world-space vector rather than two scalars against the tangent basis,
    /// because that basis is rebuilt from <see cref="Normal"/> every <c>Prepare()</c> and a scalar pair tied to a
    /// stale basis would silently misapply the moment the normal rotates.</summary>
    internal FixedVector3 FrictionImpulse;
    /// <summary>The first tangent direction, rebuilt every <c>Prepare()</c>; not persisted.</summary>
    internal FixedVector3 Tangent1;
    /// <summary>The second tangent direction, rebuilt every <c>Prepare()</c>; not persisted.</summary>
    internal FixedVector3 Tangent2;
    /// <summary>The coupled tangent effective-mass tensor's <c>(0,0)</c> entry, at
    /// <see cref="FixedRigidScales.EffectiveMass"/>; rebuilt every <c>Prepare()</c>, not persisted.</summary>
    internal long TangentMassXXRaw;
    /// <summary>The coupled tangent effective-mass tensor's <c>(0,1) = (1,0)</c> entry, at
    /// <see cref="FixedRigidScales.EffectiveMass"/>; rebuilt every <c>Prepare()</c>, not persisted.</summary>
    internal long TangentMassXYRaw;
    /// <summary>The coupled tangent effective-mass tensor's <c>(1,1)</c> entry, at
    /// <see cref="FixedRigidScales.EffectiveMass"/>; rebuilt every <c>Prepare()</c>, not persisted.</summary>
    internal long TangentMassYYRaw;
    /// <summary>The per-step working tangential impulse raw along <see cref="Tangent1"/>, at Q48.16; seeded from
    /// <see cref="FrictionImpulse"/> each <c>Prepare()</c> and mutated in place through the relax iterations.</summary>
    internal long TangentImpulseXRaw;
    /// <summary>The per-step working tangential impulse raw along <see cref="Tangent2"/>, at Q48.16.</summary>
    internal long TangentImpulseYRaw;

    readonly bool IManifoldSlotEvictionKey.Occupied => Occupied;
    readonly int IManifoldSlotEvictionKey.LastTouchedStep => LastTouchedStep;
    readonly long IManifoldSlotEvictionKey.NormalImpulseRaw => NormalImpulseRaw;
    readonly int IManifoldSlotState.SourceId => SourceId;
    readonly int IManifoldSlotState.FeatureId => FeatureId;
    readonly FixedVector3 IManifoldSlotState.Normal => Normal;
    readonly bool IManifoldSlotState.IsIdle => (Disposition == FixedManifoldSlotDisposition.Idle);

    void IManifoldSlotState.MarkIdle() {
        Disposition = FixedManifoldSlotDisposition.Idle;
    }
    void IManifoldSlotState.ClearWarmStart() {
        NormalImpulseRaw = 0L;
        FrictionImpulse = FixedVector3.Zero;
    }
    readonly ulong IManifoldSlotState.Fold(ulong digest, int step) {
        digest = FixedRigidArithmetic.Fold(
            digest: digest,
            value: (Occupied
            ? 1L
            : 0L)
        );
        digest = FixedRigidArithmetic.Fold(
            digest: digest,
            value: SourceId
        );
        digest = FixedRigidArithmetic.Fold(
            digest: digest,
            value: FeatureId
        );
        digest = FixedRigidArithmetic.Fold(
            digest: digest,
            value: NormalImpulseRaw
        );
        digest = FixedRigidArithmetic.Fold(
            digest: digest,
            value: FrictionImpulse.X.Value
        );
        digest = FixedRigidArithmetic.Fold(
            digest: digest,
            value: FrictionImpulse.Y.Value
        );
        digest = FixedRigidArithmetic.Fold(
            digest: digest,
            value: FrictionImpulse.Z.Value
        );
        digest = FixedRigidArithmetic.Fold(
            digest: digest,
            value: Normal.X.Value
        );
        digest = FixedRigidArithmetic.Fold(
            digest: digest,
            value: Normal.Y.Value
        );
        digest = FixedRigidArithmetic.Fold(
            digest: digest,
            value: Normal.Z.Value
        );

        return FixedRigidArithmetic.Fold(
            digest: digest,
            value: ((long)(step - LastTouchedStep))
        );
    }
    void IManifoldSlot<FixedContactCandidate>.Claim(in FixedContactCandidate candidate, int step) {
        Occupied = true;
        SourceId = candidate.SourceId;
        FeatureId = candidate.FeatureId;
        Anchor = candidate.Anchor;
        Normal = candidate.Normal;
        Separation = candidate.Separation;
        TotalNormalImpulseRaw = 0L;
        LastTouchedStep = step;
        Disposition = FixedManifoldSlotDisposition.Constraint;
    }
    readonly bool IManifoldSlot<FixedContactCandidate>.TryMatchDistance(in FixedContactCandidate candidate, FixedQ4816 matchRadiusSquared, out FixedQ4816 distanceSquared) {
        var offset = (Anchor - candidate.Anchor);

        return (offset.TryLengthSquared(squaredLength: out distanceSquared) &&
            (distanceSquared <= matchRadiusSquared));
    }
}

/// <summary>
/// The persistent manifold slots one body carries, and the deterministic association that maps this step's candidates
/// into them. The table is a fixed-capacity ORDERED array: association scans it in index order, solving reads it in
/// index order, and eviction picks by an explicit total key. No hash container appears anywhere in the type, so no
/// enumeration order can leak into a result.
/// </summary>
public sealed class FixedManifoldSlotTable {
    /// <summary>The number of slots one body carries.</summary>
    internal const int Capacity = 16;
    /// <summary>The number of steps a slot survives without being associated.</summary>
    internal const int IdleStepBudget = 4;

    private readonly FixedManifoldSlot[] m_slots = new FixedManifoldSlot[Capacity];
    private readonly bool[] m_claimed = new bool[Capacity];

    /// <summary>Gets the slot at an index.</summary>
    /// <param name="index">The slot index.</param>
    /// <returns>A reference to the slot.</returns>
    internal ref FixedManifoldSlot this[int index] => ref m_slots[index];

    /// <summary>Gets the number of slots associated with a candidate on the most recent step.</summary>
    public int ActiveCount => FixedManifoldSlotCore.ActiveCount(
        capacity: Capacity,
        slots: m_slots
    );

    /// <summary>Associates one step's candidates into slots.</summary>
    /// <param name="candidates">The candidates, already canonically ordered when the options ask for it.</param>
    /// <param name="step">The step ordinal, used for the idle budget and for eviction ordering.</param>
    /// <param name="compositeIdentity">Whether a candidate is associated by its composite identity plus geometric
    /// matching. When false, the body feature index alone selects the slot, which is the refuted scheme.</param>
    /// <remarks>Each candidate claims at most one slot and each slot is claimed at most once, both in the order the
    /// candidate list already carries. A candidate with no match takes the lowest free slot; with no free slot it
    /// evicts by the total key <c>(lastTouchedStep, accumulatedImpulse, slotIndex)</c>, never by access time. When
    /// every slot is already claimed this step, the overflow candidate is dropped rather than destroying a claimed
    /// contact and its warm-start impulse.</remarks>
    internal void Associate(List<FixedContactCandidate> candidates, int step, bool compositeIdentity) =>
        FixedManifoldSlotCore.Associate(
            candidates: candidates,
            capacity: Capacity,
            claimed: m_claimed,
            compositeIdentity: compositeIdentity,
            idleStepBudget: IdleStepBudget,
            slots: m_slots,
            step: step
        );
    /// <summary>Folds every slot's persistent state into a running digest, in slot index order.</summary>
    /// <param name="digest">The running digest.</param>
    /// <param name="step">The current step ordinal, so the folded age is RELATIVE (<c>step - LastTouchedStep</c>)
    /// rather than absolute — two runs starting at different step offsets still fold the same age for the same
    /// history.</param>
    /// <returns>The updated digest.</returns>
    internal ulong Fold(ulong digest, int step) => FixedManifoldSlotCore.Fold(
        capacity: Capacity,
        digest: digest,
        slots: m_slots,
        step: step
    );
}
