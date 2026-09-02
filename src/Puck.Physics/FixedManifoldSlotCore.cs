using Puck.Maths;

namespace Puck.Physics;

/// <summary>The per-slot fields <see cref="FixedManifoldSlotCore"/>'s eviction helpers read: eviction ranking,
/// occupancy, and the free/idle-sweep tests every persistent manifold slot table shares regardless of whether it
/// associates by one body or a body pair.</summary>
internal interface IManifoldSlotEvictionKey {
    /// <summary>Whether the slot holds a live association.</summary>
    bool Occupied { get; }
    /// <summary>The step ordinal the slot was last associated on.</summary>
    int LastTouchedStep { get; }
    /// <summary>The accumulated normal impulse raw, at Q48.16.</summary>
    long NormalImpulseRaw { get; }
}
/// <summary>The candidate identity fields <see cref="FixedManifoldSlotCore"/>'s association reads.</summary>
internal interface IManifoldCandidate {
    /// <summary>The identity of the generator that produced the candidate.</summary>
    int SourceId { get; }
    /// <summary>The identity of the contact feature within that generator's output.</summary>
    int FeatureId { get; }
    /// <summary>The unit contact normal, in world axes.</summary>
    FixedVector3 Normal { get; }
}
/// <summary>The candidate-independent slot surface <see cref="FixedManifoldSlotCore"/>'s association, count, and
/// fold helpers drive.</summary>
internal interface IManifoldSlotState : IManifoldSlotEvictionKey {
    /// <summary>The associated candidate's source identity.</summary>
    int SourceId { get; }
    /// <summary>The associated candidate's feature identity.</summary>
    int FeatureId { get; }
    /// <summary>The unit contact normal, in world axes.</summary>
    FixedVector3 Normal { get; }
    /// <summary>Whether the slot is not associated with a candidate this step.</summary>
    bool IsIdle { get; }

    /// <summary>Marks the slot idle at the start of a step's association.</summary>
    void MarkIdle();
    /// <summary>Clears the impulse the slot warm starts from.</summary>
    void ClearWarmStart();
    /// <summary>Folds the slot's persistent state into a running digest.</summary>
    /// <param name="digest">The running digest.</param>
    /// <param name="step">The current step ordinal, so the folded age is relative.</param>
    /// <returns>The updated digest.</returns>
    ulong Fold(ulong digest, int step);
}
/// <summary>One persistent manifold slot as <see cref="FixedManifoldSlotCore"/> associates it with one candidate
/// type.</summary>
/// <typeparam name="TCandidate">The candidate type the slot claims.</typeparam>
internal interface IManifoldSlot<TCandidate> : IManifoldSlotState where TCandidate : struct, IManifoldCandidate {
    /// <summary>Claims the slot for a candidate: overwrites the association geometry, resets the per-step impulse
    /// total, and touches the step.</summary>
    /// <param name="candidate">The candidate to claim for.</param>
    /// <param name="step">The step ordinal.</param>
    void Claim(in TCandidate candidate, int step);
    /// <summary>Measures the slot's geometric match distance to a candidate.</summary>
    /// <param name="candidate">The candidate to match.</param>
    /// <param name="matchRadiusSquared">The largest movement, squared, any one anchor may make and still match.</param>
    /// <param name="distanceSquared">The distance matching candidates rank by.</param>
    /// <returns><see langword="true"/> when every anchor moved within the match radius.</returns>
    bool TryMatchDistance(in TCandidate candidate, FixedQ4816 matchRadiusSquared, out FixedQ4816 distanceSquared);
}
/// <summary>The slot-table discipline shared by every persistent manifold slot table: association, geometric
/// matching, free-slot search, eviction victim selection, the idle-eviction sweep, the active count, and the digest
/// fold. One home so a correction to any of them reaches both the single-body (<see cref="FixedManifoldSlotTable"/>)
/// and pair (<see cref="FixedPairManifoldSlotTable"/>) tables in the same change.</summary>
internal static class FixedManifoldSlotCore {
    // Two candidates match one slot only when their normals agree to within this cosine — a slot is a persistent
    // contact, and a surface whose normal has swung this far is a different contact wearing the same identity.
    private static readonly FixedQ4816 NormalAgreement = FixedQ4816.FromDouble(value: 0.9d);
    // The largest anchor movement, squared, that still reads as the same contact point rather than a new one.
    private static readonly FixedQ4816 MatchRadiusSquared = FixedQ4816.FromDouble(value: 0.09d);

    private static int FindMatch<TSlot, TCandidate>(TSlot[] slots, bool[] claimed, in TCandidate candidate, int capacity)
        where TSlot : struct, IManifoldSlot<TCandidate>
        where TCandidate : struct, IManifoldCandidate {
        var best = -1;
        var bestDistance = FixedQ4816.MaxValue;
        var candidateFeatureId = candidate.FeatureId;
        var candidateSourceId = candidate.SourceId;
        var candidateNormal = candidate.Normal;

        for (var index = 0; (index < capacity); ++index) {
            ref var slot = ref slots[index];

            if (
                !slot.Occupied ||
                claimed[index] ||
                (slot.FeatureId != candidateFeatureId) ||
                (slot.SourceId != candidateSourceId) ||
                (FixedVector3.Dot(
                left: slot.Normal,
                right: candidateNormal
            ) < NormalAgreement)
            ) {
                continue;
            }

            if (!slot.TryMatchDistance(
                candidate: in candidate,
                distanceSquared: out var distance,
                matchRadiusSquared: MatchRadiusSquared
            )) {
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

    /// <summary>Counts the slots associated with a candidate on the most recent step.</summary>
    /// <param name="slots">The slot array.</param>
    /// <param name="capacity">The number of slots to scan.</param>
    /// <returns>The number of non-idle slots.</returns>
    internal static int ActiveCount<TSlot>(TSlot[] slots, int capacity) where TSlot : struct, IManifoldSlotState {
        var count = 0;

        for (var index = 0; (index < capacity); ++index) {
            ref var slot = ref slots[index];

            if (!slot.IsIdle) {
                ++count;
            }
        }

        return count;
    }
    /// <summary>Associates one step's candidates into slots.</summary>
    /// <param name="slots">The slot array, index-aligned with <paramref name="claimed"/>.</param>
    /// <param name="claimed">Whether each slot is already claimed by this step's association.</param>
    /// <param name="candidates">The candidates, already ordered by the caller's contract.</param>
    /// <param name="step">The step ordinal, used for the idle budget and for eviction ordering.</param>
    /// <param name="capacity">The number of slots to scan.</param>
    /// <param name="idleStepBudget">The number of steps a slot survives without being associated.</param>
    /// <param name="compositeIdentity">Whether a candidate is associated by its composite identity plus geometric
    /// matching. When false, the feature index alone selects the slot, which is the refuted scheme.</param>
    /// <remarks>Each candidate claims at most one slot and each slot is claimed at most once, both in the order the
    /// candidate list already carries. A candidate with no match takes the lowest free slot; with no free slot it
    /// evicts by the total key <c>(lastTouchedStep, accumulatedImpulse, slotIndex)</c>, never by access time. When
    /// every slot is already claimed this step, the overflow candidate is dropped rather than destroying a claimed
    /// contact and its warm-start impulse.</remarks>
    internal static void Associate<TSlot, TCandidate>(
        TSlot[] slots,
        bool[] claimed,
        List<TCandidate> candidates,
        int step,
        int capacity,
        int idleStepBudget,
        bool compositeIdentity
    )
        where TSlot : struct, IManifoldSlot<TCandidate>
        where TCandidate : struct, IManifoldCandidate {
        ArgumentNullException.ThrowIfNull(argument: candidates);
        Array.Clear(array: claimed);

        for (var index = 0; (index < capacity); ++index) {
            ref var idled = ref slots[index];

            idled.MarkIdle();
        }

        for (var index = 0; (index < candidates.Count); ++index) {
            var candidate = candidates[index];
            var target = (compositeIdentity
                ? FindMatch(
                    candidate: in candidate,
                    capacity: capacity,
                    claimed: claimed,
                    slots: slots
                )
                : (candidate.FeatureId % capacity)
            );
            var retainImpulse = (target >= 0);

            if (target < 0) {
                target = FindFree(
                    capacity: capacity,
                    slots: slots
                );
            }

            if (target < 0) {
                target = Evict(
                    capacity: capacity,
                    claimed: claimed,
                    slots: slots
                );
            }

            if (target < 0) {
                continue;
            }

            ref var slot = ref slots[target];

            if (!compositeIdentity) {
                retainImpulse = slot.Occupied;
            }

            if (!retainImpulse) {
                slot.ClearWarmStart();
            }

            slot.Claim(
                candidate: in candidate,
                step: step
            );
            claimed[target] = true;
        }

        SweepIdle(
            capacity: capacity,
            claimed: claimed,
            idleStepBudget: idleStepBudget,
            slots: slots,
            step: step
        );
    }
    /// <summary>Selects, among the slots not already claimed this step, the one that yields to a new candidate: the
    /// least recently touched; a tie breaks toward the smaller accumulated impulse, and a further tie toward the
    /// lower index (scan order), never toward access time — then evicts it (resets it to default) and returns its
    /// index.</summary>
    /// <param name="slots">The slot array, index-aligned with <paramref name="claimed"/>.</param>
    /// <param name="claimed">Whether each slot is already claimed by this step's association.</param>
    /// <param name="capacity">The number of slots to scan.</param>
    /// <returns>The evicted victim's index, or <c>-1</c> when every slot is already claimed.</returns>
    internal static int Evict<TSlot>(TSlot[] slots, bool[] claimed, int capacity) where TSlot : struct, IManifoldSlotEvictionKey {
        var victim = SelectVictim(
            capacity: capacity,
            claimed: claimed,
            slots: slots
        );

        if (victim < 0) {
            return -1;
        }

        slots[victim] = default;

        return victim;
    }
    /// <summary>Finds the lowest-index unoccupied slot.</summary>
    /// <param name="slots">The slot array.</param>
    /// <param name="capacity">The number of slots to scan.</param>
    /// <returns>The free slot's index, or <c>-1</c> when every slot is occupied.</returns>
    internal static int FindFree<TSlot>(TSlot[] slots, int capacity) where TSlot : struct, IManifoldSlotEvictionKey {
        for (var index = 0; (index < capacity); ++index) {
            if (!slots[index].Occupied) {
                return index;
            }
        }

        return -1;
    }
    /// <summary>Folds every slot's persistent state into a running digest, in slot index order.</summary>
    /// <param name="slots">The slot array.</param>
    /// <param name="digest">The running digest.</param>
    /// <param name="step">The current step ordinal, so the folded age is relative (<c>step - LastTouchedStep</c>)
    /// rather than absolute — two runs starting at different step offsets still fold the same age for the same
    /// history.</param>
    /// <param name="capacity">The number of slots to scan.</param>
    /// <returns>The updated digest.</returns>
    internal static ulong Fold<TSlot>(TSlot[] slots, ulong digest, int step, int capacity) where TSlot : struct, IManifoldSlotState {
        for (var index = 0; (index < capacity); ++index) {
            ref var slot = ref slots[index];

            digest = slot.Fold(
                digest: digest,
                step: step
            );
        }

        return digest;
    }
    /// <summary>Selects, among the slots not already claimed this step, the one that yields to a new candidate: the
    /// least recently touched; a tie breaks toward the smaller accumulated impulse, and a further tie toward the
    /// lower index (scan order), never toward access time.</summary>
    /// <param name="slots">The slot array, index-aligned with <paramref name="claimed"/>.</param>
    /// <param name="claimed">Whether each slot is already claimed by this step's association.</param>
    /// <param name="capacity">The number of slots to scan.</param>
    /// <returns>The victim's index, or <c>-1</c> when every slot is already claimed.</returns>
    internal static int SelectVictim<TSlot>(TSlot[] slots, bool[] claimed, int capacity) where TSlot : struct, IManifoldSlotEvictionKey {
        var victim = -1;

        for (var index = 0; (index < capacity); ++index) {
            if (claimed[index]) {
                continue;
            }

            if (victim < 0) {
                victim = index;

                continue;
            }

            ref readonly var candidate = ref slots[index];
            ref readonly var current = ref slots[victim];

            if (
                (candidate.LastTouchedStep < current.LastTouchedStep) ||
                ((candidate.LastTouchedStep == current.LastTouchedStep) && (candidate.NormalImpulseRaw < current.NormalImpulseRaw))
            ) {
                victim = index;
            }
        }

        return victim;
    }
    /// <summary>Clears every occupied, unclaimed slot whose age has exceeded the idle budget — the trailing sweep
    /// <see cref="Associate"/> runs after claiming this step's candidates.</summary>
    /// <param name="slots">The slot array, index-aligned with <paramref name="claimed"/>.</param>
    /// <param name="claimed">Whether each slot was claimed by this step's association.</param>
    /// <param name="capacity">The number of slots to scan.</param>
    /// <param name="step">The current step ordinal.</param>
    /// <param name="idleStepBudget">The number of steps a slot survives without being associated.</param>
    internal static void SweepIdle<TSlot>(TSlot[] slots, bool[] claimed, int capacity, int step, int idleStepBudget) where TSlot : struct, IManifoldSlotEvictionKey {
        for (var index = 0; (index < capacity); ++index) {
            ref var slot = ref slots[index];

            if (
                slot.Occupied &&
                !claimed[index] &&
                ((step - slot.LastTouchedStep) > idleStepBudget)
            ) {
                slot = default;
            }
        }
    }
}
