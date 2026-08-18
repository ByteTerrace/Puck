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
public struct FixedPairManifoldSlot : IManifoldSlotEvictionKey {
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

    readonly int IManifoldSlotEvictionKey.LastTouchedStep => LastTouchedStep;
    readonly long IManifoldSlotEvictionKey.NormalImpulseRaw => NormalImpulseRaw;
}
/// <summary>
/// The persistent manifold slots one BODY PAIR carries, and the deterministic association that maps this step's
/// candidates into them. Mirrors <see cref="FixedManifoldSlotTable"/>'s discipline exactly — a fixed-capacity ORDERED
/// array, association and solving both scanning it in index order, eviction by an explicit total key, no hash
/// container anywhere — with <see cref="FixedManifoldSlotTable.Capacity"/>'s per-body cap now read as a per-PAIR cap:
/// a two-body contact owns one of these, not a slice of one shared flat array.
/// </summary>
public sealed class FixedPairManifoldSlotTable {
    /// <summary>The number of slots one pair carries.</summary>
    public const int Capacity = FixedManifoldSlotTable.Capacity;
    /// <summary>The number of steps a slot survives without being associated.</summary>
    public const int IdleStepBudget = FixedManifoldSlotTable.IdleStepBudget;

    private static readonly FixedQ4816 NormalAgreement = FixedQ4816.FromDouble(value: 0.9d);
    private static readonly FixedQ4816 MatchRadiusSquared = FixedQ4816.FromDouble(value: 0.09d);
    private readonly FixedPairManifoldSlot[] m_slots = new FixedPairManifoldSlot[Capacity];
    private readonly bool[] m_claimed = new bool[Capacity];

    /// <summary>Gets the slot at an index.</summary>
    /// <param name="index">The slot index.</param>
    /// <returns>A reference to the slot.</returns>
    public ref FixedPairManifoldSlot this[int index] => ref m_slots[index];

    /// <summary>Gets the number of slots associated with a candidate on the most recent step.</summary>
    public int ActiveCount {
        get {
            var count = 0;

            for (var index = 0; (index < Capacity); ++index) {
                if (m_slots[index].Disposition != FixedPairManifoldSlotDisposition.Idle) {
                    ++count;
                }
            }

            return count;
        }
    }

    private int Evict() {
        var victim = FixedManifoldEviction.SelectVictim(
            capacity: Capacity,
            claimed: m_claimed,
            slots: m_slots
        );

        if (victim < 0) {
            return -1;
        }

        m_slots[victim] = default;

        return victim;
    }
    private int FindFree() {
        for (var index = 0; (index < Capacity); ++index) {
            if (!m_slots[index].Occupied) {
                return index;
            }
        }

        return -1;
    }
    private int FindMatch(FixedTwoBodyContact candidate) {
        var best = -1;
        var bestDistance = FixedQ4816.MaxValue;

        for (var index = 0; (index < Capacity); ++index) {
            ref readonly var slot = ref m_slots[index];

            if (
                !slot.Occupied ||
                m_claimed[index] ||
                (slot.FeatureId != candidate.FeatureId) ||
                (slot.SourceId != candidate.SourceId) ||
                (FixedVector3.Dot(
                left: slot.Normal,
                right: candidate.Normal
            ) < NormalAgreement)
            ) {
                continue;
            }

            var offsetA = (slot.AnchorA - candidate.AnchorA);

            if (
                !offsetA.TryLengthSquared(squaredLength: out var distanceA) ||
                (distanceA > MatchRadiusSquared)
            ) {
                continue;
            }

            var offsetB = (slot.AnchorB - candidate.AnchorB);

            if (
                !offsetB.TryLengthSquared(squaredLength: out var distanceB) ||
                (distanceB > MatchRadiusSquared)
            ) {
                continue;
            }

            var distance = (distanceA + distanceB);

            if (distance < bestDistance) {
                best = index;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>Associates one step's candidates — already filtered to this pair, in the world's canonical order —
    /// into slots.</summary>
    /// <param name="candidates">This pair's candidates for the step, both anchors already normalized to this pair's
    /// own (BodyIdA, BodyIdB) role assignment.</param>
    /// <param name="step">The step ordinal, used for the idle budget and for eviction ordering.</param>
    /// <remarks>Each candidate claims at most one slot and each slot is claimed at most once, both in the order the
    /// candidate list already carries. A candidate with no match takes the lowest free slot; with no free slot it
    /// evicts by the total key <c>(lastTouchedStep, accumulatedImpulse, slotIndex)</c>. When every slot is already
    /// claimed this step, the overflow candidate is dropped rather than destroying a claimed contact.</remarks>
    public void Associate(List<FixedTwoBodyContact> candidates, int step) {
        ArgumentNullException.ThrowIfNull(argument: candidates);
        Array.Clear(array: m_claimed);

        for (var index = 0; (index < Capacity); ++index) {
            m_slots[index].Disposition = FixedPairManifoldSlotDisposition.Idle;
        }

        for (var index = 0; (index < candidates.Count); ++index) {
            var candidate = candidates[index];
            var target = FindMatch(candidate: candidate);
            var retainImpulse = (target >= 0);

            if (target < 0) {
                target = FindFree();
            }

            if (target < 0) {
                target = Evict();
            }

            if (target < 0) {
                continue;
            }

            ref var slot = ref m_slots[target];

            if (!retainImpulse) {
                slot.NormalImpulseRaw = 0L;
            }

            slot.Occupied = true;
            slot.SourceId = candidate.SourceId;
            slot.FeatureId = candidate.FeatureId;
            slot.AnchorA = candidate.AnchorA;
            slot.AnchorB = candidate.AnchorB;
            slot.Normal = candidate.Normal;
            slot.Separation = candidate.Separation;
            slot.TotalNormalImpulseRaw = 0L;
            slot.LastTouchedStep = step;
            slot.Disposition = FixedPairManifoldSlotDisposition.Constraint;
            m_claimed[target] = true;
        }

        for (var index = 0; (index < Capacity); ++index) {
            ref var slot = ref m_slots[index];

            if (
                slot.Occupied &&
                !m_claimed[index] &&
                ((step - slot.LastTouchedStep) > IdleStepBudget)
            ) {
                slot = default;
            }
        }
    }
    /// <summary>Folds every slot's persistent state into a running digest, in slot index order.</summary>
    /// <param name="digest">The running digest.</param>
    /// <param name="step">The current step ordinal, so the folded age is RELATIVE.</param>
    /// <returns>The updated digest.</returns>
    public ulong Fold(ulong digest, int step) {
        for (var index = 0; (index < Capacity); ++index) {
            ref readonly var slot = ref m_slots[index];

            digest = FixedRigidArithmetic.Fold(
                digest: digest,
                value: (slot.Occupied
                ? 1L
                : 0L)
            );
            digest = FixedRigidArithmetic.Fold(
                digest: digest,
                value: slot.SourceId
            );
            digest = FixedRigidArithmetic.Fold(
                digest: digest,
                value: slot.FeatureId
            );
            digest = FixedRigidArithmetic.Fold(
                digest: digest,
                value: slot.NormalImpulseRaw
            );
            digest = FixedRigidArithmetic.Fold(
                digest: digest,
                value: slot.Normal.X.Value
            );
            digest = FixedRigidArithmetic.Fold(
                digest: digest,
                value: slot.Normal.Y.Value
            );
            digest = FixedRigidArithmetic.Fold(
                digest: digest,
                value: slot.Normal.Z.Value
            );
            digest = FixedRigidArithmetic.Fold(
                digest: digest,
                value: ((long)(step - slot.LastTouchedStep))
            );
        }

        return digest;
    }
}
