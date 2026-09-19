namespace Puck.State;

public sealed partial class ArenaSearch {
    // Writes one baked outcome's per-cell values into the chance row, in the row's own cell order — the door a
    // chance draw applies through, parallel to ApplyCandidate's door for a shape's own move.
    private void ApplyChanceOutcome(Job job, int outcome) {
        var chance = job.Plan.Chance!;
        var baseIndex = (outcome * chance.CellCount);

        for (var index = 0; ((index < chance.CellCount) && (index < job.ChanceKeys.Length)); index++) {
            _ = m_arena.TryWrite(
                key: job.ChanceKeys[index],
                operand: chance.Outcomes[(baseIndex + index)],
                reason: out _,
                rowOrdinal: chance.RowOrdinal,
                write: StateWriteKind.Set
            );
        }
    }
    // Folds one outcome's value into a chance ply at the outcome's own weight.
    private static void FoldOutcome(Level level, long value, ulong weight) {
        level.ChanceSum += (((Int128)value) * weight);
        level.ChanceWeight += weight;
    }
    // Round-half-away-from-zero over a weighted sum, exact in Int128 so SearchCapacity.MaxChanceOutcomes outcomes at
    // MateScore magnitude each never overflow the accumulator.
    private static long RoundedWeightedAverage(Int128 weightedSum, ulong totalWeight) {
        if (totalWeight == 0UL) {
            return 0L;
        }

        var half = (((Int128)totalWeight) / 2);

        return ((weightedSum >= 0)
            ? ((long)((weightedSum + half) / totalWeight))
            : (-((long)(((-weightedSum) + half) / totalWeight)))
        );
    }
    // The one outcome a tree job's playout draws at a chance ply: a weighted pick over the job's own stream, the
    // same SplitMix64 sequence the playout's move draws use.
    private static int SampleChanceOutcome(ArenaSearchChancePlan chance, ref ulong seed) {
        var total = 0UL;

        foreach (var weight in chance.Weights) {
            total += weight;
        }
        if (total == 0UL) {
            return 0;
        }

        var draw = (Next(seed: ref seed) % total);
        var cumulative = 0UL;

        for (var index = 0; (index < chance.Weights.Length); index++) {
            cumulative += chance.Weights[index];

            if (draw < cumulative) {
                return index;
            }
        }

        return (chance.Weights.Length - 1);
    }
}
