using Puck.Maths;

namespace Puck.Physics;

/// <summary>What a pair-manifold slot is doing this step.</summary>
public enum FixedPairManifoldSlotDisposition {
    /// <summary>The slot is not associated with a candidate this step.</summary>
    Idle,
    /// <summary>The slot is solved as an ordinary or speculative contact.</summary>
    Constraint,
}
/// <summary>One persistent manifold slot for a two-body contact: the geometry a candidate wrote into it, the impulses it
/// accumulates across steps, and the per-step terms the solver derives from both. The persistent part — occupancy,
/// identity, anchors, normal, the normal and friction impulses and the idle count — is what a checkpoint carries
/// (<see cref="IsPersistentStateEqual"/> names it); every other field is re-derived before it is read.</summary>
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
    /// <summary>The accumulated normal impulse raw, at Q48.16; the normal warm-start carrier.</summary>
    public long NormalImpulseRaw;
    /// <summary>The accumulated friction impulse in world axes; the friction warm-start carrier, kept as a world vector
    /// because the tangent basis is rebuilt from <see cref="Normal"/> every step.</summary>
    public FixedVector3 FrictionImpulse;
    /// <summary>The associations the slot has missed since it was last claimed.</summary>
    public int IdleSteps;
    /// <summary>The separation the candidate reported this step.</summary>
    public FixedQ4816 Separation;
    /// <summary>The separation with both anchors' own normal components removed, so a substep can re-derive the
    /// current separation from the relative displacement the two bodies have accumulated.</summary>
    public FixedQ4816 BaseSeparation;
    /// <summary>The constraint's effective mass raw, at <see cref="FixedRigidScales.EffectiveMass"/>.</summary>
    public long NormalMassRaw;
    /// <summary>The relative normal velocity captured before the step's first solve, for restitution.</summary>
    public FixedQ4816 RelativeVelocity;
    /// <summary>The total normal impulse applied within the current step.</summary>
    public long TotalNormalImpulseRaw;
    /// <summary>The first friction direction, orthogonal to <see cref="Normal"/>, for the current step.</summary>
    public FixedVector3 Tangent1;
    /// <summary>The second friction direction, orthogonal to <see cref="Normal"/> and <see cref="Tangent1"/>.</summary>
    public FixedVector3 Tangent2;
    /// <summary>The effective mass raw along <see cref="Tangent1"/>, at <see cref="FixedRigidScales.EffectiveMass"/>;
    /// zero disables friction on the slot this step.</summary>
    public long TangentMass1Raw;
    /// <summary>The effective mass raw along <see cref="Tangent2"/>, at <see cref="FixedRigidScales.EffectiveMass"/>.</summary>
    public long TangentMass2Raw;
    /// <summary>The accumulated friction impulse raw along <see cref="Tangent1"/>, at Q48.16, for the current step.</summary>
    public long TangentImpulse1Raw;
    /// <summary>The accumulated friction impulse raw along <see cref="Tangent2"/>, at Q48.16, for the current step.</summary>
    public long TangentImpulse2Raw;
    /// <summary>What the slot is doing this step.</summary>
    public FixedPairManifoldSlotDisposition Disposition;

    readonly bool IManifoldSlot<FixedTwoBodyContact>.Occupied => Occupied;
    readonly int IManifoldSlot<FixedTwoBodyContact>.IdleSteps => IdleSteps;
    readonly long IManifoldSlot<FixedTwoBodyContact>.NormalImpulseRaw => NormalImpulseRaw;
    readonly int IManifoldSlot<FixedTwoBodyContact>.SourceId => SourceId;
    readonly int IManifoldSlot<FixedTwoBodyContact>.FeatureId => FeatureId;
    readonly FixedVector3 IManifoldSlot<FixedTwoBodyContact>.Normal => Normal;
    readonly bool IManifoldSlot<FixedTwoBodyContact>.IsIdle => (Disposition == FixedPairManifoldSlotDisposition.Idle);

    /// <summary>Compares the persistent part of two slots — the part a checkpoint carries.</summary>
    /// <param name="other">The other slot.</param>
    /// <returns><see langword="true"/> when occupancy, identity, anchors, normal, both impulses and the idle count are
    /// bit-identical.</returns>
    public readonly bool IsPersistentStateEqual(in FixedPairManifoldSlot other) =>
        (Occupied == other.Occupied) &&
        (SourceId == other.SourceId) &&
        (FeatureId == other.FeatureId) &&
        (AnchorA == other.AnchorA) &&
        (AnchorB == other.AnchorB) &&
        (Normal == other.Normal) &&
        (NormalImpulseRaw == other.NormalImpulseRaw) &&
        (FrictionImpulse == other.FrictionImpulse) &&
        (IdleSteps == other.IdleSteps);

    void IManifoldSlot<FixedTwoBodyContact>.Claim(in FixedTwoBodyContact candidate) {
        Occupied = true;
        SourceId = candidate.SourceId;
        FeatureId = candidate.FeatureId;
        AnchorA = candidate.AnchorA;
        AnchorB = candidate.AnchorB;
        Normal = candidate.Normal;
        Separation = candidate.Separation;
        TotalNormalImpulseRaw = 0L;
        IdleSteps = 0;
        Disposition = FixedPairManifoldSlotDisposition.Constraint;
    }
    void IManifoldSlot<FixedTwoBodyContact>.ClearWarmStart() {
        NormalImpulseRaw = 0L;
        FrictionImpulse = FixedVector3.Zero;
    }
    void IManifoldSlot<FixedTwoBodyContact>.MarkIdle() {
        Disposition = FixedPairManifoldSlotDisposition.Idle;

        if (Occupied) {
            ++IdleSteps;
        }
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
/// The persistent manifold slots one body pair carries, and the deterministic association that maps a step's candidates
/// into them (<see cref="FixedManifoldSlotCore"/>): a fixed-capacity ordered array, association and solving both scanning
/// it in index order, eviction by an explicit total key, no hash container anywhere.
/// </summary>
public sealed class FixedPairManifoldSlotTable {
    /// <summary>The number of slots one pair carries.</summary>
    public const int Capacity = 16;
    /// <summary>The number of associations a slot survives without being claimed.</summary>
    public const int IdleStepBudget = 4;

    private readonly FixedPairManifoldSlot[] m_slots = new FixedPairManifoldSlot[Capacity];
    private readonly bool[] m_claimed = new bool[Capacity];

    /// <summary>Gets the slot at an index.</summary>
    /// <param name="index">The slot index, below <see cref="Capacity"/>.</param>
    /// <returns>A reference to the slot.</returns>
    public ref FixedPairManifoldSlot this[int index] => ref m_slots[index];

    /// <summary>Gets the number of slots associated with a candidate on the most recent association.</summary>
    public int ActiveCount => FixedManifoldSlotCore.ActiveCount<FixedPairManifoldSlot, FixedTwoBodyContact>(slots: m_slots);
    /// <summary>Gets whether any slot holds a live association.</summary>
    public bool AnyOccupied {
        get {
            for (var index = 0; (index < Capacity); ++index) {
                if (m_slots[index].Occupied) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Associates one step's candidates — already filtered to this pair, oriented from its A body toward its B
    /// body and in canonical order — into slots.</summary>
    /// <param name="candidates">The pair's candidates for the step.</param>
    public void Associate(List<FixedTwoBodyContact> candidates) =>
        FixedManifoldSlotCore.Associate<FixedPairManifoldSlot, FixedTwoBodyContact>(
            candidates: candidates,
            claimed: m_claimed,
            idleStepBudget: IdleStepBudget,
            slots: m_slots
        );
    /// <summary>Clears every slot.</summary>
    public void Clear() => Array.Clear(array: m_slots);
    /// <summary>Folds every slot's persistent state into a running digest, in slot index order.</summary>
    /// <param name="digest">The running digest, advanced in place.</param>
    public void Fold(ref Fnv1aHash digest) {
        for (var index = 0; (index < Capacity); ++index) {
            ref readonly var slot = ref m_slots[index];

            digest.Add(value: (slot.Occupied ? 1L : 0L));
            digest.Add(value: ((long)slot.SourceId));
            digest.Add(value: ((long)slot.FeatureId));
            digest.Add(value: slot.NormalImpulseRaw);
            digest.Add(value: slot.FrictionImpulse.X.Value);
            digest.Add(value: slot.FrictionImpulse.Y.Value);
            digest.Add(value: slot.FrictionImpulse.Z.Value);
            digest.Add(value: slot.Normal.X.Value);
            digest.Add(value: slot.Normal.Y.Value);
            digest.Add(value: slot.Normal.Z.Value);
            digest.Add(value: ((long)slot.IdleSteps));
        }
    }
}
