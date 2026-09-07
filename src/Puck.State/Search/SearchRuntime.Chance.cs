namespace Puck.State;

public sealed partial class SearchRuntime {
    // Writes one baked outcome's per-cell values into `frame`'s own copy of `row`, in the row's own cell order — the
    // door a chance draw applies through, parallel to ApplyCandidate's door for a shape's own move.
    private static void ApplyChanceOutcome(StateFrame frame, StateRow row, SearchChancePlan chance, int outcome) {
        var cells = row.Cells!;
        var baseIndex = (outcome * chance.CellCount);

        for (var index = 0; index < chance.CellCount; index++) {
            _ = frame.TryWrite(row: row, key: cells[index].Key, value: chance.Outcomes[baseIndex + index], write: StateWriteKind.Set, reason: out _);
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
            : (-(long)(((-weightedSum) + half) / totalWeight)));
    }

    // Reads plan.Score at an arbitrary position rather than the job's own current ply — the host's one scratch
    // frame is the evaluator's only input, so a position that is not already there is copied in first.
    private long EvaluateScoreAt(SearchPlan plan, StateFrame position, ulong tick) {
        var scratch = m_host!.Frame;

        if (!ReferenceEquals(objA: scratch, objB: position)) {
            scratch.CopyFrom(other: position);
        }

        return (m_host.Evaluator.TryEvaluateExpression(program: plan.Score!, kind: CellKind.Int, tick: tick, value: out var value) ? value : 0L);
    }

    // The value a chance node folds to its parent: the weighted average, over every baked outcome, of the position
    // `remainingDepth` further move plies on — read directly when none remain, or through a bounded, non-resumable
    // negamax that mirrors the incremental walk's own candidate door exactly, when they do.
    private long ChanceExpectation(Job job, StateFrame position, StateRow chanceRow, int remainingDepth, ulong tick) {
        var chance = job.Plan.Chance!;
        var scratch = job.ChanceFrames![0];
        var sum = Int128.Zero;
        var totalWeight = 0UL;

        for (var outcome = 0; outcome < chance.Weights.Length; outcome++) {
            scratch.CopyFrom(other: position);
            ApplyChanceOutcome(frame: scratch, row: chanceRow, chance: chance, outcome: outcome);

            var value = ((remainingDepth <= 0)
                ? EvaluateScoreAt(plan: job.Plan, position: scratch, tick: tick)
                : RecursiveNegamax(job: job, position: scratch, depthRemaining: remainingDepth, alpha: -SearchCapacity.MateScore, beta: SearchCapacity.MateScore, level: 1, tick: tick));

            sum += (((Int128)value) * chance.Weights[outcome]);
            totalWeight += chance.Weights[outcome];
        }

        return RoundedWeightedAverage(weightedSum: sum, totalWeight: totalWeight);
    }

    // A bounded, non-resumable negamax beneath a chance node: every accepted candidate at `position`, resolved and
    // applied through the identical TryResolveCandidate/ApplyCandidate door the incremental walk uses, negated and
    // alpha-beta folded. `level` indexes job.ChanceFrames — pooled once per chance-bearing job so this never
    // allocates on a tick that reaches it.
    private long RecursiveNegamax(Job job, StateFrame position, int depthRemaining, long alpha, long beta, int level, ulong tick) {
        var plan = job.Plan;

        if ((depthRemaining <= 0) || (plan.Score is null)) {
            return EvaluateScoreAt(plan: plan, position: position, tick: tick);
        }

        var rows = m_base!.Rows;
        var tokens = StateRows.FindStateRow(rows: rows, name: plan.Tokens);

        if (tokens?.Cells is not { } tokenCells) {
            return EvaluateScoreAt(plan: plan, position: position, tick: tick);
        }

        var cells = plan.CellCount;
        var mover = Slot(store: position, name: plan.Turn);
        var host = m_host!;
        var scratch = job.ChanceFrames![level];
        var best = -SearchCapacity.MateScore;

        foreach (var shape in plan.Shapes) {
            for (var token = 0; token < tokenCells.Count; token++) {
                var from = (position.TryStoredAt(row: tokens, index: token, value: out var stored) ? stored : plan.Off);
                var onBoard = ((from >= 0L) && (from < cells));

                if (onBoard != (shape.Kind != SearchShapeKind.Drop)) {
                    continue;
                }

                var bound = shape.CandidateCount(cellCount: cells);

                for (var candidateIndex = 0; candidateIndex < bound; candidateIndex++) {
                    if (!TryResolveCandidate(shape: shape, plan: plan, zones: job.ZoneRows, frame: position, tokens: tokens, tokenCells: tokenCells, token: token, from: from, candidateIndex: candidateIndex, cells: cells,
                        target: out var target, mid: out var mid, companionIndex: out var companionIndex, companionTarget: out var companionTarget, code: out var code)) {
                        continue;
                    }

                    host.Frame.CopyFrom(other: position);
                    ApplyCandidate(shape: shape, plan: plan, zones: job.ZoneRows, frame: position, scratch: host.Frame, rows: rows, tokens: tokens, tokenCells: tokenCells, token: token, from: from,
                        target: target, mid: mid, companionIndex: companionIndex, companionTarget: companionTarget, code: code);
                    _ = host.Judge(rules: m_judge, tick: tick);

                    var accepted = ((Slot(store: host.Frame, name: plan.Verdict) == plan.Accept) && (Slot(store: host.Frame, name: plan.Turn) != mover));

                    if (!accepted) {
                        continue;
                    }

                    // Snapshot the child before recursing: the recursive call reuses host.Frame as its own scratch.
                    scratch.CopyFrom(other: host.Frame);

                    var value = -RecursiveNegamax(job: job, position: scratch, depthRemaining: (depthRemaining - 1), alpha: -beta, beta: -alpha, level: (level + 1), tick: tick);

                    if (value > best) {
                        best = value;
                    }
                    if (value > alpha) {
                        alpha = value;
                    }
                    if (alpha >= beta) {
                        return best;
                    }
                }
            }
        }

        return best;
    }

    // The one outcome a Tree job's playout draws at a chance ply: a weighted pick over job.Seed's own stream, the
    // same SplitMix64 sequence StepUct's move draws use.
    private static int SampleChanceOutcome(SearchChancePlan chance, ref ulong seed) {
        var total = 0UL;

        foreach (var weight in chance.Weights) {
            total += weight;
        }
        if (total == 0UL) {
            return 0;
        }

        var draw = (Next(ref seed) % total);
        var cumulative = 0UL;

        for (var index = 0; index < chance.Weights.Length; index++) {
            cumulative += chance.Weights[index];

            if (draw < cumulative) {
                return index;
            }
        }

        return (chance.Weights.Length - 1);
    }
}
