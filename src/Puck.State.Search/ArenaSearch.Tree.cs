using System.Numerics;

using Puck.Maths;

namespace Puck.State;

public sealed partial class ArenaSearch {
    // The tree search a job with the tree method runs once its root walk has landed: one judge per node of budget,
    // resumable at any phase. Selection descends by UCB1, expansion judges every candidate of the leaf once and
    // keeps the accepted ones as children, a playout draws from the job's own seeded stream until no candidate is accepted
    // or the depth cap is reached, and the score folds back along the path with alternating sign, read from the
    // perspective of the side that moved into the position it describes.
    private const int TreeExpandPhase = 1;
    private const int TreePlayoutPhase = 2;
    private const int TreeSelectPhase = 0;
    private const long TreeScale = 1_000L;

    private static ulong Next(ref ulong seed) {
        // SplitMix64: one deterministic stream per job, advanced only here.
        seed += 0x9E3779B97F4A7C15UL;

        var z = seed;

        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EBUL);

        return z ^ (z >> 31);
    }
    private static int TotalCandidates(SearchShapePlan[] shapes, int cellCount, int tokenCount) {
        var total = 0;

        foreach (var shape in shapes) {
            total += (tokenCount * shape.CandidateCount(cellCount: cellCount));
        }

        return total;
    }
    private static void DecodeCandidate(SearchShapePlan[] shapes, int cellCount, int tokenCount, int flat, out int shape, out int token, out int candidate) {
        for (shape = 0; (shape < shapes.Length); shape++) {
            var span = (tokenCount * shapes[shape].CandidateCount(cellCount: cellCount));

            if (flat < span) {
                var per = shapes[shape].CandidateCount(cellCount: cellCount);

                candidate = (flat % per);
                token = (flat / per);

                return;
            }

            flat -= span;
        }

        candidate = 0;
        token = 0;
    }
    // UCB1 over the children's means (outcome scale) with a log2-based exploration term, ties to the lowest child.
    private static int SelectChild(Job job, int node) {
        var first = job.TreeFirstChild![node];
        var count = job.TreeChildCount![node];
        var parentVisits = Math.Max(
            val1: 1L,
            val2: job.TreeVisits![node]
        );
        var log2 = (63 - BitOperations.LeadingZeroCount(value: ((ulong)parentVisits)));
        var best = -1;
        var bestScore = long.MinValue;

        for (var child = first; (child < (first + count)); child++) {
            var visits = job.TreeVisits[child];

            if (visits == 0L) {
                return child;
            }

            var mean = (job.TreeTotal![child] / visits);
            var explore = ((long)((ulong)((((2L * (log2 + 1)) * TreeScale) * TreeScale) / visits)).SquareRoot());
            var score = (mean + explore);

            if (score > bestScore) {
                best = child;
                bestScore = score;
            }
        }

        return ((best < 0)
            ? first
            : best
        );
    }
    // Resolves one candidate from the position the arena holds, opens a scope for it, applies it, and judges it.
    // A scope is open on return exactly when this returned true; the caller pops it unless it keeps the position.
    private bool TryOpenCandidate(Job job, int shapeIndex, int token, int candidateIndex, out int target, out long code, out bool accepted) {
        var plan = job.Plan;
        var shape = plan.Shapes[shapeIndex];
        var from = TokenCell(
            job: job,
            token: token
        );
        var onBoard = ((from >= 0L) && (from < plan.CellCount));

        accepted = false;
        code = 0L;
        target = -1;

        if (onBoard != (shape.Kind != SearchShapeKind.Drop)) {
            return false;
        }
        if (!TryResolveCandidate(
            candidateIndex: candidateIndex,
            code: out code,
            companionIndex: out var companionIndex,
            companionTarget: out var companionTarget,
            from: from,
            job: job,
            mid: out var mid,
            shapeIndex: shapeIndex,
            target: out target,
            token: token
        )) {
            return false;
        }

        var mover = Slot(rowOrdinal: plan.TurnOrdinal);

        PushScope(
            candidateIndex: candidateIndex,
            code: code,
            companionIndex: companionIndex,
            companionTarget: companionTarget,
            job: job,
            mid: mid,
            shapeIndex: shapeIndex,
            target: target,
            token: token
        );

        accepted = ((Slot(rowOrdinal: plan.VerdictOrdinal) == plan.Accept) && (Slot(rowOrdinal: plan.TurnOrdinal) != mover));

        return true;
    }
    private bool TryKeepCandidate(Job job, int shapeIndex, int token, int candidateIndex) {
        if (!TryOpenCandidate(
            accepted: out var accepted,
            candidateIndex: candidateIndex,
            code: out _,
            job: job,
            shapeIndex: shapeIndex,
            target: out _,
            token: token
        )) {
            return false;
        }
        if (accepted) {
            return true;
        }

        PopScope(job: job);

        return false;
    }
    private long EvaluateOutcome(Job job) => EvaluateOutcome(
        job: job,
        ply: job.ScopeCount
    );
    // `ply` is how many candidates the position sits below the root. The job's own scope count says so only for a
    // position its scope stack built; the negamax beneath a chance node opens scopes of its own and says so itself.
    private long EvaluateOutcome(Job job, int ply) => (job.Judge.Scores
        ? job.Judge.Score(view: View(ply: ply))
        : 0L
    );
    private void StartTree(Job job) {
        job.Best = -SearchCapacity.MateScore;
        job.BestTarget = -1;
        job.BestToken = -1;
        job.Iteration = 0;
        job.Phase = TreeSelectPhase;
        job.Seed = job.Plan.DrawSeed;
        job.TreeActive = true;
        job.TreeChildCount![0] = 0;
        job.TreeCount = 1;
        job.TreeExpanded![0] = 0L;
        job.TreeFirstChild![0] = -1;
        job.TreeParent![0] = -1;
        job.TreeTotal![0] = 0L;
        job.TreeVisits![0] = 0L;

        BeginIteration(job: job);
    }
    private void BeginIteration(Job job) {
        PopAllScopes(job: job);
        job.Path![0] = 0;
        job.PathLength = 1;
        job.Phase = TreeSelectPhase;
        job.PlayCount = 0;
        job.PlayoutPlies = 0;
    }
    // One judge's worth of tree search.
    private void StepTree(Job job) {
        var plan = job.Plan;
        var shapes = plan.Shapes;
        var cells = plan.CellCount;
        var node = job.Path![(job.PathLength - 1)];

        switch (job.Phase) {
            case TreeSelectPhase: {
                    // The depth cap ends the path before the leaf is ever expanded: a leaf at the cap is scored as
                    // it stands.
                    if ((job.PathLength - 1) >= plan.Depth) {
                        Backpropagate(
                            job: job,
                            value: EvaluateOutcome(job: job)
                        );

                        return;
                    }
                    if (job.TreeExpanded![node] == 0L) {
                        job.Phase = TreeExpandPhase;
                        job.UShape = 0;
                        job.UTarget = 0;
                        job.UToken = 0;

                        return;
                    }
                    if (job.TreeChildCount![node] == 0) {
                        Backpropagate(
                            job: job,
                            value: EvaluateOutcome(job: job)
                        );

                        return;
                    }

                    var chosen = SelectChild(
                        job: job,
                        node: node
                    );

                    if (TryKeepCandidate(
                        candidateIndex: job.TreeTarget![chosen],
                        job: job,
                        shapeIndex: job.TreeShape![chosen],
                        token: job.TreeToken![chosen]
                    )) {
                        job.Path[job.PathLength++] = chosen;
                    } else {
                        // A child that no longer judges accepted (a code change or a shape's own refusal) is a
                        // terminal, and it joins the path so its own visit is counted: selection takes an unvisited
                        // child first, and one never counted would be taken again on every iteration through here.
                        // Its parent folds the same value it would from a path ending at the parent.
                        job.Path[job.PathLength++] = chosen;
                        Backpropagate(
                            job: job,
                            value: EvaluateOutcome(job: job)
                        );
                    }

                    return;
                }
            case TreeExpandPhase: {
                    if (job.UShape >= shapes.Length) {
                        job.TreeExpanded![node] = 1L;

                        if (job.TreeChildCount![node] == 0) {
                            Backpropagate(
                                job: job,
                                value: EvaluateOutcome(job: job)
                            );

                            return;
                        }

                        // The first playout starts from a child drawn from the fresh children.
                        var draw = (job.TreeFirstChild![node] + ((int)(Next(seed: ref job.Seed) % ((ulong)job.TreeChildCount[node]))));

                        if (TryKeepCandidate(
                            candidateIndex: job.TreeTarget![draw],
                            job: job,
                            shapeIndex: job.TreeShape![draw],
                            token: job.TreeToken![draw]
                        )) {
                            job.Path[job.PathLength++] = draw;
                            job.Phase = TreePlayoutPhase;
                            job.PlayoutPlies = 1;
                            job.UScan = 0;
                            job.UStart = ((int)(Next(seed: ref job.Seed) % ((ulong)Math.Max(
                                val1: 1,
                                val2: TotalCandidates(
                                    cellCount: cells,
                                    shapes: shapes,
                                    tokenCount: job.TokenCount
                                )
                            ))));
                        } else {
                            // The drawn child is a terminal and joins the path for the reason a selected one does.
                            job.Path[job.PathLength++] = draw;
                            Backpropagate(
                                job: job,
                                value: EvaluateOutcome(job: job)
                            );
                        }

                        return;
                    }

                    var shape = shapes[job.UShape];

                    if (job.UToken >= job.TokenCount) {
                        job.UShape++;
                        job.UTarget = 0;
                        job.UToken = 0;

                        return;
                    }
                    if (job.UTarget >= shape.CandidateCount(cellCount: cells)) {
                        job.UTarget = 0;
                        job.UToken++;

                        return;
                    }

                    var candidate = job.UTarget++;
                    var kept = TryKeepCandidate(
                        candidateIndex: candidate,
                        job: job,
                        shapeIndex: job.UShape,
                        token: job.UToken
                    );

                    if (kept) {
                        PopScope(job: job);
                    }
                    if (
                        kept &&
                        (job.TreeCount < job.TreeParent!.Length)
                    ) {
                        var child = job.TreeCount++;

                        job.TreeChildCount![child] = 0;
                        job.TreeExpanded![child] = 0L;
                        job.TreeFirstChild![child] = -1;
                        job.TreeParent[child] = node;
                        job.TreeShape![child] = job.UShape;
                        job.TreeTarget![child] = candidate;
                        job.TreeToken![child] = job.UToken;
                        job.TreeTotal![child] = 0L;
                        job.TreeVisits![child] = 0L;

                        if (job.TreeChildCount[node] == 0) {
                            job.TreeFirstChild[node] = child;
                        }

                        job.TreeChildCount[node]++;
                    }

                    return;
                }
            default: {
                    if (job.PlayoutPlies >= plan.Depth) {
                        Backpropagate(
                            job: job,
                            value: EvaluateOutcome(job: job)
                        );

                        return;
                    }

                    // A chance ply draws rather than chooses: one weighted pick over this job's own stream, the same
                    // stream every other playout draw uses, so two jobs sharing a seed make the same choice.
                    if (
                        (plan.Chance is { } chance) &&
                        (job.PlayoutPlies == chance.AtDepth)
                    ) {
                        PushChanceScope(
                            job: job,
                            outcome: SampleChanceOutcome(
                                chance: chance,
                                seed: ref job.Seed
                            )
                        );

                        job.PlayoutPlies++;

                        return;
                    }

                    var total = TotalCandidates(
                        cellCount: cells,
                        shapes: shapes,
                        tokenCount: job.TokenCount
                    );

                    if (job.UScan >= total) {
                        // No candidate of this position is accepted: terminal for the side to move, whose opponent
                        // moved into it.
                        Backpropagate(
                            job: job,
                            value: EvaluateOutcome(job: job)
                        );

                        return;
                    }

                    var flat = ((job.UStart + job.UScan++) % total);

                    DecodeCandidate(
                        candidate: out var candidateIndex,
                        cellCount: cells,
                        flat: flat,
                        shape: out var shapeIndex,
                        shapes: shapes,
                        token: out var token,
                        tokenCount: job.TokenCount
                    );

                    if (TryKeepCandidate(
                        candidateIndex: candidateIndex,
                        job: job,
                        shapeIndex: shapeIndex,
                        token: token
                    )) {
                        job.PlayCount++;
                        job.PlayoutPlies++;
                        job.UScan = 0;
                        job.UStart = ((int)(Next(seed: ref job.Seed) % ((ulong)total)));
                    }

                    return;
                }
        }
    }
    // Folds a leaf value along the path: the leaf's own node gets it as is, its parent negated, and so on up to the
    // root, which counts visits alone. Then either the next iteration begins or the job lands.
    private void Backpropagate(Job job, long value) {
        var sign = 1L;

        for (var index = (job.PathLength - 1); (index >= 0); index--) {
            var node = job.Path![index];

            job.TreeVisits![node]++;

            if (index > 0) {
                job.TreeTotal![node] += (sign * value);
            }

            sign = -sign;
        }

        job.Iteration++;

        if (job.Iteration >= job.Plan.Iterations) {
            PopAllScopes(job: job);

            var root = 0;
            var first = job.TreeFirstChild![root];
            var most = -1L;

            for (var child = first; (child < (first + job.TreeChildCount![root])); child++) {
                if (job.TreeVisits![child] > most) {
                    job.Best = (job.TreeTotal![child] / Math.Max(
                        val1: 1L,
                        val2: job.TreeVisits[child]
                    ));
                    job.BestTarget = ResolveTarget(
                        child: child,
                        job: job
                    );
                    job.BestToken = job.TreeToken![child];
                    most = job.TreeVisits[child];
                }
            }

            job.Running = false;
            job.TreeActive = false;

            return;
        }

        BeginIteration(job: job);
    }
    // The destination cell a root child's candidate lands on, decoded the way the walk would have.
    private int ResolveTarget(Job job, int child) {
        var shape = job.Plan.Shapes[job.TreeShape![child]];
        var candidate = job.TreeTarget![child];

        return (shape.Kind switch {
            SearchShapeKind.Promote => (candidate / Math.Max(
            val1: 1,
            val2: (shape.PromoteTo?.Length ?? 1)
        )),
            SearchShapeKind.Jump => JumpTarget(
            candidate: candidate,
            child: child,
            job: job,
            shape: shape
        ),
            _ => candidate,
        });
    }
    private int JumpTarget(Job job, int child, SearchShapePlan shape, int candidate) {
        if (
            !TryNumberAt(
            position: job.TreeToken![child],
            rowOrdinal: job.Plan.TokensOrdinal,
            value: out var from
        ) ||
            (from < 0L)
        ) {
            return -1;
        }

        var mid = job.Plan.Topology!.Neighbour(
            cell: ((int)from),
            direction: shape.Directions[candidate]
        );

        return ((mid < 0)
            ? -1
            : job.Plan.Topology.Neighbour(
                cell: mid,
                direction: shape.Directions[candidate]
            )
        );
    }
}
