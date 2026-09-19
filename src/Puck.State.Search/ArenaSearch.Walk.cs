namespace Puck.State;

public sealed partial class ArenaSearch {
    private const long TranspositionExact = 1L;
    private const long TranspositionLower = 2L;
    private const long TranspositionUpper = 3L;

    // One step's worth of iterative-deepening negamax over the job's own explicit ply stack: the recursion below the
    // root runs on that stack rather than the call stack, so a step boundary can suspend it anywhere. The root ply
    // never prunes and never skips a candidate, so an unscored job's root outputs are the same set either way.
    private void Walk(Job job) {
        var plan = job.Plan;
        var shapes = plan.Shapes;
        var cells = plan.CellCount;

        if (job.TokenCount != job.Legal.Length) {
            job.Running = false;

            return;
        }

        // A chance node at the root replaces the whole root's move choice — there is nothing to choose before the
        // draw, so this job never enumerates a shape and lands its one averaged value in a single step. One step is
        // one pass, so it searches the plan's whole depth rather than the first deepening pass's: the draw spends
        // one ply of it, as a chance node inside the walk does.
        if (plan.Chance is { AtDepth: 0 } rootChance) {
            job.Best = ChanceExpectation(
                atRoot: true,
                job: job,
                remainingDepth: (plan.Depth - 1)
            );
            job.BestTarget = -1;
            job.BestToken = -1;
            job.Nodes += Math.Max(
                val1: 1,
                val2: rootChance.Weights.Length
            );
            job.Running = false;

            return;
        }

        var maxN = (plan.ScoresOrdinal >= 0);
        var hasScore = ((plan.Scored || maxN) && (plan.Method == SearchMethod.Negamax));
        var wideWords = ((job.Wide is not null)
            ? WideWordsPerToken(cellCount: cells)
            : 0
        );
        var budget = plan.Nodes;

        while (
            (budget > 0) &&
            job.Running
        ) {
            if (job.TreeActive) {
                StepTree(job: job);
                job.Nodes++;
                budget--;

                continue;
            }

            var p = job.Active;
            var shapeIndex = CursorShape(
                job: job,
                p: p
            );

            if (shapeIndex >= shapes.Length) {
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
                } else {
                    var parent = (p - 1);

                    if (maxN) {
                        // The completed ply's own side buffer carries the best line's vector — the leaf's, if one
                        // was ever folded there, or, when no candidate was accepted, the position's own scores as
                        // the ply found them.
                        FoldSeats(
                            job: job,
                            p: parent,
                            target: CursorTarget(
                                job: job,
                                p: parent
                            ),
                            token: CursorToken(
                                job: job,
                                p: parent
                            ),
                            vector: job.Levels[(p - 1)].Seats
                        );
                    } else {
                        StoreTransposition(
                            job: job,
                            level: job.Levels[(p - 1)],
                            remaining: (job.PassDepth - p)
                        );
                        Fold(
                            job: job,
                            p: parent,
                            target: CursorTarget(
                                job: job,
                                p: parent
                            ),
                            token: CursorToken(
                                job: job,
                                p: parent
                            ),
                            value: -CursorBest(
                                job: job,
                                p: p
                            )
                        );
                    }

                    PopScope(job: job);
                    AdvanceCandidate(
                        cellCount: cells,
                        job: job,
                        p: parent,
                        shapes: shapes,
                        tokenCount: job.TokenCount
                    );
                    job.Active = parent;
                }

                continue;
            }

            var shape = shapes[shapeIndex];
            var token = CursorToken(
                job: job,
                p: p
            );

            if (token >= job.TokenCount) {
                SetCursorShape(
                    job: job,
                    p: p,
                    value: (shapeIndex + 1)
                );
                SetCursorTarget(
                    job: job,
                    p: p,
                    value: 0
                );
                SetCursorToken(
                    job: job,
                    p: p,
                    value: 0
                );

                continue;
            }

            var from = TokenCell(
                job: job,
                token: token
            );
            var onBoard = ((from >= 0L) && (from < cells));

            if (onBoard != (shape.Kind != SearchShapeKind.Drop)) {
                // This shape does not apply to the token in its current state (on the board for every shape but
                // drop, off it for drop), so every candidate for this token under this shape is skipped.
                SetCursorTarget(
                    job: job,
                    p: p,
                    value: 0
                );
                SetCursorToken(
                    job: job,
                    p: p,
                    value: (token + 1)
                );

                continue;
            }

            var candidateIndex = CursorTarget(
                job: job,
                p: p
            );
            var bound = shape.CandidateCount(cellCount: cells);

            if (candidateIndex >= bound) {
                SetCursorTarget(
                    job: job,
                    p: p,
                    value: 0
                );
                SetCursorToken(
                    job: job,
                    p: p,
                    value: (token + 1)
                );

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
                SetCursorTarget(
                    job: job,
                    p: p,
                    value: (candidateIndex + 1)
                );

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

            var mover = CursorBaseTurn(
                job: job,
                p: p
            );
            var accepted = ((Slot(rowOrdinal: plan.VerdictOrdinal) == plan.Accept) && (Slot(rowOrdinal: plan.TurnOrdinal) != mover));

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

            var descends = (hasScore && accepted && (p < (job.PassDepth - 1)));

            if (
                hasScore &&
                accepted &&
                (plan.Chance is { } chance) &&
                (chance.AtDepth == (p + 1))
            ) {
                // The next ply is the job's own chance node: it never enumerates a move of its own, so this
                // candidate folds directly to the chance-averaged value of the position it reached.
                Fold(
                    job: job,
                    p: p,
                    target: target,
                    token: token,
                    value: ChanceExpectation(
                        atRoot: false,
                        job: job,
                        remainingDepth: ((job.PassDepth - p) - 2)
                    )
                );
                PopScope(job: job);
                AdvanceCandidate(
                    cellCount: cells,
                    job: job,
                    p: p,
                    shapes: shapes,
                    tokenCount: job.TokenCount
                );
            } else if (
                descends &&
                !maxN &&
                TryProbeTransposition(
                job: job,
                p: p,
                remaining: ((job.PassDepth - p) - 1),
                value: out var known
            )
            ) {
                // The child position was searched to at least this depth already: fold its value without descending.
                Fold(
                    job: job,
                    p: p,
                    target: target,
                    token: token,
                    value: -known
                );
                PopScope(job: job);
                AdvanceCandidate(
                    cellCount: cells,
                    job: job,
                    p: p,
                    shapes: shapes,
                    tokenCount: job.TokenCount
                );
            } else if (descends) {
                var next = (p + 1);
                var level = job.Levels[(next - 1)];

                level.AlphaEntry = -CursorBeta(
                    job: job,
                    p: p
                );
                level.Key = PositionKey(job: job);

                // The child ply starts out remembering the position it was handed; a max-n fold that never improves
                // on it reads the same vector a static evaluation of a side with no move would.
                ReadSeats(
                    into: level.Seats,
                    job: job
                );
                SetCursorAlpha(
                    job: job,
                    p: next,
                    // A max-n level never prunes on another seat's bound — one seat's gain is not another's loss —
                    // so it descends the full window instead of the negamax negate-and-swap.
                    value: (maxN
                        ? -SearchCapacity.MateScore
                        : -CursorBeta(
                            job: job,
                            p: p
                        ))
                );
                SetCursorBaseTurn(
                    job: job,
                    p: next,
                    value: Slot(rowOrdinal: plan.TurnOrdinal)
                );
                SetCursorBest(
                    job: job,
                    p: next,
                    value: -SearchCapacity.MateScore
                );
                SetCursorBestMove(
                    job: job,
                    p: next,
                    target: -1,
                    token: -1
                );
                SetCursorBeta(
                    job: job,
                    p: next,
                    value: (maxN
                        ? SearchCapacity.MateScore
                        : -CursorAlpha(
                            job: job,
                            p: p
                        ))
                );
                SetCursorShape(
                    job: job,
                    p: next,
                    value: 0
                );
                SetCursorTarget(
                    job: job,
                    p: next,
                    value: 0
                );
                SetCursorToken(
                    job: job,
                    p: next,
                    value: 0
                );
                job.Active = next;
            } else {
                if (
                    hasScore &&
                    accepted &&
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
                } else if (
                    hasScore &&
                    accepted
                ) {
                    Fold(
                        job: job,
                        p: p,
                        target: target,
                        token: token,
                        value: job.Judge.Score(view: View(ply: job.ScopeCount))
                    );
                }

                PopScope(job: job);
                AdvanceCandidate(
                    cellCount: cells,
                    job: job,
                    p: p,
                    shapes: shapes,
                    tokenCount: job.TokenCount
                );
            }

            job.Nodes++;
            budget--;
        }
    }
    // Reads a stored value for the position the arena holds when it was searched to at least `remaining` plies and
    // its bound decides the child's window (-beta, -alpha) the way a fresh search would have.
    private bool TryProbeTransposition(Job job, int p, int remaining, out long value) {
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
        var childAlpha = -CursorBeta(
            job: job,
            p: p
        );
        var childBeta = -CursorAlpha(
            job: job,
            p: p
        );

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
    private static void AdvanceCandidate(Job job, int p, SearchShapePlan[] shapes, int cellCount, int tokenCount) {
        var shapeIndex = CursorShape(
            job: job,
            p: p
        );
        var bound = shapes[shapeIndex].CandidateCount(cellCount: cellCount);
        var candidate = (CursorTarget(
            job: job,
            p: p
        ) + 1);

        if (candidate >= bound) {
            SetCursorToken(
                job: job,
                p: p,
                value: (CursorToken(
                    job: job,
                    p: p
                ) + 1)
            );
            candidate = 0;
        }

        SetCursorTarget(
            job: job,
            p: p,
            value: candidate
        );

        // The root ply never prunes: every candidate is tried, so the root outputs are exhaustive whether or not a
        // score is authored. A ply past the root may cut once its window has closed — forcing both the token and
        // shape cursors to their sentinel completes the ply on the very next iteration.
        if (
            (p > 0) &&
            (CursorAlpha(
            job: job,
            p: p
        ) >= CursorBeta(
            job: job,
            p: p
        ))
        ) {
            SetCursorShape(
                job: job,
                p: p,
                value: shapes.Length
            );
            SetCursorToken(
                job: job,
                p: p,
                value: tokenCount
            );
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
        var mover = CursorBaseTurn(
            job: job,
            p: p
        );
        var value = (((mover >= 0L) && (mover < vector.Length))
            ? vector[((int)mover)]
            : 0L
        );

        if (value > CursorBest(
            job: job,
            p: p
        )) {
            SetCursorBest(
                job: job,
                p: p,
                value: value
            );
            SetCursorBestMove(
                job: job,
                p: p,
                target: target,
                token: token
            );

            if (p > 0) {
                vector.CopyTo(destination: job.Levels[(p - 1)].Seats);
            }
        }
    }
    private static void Fold(Job job, int p, long value, int token, int target) {
        if (value > CursorBest(
            job: job,
            p: p
        )) {
            SetCursorBest(
                job: job,
                p: p,
                value: value
            );
            SetCursorBestMove(
                job: job,
                p: p,
                target: target,
                token: token
            );
        }
        if (value > CursorAlpha(
            job: job,
            p: p
        )) {
            SetCursorAlpha(
                job: job,
                p: p,
                value: value
            );
        }
    }
    private static long CursorAlpha(Job job, int p) => ((p == 0)
        ? job.Alpha
        : job.Levels[(p - 1)].Alpha
    );
    private static long CursorBaseTurn(Job job, int p) => ((p == 0)
        ? job.BaseTurn
        : job.Levels[(p - 1)].BaseTurn
    );
    private static long CursorBest(Job job, int p) => ((p == 0)
        ? job.Best
        : job.Levels[(p - 1)].Best
    );
    private static long CursorBeta(Job job, int p) => ((p == 0)
        ? job.Beta
        : job.Levels[(p - 1)].Beta
    );
    private static int CursorShape(Job job, int p) => ((p == 0)
        ? job.Shape
        : job.Levels[(p - 1)].Shape
    );
    private static int CursorTarget(Job job, int p) => ((p == 0)
        ? job.Target
        : job.Levels[(p - 1)].Target
    );
    private static int CursorToken(Job job, int p) => ((p == 0)
        ? job.Token
        : job.Levels[(p - 1)].Token
    );
    private static void SetCursorAlpha(Job job, int p, long value) {
        if (p == 0) {
            job.Alpha = value;
        } else {
            job.Levels[(p - 1)].Alpha = value;
        }
    }
    private static void SetCursorBaseTurn(Job job, int p, long value) {
        if (p == 0) {
            job.BaseTurn = value;
        } else {
            job.Levels[(p - 1)].BaseTurn = value;
        }
    }
    private static void SetCursorBest(Job job, int p, long value) {
        if (p == 0) {
            job.Best = value;
        } else {
            job.Levels[(p - 1)].Best = value;
        }
    }
    private static void SetCursorBestMove(Job job, int p, int token, int target) {
        if (p == 0) {
            job.BestTarget = target;
            job.BestToken = token;
        } else {
            job.Levels[(p - 1)].BestTarget = target;
            job.Levels[(p - 1)].BestToken = token;
        }
    }
    private static void SetCursorBeta(Job job, int p, long value) {
        if (p == 0) {
            job.Beta = value;
        } else {
            job.Levels[(p - 1)].Beta = value;
        }
    }
    private static void SetCursorShape(Job job, int p, int value) {
        if (p == 0) {
            job.Shape = value;
        } else {
            job.Levels[(p - 1)].Shape = value;
        }
    }
    private static void SetCursorTarget(Job job, int p, int value) {
        if (p == 0) {
            job.Target = value;
        } else {
            job.Levels[(p - 1)].Target = value;
        }
    }
    private static void SetCursorToken(Job job, int p, int value) {
        if (p == 0) {
            job.Token = value;
        } else {
            job.Levels[(p - 1)].Token = value;
        }
    }
}
