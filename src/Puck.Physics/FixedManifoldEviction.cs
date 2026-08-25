namespace Puck.Physics;

/// <summary>The per-slot fields the generic manifold slot table helpers in <see cref="FixedManifoldEviction"/> read:
/// eviction ranking, occupancy, and the free/idle-sweep tests every persistent manifold slot table shares regardless
/// of whether it associates by one body or a body pair.</summary>
internal interface IManifoldSlotEvictionKey {
    /// <summary>Whether the slot holds a live association.</summary>
    bool Occupied { get; }
    /// <summary>The step ordinal the slot was last associated on.</summary>
    int LastTouchedStep { get; }
    /// <summary>The accumulated normal impulse raw, at Q48.16.</summary>
    long NormalImpulseRaw { get; }
}
/// <summary>The slot-table bookkeeping shared by every persistent manifold slot table: free-slot search, eviction
/// victim selection, and the idle-eviction sweep. One home so a correction to any of the three reaches both the
/// single-body (<see cref="FixedManifoldSlotTable"/>) and pair (<see cref="FixedPairManifoldSlotTable"/>) tables in
/// the same change.</summary>
internal static class FixedManifoldEviction {
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
    /// both slot tables' <c>Associate</c> runs after claiming this step's candidates.</summary>
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
