using Puck.Maths;

namespace Puck.State;

public sealed partial class ArenaSearch {
    // Writes one baked outcome's per-cell values into the chance row, in the row's own cell order — the door a
    // chance draw applies through, parallel to ApplyCandidate's door for a shape's own move.
    private void ApplyChanceOutcome(Job job, int outcome) {
        var chance = job.Plan.Chance!;
        var baseIndex = (outcome * chance.CellCount);

        for (var index = 0; ((index < chance.CellCount) && (index < job.ChanceKeys.Length)); index++) {
            _ = m_arena.TryWriteLive(
                key: job.ChanceKeys[index],
                operand: chance.Outcomes[(baseIndex + index)],
                reason: out _,
                rowOrdinal: chance.RowOrdinal,
                time: Time,
                write: StateWriteKind.Set
            );
        }
    }
    // Folds one outcome's value into a chance ply at the outcome's own weight.
    private static void FoldOutcome(Level level, long value, ulong weight) {
        level.ChanceSum += (((Int128)value) * weight);
        level.ChanceWeight += weight;
    }
    // The weighted mean of a chance ply's outcomes, rounded to nearest with ties to even. The sum is exact in Int128:
    // the table's whole weight fits one word, so outcomes at MateScore magnitude never overflow the accumulator, and
    // the mean lies between the least and greatest outcome.
    private static long RoundedWeightedAverage(Int128 weightedSum, ulong totalWeight) => ((totalWeight == 0UL)
        ? 0L
        : ((long)FixedPointRounding.RoundRational(
            denominator: totalWeight,
            fractionBitCount: 0,
            numerator: weightedSum
        ))
    );
    // The one outcome a tree job's playout draws at a chance ply: a one-shot alias pick over the job's own stream,
    // the stream the playout's move draws advance too.
    private static int SampleChanceOutcome(ArenaSearchChancePlan chance, ref Pcg32XshRr stream) {
        foreach (var weight in chance.Weights) {
            if (weight != 0UL) {
                return WeightedSampler.SampleOnce(
                    generator: ref stream,
                    weights: chance.Weights
                );
            }
        }

        return 0;
    }
}
