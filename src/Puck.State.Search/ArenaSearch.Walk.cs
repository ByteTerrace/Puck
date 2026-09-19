namespace Puck.State;

public sealed partial class ArenaSearch {
    private const long TranspositionExact = 1L;
    private const long TranspositionLower = 2L;
    private const long TranspositionUpper = 3L;

    private static Level At(Job job, int p) => ((p == 0)
        ? job.Root
        : job.Levels[(p - 1)]
    );

    // One step's worth of iterative-deepening negamax over the job's own explicit ply stack: the recursion below the
    // root runs on that stack rather than the call stack, so a step boundary can suspend it anywhere, a chance ply
    // included. The root ply never prunes and never skips a candidate, so an unscored job's root outputs are the
    // same set either way.
    //
    // Every iteration is one unit. The walk runs a unit only while the step's allowance still holds the costliest
    // unit the job has, then charges what the unit it ran actually cost, so `spent` never passes the allowance.
    // Returns what the step has spent once it yields.
    private long Walk(Job job, long spent) {
        var plan = job.Plan;
        var shapes = plan.Shapes;
        var cells = plan.CellCount;
        var work = plan.Work;

        if (job.TokenCount != job.Legal.Length) {
            job.Running = false;

            return spent;
        }

        // A chance node at the root replaces the whole root's move choice, so the root is the chance ply and the
        // job's one pass searches beneath it with whatever score its judge reads.
        var chanceAt = (plan.Chance?.AtDepth ?? -1);
        var maxN = (plan.ScoresOrdinal >= 0);
        var hasScore = ((chanceAt == 0)
            ? job.Judge.Scores
            : ((plan.Scored || maxN) && (plan.Method == SearchMethod.Negamax))
        );

        if (
            (chanceAt > 0) &&
            !hasScore
        ) {
            chanceAt = -1;
        }

        var wideWords = ((job.Wide is not null)
            ? WideWordsPerToken(cellCount: cells)
            : 0
        );
        var judgedAtEntry = job.Nodes;
        var unit = work.Unit;

        while (
            job.Running &&
            ((job.Nodes - judgedAtEntry) < plan.Nodes) &&
            ((work.Allowance - spent) >= unit)
        ) {
            if (job.TreeActive) {
                spent += StepTree(job: job);

                continue;
            }

            var p = job.Active;
            var level = At(
                job: job,
                p: p
            );

            if (p == chanceAt) {
                spent += StepChance(
                    hasScore: hasScore,
                    job: job,
                    level: level,
                    p: p
                );

                continue;
            }

            var shapeIndex = level.Shape;

            if (shapeIndex >= shapes.Length) {
                CompletePly(
                    chanceAt: chanceAt,
                    hasScore: hasScore,
                    job: job,
                    level: level,
                    maxN: maxN,
                    p: p
                );
                spent += SearchWork.Cursor;

                continue;
            }

            var shape = shapes[shapeIndex];
            var token = level.Token;

            if (token >= job.TokenCount) {
                level.Shape = (shapeIndex + 1);
                level.Target = 0;
                level.Token = 0;
                spent += SearchWork.Cursor;

                continue;
            }

            var from = TokenCell(
                job: job,
                token: token
            );
            var onBoard = ((from >= 0L) && (from < cells));
            var candidateIndex = level.Target;

            // A shape that does not apply to the token where it stands (on the board for every shape but drop, off
            // it for drop) offers it no candidate, and a cursor past the shape's last candidate is done with it.
            if (
                (onBoard != (shape.Kind != SearchShapeKind.Drop)) ||
                (candidateIndex >= shape.CandidateCount(cellCount: cells))
            ) {
                level.Target = 0;
                level.Token = (token + 1);
                spent += SearchWork.Cursor;

                continue;
            }
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
                level.Target = (candidateIndex + 1);
                spent += work.Inspect;

                continue;
            }

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
            spent += work.Candidate;

            var accepted = ((Slot(rowOrdinal: plan.VerdictOrdinal) == plan.Accept) && (Slot(rowOrdinal: plan.TurnOrdinal) != level.BaseTurn));

            if (
                (p == 0) &&
                accepted
            ) {
                job.Count++;
                job.Counts[token]++;

                if (target < BoardMask.MaxCells) {
                    job.Legal[token] |= (1L << target);
                }
                if (job.Wide is { } wide) {
                    SetWideBit(
                        cell: target,
                        token: token,
                        wide: wide,
                        words: wideWords
                    );
                }
            }

            var scores = (hasScore && accepted);
            // A table entry is a negamax value of a position searched without a draw beneath it, so only the plies
            // above the chance ply read and write one.
            var keyed = (!maxN && (job.TtKey is not null) && ((chanceAt < 0) || ((p + 1) < chanceAt)));

            if (
                scores &&
                ((p + 1) == chanceAt)
            ) {
                // The next ply is the job's chance ply: it enumerates outcomes rather than moves, and folds their
                // weighted average back here as this candidate's value.
                var next = OpenPly(
                    alpha: -SearchCapacity.MateScore,
                    beta: SearchCapacity.MateScore,
                    entryTarget: target,
                    job: job,
                    keyed: false,
                    p: (p + 1)
                );

                next.ChanceSum = Int128.Zero;
                next.ChanceWeight = 0UL;
            } else if (
                scores &&
                (p < (job.PassDepth - 1))
            ) {
                if (
                    keyed &&
                    TryProbeTransposition(
                        job: job,
                        level: level,
                        remaining: ((job.PassDepth - p) - 1),
                        value: out var known
                    )
                ) {
                    // The child position was searched to at least this depth already: fold its value without
                    // descending.
                    Fold(
                        level: level,
                        target: target,
                        token: token,
                        value: -known
                    );
                    PopScope(job: job);
                    AdvanceCandidate(
                        cellCount: cells,
                        level: level,
                        p: p,
                        shapes: shapes,
                        tokenCount: job.TokenCount
                    );
                } else {
                    // A max-n level never prunes on another seat's bound — one seat's gain is not another's loss —
                    // so it descends the full window instead of the negamax negate-and-swap.
                    _ = OpenPly(
                        alpha: (maxN
                            ? -SearchCapacity.MateScore
                            : -level.Beta),
                        beta: (maxN
                            ? SearchCapacity.MateScore
                            : -level.Alpha),
                        entryTarget: target,
                        job: job,
                        keyed: keyed,
                        p: (p + 1)
                    );
                }
            } else {
                if (
                    scores &&
                    maxN
                ) {
                    ReadSeats(
                        into: job.Seats,
                        job: job
                    );
                    FoldSeats(
                        job: job,
                        p: p,
                        target: target,
                        token: token,
                        vector: job.Seats
                    );
                } else if (scores) {
                    Fold(
                        level: level,
                        target: target,
                        token: token,
                        value: job.Judge.Score(view: View(ply: MovePly(job: job)))
                    );
                }

                PopScope(job: job);
                AdvanceCandidate(
                    cellCount: cells,
                    level: level,
                    p: p,
                    shapes: shapes,
                    tokenCount: job.TokenCount
                );
            }
        }

        return spent;
    }
    // Opens ply p over the position the arena holds and makes it the active one. A chance ply and the move ply
    // beneath one open on the full window: an average has no bound to prune against.
    private Level OpenPly(Job job, int p, int entryTarget, long alpha, long beta, bool keyed) {
        var level = job.Levels[(p - 1)];

        level.Alpha = alpha;
        level.AlphaEntry = alpha;
        level.BaseTurn = Slot(rowOrdinal: job.Plan.TurnOrdinal);
        level.Best = -SearchCapacity.MateScore;
        level.BestTarget = -1;
        level.BestToken = -1;
        level.Beta = beta;
        level.EntryTarget = entryTarget;
        // Only a ply whose value the table will store needs the key of the position it opened on.
        level.Key = (keyed
            ? PositionKey(job: job)
            : 0UL
        );
        level.Shape = 0;
        level.Target = 0;
        level.Token = 0;

        // The ply starts out remembering the position it was handed; a max-n fold that never improves on it reads
        // the same vector a static evaluation of a side with no move would.
        ReadSeats(
            into: level.Seats,
            job: job
        );
        job.Active = p;

        return level;
    }
    // One unit of a chance ply: the next outcome applied and, at the frontier, scored; or, once every outcome has
    // folded, the ply's weighted average handed to its parent. Returns what the unit cost.
    //
    // The average is the side's that moved into the node. Beneath the root that is the mover of the ply above, so
    // a reply searched under an outcome folds negated like any other; at the root the side to move after the draw
    // is the one the job searches for, and its reply folds as it stands.
    private long StepChance(Job job, Level level, int p, bool hasScore) {
        var chance = job.Plan.Chance!;
        var outcome = level.Target;

        if (outcome >= chance.Weights.Length) {
            var value = RoundedWeightedAverage(
                totalWeight: level.ChanceWeight,
                weightedSum: level.ChanceSum
            );

            if (p == 0) {
                job.Best = value;
                job.BestTarget = -1;
                job.BestToken = -1;
                job.Running = false;
            } else {
                var parent = At(
                    job: job,
                    p: (p - 1)
                );

                Fold(
                    level: parent,
                    target: level.EntryTarget,
                    token: parent.Token,
                    value: value
                );
                PopScope(job: job);
                AdvanceCandidate(
                    cellCount: job.Plan.CellCount,
                    level: parent,
                    p: (p - 1),
                    shapes: job.Plan.Shapes,
                    tokenCount: job.TokenCount
                );
                job.Active = (p - 1);
            }

            return SearchWork.Cursor;
        }

        PushChanceScope(
            job: job,
            outcome: outcome
        );

        if (
            hasScore &&
            (((job.PassDepth - p) - 1) > 0)
        ) {
            _ = OpenPly(
                alpha: -SearchCapacity.MateScore,
                beta: SearchCapacity.MateScore,
                entryTarget: -1,
                job: job,
                keyed: false,
                p: (p + 1)
            );
        } else {
            FoldOutcome(
                level: level,
                value: EvaluateOutcome(job: job),
                weight: chance.Weights[outcome]
            );
            PopScope(job: job);
            level.Target = (outcome + 1);
        }

        return job.Plan.Work.Outcome;
    }
    // Ply p has tried every candidate. The root starts its next pass, hands over to the tree, or finishes; a ply
    // beneath a chance ply folds its reply into the outcome that opened it; every other ply folds into the
    // candidate that opened it and lets the parent move on.
    private void CompletePly(Job job, Level level, int p, int chanceAt, bool hasScore, bool maxN) {
        var plan = job.Plan;

        if (p == 0) {
            if (
                !hasScore ||
                (job.PassDepth >= plan.Depth)
            ) {
                if (plan.Method == SearchMethod.Tree) {
                    StartTree(job: job);
                } else {
                    job.Running = false;
                }
            } else {
                job.PassDepth++;

                ResetPass(job: job);
            }

            return;
        }

        var parent = At(
            job: job,
            p: (p - 1)
        );

        if ((p - 1) == chanceAt) {
            FoldOutcome(
                level: parent,
                value: ((chanceAt == 0)
                    ? level.Best
                    : -level.Best),
                weight: plan.Chance!.Weights[parent.Target]
            );
            PopScope(job: job);
            parent.Target++;
            job.Active = (p - 1);

            return;
        }

        if (maxN) {
            // The completed ply's own side buffer carries the best line's vector — the leaf's, if one was ever
            // folded there, or, when no candidate was accepted, the position's own scores as the ply found them.
            FoldSeats(
                job: job,
                p: (p - 1),
                target: level.EntryTarget,
                token: parent.Token,
                vector: level.Seats
            );
        } else {
            if (
                (chanceAt < 0) ||
                (p < chanceAt)
            ) {
                StoreTransposition(
                    job: job,
                    level: level,
                    remaining: (job.PassDepth - p)
                );
            }

            Fold(
                level: parent,
                target: level.EntryTarget,
                token: parent.Token,
                value: -level.Best
            );
        }

        PopScope(job: job);
        AdvanceCandidate(
            cellCount: plan.CellCount,
            level: parent,
            p: (p - 1),
            shapes: plan.Shapes,
            tokenCount: job.TokenCount
        );
        job.Active = (p - 1);
    }
    // Reads a stored value for the position the arena holds when it was searched to at least `remaining` plies and
    // its bound decides the child's window (-beta, -alpha) the way a fresh search would have.
    private bool TryProbeTransposition(Job job, Level level, int remaining, out long value) {
        value = 0L;

        if (job.TtKey is not { } keys) {
            return false;
        }

        var key = PositionKey(job: job);
        var slot = ((int)(key & ((ulong)(keys.Length - 1))));
        var meta = job.TtMeta![slot];

        if (
            (meta == 0L) ||
            (keys[slot] != key) ||
            ((meta & 0xFFL) < remaining)
        ) {
            return false;
        }

        var stored = job.TtValue![slot];
        var flag = (meta >> 8);
        var childAlpha = -level.Beta;
        var childBeta = -level.Alpha;

        if (
            (flag == TranspositionExact) ||
            ((flag == TranspositionLower) && (stored >= childBeta)) ||
            ((flag == TranspositionUpper) && (stored <= childAlpha))
        ) {
            value = stored;

            return true;
        }

        return false;
    }
    // Stores a completed level's value for its position, flagged by where it landed against the window it entered with.
    private static void StoreTransposition(Job job, Level level, int remaining) {
        if (job.TtKey is not { } keys) {
            return;
        }

        var slot = ((int)(level.Key & ((ulong)(keys.Length - 1))));
        var flag = ((level.Best <= level.AlphaEntry)
            ? TranspositionUpper
            : ((level.Best >= level.Beta)
                ? TranspositionLower
                : TranspositionExact
        ));

        job.TtMeta![slot] = (flag << 8) | ((long)remaining);
        job.TtValue![slot] = level.Best;
        keys[slot] = level.Key;
    }
    private static void AdvanceCandidate(Level level, int p, SearchShapePlan[] shapes, int cellCount, int tokenCount) {
        var candidate = (level.Target + 1);

        if (candidate >= shapes[level.Shape].CandidateCount(cellCount: cellCount)) {
            level.Token++;
            candidate = 0;
        }

        level.Target = candidate;

        // The root ply never prunes: every candidate is tried, so the root outputs are exhaustive whether or not a
        // score is authored. A ply past the root may cut once its window has closed — forcing both the token and
        // shape cursors to their sentinel completes the ply on the very next iteration.
        if (
            (p > 0) &&
            (level.Alpha >= level.Beta)
        ) {
            level.Shape = shapes.Length;
            level.Token = tokenCount;
        }
    }
    // Reads every seat's own score off the arena, in the scores row's own cell order. A seat the row does not cover
    // reads 0 rather than throwing, since the turn row's envelope is what bounds a mover value.
    private void ReadSeats(Job job, long[] into) {
        for (var seat = 0; (seat < into.Length); seat++) {
            into[seat] = (TryNumberAt(
                position: seat,
                rowOrdinal: job.Plan.ScoresOrdinal,
                value: out var value
            )
                ? value
                : 0L
            );
        }
    }
    // Max-n's own fold: ply p's mover maximizes its own seat's entry of the vector, never a negated reply, so one
    // seat's gain need not be another's loss. An improving candidate's whole vector is remembered in ply p's side
    // buffer, which a later fold up to p's parent reads its own seat back out of. Alpha is left alone, so the
    // window never closes and AdvanceCandidate's cutoff never fires.
    private static void FoldSeats(Job job, int p, ReadOnlySpan<long> vector, int token, int target) {
        var level = At(
            job: job,
            p: p
        );
        var mover = level.BaseTurn;
        var value = (((mover >= 0L) && (mover < vector.Length))
            ? vector[((int)mover)]
            : 0L
        );

        if (value > level.Best) {
            level.Best = value;
            level.BestTarget = target;
            level.BestToken = token;

            if (p > 0) {
                vector.CopyTo(destination: level.Seats);
            }
        }
    }
    private static void Fold(Level level, long value, int token, int target) {
        if (value > level.Best) {
            level.Best = value;
            level.BestTarget = target;
            level.BestToken = token;
        }
        if (value > level.Alpha) {
            level.Alpha = value;
        }
    }
}
