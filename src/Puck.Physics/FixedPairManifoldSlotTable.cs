using Puck.Maths;

namespace Puck.Physics;

/// <summary>What a pair-manifold slot is doing this step.</summary>
public enum FixedPairManifoldSlotDisposition {
    /// <summary>Not associated with a candidate this step.</summary>
    Idle,
    /// <summary>Solved as an ordinary or speculative contact.</summary>
    Constraint,
}
/// <summary>One persistent manifold slot for a two-body contact: the geometry a candidate wrote into it, and the
/// impulse it accumulates across steps. Both anchors are carried because, unlike a single-body
/// <see cref="FixedManifoldSlot"/>, neither side of a dynamic-dynamic contact is the caller's own absolute frame.</summary>
public struct FixedPairManifoldSlot : IManifoldSlot<FixedTwoBodyContact> {
    /// <summary>Whether the slot holds a live association.</summary>
    public bool Occupied;
    /// <summary>The associated candidate's source identity.</summary>
    public int SourceId;
    /// <summary>The associated candidate's feature identity.</summary>
    public int FeatureId;
    /// <summary>The contact point relative to body A's centre of mass, in world axes.</summary>
    public FixedVector3 AnchorA;
    /// <summary>The contact point relative to body B's centre of mass, in world axes.</summary>
    public FixedVector3 AnchorB;
    /// <summary>The unit contact normal, in world axes, pointing from A toward B.</summary>
    public FixedVector3 Normal;
    /// <summary>The separation the candidate reported this step.</summary>
    public FixedQ4816 Separation;
    /// <summary>The separation with both anchors' own normal components removed, so a substep can re-derive the
    /// current separation from the relative displacement the two bodies have accumulated.</summary>
    public FixedQ4816 BaseSeparation;
    /// <summary>The accumulated normal impulse raw, at Q48.16; this is the warm-start carrier.</summary>
    public long NormalImpulseRaw;
    /// <summary>The constraint's effective mass raw, at <see cref="FixedRigidScales.EffectiveMass"/>.</summary>
    public long NormalMassRaw;
    /// <summary>The relative normal velocity captured before the step's first solve, for restitution.</summary>
    public FixedQ4816 RelativeVelocity;
    /// <summary>The total normal impulse applied within the current step.</summary>
    public long TotalNormalImpulseRaw;
    /// <summary>The step ordinal the slot was last associated on.</summary>
    public int LastTouchedStep;
    /// <summary>What the slot is doing this step.</summary>
    public FixedPairManifoldSlotDisposition Disposition;

    readonly bool IManifoldSlotEvictionKey.Occupied => Occupied;
    readonly int IManifoldSlotEvictionKey.LastTouchedStep => LastTouchedStep;
    readonly long IManifoldSlotEvictionKey.NormalImpulseRaw => NormalImpulseRaw;
    readonly int IManifoldSlotState.SourceId => SourceId;
    readonly int IManifoldSlotState.FeatureId => FeatureId;
    readonly FixedVector3 IManifoldSlotState.Normal => Normal;
    readonly bool IManifoldSlotState.IsIdle => (Disposition == FixedPairManifoldSlotDisposition.Idle);

    void IManifoldSlotState.MarkIdle() {
        Disposition = FixedPairManifoldSlotDisposition.Idle;
    }
    void IManifoldSlotState.ClearWarmStart() {
        NormalImpulseRaw = 0L;
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
    void IManifoldSlot<FixedTwoBodyContact>.Claim(in FixedTwoBodyContact candidate, int step) {
        Occupied = true;
        SourceId = candidate.SourceId;
        FeatureId = candidate.FeatureId;
        AnchorA = candidate.AnchorA;
        AnchorB = candidate.AnchorB;
        Normal = candidate.Normal;
        Separation = candidate.Separation;
        TotalNormalImpulseRaw = 0L;
        LastTouchedStep = step;
        Disposition = FixedPairManifoldSlotDisposition.Constraint;
    }
    readonly bool IManifoldSlot<FixedTwoBodyContact>.TryMatchDistance(in FixedTwoBodyContact candidate, FixedQ4816 matchRadiusSquared, out FixedQ4816 distanceSquared) {
        distanceSquared = default;

        var offsetA = (AnchorA - candidate.AnchorA);

        if (
            !offsetA.TryLengthSquared(squaredLength: out var distanceA) ||
            (distanceA > matchRadiusSquared)
        ) {
            return false;
        }

        var offsetB = (AnchorB - candidate.AnchorB);

        if (
            !offsetB.TryLengthSquared(squaredLength: out var distanceB) ||
            (distanceB > matchRadiusSquared)
        ) {
            return false;
        }

        distanceSquared = (distanceA + distanceB);

        return true;
    }
}
/// <summary>
/// The persistent manifold slots one BODY PAIR carries, and the deterministic association that maps this step's
/// candidates into them. Shares <see cref="FixedManifoldSlotTable"/>'s discipline through
/// <see cref="FixedManifoldSlotCore"/> — a fixed-capacity ORDERED array, association and solving both scanning it in
/// index order, eviction by an explicit total key, no hash container anywhere — with
/// <see cref="FixedManifoldSlotTable.Capacity"/>'s per-body cap now read as a per-PAIR cap: a two-body contact owns
/// one of these, not a slice of one shared flat array.
/// </summary>
public sealed class FixedPairManifoldSlotTable {
    /// <summary>The number of slots one pair carries.</summary>
    public const int Capacity = FixedManifoldSlotTable.Capacity;
    /// <summary>The number of steps a slot survives without being associated.</summary>
    public const int IdleStepBudget = FixedManifoldSlotTable.IdleStepBudget;

    private readonly FixedPairManifoldSlot[] m_slots = new FixedPairManifoldSlot[Capacity];
    private readonly bool[] m_claimed = new bool[Capacity];

    /// <summary>Gets the slot at an index.</summary>
    /// <param name="index">The slot index.</param>
    /// <returns>A reference to the slot.</returns>
    public ref FixedPairManifoldSlot this[int index] => ref m_slots[index];

    /// <summary>Gets the number of slots associated with a candidate on the most recent step.</summary>
    public int ActiveCount => FixedManifoldSlotCore.ActiveCount(
        capacity: Capacity,
        slots: m_slots
    );

    /// <summary>Associates one step's candidates — already filtered to this pair, in the world's canonical order —
    /// into slots.</summary>
    /// <param name="candidates">This pair's candidates for the step, both anchors already normalized to this pair's
    /// own (BodyIdA, BodyIdB) role assignment.</param>
    /// <param name="step">The step ordinal, used for the idle budget and for eviction ordering.</param>
    /// <remarks>Each candidate claims at most one slot and each slot is claimed at most once, both in the order the
    /// candidate list already carries. A candidate with no match takes the lowest free slot; with no free slot it
    /// evicts by the total key <c>(lastTouchedStep, accumulatedImpulse, slotIndex)</c>. When every slot is already
    /// claimed this step, the overflow candidate is dropped rather than destroying a claimed contact.</remarks>
    public void Associate(List<FixedTwoBodyContact> candidates, int step) =>
        FixedManifoldSlotCore.Associate(
            candidates: candidates,
            capacity: Capacity,
            claimed: m_claimed,
            compositeIdentity: true,
            idleStepBudget: IdleStepBudget,
            slots: m_slots,
            step: step
        );
    /// <summary>Folds every slot's persistent state into a running digest, in slot index order.</summary>
    /// <param name="digest">The running digest.</param>
    /// <param name="step">The current step ordinal, so the folded age is RELATIVE.</param>
    /// <returns>The updated digest.</returns>
    public ulong Fold(ulong digest, int step) => FixedManifoldSlotCore.Fold(
        capacity: Capacity,
        digest: digest,
        slots: m_slots,
        step: step
    );
}
