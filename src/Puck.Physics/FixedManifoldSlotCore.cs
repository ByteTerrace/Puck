using Puck.Maths;

namespace Puck.Physics;

/// <summary>The candidate identity fields <see cref="FixedManifoldSlotCore"/>'s association reads.</summary>
internal interface IManifoldCandidate {
    /// <summary>Gets the identity of the generator that produced the candidate.</summary>
    int SourceId { get; }
    /// <summary>Gets the identity of the contact feature within that generator's output.</summary>
    int FeatureId { get; }
    /// <summary>Gets the unit contact normal, in world axes.</summary>
    FixedVector3 Normal { get; }
}
/// <summary>One persistent manifold slot as <see cref="FixedManifoldSlotCore"/> associates it with one candidate
/// type.</summary>
/// <typeparam name="TCandidate">The candidate type the slot claims.</typeparam>
internal interface IManifoldSlot<TCandidate> where TCandidate : struct, IManifoldCandidate {
    /// <summary>Gets whether the slot holds a live association.</summary>
    bool Occupied { get; }
    /// <summary>Gets the associations the slot has missed since it was last claimed.</summary>
    int IdleSteps { get; }
    /// <summary>Gets the accumulated normal impulse raw, at Q48.16.</summary>
    long NormalImpulseRaw { get; }
    /// <summary>Gets the associated candidate's source identity.</summary>
    int SourceId { get; }
    /// <summary>Gets the associated candidate's feature identity.</summary>
    int FeatureId { get; }
    /// <summary>Gets the unit contact normal, in world axes.</summary>
    FixedVector3 Normal { get; }
    /// <summary>Gets whether the slot is not associated with a candidate this step.</summary>
    bool IsIdle { get; }

    /// <summary>Marks the slot idle at the start of an association and counts the miss.</summary>
    void MarkIdle();
    /// <summary>Clears the impulses the slot warm starts from.</summary>
    void ClearWarmStart();
    /// <summary>Claims the slot for a candidate: overwrites the association geometry, resets the per-step impulse
    /// total, and zeroes the idle count.</summary>
    /// <param name="candidate">The candidate to claim for.</param>
    void Claim(in TCandidate candidate);
    /// <summary>Measures the slot's geometric match distance to a candidate.</summary>
    /// <param name="candidate">The candidate to match.</param>
    /// <param name="matchRadiusSquared">The largest movement, squared, any one anchor may make and still match.</param>
    /// <param name="distanceSquared">The distance matching candidates rank by.</param>
    /// <returns><see langword="true"/> when every anchor moved within the match radius.</returns>
    bool TryMatchDistance(in TCandidate candidate, FixedQ4816 matchRadiusSquared, out FixedQ4816 distanceSquared);
}
/// <summary>The slot-table discipline of a persistent manifold: association by composite identity plus geometric
/// matching, free-slot search, eviction by an explicit total key, and the idle sweep — a fixed-capacity ordered array
/// scanned by index, with no hash container and no absolute step ordinal, so a slot table restored from its own image
/// associates exactly as the uninterrupted one does.</summary>
internal static class FixedManifoldSlotCore {
    // Two candidates match one slot only when their normals agree to within this cosine — a slot is a persistent
    // contact, and a surface whose normal has swung this far is a different contact wearing the same identity.
    private static readonly FixedQ4816 NormalAgreement = FixedQ4816.FromDouble(value: 0.9d);
    // The largest anchor movement, squared, that still reads as the same contact point rather than a new one.
    private static readonly FixedQ4816 MatchRadiusSquared = FixedQ4816.FromDouble(value: 0.09d);

    /// <summary>Counts the slots associated with a candidate on the most recent association.</summary>
    /// <param name="slots">The slot array.</param>
    /// <returns>The number of non-idle slots.</returns>
    internal static int ActiveCount<TSlot, TCandidate>(TSlot[] slots)
        where TSlot : struct, IManifoldSlot<TCandidate>
        where TCandidate : struct, IManifoldCandidate {
        var count = 0;

        for (var index = 0; (index < slots.Length); ++index) {
            if (!slots[index].IsIdle) {
                ++count;
            }
        }

        return count;
    }
    /// <summary>Associates one step's candidates into slots.</summary>
    /// <param name="slots">The slot array, index-aligned with <paramref name="claimed"/>.</param>
    /// <param name="claimed">The scratch recording whether each slot is already claimed by this association.</param>
    /// <param name="candidates">The candidates, already in canonical order.</param>
    /// <param name="idleStepBudget">The number of associations a slot survives without being claimed.</param>
    /// <remarks>Each candidate claims at most one slot and each slot is claimed at most once, both in the order the
    /// candidate list carries. A candidate matches the nearest occupied slot of the same source, feature and normal; with
    /// no match it takes the lowest free slot, and with no free slot it evicts by the total key
    /// <c>(idle count descending, accumulated impulse ascending, slot index)</c>. When every slot is already claimed this
    /// step, the overflow candidate is dropped rather than destroying a claimed contact and its warm-start impulse; a
    /// caller that cannot accept a dropped candidate bounds its candidates by the capacity.</remarks>
    internal static void Associate<TSlot, TCandidate>(TSlot[] slots, bool[] claimed, List<TCandidate> candidates, int idleStepBudget)
        where TSlot : struct, IManifoldSlot<TCandidate>
        where TCandidate : struct, IManifoldCandidate {
        ArgumentNullException.ThrowIfNull(argument: candidates);
        Array.Clear(array: claimed);

        for (var index = 0; (index < slots.Length); ++index) {
            slots[index].MarkIdle();
        }

        for (var index = 0; (index < candidates.Count); ++index) {
            var candidate = candidates[index];
            var target = FindMatch<TSlot, TCandidate>(
                candidate: in candidate,
                claimed: claimed,
                slots: slots
            );
            var retainImpulse = (target >= 0);

            if (target < 0) {
                target = FindFree<TSlot, TCandidate>(slots: slots);
            }

            if (target < 0) {
                target = SelectVictim<TSlot, TCandidate>(
                    claimed: claimed,
                    slots: slots
                );

                if (target >= 0) {
                    slots[target] = default;
                }
            }

            if (target < 0) {
                continue;
            }

            ref var slot = ref slots[target];

            if (!retainImpulse) {
                slot.ClearWarmStart();
            }

            slot.Claim(candidate: in candidate);
            claimed[target] = true;
        }

        for (var index = 0; (index < slots.Length); ++index) {
            if (
                slots[index].Occupied &&
                !claimed[index] &&
                (slots[index].IdleSteps > idleStepBudget)
            ) {
                slots[index] = default;
            }
        }
    }

    private static int FindFree<TSlot, TCandidate>(TSlot[] slots)
        where TSlot : struct, IManifoldSlot<TCandidate>
        where TCandidate : struct, IManifoldCandidate {
        for (var index = 0; (index < slots.Length); ++index) {
            if (!slots[index].Occupied) {
                return index;
            }
        }

        return -1;
    }
    private static int FindMatch<TSlot, TCandidate>(TSlot[] slots, bool[] claimed, in TCandidate candidate)
        where TSlot : struct, IManifoldSlot<TCandidate>
        where TCandidate : struct, IManifoldCandidate {
        var best = -1;
        var bestDistance = FixedQ4816.MaxValue;

        for (var index = 0; (index < slots.Length); ++index) {
            ref var slot = ref slots[index];

            if (
                !slot.Occupied ||
                claimed[index] ||
                (slot.FeatureId != candidate.FeatureId) ||
                (slot.SourceId != candidate.SourceId) ||
                (FixedVector3.Dot(
                    left: slot.Normal,
                    right: candidate.Normal
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

            // Nearest witness wins; the lowest slot index breaks an exact tie.
            if (distance < bestDistance) {
                best = index;
                bestDistance = distance;
            }
        }

        return best;
    }
    private static int SelectVictim<TSlot, TCandidate>(TSlot[] slots, bool[] claimed)
        where TSlot : struct, IManifoldSlot<TCandidate>
        where TCandidate : struct, IManifoldCandidate {
        var victim = -1;

        for (var index = 0; (index < slots.Length); ++index) {
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
                (candidate.IdleSteps > current.IdleSteps) ||
                ((candidate.IdleSteps == current.IdleSteps) && (candidate.NormalImpulseRaw < current.NormalImpulseRaw))
            ) {
                victim = index;
            }
        }

        return victim;
    }
}
