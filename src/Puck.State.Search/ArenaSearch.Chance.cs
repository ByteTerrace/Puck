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
    // The value a chance node folds to its parent: the weighted average, over every baked outcome, of the position
    // `remainingDepth` further move plies on — read directly when none remain, or through a bounded, non-resumable
    // negamax that mirrors the incremental walk's own candidate door exactly, when they do. Each outcome is one
    // scope on the position the caller left open, rewound before the next outcome is tried.
    //
    // The value is the searching side's at the root, where the side to move after the draw is the one the job
    // searches for, and the side's that moved into the node everywhere else, where the side to move after the draw
    // is its opponent and the negamax's answer is negated like any other reply.
    private long ChanceExpectation(Job job, int remainingDepth, bool atRoot) {
        var chance = job.Plan.Chance!;
        var sum = Int128.Zero;
        var totalWeight = 0UL;

        for (var outcome = 0; (outcome < chance.Weights.Length); outcome++) {
            var scope = ArenaSearchCandidate.Begin(arena: m_arena);

            try {
                ApplyChanceOutcome(
                    job: job,
                    outcome: outcome
                );

                long value;

                if (remainingDepth <= 0) {
                    value = EvaluateOutcome(job: job);
                } else {
                    var reply = RecursiveNegamax(
                        alpha: -SearchCapacity.MateScore,
                        beta: SearchCapacity.MateScore,
                        depthRemaining: remainingDepth,
                        job: job,
                        ply: (job.ScopeCount + 1)
                    );

                    value = (atRoot
                        ? reply
                        : -reply
                    );
                }

                sum += (((Int128)value) * chance.Weights[outcome]);
                totalWeight += chance.Weights[outcome];
            } finally {
                scope.Rewind();
            }
        }

        return RoundedWeightedAverage(
            totalWeight: totalWeight,
            weightedSum: sum
        );
    }
    // A bounded, non-resumable negamax beneath a chance node: every accepted candidate of the position the arena
    // holds, resolved and applied through the identical TryResolveCandidate/ApplyCandidate door the incremental walk
    // uses, negated and alpha-beta folded. Every scope it opens is rewound before it returns, including out of a
    // judge or score that throws.
    //
    // `ply` is the ply the next candidate applied here would sit at, so the position the arena holds sits one above
    // it. The answer is the side to move's. A score is the side's that moved into the position, which is the other
    // one, so a position scored as it stands answers negated — the walk's own leaf, folded one level up.
    private long RecursiveNegamax(Job job, int depthRemaining, long alpha, long beta, int ply) {
        var plan = job.Plan;

        if (
            (depthRemaining <= 0) ||
            !job.Judge.Scores
        ) {
            return -EvaluateOutcome(
                job: job,
                ply: (ply - 1)
            );
        }

        var cells = plan.CellCount;
        var mover = Slot(rowOrdinal: plan.TurnOrdinal);
        var best = -SearchCapacity.MateScore;

        for (var shapeIndex = 0; (shapeIndex < plan.Shapes.Length); shapeIndex++) {
            var shape = plan.Shapes[shapeIndex];

            for (var token = 0; (token < job.TokenCount); token++) {
                var from = TokenCell(
                    job: job,
                    token: token
                );
                var onBoard = ((from >= 0L) && (from < cells));

                if (onBoard != (shape.Kind != SearchShapeKind.Drop)) {
                    continue;
                }

                var bound = shape.CandidateCount(cellCount: cells);

                for (var candidateIndex = 0; (candidateIndex < bound); candidateIndex++) {
                    if (!TryResolveCandidate(
                        candidateIndex: candidateIndex,
                        code: out var code,
                        companionIndex: out var companionIndex,
                        companionTarget: out var companionTarget,
                        from: from,
                        job: job,
                        mid: out var mid,
                        shapeIndex: shapeIndex,
                        target: out var target,
                        token: token
                    )) {
                        continue;
                    }

                    var scope = ArenaSearchCandidate.Begin(arena: m_arena);
                    var cut = false;

                    try {
                        ApplyCandidate(
                            code: code,
                            companionIndex: companionIndex,
                            companionTarget: companionTarget,
                            job: job,
                            mid: mid,
                            shapeIndex: shapeIndex,
                            target: target,
                            token: token
                        );
                        _ = job.Judge.Judge(view: View(ply: ply));

                        if ((Slot(rowOrdinal: plan.VerdictOrdinal) == plan.Accept) && (Slot(rowOrdinal: plan.TurnOrdinal) != mover)) {
                            var value = -RecursiveNegamax(
                                alpha: -beta,
                                beta: -alpha,
                                depthRemaining: (depthRemaining - 1),
                                job: job,
                                ply: (ply + 1)
                            );

                            if (value > best) {
                                best = value;
                            }
                            if (value > alpha) {
                                alpha = value;
                            }

                            cut = (alpha >= beta);
                        }
                    } finally {
                        scope.Rewind();
                    }

                    if (cut) {
                        return best;
                    }
                }
            }
        }

        return best;
    }
}
