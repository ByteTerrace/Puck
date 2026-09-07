using Puck.Maths;
namespace Puck.State;

public sealed partial class SearchRuntime {
    /// <summary>Gets how many 64-bit words one token's wide reach bitset needs for a board of <paramref name="cellCount"/> cells.</summary>
    private static int WideWordsPerToken(int cellCount) => ((cellCount + 63) >> 6);

    private static void SetWideBit(long[] wide, int token, int cell, int words) {
        var index = ((token * words) + (cell >> 6));

        wide[index] |= (1L << (cell & 63));
    }
    private static bool WideBit(long[] wide, int token, int cell, int words) {
        var index = ((token * words) + (cell >> 6));

        return ((wide[index] & (1L << (cell & 63))) != 0L);
    }

    // One candidate per node: a shape's own move applies to a scratch copy of the position, the judge runs, and the
    // verdict at its accept value with the turn changed is an accepted candidate. Ply 0 is the root — its own
    // (shape, token, target/direction) cursor lives on the job and is always exhausted before the job finishes, so
    // the root outputs never depend on whether a score is authored or how deep the search goes. A ply past 0 lives
    // on job.Levels and exists only long enough to negamax one accepted root candidate (or a descendant of one) to
    // the pass's depth. The walk enumerates in fixed order: shape, then token, then target/direction.
    private void Walk(Job job, ulong tick) {
        var host = m_host!;
        var scratch = host.Frame;
        var plan = job.Plan;
        var rows = m_base!.Rows;

        // A chance node at the root replaces the whole root's move choice — there is nothing to choose before the
        // draw, so this job never enumerates a shape and lands its one averaged value in a single step.
        if (plan.Chance is { AtDepth: 0 } rootChance) {
            var chanceRow = StateRows.FindStateRow(rows: rows, name: rootChance.Row);

            job.Best = ((chanceRow is not null) ? ChanceExpectation(job: job, position: m_base!, chanceRow: chanceRow, remainingDepth: (job.PassDepth - 1), tick: tick) : 0L);
            job.BestToken = -1;
            job.BestTarget = -1;
            job.Nodes += Math.Max(val1: 1, val2: rootChance.Weights.Length);
            job.Running = false;

            return;
        }

        var tokens = StateRows.FindStateRow(rows: rows, name: plan.Tokens);
        var turn = StateRows.FindStateRow(rows: rows, name: plan.Turn);
        var verdict = StateRows.FindStateRow(rows: rows, name: plan.Verdict);
        var cells = plan.CellCount;
        var shapes = plan.Shapes;

        if ((tokens?.Cells is not { } tokenCells) || (turn is null) || (verdict is null) || (tokenCells.Count != job.Legal.Length)) {
            job.Running = false;

            return;
        }

        var hasScore = ((plan.Score is not null) && (plan.Method == SearchMethod.Negamax));
        var wideWords = ((job.Wide is not null) ? WideWordsPerToken(cellCount: cells) : 0);
        var budget = plan.Nodes;

        while ((budget > 0) && job.Running) {
            if (job.UctActive) {
                StepUct(job: job, tick: tick, tokens: tokens, tokenCells: tokenCells, rows: rows);
                job.Nodes++;
                budget--;

                continue;
            }

            var p = job.Active;
            var frame = Position(job: job, p: p);
            var shapeIndex = CursorShape(job: job, p: p);

            if (shapeIndex >= shapes.Length) {
                if (p == 0) {
                    if (!hasScore || (job.PassDepth >= plan.Depth)) {
                        if (plan.Method == SearchMethod.Tree) {
                            StartUct(job: job);
                        } else {
                            job.Running = false;
                        }
                    } else {
                        job.PassDepth++;
                        ResetPass(job: job);
                    }
                } else {
                    var value = -CursorBest(job: job, p: p);
                    var parent = (p - 1);

                    StoreTransposition(job: job, level: job.Levels[p - 1], remaining: (job.PassDepth - p));
                    Fold(job: job, p: parent, value: value, token: CursorToken(job: job, p: parent), target: CursorTarget(job: job, p: parent));
                    AdvanceCandidate(job: job, p: parent, shapes: shapes, cellCount: cells, tokenCount: tokenCells.Count);
                    job.Active = parent;
                }

                continue;
            }

            var shape = shapes[shapeIndex];
            var token = CursorToken(job: job, p: p);

            if (token >= tokenCells.Count) {
                SetCursorShape(job: job, p: p, value: (shapeIndex + 1));
                SetCursorToken(job: job, p: p, value: 0);
                SetCursorTarget(job: job, p: p, value: 0);

                continue;
            }

            var from = TokenCell(job: job, frame: frame, tokens: tokens, tokenCells: tokenCells, token: token);
            var onBoard = ((from >= 0L) && (from < cells));

            if (onBoard != (shape.Kind != SearchShapeKind.Drop)) {
                // This shape does not apply to the token in its current state (on the board for every shape but
                // drop, off it for drop) — skip every candidate for this token under this shape.
                SetCursorToken(job: job, p: p, value: (token + 1));
                SetCursorTarget(job: job, p: p, value: 0);

                continue;
            }

            var candidateIndex = CursorTarget(job: job, p: p);
            var bound = shape.CandidateCount(cellCount: cells);

            if (candidateIndex >= bound) {
                SetCursorToken(job: job, p: p, value: (token + 1));
                SetCursorTarget(job: job, p: p, value: 0);

                continue;
            }
            if (!TryResolveCandidate(shape: shape, plan: plan, zones: job.ZoneRows, frame: frame, tokens: tokens, tokenCells: tokenCells, token: token, from: from, candidateIndex: candidateIndex, cells: cells,
                target: out var target, mid: out var mid, companionIndex: out var companionIndex, companionTarget: out var companionTarget, code: out var code)) {
                SetCursorTarget(job: job, p: p, value: (candidateIndex + 1));

                continue;
            }

            scratch.CopyFrom(other: frame);
            ApplyCandidate(shape: shape, plan: plan, zones: job.ZoneRows, frame: frame, scratch: scratch, rows: rows, tokens: tokens, tokenCells: tokenCells, token: token, from: from,
                target: target, mid: mid, companionIndex: companionIndex, companionTarget: companionTarget, code: code);

            _ = host.Judge(rules: m_judge, tick: tick);

            var mover = CursorBaseTurn(job: job, p: p);
            var accepted = ((Slot(store: scratch, name: plan.Verdict) == plan.Accept) && (Slot(store: scratch, name: plan.Turn) != mover));

            if ((p == 0) && accepted) {
                job.Count++;
                job.Counts[token]++;

                if (target < BoardMask.MaxCells) {
                    job.Legal[token] |= (1L << target);
                }
                if (job.Wide is { } wide) {
                    SetWideBit(wide: wide, token: token, cell: target, words: wideWords);
                }
            }
            if (hasScore && accepted && (plan.Chance is { } chance) && (chance.AtDepth == (p + 1))) {
                // The next ply is the job's own chance node: it never enumerates a move of its own, so this candidate
                // folds directly to the chance-averaged value of the position it reached, exactly as a leaf does.
                var chanceRow = StateRows.FindStateRow(rows: rows, name: chance.Row);
                var value = ((chanceRow is not null) ? ChanceExpectation(job: job, position: scratch, chanceRow: chanceRow, remainingDepth: (job.PassDepth - p - 2), tick: tick) : 0L);

                Fold(job: job, p: p, value: value, token: token, target: target);
                AdvanceCandidate(job: job, p: p, shapes: shapes, cellCount: cells, tokenCount: tokenCells.Count);
            } else if (hasScore && accepted && (p < (job.PassDepth - 1)) && TryProbeTransposition(job: job, p: p, position: scratch, remaining: (job.PassDepth - p - 1), value: out var known)) {
                // The child position was searched to at least this depth already: fold its value without descending.
                Fold(job: job, p: p, value: -known, token: token, target: target);
                AdvanceCandidate(job: job, p: p, shapes: shapes, cellCount: cells, tokenCount: tokenCells.Count);
            } else if (hasScore && accepted && (p < (job.PassDepth - 1))) {
                var next = (p + 1);
                var level = job.Levels[next - 1];
                var levelFrame = level.Frame;

                levelFrame.CopyFrom(other: scratch);
                level.Key = PositionKey(frame: scratch);
                level.AlphaEntry = -CursorBeta(job: job, p: p);
                SetCursorShape(job: job, p: next, value: 0);
                SetCursorToken(job: job, p: next, value: 0);
                SetCursorTarget(job: job, p: next, value: 0);
                SetCursorBest(job: job, p: next, value: -SearchCapacity.MateScore);
                SetCursorBestMove(job: job, p: next, token: -1, target: -1);
                SetCursorAlpha(job: job, p: next, value: -CursorBeta(job: job, p: p));
                SetCursorBeta(job: job, p: next, value: -CursorAlpha(job: job, p: p));
                SetCursorBaseTurn(job: job, p: next, value: Slot(store: scratch, name: plan.Turn));
                job.Active = next;
            } else if (hasScore && accepted) {
                var value = EvaluateScore(plan: plan, tick: tick);

                Fold(job: job, p: p, value: value, token: token, target: target);
                AdvanceCandidate(job: job, p: p, shapes: shapes, cellCount: cells, tokenCount: tokenCells.Count);
            } else {
                AdvanceCandidate(job: job, p: p, shapes: shapes, cellCount: cells, tokenCount: tokenCells.Count);
            }

            job.Nodes++;
            budget--;
        }
    }

    // Resolves one shape's candidate from the pre-move frame alone, without allocating a scratch copy: relocate
    // refuses a no-op target; drop and jump refuse an occupied landing (and jump additionally requires an occupied
    // intermediate cell, its own defining feature); paired resolves the companion's destination by the same grid
    // offset the walked token takes and refuses an occupied one. Every refusal here means "not a candidate", judged
    // exactly like the section's original target-equals-source skip.
    private static bool TryResolveCandidate(
        SearchShapePlan shape, SearchPlan plan, StateRow[]? zones, StateFrame frame, StateRow tokens, IReadOnlyList<StateCell> tokenCells,
        int token, long from, int candidateIndex, int cells,
        out int target, out int mid, out int companionIndex, out int companionTarget, out long code
    ) {
        target = -1;
        mid = -1;
        companionIndex = -1;
        companionTarget = -1;
        code = 0L;

        switch (shape.Kind) {
            case SearchShapeKind.Transfer: {
                // The token must stand at the selected end of its zone, and the destination must be another zone with
                // room: pile order is the zones' own, so nothing but the end token ever moves.
                if ((zones is null) || (candidateIndex == from)) {
                    return false;
                }

                var source = zones[from];
                var members = frame.CellCount(row: source);
                var end = ((shape.Selector == ZoneSelector.First) ? 0 : (members - 1));

                if ((members == 0) || !frame.TryKeyAt(row: source, index: end, key: out var endKey) || (endKey != tokenCells[token].Key) || !frame.ZoneHasRoom(row: zones[candidateIndex])) {
                    return false;
                }

                target = candidateIndex;

                return true;
            }
            case SearchShapeKind.Promote: {
                var offered = shape.PromoteTo!.Length;
                var cell = (candidateIndex / offered);

                if (cell == from) {
                    return false;
                }

                target = cell;
                code = shape.PromoteTo[candidateIndex % offered];

                return true;
            }
            case SearchShapeKind.Relocate: {
                if (candidateIndex == from) {
                    return false;
                }

                target = candidateIndex;

                return true;
            }
            case SearchShapeKind.Drop: {
                if (AnyTokenAt(frame: frame, tokens: tokens, tokenCells: tokenCells, cell: candidateIndex, excludeA: -1, excludeB: -1)) {
                    return false;
                }

                target = candidateIndex;

                return true;
            }
            case SearchShapeKind.Jump: {
                var direction = shape.Directions[candidateIndex];
                var midCell = plan.Topology!.Neighbour(cell: (int)from, direction: direction);

                if (midCell < 0) {
                    return false;
                }

                var targetCell = plan.Topology.Neighbour(cell: midCell, direction: direction);

                if (targetCell < 0) {
                    return false;
                }
                if (!AnyTokenAt(frame: frame, tokens: tokens, tokenCells: tokenCells, cell: midCell, excludeA: token, excludeB: -1)) {
                    return false;
                }
                if (AnyTokenAt(frame: frame, tokens: tokens, tokenCells: tokenCells, cell: targetCell, excludeA: token, excludeB: -1)) {
                    return false;
                }

                mid = midCell;
                target = targetCell;

                return true;
            }
            case SearchShapeKind.Pair: {
                if (candidateIndex == from) {
                    return false;
                }

                var companion = shape.PairWithIndex;

                if (companion == token) {
                    return false;
                }

                var companionFrom = (frame.TryStoredAt(row: tokens, index: companion, value: out var stored) ? stored : plan.Off);

                if ((companionFrom < 0L) || (companionFrom >= cells)) {
                    return false;
                }

                var topology = plan.Topology!;

                if (!topology.TryTranslation(from: (int)from, to: candidateIndex, dx: out var dx, dz: out var dz, dy: out var dy) ||
                    !topology.TryOffset(cell: (int)companionFrom, dx: dx, dz: dz, result: out var companionCell, dy: dy) || (companionCell == candidateIndex)) {
                    return false;
                }
                if (AnyTokenAt(frame: frame, tokens: tokens, tokenCells: tokenCells, cell: companionCell, excludeA: token, excludeB: companion)) {
                    return false;
                }

                companionIndex = companion;
                companionTarget = companionCell;
                target = candidateIndex;

                return true;
            }
            default:
                return false;
        }
    }

    // Where a token stands: its value on a board job; on a zone job, the index of the zone holding it, or -1 in none.
    private static long TokenCell(Job job, StateFrame frame, StateRow tokens, IReadOnlyList<StateCell> tokenCells, int token) {
        if (job.ZoneRows is not { } zones) {
            return (frame.TryStoredAt(row: tokens, index: token, value: out var stored) ? stored : job.Plan.Off);
        }

        var key = tokenCells[token].Key;

        for (var zone = 0; zone < zones.Length; zone++) {
            if (frame.TryStored(row: zones[zone], key: key, value: out _, text: out _)) {
                return zone;
            }
        }

        return -1L;
    }
    // Writes one resolved candidate into the scratch frame: the walked token's move and every consequence the shape
    // carries (an eviction, the companion's move, the new code, the zone transfer). The walk and the tree search
    // apply candidates through this one door, so they cannot disagree about what a shape does.
    private static void ApplyCandidate(
        SearchShapePlan shape, SearchPlan plan, StateRow[]? zones, StateFrame frame, StateFrame scratch, IReadOnlyList<StateRow> rows,
        StateRow tokens, IReadOnlyList<StateCell> tokenCells, int token, long from, int target, int mid, int companionIndex, int companionTarget, long code
    ) {
        if (shape.Kind == SearchShapeKind.Transfer) {
            _ = scratch.TryTransferToken(from: zones![from], to: zones[target], key: tokenCells[token].Key, insertFirst: shape.InsertFirst, reason: out _);

            return;
        }

        _ = scratch.TryWrite(row: tokens, key: tokenCells[token].Key, value: target, write: StateWriteKind.Set, reason: out _);

        switch (shape.Kind) {
            case SearchShapeKind.Relocate when shape.Displace:
                EvictAt(frame: frame, scratch: scratch, tokens: tokens, tokenCells: tokenCells, cell: target, exclude: token, off: plan.Off);

                break;
            case SearchShapeKind.Jump:
                EvictAt(frame: frame, scratch: scratch, tokens: tokens, tokenCells: tokenCells, cell: mid, exclude: token, off: plan.Off);

                break;
            case SearchShapeKind.Pair:
                _ = scratch.TryWrite(row: tokens, key: tokenCells[companionIndex].Key, value: companionTarget, write: StateWriteKind.Set, reason: out _);

                break;
            case SearchShapeKind.Promote:
                EvictAt(frame: frame, scratch: scratch, tokens: tokens, tokenCells: tokenCells, cell: target, exclude: token, off: plan.Off);

                if (StateRows.FindStateRow(rows: rows, name: shape.Codes!) is { } codes) {
                    _ = scratch.TryWrite(row: codes, key: tokenCells[token].Key, value: code, write: StateWriteKind.Set, reason: out _);
                }

                break;
            default:
                break;
        }
    }

    private static bool AnyTokenAt(StateFrame frame, StateRow tokens, IReadOnlyList<StateCell> tokenCells, long cell, int excludeA, int excludeB) {
        for (var index = 0; index < tokenCells.Count; index++) {
            if ((index == excludeA) || (index == excludeB)) {
                continue;
            }
            if (frame.TryStoredAt(row: tokens, index: index, value: out var standing) && (standing == cell)) {
                return true;
            }
        }

        return false;
    }
    private static void EvictAt(StateFrame frame, StateFrame scratch, StateRow tokens, IReadOnlyList<StateCell> tokenCells, long cell, int exclude, long off) {
        for (var other = 0; other < tokenCells.Count; other++) {
            if ((other != exclude) && frame.TryStoredAt(row: tokens, index: other, value: out var standing) && (standing == cell)) {
                _ = scratch.TryWrite(row: tokens, key: tokenCells[other].Key, value: off, write: StateWriteKind.Set, reason: out _);
            }
        }
    }

    // A position's identity for the transposition table: every framed value, in layout order.
    private static ulong PositionKey(StateFrame frame) {
        var hash = Fnv1aHash.Create();

        foreach (var value in frame.Values) {
            hash.Add(value: value);
        }

        return hash.Value;
    }

    private const long TranspositionExact = 1L;
    private const long TranspositionLower = 2L;
    private const long TranspositionUpper = 3L;

    // Reads a stored value for the position the frame holds when it was searched to at least `remaining` plies and
    // its bound decides the child's window (-beta, -alpha) the way a fresh search would have.
    private static bool TryProbeTransposition(Job job, int p, StateFrame position, int remaining, out long value) {
        value = 0L;

        if (job.TtKey is not { } keys) {
            return false;
        }

        var key = PositionKey(frame: position);
        var slot = (int)(key & (ulong)(keys.Length - 1));
        var meta = job.TtMeta![slot];

        if ((meta == 0L) || (keys[slot] != key) || ((meta & 0xFFL) < remaining)) {
            return false;
        }

        var stored = job.TtValue![slot];
        var flag = (meta >> 8);
        var childAlpha = -CursorBeta(job: job, p: p);
        var childBeta = -CursorAlpha(job: job, p: p);

        if ((flag == TranspositionExact) || ((flag == TranspositionLower) && (stored >= childBeta)) || ((flag == TranspositionUpper) && (stored <= childAlpha))) {
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

        var slot = (int)(level.Key & (ulong)(keys.Length - 1));
        var flag = ((level.Best <= level.AlphaEntry) ? TranspositionUpper : ((level.Best >= level.Beta) ? TranspositionLower : TranspositionExact));

        keys[slot] = level.Key;
        job.TtValue![slot] = level.Best;
        job.TtMeta![slot] = ((flag << 8) | (long)remaining);
    }

    private long EvaluateScore(SearchPlan plan, ulong tick) =>
        (m_host!.Evaluator.TryEvaluateExpression(program: plan.Score!, kind: CellKind.Int, tick: tick, value: out var value) ? value : 0L);

    private static void AdvanceCandidate(Job job, int p, SearchShapePlan[] shapes, int cellCount, int tokenCount) {
        var shapeIndex = CursorShape(job: job, p: p);
        var bound = shapes[shapeIndex].CandidateCount(cellCount: cellCount);
        var candidate = (CursorTarget(job: job, p: p) + 1);

        if (candidate >= bound) {
            SetCursorToken(job: job, p: p, value: (CursorToken(job: job, p: p) + 1));
            candidate = 0;
        }

        SetCursorTarget(job: job, p: p, value: candidate);

        // The root ply never prunes: every candidate is tried, so the root outputs are exhaustive whether or not a
        // score is authored. A ply past the root may cut once its window has closed — forcing both the token and
        // shape cursors to their sentinel completes the ply on the very next iteration.
        if ((p > 0) && (CursorAlpha(job: job, p: p) >= CursorBeta(job: job, p: p))) {
            SetCursorToken(job: job, p: p, value: tokenCount);
            SetCursorShape(job: job, p: p, value: shapes.Length);
        }
    }
    private static void Fold(Job job, int p, long value, int token, int target) {
        if (value > CursorBest(job: job, p: p)) {
            SetCursorBest(job: job, p: p, value: value);
            SetCursorBestMove(job: job, p: p, token: token, target: target);
        }
        if (value > CursorAlpha(job: job, p: p)) {
            SetCursorAlpha(job: job, p: p, value: value);
        }
    }

    private StateFrame Position(Job job, int p) => ((p == 0) ? m_base! : job.Levels[p - 1].Frame);
    private static int CursorShape(Job job, int p) => ((p == 0) ? job.Shape : job.Levels[p - 1].Shape);
    private static void SetCursorShape(Job job, int p, int value) { if (p == 0) { job.Shape = value; } else { job.Levels[p - 1].Shape = value; } }
    private static int CursorToken(Job job, int p) => ((p == 0) ? job.Token : job.Levels[p - 1].Token);
    private static void SetCursorToken(Job job, int p, int value) { if (p == 0) { job.Token = value; } else { job.Levels[p - 1].Token = value; } }
    private static int CursorTarget(Job job, int p) => ((p == 0) ? job.Target : job.Levels[p - 1].Target);
    private static void SetCursorTarget(Job job, int p, int value) { if (p == 0) { job.Target = value; } else { job.Levels[p - 1].Target = value; } }
    private static long CursorAlpha(Job job, int p) => ((p == 0) ? job.Alpha : job.Levels[p - 1].Alpha);
    private static void SetCursorAlpha(Job job, int p, long value) { if (p == 0) { job.Alpha = value; } else { job.Levels[p - 1].Alpha = value; } }
    private static long CursorBeta(Job job, int p) => ((p == 0) ? job.Beta : job.Levels[p - 1].Beta);
    private static void SetCursorBeta(Job job, int p, long value) { if (p == 0) { job.Beta = value; } else { job.Levels[p - 1].Beta = value; } }
    private static long CursorBest(Job job, int p) => ((p == 0) ? job.Best : job.Levels[p - 1].Best);
    private static void SetCursorBest(Job job, int p, long value) { if (p == 0) { job.Best = value; } else { job.Levels[p - 1].Best = value; } }
    private static void SetCursorBestMove(Job job, int p, int token, int target) {
        if (p == 0) {
            job.BestToken = token;
            job.BestTarget = target;
        } else {
            job.Levels[p - 1].BestToken = token;
            job.Levels[p - 1].BestTarget = target;
        }
    }
    private static long CursorBaseTurn(Job job, int p) => ((p == 0) ? job.BaseTurn : job.Levels[p - 1].BaseTurn);
    private static void SetCursorBaseTurn(Job job, int p, long value) { if (p == 0) { job.BaseTurn = value; } else { job.Levels[p - 1].BaseTurn = value; } }
}
