using Puck.Maths;

namespace Puck.State;

public sealed partial class SearchRuntime {
    // The tree search a job with the tree method runs once its root walk has landed: a bounded node pool over
    // the base position, one judge per node of budget, resumable at any phase. Selection descends by UCB1 from the
    // root, expansion judges every candidate of the leaf once and keeps the accepted ones as children, a playout
    // draws candidates by a hash-seeded sequence until no candidate is accepted or the depth cap is reached, and the
    // score is folded back along the path with alternating sign, read from the perspective of the side that moved
    // into the position it describes.
    private const int UctSelect = 0;
    private const int UctExpand = 1;
    private const int UctPlayout = 2;
    private const long UctScale = 1_000L;

    private static ulong Next(ref ulong seed) {
        // SplitMix64: one deterministic stream per job, advanced only here.
        seed += 0x9E3779B97F4A7C15UL;
        var z = seed;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return (z ^ (z >> 31));
    }

    private static int TotalCandidates(SearchShapePlan[] shapes, int cellCount, int tokenCount) {
        var total = 0;

        foreach (var shape in shapes) {
            total += (tokenCount * shape.CandidateCount(cellCount: cellCount));
        }

        return total;
    }
    private static void DecodeCandidate(SearchShapePlan[] shapes, int cellCount, int tokenCount, int flat, out int shape, out int token, out int candidate) {
        for (shape = 0; shape < shapes.Length; shape++) {
            var span = (tokenCount * shapes[shape].CandidateCount(cellCount: cellCount));

            if (flat < span) {
                var per = shapes[shape].CandidateCount(cellCount: cellCount);
                token = (flat / per);
                candidate = (flat % per);

                return;
            }

            flat -= span;
        }

        token = 0;
        candidate = 0;
    }

    // Resolves and applies one candidate from `from` into the host's scratch frame and judges it; true when the
    // judge accepted it (verdict at accept, turn changed).
    private bool TryJudgeCandidate(Job job, StateFrame from, SearchShapePlan shape, int token, int candidateIndex, StateRow tokens, IReadOnlyList<StateCell> tokenCells, IReadOnlyList<StateRow> rows, ulong tick, out int target, out long code) {
        var plan = job.Plan;
        var cells = plan.CellCount;
        var scratch = m_host!.Frame;
        var fromCell = TokenCell(job: job, frame: from, tokens: tokens, tokenCells: tokenCells, token: token);
        var onBoard = ((fromCell >= 0L) && (fromCell < cells));

        if (onBoard != (shape.Kind != SearchShapeKind.Drop)) {
            target = -1;
            code = 0L;

            return false;
        }
        if (!TryResolveCandidate(shape: shape, plan: plan, zones: job.ZoneRows, frame: from, tokens: tokens, tokenCells: tokenCells, token: token, from: fromCell, candidateIndex: candidateIndex, cells: cells,
            target: out target, mid: out var mid, companionIndex: out var companionIndex, companionTarget: out var companionTarget, code: out code)) {
            return false;
        }

        scratch.CopyFrom(other: from);
        ApplyCandidate(shape: shape, plan: plan, zones: job.ZoneRows, frame: from, scratch: scratch, rows: rows, tokens: tokens, tokenCells: tokenCells, token: token, from: fromCell,
            target: target, mid: mid, companionIndex: companionIndex, companionTarget: companionTarget, code: code);

        var mover = Slot(store: from, name: plan.Turn);

        _ = m_host.Judge(rules: m_judge, tick: tick);

        return ((Slot(store: scratch, name: plan.Verdict) == plan.Accept) && (Slot(store: scratch, name: plan.Turn) != mover));
    }

    private long EvaluateOutcome(SearchPlan plan, StateFrame position, ulong tick) {
        // The outcome reads the position it is asked about: the host's scratch frame.
        var scratch = m_host!.Frame;

        if (!ReferenceEquals(objA: scratch, objB: position)) {
            scratch.CopyFrom(other: position);
        }

        return (m_host.Evaluator.TryEvaluateExpression(program: plan.Score!, kind: CellKind.Int, tick: tick, value: out var value) ? value : 0L);
    }

    private void StartUct(Job job) {
        job.UctActive = true;
        job.Phase = UctSelect;
        job.TreeCount = 1;
        job.TreeParent![0] = -1;
        job.TreeFirstChild![0] = -1;
        job.TreeChildCount![0] = 0;
        job.TreeVisits![0] = 0L;
        job.TreeTotal![0] = 0L;
        job.TreeExpanded![0] = 0L;
        job.Iteration = 0;
        job.Seed = job.Stamp;
        job.Best = -SearchCapacity.MateScore;
        job.BestToken = -1;
        job.BestTarget = -1;
        BeginIteration(job: job);
    }
    private void BeginIteration(Job job) {
        job.Path![0] = 0;
        job.PathLength = 1;
        job.Phase = UctSelect;
        job.UctFrame!.CopyFrom(other: m_base!);
    }

    // One judge's worth of tree search.
    private void StepUct(Job job, ulong tick, StateRow tokens, IReadOnlyList<StateCell> tokenCells, IReadOnlyList<StateRow> rows) {
        var plan = job.Plan;
        var shapes = plan.Shapes;
        var cells = plan.CellCount;
        var node = job.Path![job.PathLength - 1];

        switch (job.Phase) {
            case UctSelect: {
                // The depth cap ends the path before the leaf is ever expanded: a leaf at the cap is scored as it stands.
                if ((job.PathLength - 1) >= plan.Depth) {
                    Backpropagate(job: job, value: EvaluateOutcome(plan: plan, position: job.UctFrame!, tick: tick));

                    return;
                }
                if (job.TreeExpanded![node] == 0L) {
                    job.Phase = UctExpand;
                    job.UShape = 0;
                    job.UToken = 0;
                    job.UTarget = 0;

                    return;
                }
                if (job.TreeChildCount![node] == 0) {
                    Backpropagate(job: job, value: EvaluateOutcome(plan: plan, position: job.UctFrame!, tick: tick));

                    return;
                }

                var chosen = SelectChild(job: job, node: node);

                if (TryJudgeCandidate(job: job, from: job.UctFrame!, shape: shapes[job.TreeShape![chosen]], token: job.TreeToken![chosen], candidateIndex: job.TreeTarget![chosen], tokens: tokens, tokenCells: tokenCells, rows: rows, tick: tick, target: out _, code: out _)) {
                    job.UctFrame!.CopyFrom(other: m_host!.Frame);
                    job.Path[job.PathLength++] = chosen;
                } else {
                    // A child that no longer judges accepted (a code change or a shape's own refusal) is a terminal.
                    Backpropagate(job: job, value: -EvaluateOutcome(plan: plan, position: job.UctFrame!, tick: tick));
                }

                return;
            }
            case UctExpand: {
                if (job.UShape >= shapes.Length) {
                    job.TreeExpanded![node] = 1L;

                    if (job.TreeChildCount![node] == 0) {
                        Backpropagate(job: job, value: EvaluateOutcome(plan: plan, position: job.UctFrame!, tick: tick));

                        return;
                    }

                    // The first playout starts from a child drawn from the fresh children.
                    var draw = (job.TreeFirstChild![node] + (int)(Next(ref job.Seed) % (ulong)job.TreeChildCount[node]));

                    if (TryJudgeCandidate(job: job, from: job.UctFrame!, shape: shapes[job.TreeShape![draw]], token: job.TreeToken![draw], candidateIndex: job.TreeTarget![draw], tokens: tokens, tokenCells: tokenCells, rows: rows, tick: tick, target: out _, code: out _)) {
                        job.PlayFrame!.CopyFrom(other: m_host!.Frame);
                        job.Path[job.PathLength++] = draw;
                        job.PlayoutPlies = 1;
                        job.Phase = UctPlayout;
                        job.UScan = 0;
                        job.UStart = (int)(Next(ref job.Seed) % (ulong)Math.Max(val1: 1, val2: TotalCandidates(shapes: shapes, cellCount: cells, tokenCount: tokenCells.Count)));
                    } else {
                        Backpropagate(job: job, value: EvaluateOutcome(plan: plan, position: job.UctFrame!, tick: tick));
                    }

                    return;
                }

                var shape = shapes[job.UShape];

                if (job.UToken >= tokenCells.Count) {
                    job.UShape++;
                    job.UToken = 0;
                    job.UTarget = 0;

                    return;
                }
                if (job.UTarget >= shape.CandidateCount(cellCount: cells)) {
                    job.UToken++;
                    job.UTarget = 0;

                    return;
                }

                var candidate = job.UTarget++;

                if (TryJudgeCandidate(job: job, from: job.UctFrame!, shape: shape, token: job.UToken, candidateIndex: candidate, tokens: tokens, tokenCells: tokenCells, rows: rows, tick: tick, target: out _, code: out _) && (job.TreeCount < job.TreeParent!.Length)) {
                    var child = job.TreeCount++;

                    job.TreeParent[child] = node;
                    job.TreeFirstChild![child] = -1;
                    job.TreeChildCount![child] = 0;
                    job.TreeVisits![child] = 0L;
                    job.TreeTotal![child] = 0L;
                    job.TreeExpanded![child] = 0L;
                    job.TreeShape![child] = job.UShape;
                    job.TreeToken![child] = job.UToken;
                    job.TreeTarget![child] = candidate;

                    if (job.TreeChildCount[node] == 0) {
                        job.TreeFirstChild[node] = child;
                    }

                    job.TreeChildCount[node]++;
                }

                return;
            }
            default: {
                if (job.PlayoutPlies >= plan.Depth) {
                    Backpropagate(job: job, value: EvaluateOutcome(plan: plan, position: job.PlayFrame!, tick: tick));

                    return;
                }
                // A chance ply draws rather than chooses: one weighted pick over this job's own stream, the same
                // stream every other playout draw uses, so two jobs sharing a seed make the same choice.
                if ((plan.Chance is { } chance) && (job.PlayoutPlies == chance.AtDepth)) {
                    if (StateRows.FindStateRow(rows: rows, name: chance.Row) is { } chanceRow) {
                        var outcome = SampleChanceOutcome(chance: chance, seed: ref job.Seed);

                        ApplyChanceOutcome(frame: job.PlayFrame!, row: chanceRow, chance: chance, outcome: outcome);
                    }

                    job.PlayoutPlies++;

                    return;
                }

                var total = TotalCandidates(shapes: shapes, cellCount: cells, tokenCount: tokenCells.Count);

                if (job.UScan >= total) {
                    // No candidate of this position is accepted: terminal for the side to move, whose opponent
                    // moved into it.
                    Backpropagate(job: job, value: EvaluateOutcome(plan: plan, position: job.PlayFrame!, tick: tick));

                    return;
                }

                var flat = ((job.UStart + job.UScan++) % total);

                DecodeCandidate(shapes: shapes, cellCount: cells, tokenCount: tokenCells.Count, flat: flat, shape: out var shapeIndex, token: out var token, candidate: out var candidateIndex);

                if (TryJudgeCandidate(job: job, from: job.PlayFrame!, shape: shapes[shapeIndex], token: token, candidateIndex: candidateIndex, tokens: tokens, tokenCells: tokenCells, rows: rows, tick: tick, target: out _, code: out _)) {
                    job.PlayFrame!.CopyFrom(other: m_host!.Frame);
                    job.PlayoutPlies++;
                    job.UScan = 0;
                    job.UStart = (int)(Next(ref job.Seed) % (ulong)total);
                }

                return;
            }
        }
    }

    // UCB1 over the children's means (outcome scale) with a log2-based exploration term, ties to the lowest child.
    private static int SelectChild(Job job, int node) {
        var first = job.TreeFirstChild![node];
        var count = job.TreeChildCount![node];
        var parentVisits = Math.Max(val1: 1L, val2: job.TreeVisits![node]);
        var log2 = (63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)parentVisits));
        var best = -1;
        var bestScore = long.MinValue;

        for (var child = first; child < (first + count); child++) {
            var visits = job.TreeVisits[child];

            if (visits == 0L) {
                return child;
            }

            var mean = (job.TreeTotal![child] / visits);
            var explore = (long)((ulong)((2L * (log2 + 1) * UctScale * UctScale) / visits)).SquareRoot();
            var score = (mean + explore);

            if (score > bestScore) {
                bestScore = score;
                best = child;
            }
        }

        return ((best < 0) ? first : best);
    }

    // Folds a leaf value along the path: the leaf's own node gets it as is, its parent negated, and so on up to the
    // root, which counts visits alone. Then either the next iteration begins or the job lands.
    private void Backpropagate(Job job, long value) {
        var sign = 1L;

        for (var index = (job.PathLength - 1); index >= 0; index--) {
            var node = job.Path![index];

            job.TreeVisits![node]++;

            if (index > 0) {
                job.TreeTotal![node] += (sign * value);
            }

            sign = -sign;
        }

        job.Iteration++;

        if (job.Iteration >= job.Plan.Iterations) {
            var root = 0;
            var first = job.TreeFirstChild![root];
            var most = -1L;

            for (var child = first; child < (first + job.TreeChildCount![root]); child++) {
                if (job.TreeVisits![child] > most) {
                    most = job.TreeVisits[child];
                    job.BestToken = job.TreeToken![child];
                    job.BestTarget = ResolveTarget(job: job, child: child);
                    job.Best = (job.TreeTotal![child] / Math.Max(val1: 1L, val2: job.TreeVisits[child]));
                }
            }

            job.UctActive = false;
            job.Running = false;

            return;
        }

        BeginIteration(job: job);
    }

    // The destination cell a root child's candidate lands on, decoded the way the walk would have.
    private int ResolveTarget(Job job, int child) {
        var shape = job.Plan.Shapes[job.TreeShape![child]];
        var candidate = job.TreeTarget![child];

        return shape.Kind switch {
            SearchShapeKind.Promote => (candidate / Math.Max(val1: 1, val2: (shape.PromoteTo?.Length ?? 1))),
            SearchShapeKind.Jump => JumpTarget(job: job, child: child, shape: shape, candidate: candidate),
            _ => candidate,
        };
    }
    private int JumpTarget(Job job, int child, SearchShapePlan shape, int candidate) {
        var tokens = StateRows.FindStateRow(rows: m_base!.Rows, name: job.Plan.Tokens);

        if ((tokens is null) || !m_base.TryStoredAt(row: tokens, index: job.TreeToken![child], value: out var from) || (from < 0L)) {
            return -1;
        }

        var mid = job.Plan.Topology!.Neighbour(cell: (int)from, direction: shape.Directions[candidate]);

        return ((mid < 0) ? -1 : job.Plan.Topology.Neighbour(cell: mid, direction: shape.Directions[candidate]));
    }
}
