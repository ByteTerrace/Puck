namespace Puck.Physics;

/// <summary>The per-slot fields <see cref="FixedManifoldEviction.SelectVictim{TSlot}"/> reads to pick which
/// unclaimed slot yields under contention.</summary>
internal interface IManifoldSlotEvictionKey {
    /// <summary>The step ordinal the slot was last associated on.</summary>
    int LastTouchedStep { get; }
    /// <summary>The accumulated normal impulse raw, at Q48.16.</summary>
    long NormalImpulseRaw { get; }
}
/// <summary>The eviction victim selection shared by every persistent manifold slot table.</summary>
internal static class FixedManifoldEviction {
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
}
