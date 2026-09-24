using System.Numerics;

using Puck.Maths;

namespace Puck.State;

public sealed partial class ArenaSearch {
    // The tree search a job with the MonteCarlo method runs once its root walk has landed: at most one judge a step,
    // resumable at any phase. Selection descends by UCB1 through nodes that may hold no further child. A node that
    // may grow scans its own candidates, from a start drawn when the node was made, until one is accepted; that one
    // becomes the node's newest child and the playout starts from it. A playout draws from the job's own seeded
    // stream until no candidate is accepted or the depth cap is reached, and the score folds back along the path with
    // alternating sign, read from the perspective of the side that moved into the position it describes.
    //
    // A node holds at most one child more than the integer square root of its visits (progressive widening), so a
    // position with hundreds of candidates grows its children with its visits instead of spending every iteration on
    // a different child of the root, and one iteration judges a handful of candidates, never a whole board.
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
    // The children a node may hold after its visits: one, and one more for every whole step of the square root.
    private static bool MayGrow(Job job, int node, int total) => (
        (job.TreeScanned![node] < total) &&
        (job.TreeChildCount![node] <= ((long)((ulong)job.TreeVisits![node]).SquareRoot()))
    );
    // UCB1 over the children's means (outcome scale) with a log2-based exploration term, ties to the eldest child.
    private static int SelectChild(Job job, int node) {
        var parentVisits = Math.Max(
            val1: 1L,
            val2: job.TreeVisits![node]
        );
        var log2 = (63 - BitOperations.LeadingZeroCount(value: ((ulong)parentVisits)));
        var best = -1;
        var bestScore = long.MinValue;

        for (var child = job.TreeFirstChild![node]; (child >= 0); child = job.TreeNextSibling![child]) {
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

        return best;
    }
    // Makes a node in the pool, draws the flat candidate its own expansion will scan from, and appends it to its
    // parent's children so a node's children stand in the order they were made.
    private static int NewNode(Job job, int parent, int shape, int token, int target, int total) {
        var node = job.TreeCount++;

        job.TreeChildCount![node] = 0;
        job.TreeFirstChild![node] = -1;
        job.TreeNextSibling![node] = -1;
        job.TreeParent![node] = parent;
        job.TreeScanned![node] = 0;
        job.TreeScanStart![node] = ((int)(Next(seed: ref job.Seed) % ((ulong)Math.Max(
            val1: 1,
            val2: total
        ))));
        job.TreeShape![node] = shape;
        job.TreeTarget![node] = target;
        job.TreeToken![node] = token;
        job.TreeTotal![node] = 0L;
        job.TreeVisits![node] = 0L;

        if (parent < 0) {
            return node;
        }
        if (job.TreeFirstChild[parent] < 0) {
            job.TreeFirstChild[parent] = node;
        } else {
            var last = job.TreeFirstChild[parent];

            while (job.TreeNextSibling[last] >= 0) {
                last = job.TreeNextSibling[last];
            }

            job.TreeNextSibling[last] = node;
        }

        job.TreeChildCount[parent]++;

        return node;
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
    // The score of the position the arena holds, from the perspective of the side that moved into it.
    private long EvaluateOutcome(Job job) => (job.Judge.Scores
        ? job.Judge.Score(view: View(ply: MovePly(job: job)))
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
        job.TreeCount = 0;

        _ = NewNode(
            job: job,
            parent: -1,
            shape: -1,
            target: -1,
            token: -1,
            total: TotalCandidates(
                cellCount: job.Plan.CellCount,
                shapes: job.Plan.Shapes,
                tokenCount: job.TokenCount
            )
        );
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
    // A playout from the position the arena holds, which the path's last candidate (or none) reached.
    private static void StartPlayout(Job job, int plies, int total) {
        job.Phase = TreePlayoutPhase;
        job.PlayoutPlies = plies;
        job.UScan = 0;
        job.UStart = ((int)(Next(seed: ref job.Seed) % ((ulong)Math.Max(
            val1: 1,
            val2: total
        ))));
    }
    // One unit of tree search. Returns what it cost: a cursor move, an inspected or a judged candidate, a chance
    // draw, or a whole step that selects, opens, scores, and folds back.
    private long StepTree(Job job) {
        var plan = job.Plan;
        var work = plan.Work;
        var judged = job.Nodes;
        var shapes = plan.Shapes;
        var cells = plan.CellCount;
        var node = job.Path![(job.PathLength - 1)];
        var total = TotalCandidates(
            cellCount: cells,
            shapes: shapes,
            tokenCount: job.TokenCount
        );
        var poolFull = (job.TreeCount >= job.TreeParent!.Length);

        switch (job.Phase) {
            case TreeSelectPhase: {
                    // The depth cap ends the path before the leaf ever grows: a leaf at the cap is scored as it
                    // stands.
                    if ((job.PathLength - 1) >= plan.Depth) {
                        Backpropagate(
                            job: job,
                            value: EvaluateOutcome(job: job)
                        );

                        return work.TreeStep;
                    }
                    if (
                        !poolFull &&
                        MayGrow(
                            job: job,
                            node: node,
                            total: total
                        )
                    ) {
                        job.Phase = TreeExpandPhase;

                        return SearchWork.Cursor;
                    }
                    if (job.TreeChildCount![node] == 0) {
                        // Every candidate was scanned and none accepted: a terminal for the side to move. A node the
                        // full pool keeps from growing plays out from where it stands instead.
                        if (job.TreeScanned![node] >= total) {
                            Backpropagate(
                                job: job,
                                value: EvaluateOutcome(job: job)
                            );

                            return work.TreeStep;
                        }

                        StartPlayout(
                            job: job,
                            plies: 0,
                            total: total
                        );

                        return SearchWork.Cursor;
                    }

                    var chosen = SelectChild(
                        job: job,
                        node: node
                    );

                    // A child that no longer judges accepted (a code change or a shape's own refusal) is a terminal,
                    // and it joins the path so its own visit is counted: selection takes an unvisited child first,
                    // and one never counted would be taken again on every iteration through here. Its parent folds
                    // the same value it would from a path ending at the parent.
                    job.Path[job.PathLength++] = chosen;

                    if (!TryKeepCandidate(
                        candidateIndex: job.TreeTarget![chosen],
                        job: job,
                        shapeIndex: job.TreeShape![chosen],
                        token: job.TreeToken![chosen]
                    )) {
                        Backpropagate(
                            job: job,
                            value: EvaluateOutcome(job: job)
                        );
                    }

                    return work.TreeStep;
                }
            case TreeExpandPhase: {
                    if (
                        poolFull ||
                        !MayGrow(
                            job: job,
                            node: node,
                            total: total
                        )
                    ) {
                        job.Phase = TreeSelectPhase;

                        return SearchWork.Cursor;
                    }

                    var flat = ((int)((job.TreeScanStart![node] + ((long)job.TreeScanned![node])) % total));

                    job.TreeScanned[node]++;
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
                        job.Path[job.PathLength++] = NewNode(
                            job: job,
                            parent: node,
                            shape: shapeIndex,
                            target: candidateIndex,
                            token: token,
                            total: total
                        );
                        m_expansions.Increment();
                        StartPlayout(
                            job: job,
                            plies: 1,
                            total: total
                        );
                    } else if (job.TreeScanned[node] >= total) {
                        job.Phase = TreeSelectPhase;
                    }

                    return ((job.Nodes != judged)
                        ? work.Candidate
                        : work.Inspect
                    );
                }
            default: {
                    if (job.PlayoutPlies >= plan.Depth) {
                        Backpropagate(
                            job: job,
                            value: EvaluateOutcome(job: job)
                        );

                        return work.TreeStep;
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

                        return work.Outcome;
                    }
                    if (job.UScan >= total) {
                        // No candidate of this position is accepted: terminal for the side to move, whose opponent
                        // moved into it.
                        Backpropagate(
                            job: job,
                            value: EvaluateOutcome(job: job)
                        );

                        return work.TreeStep;
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
                        m_playoutPlies.Increment();
                        job.UScan = 0;
                        job.UStart = ((int)(Next(seed: ref job.Seed) % ((ulong)total)));
                    }

                    return ((job.Nodes != judged)
                        ? work.Candidate
                        : work.Inspect
                    );
                }
        }
    }
    // Folds a leaf value along the path: the leaf's own node gets it as is, its parent negated, and so on up to the
    // root, which counts visits alone. Then either the next iteration begins or the job lands the root's most visited
    // child, the eldest among equals.
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

            var most = -1L;

            for (var child = job.TreeFirstChild![0]; (child >= 0); child = job.TreeNextSibling![child]) {
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
