using Puck.Maths;

namespace Puck.Dynamics.Spike.Tests.Core;

/// <summary>What a slot is doing this step.</summary>
internal enum SlotDisposition {
    /// <summary>Not associated with a candidate this step.</summary>
    Idle,
    /// <summary>Solved as an ordinary or speculative contact.</summary>
    Constraint,
    /// <summary>Routed to the bounded extraction path; no impulse is accumulated and none is warm started.</summary>
    Recovery,
}

/// <summary>One persistent manifold slot: the geometry a candidate wrote into it, and the impulse it accumulates
/// across steps.</summary>
internal struct ManifoldSlot {
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
    /// <summary>The constraint's effective mass raw, at <see cref="SpikeScales.EffectiveMass"/>.</summary>
    internal long NormalMassRaw;
    /// <summary>The relative normal velocity captured before the step's first solve, for restitution.</summary>
    internal FixedQ4816 RelativeVelocity;
    /// <summary>The total normal impulse applied within the current step.</summary>
    internal long TotalNormalImpulseRaw;
    /// <summary>The step ordinal the slot was last associated on.</summary>
    internal int LastTouchedStep;
    /// <summary>What the slot is doing this step.</summary>
    internal SlotDisposition Disposition;
}

/// <summary>
/// The persistent manifold slots one body carries, and the deterministic association that maps this step's candidates
/// into them. The table is a fixed-capacity ORDERED array: association scans it in index order, solving reads it in
/// index order, and eviction picks by an explicit total key. No hash container appears anywhere in the type, so no
/// enumeration order can leak into a result.
/// </summary>
internal sealed class ManifoldSlotTable {
    /// <summary>The number of slots one body carries.</summary>
    internal const int Capacity = 16;
    /// <summary>The number of steps a slot survives without being associated.</summary>
    internal const int IdleStepBudget = 4;

    // Two candidates match one slot only when their normals agree to within this cosine — a slot is a persistent
    // CONTACT, and a surface whose normal has swung this far is a different contact wearing the same identity.
    private static readonly FixedQ4816 NormalAgreement = FixedQ4816.FromDouble(value: 0.9d);
    // The largest anchor movement, squared, that still reads as the same contact point rather than a new one.
    private static readonly FixedQ4816 MatchRadiusSquared = FixedQ4816.FromDouble(value: 0.09d);

    private readonly ManifoldSlot[] m_slots = new ManifoldSlot[Capacity];
    private readonly bool[] m_claimed = new bool[Capacity];

    /// <summary>Gets the slot at an index.</summary>
    /// <param name="index">The slot index.</param>
    /// <returns>A reference to the slot.</returns>
    internal ref ManifoldSlot this[int index] => ref m_slots[index];

    /// <summary>Gets the number of slots associated with a candidate on the most recent step.</summary>
    internal int ActiveCount {
        get {
            var count = 0;

            for (var index = 0; (index < Capacity); ++index) {
                if (m_slots[index].Disposition != SlotDisposition.Idle) {
                    ++count;
                }
            }

            return count;
        }
    }

    /// <summary>Associates one step's candidates into slots.</summary>
    /// <param name="candidates">The candidates, already canonically ordered when the options ask for it.</param>
    /// <param name="step">The step ordinal, used for the idle budget and for eviction ordering.</param>
    /// <param name="compositeIdentity">Whether a candidate is associated by its composite identity plus geometric
    /// matching. When false, the body feature index alone selects the slot, which is the refuted scheme.</param>
    /// <remarks>Each candidate claims at most one slot and each slot is claimed at most once, both in the order the
    /// candidate list already carries. A candidate with no match takes the lowest free slot; with no free slot it
    /// evicts by the total key <c>(lastTouchedStep, accumulatedImpulse, slotIndex)</c>, never by access time.</remarks>
    internal void Associate(List<ContactCandidate> candidates, int step, bool compositeIdentity) {
        ArgumentNullException.ThrowIfNull(argument: candidates);
        Array.Clear(array: m_claimed);

        for (var index = 0; (index < Capacity); ++index) {
            m_slots[index].Disposition = SlotDisposition.Idle;
        }

        for (var index = 0; (index < candidates.Count); ++index) {
            var candidate = candidates[index];
            var target = (compositeIdentity ? FindMatch(candidate: candidate) : (candidate.FeatureId % Capacity));
            var retainImpulse = (target >= 0);

            if (target < 0) {
                target = FindFree();
            }

            if (target < 0) {
                target = Evict();
            }

            if (!compositeIdentity) {
                retainImpulse = m_slots[target].Occupied;
            }

            ref var slot = ref m_slots[target];

            if (!retainImpulse) {
                slot.NormalImpulseRaw = 0L;
            }

            slot.Occupied = true;
            slot.SourceId = candidate.SourceId;
            slot.FeatureId = candidate.FeatureId;
            slot.Anchor = candidate.Anchor;
            slot.Normal = candidate.Normal;
            slot.Separation = candidate.Separation;
            slot.TotalNormalImpulseRaw = 0L;
            slot.LastTouchedStep = step;
            slot.Disposition = SlotDisposition.Constraint;
            m_claimed[target] = true;
        }

        for (var index = 0; (index < Capacity); ++index) {
            ref var slot = ref m_slots[index];

            if (slot.Occupied && !m_claimed[index] && ((step - slot.LastTouchedStep) > IdleStepBudget)) {
                slot = default;
            }
        }
    }

    /// <summary>Folds every slot's persistent state into a running digest, in slot index order.</summary>
    /// <param name="digest">The running digest.</param>
    /// <param name="step">The current step ordinal, so the folded age is RELATIVE (<c>step - LastTouchedStep</c>)
    /// rather than absolute — two runs starting at different step offsets still fold the same age for the same
    /// history.</param>
    /// <returns>The updated digest.</returns>
    internal ulong Fold(ulong digest, int step) {
        for (var index = 0; (index < Capacity); ++index) {
            ref readonly var slot = ref m_slots[index];

            digest = SpikeArithmetic.Fold(digest: digest, value: (slot.Occupied ? 1L : 0L));
            digest = SpikeArithmetic.Fold(digest: digest, value: slot.SourceId);
            digest = SpikeArithmetic.Fold(digest: digest, value: slot.FeatureId);
            digest = SpikeArithmetic.Fold(digest: digest, value: slot.NormalImpulseRaw);
            digest = SpikeArithmetic.Fold(digest: digest, value: slot.Normal.X.Value);
            digest = SpikeArithmetic.Fold(digest: digest, value: slot.Normal.Y.Value);
            digest = SpikeArithmetic.Fold(digest: digest, value: slot.Normal.Z.Value);
            digest = SpikeArithmetic.Fold(digest: digest, value: (long)(step - slot.LastTouchedStep));
        }

        return digest;
    }

    private int FindMatch(ContactCandidate candidate) {
        var best = -1;
        var bestDistance = FixedQ4816.MaxValue;

        for (var index = 0; (index < Capacity); ++index) {
            ref readonly var slot = ref m_slots[index];

            if (!slot.Occupied || m_claimed[index] || (slot.FeatureId != candidate.FeatureId) ||
                (slot.SourceId != candidate.SourceId) ||
                (FixedVector3.Dot(left: slot.Normal, right: candidate.Normal) < NormalAgreement)) {
                continue;
            }

            var offset = (slot.Anchor - candidate.Anchor);

            if (!offset.TryLengthSquared(squaredLength: out var distance) || (distance > MatchRadiusSquared)) {
                continue;
            }

            // Nearest witness wins; the lowest slot index breaks an exact tie, so the winner never depends on which
            // slot happened to be visited first.
            if (distance < bestDistance) {
                best = index;
                bestDistance = distance;
            }
        }

        return best;
    }

    private int FindFree() {
        for (var index = 0; (index < Capacity); ++index) {
            if (!m_slots[index].Occupied) {
                return index;
            }
        }

        return -1;
    }

    private int Evict() {
        var victim = 0;

        for (var index = 1; (index < Capacity); ++index) {
            if (m_claimed[index]) {
                continue;
            }

            if (m_claimed[victim]) {
                victim = index;

                continue;
            }

            ref readonly var candidate = ref m_slots[index];
            ref readonly var current = ref m_slots[victim];

            if ((candidate.LastTouchedStep < current.LastTouchedStep) ||
                ((candidate.LastTouchedStep == current.LastTouchedStep) && (candidate.NormalImpulseRaw < current.NormalImpulseRaw))) {
                victim = index;
            }
        }

        m_slots[victim] = default;

        return victim;
    }
}
