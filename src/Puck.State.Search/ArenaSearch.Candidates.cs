namespace Puck.State;

public sealed partial class ArenaSearch {
    // The shape cursor a chance-draw scope records in place of a shape's index.
    private const int ChanceScope = -1;

    private int m_openScopes;

    // Opens one scope, writes the resolved candidate into it, and runs the judge, leaving the scope open: the
    // caller either pops it (a folded candidate) or leaves it as the deeper ply's position.
    private void PushScope(Job job, int shapeIndex, int token, int candidateIndex, int target, int mid, int companionIndex, int companionTarget, long code) {
        var index = job.ScopeCount;

        job.ScopeMark[index] = m_arena.BeginScope();
        job.ScopeShape[index] = shapeIndex;
        job.ScopeTarget[index] = candidateIndex;
        job.ScopeToken[index] = token;
        job.ScopeCount = (index + 1);
        m_openScopes = job.ScopeCount;

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
        _ = job.Judge.Judge(view: View(ply: job.ScopeCount));
    }
    // Opens one scope holding a chance draw's outcome instead of a candidate's move. The shape cursor is recorded
    // negative, which is how a replay and a checkpoint tell the two apart; the token cursor carries the outcome.
    private void PushChanceScope(Job job, int outcome) {
        var index = job.ScopeCount;

        job.ScopeMark[index] = m_arena.BeginScope();
        job.ScopeShape[index] = ChanceScope;
        job.ScopeTarget[index] = 0;
        job.ScopeToken[index] = outcome;
        job.ScopeCount = (index + 1);
        m_openScopes = job.ScopeCount;

        ApplyChanceOutcome(
            job: job,
            outcome: outcome
        );
    }
    private void PopScope(Job job) {
        if (job.ScopeCount == 0) {
            return;
        }

        job.ScopeCount--;
        m_openScopes = job.ScopeCount;
        m_arena.Rewind(mark: job.ScopeMark[job.ScopeCount]);
    }
    private void PopAllScopes(Job job) {
        Suspend(job: job);
        job.ScopeCount = 0;
    }
    // Rewinds every scope the job holds open while keeping the cursors that name them, so the arena a caller reads
    // between steps is byte-for-byte what it was before the step.
    private void Suspend(Job job) {
        while (m_openScopes > 0) {
            m_openScopes--;
            m_arena.Rewind(mark: job.ScopeMark[m_openScopes]);
        }
    }
    // Reopens the scopes a suspended job holds, resolving, applying, and judging each recorded candidate against
    // the position the ones before it built. A candidate that no longer resolves means the job's own cursors no
    // longer describe a reachable line, which the caller answers by restarting the job.
    private bool Replay(Job job) {
        m_openScopes = 0;

        while (m_openScopes < job.ScopeCount) {
            var index = m_openScopes;
            var shapeIndex = job.ScopeShape[index];
            var token = job.ScopeToken[index];

            if (shapeIndex == ChanceScope) {
                job.ScopeMark[index] = m_arena.BeginScope();
                m_openScopes = (index + 1);

                ApplyChanceOutcome(
                    job: job,
                    outcome: token
                );

                continue;
            }
            if (!TryResolveCandidate(
                candidateIndex: job.ScopeTarget[index],
                code: out var code,
                companionIndex: out var companionIndex,
                companionTarget: out var companionTarget,
                from: TokenCell(
                    job: job,
                    token: token
                ),
                job: job,
                mid: out var mid,
                shapeIndex: shapeIndex,
                target: out var target,
                token: token
            )) {
                return false;
            }

            job.ScopeMark[index] = m_arena.BeginScope();
            m_openScopes = (index + 1);

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
            _ = job.Judge.Judge(view: View(ply: m_openScopes));
        }

        return true;
    }
    // Where a token stands: its value on a board job; on a zone job, the index of the zone holding it, or -1 in none.
    private long TokenCell(Job job, int token) {
        var plan = job.Plan;

        if (plan.ZoneOrdinals.Length == 0) {
            return (TryNumberAt(
                position: token,
                rowOrdinal: plan.TokensOrdinal,
                value: out var stored
            )
                ? stored
                : plan.Off
            );
        }

        var key = job.TokenKeys[token];

        for (var zone = 0; (zone < plan.ZoneOrdinals.Length); zone++) {
            if (m_arena.TryRead(
                key: key,
                rowOrdinal: plan.ZoneOrdinals[zone],
                value: out _
            )) {
                return zone;
            }
        }

        return -1L;
    }
    private bool AnyTokenAt(Job job, long cell, int excludeA, int excludeB) {
        for (var index = 0; (index < job.TokenCount); index++) {
            if (
                (index == excludeA) ||
                (index == excludeB)
            ) {
                continue;
            }
            if (
                TryNumberAt(
                position: index,
                rowOrdinal: job.Plan.TokensOrdinal,
                value: out var standing
            ) &&
                (standing == cell)
            ) {
                return true;
            }
        }

        return false;
    }
    private void EvictAt(Job job, long cell, int exclude) {
        var plan = job.Plan;

        for (var other = 0; (other < job.TokenCount); other++) {
            if (
                (other != exclude) &&
                TryNumberAt(
                position: other,
                rowOrdinal: plan.TokensOrdinal,
                value: out var standing
            ) &&
                (standing == cell)
            ) {
                _ = m_arena.TryWrite(
                    key: job.TokenKeys[other],
                    operand: plan.Off,
                    reason: out _,
                    rowOrdinal: plan.TokensOrdinal,
                    write: StateWriteKind.Set
                );
            }
        }
    }
    private bool ZoneHasRoom(int rowOrdinal) => (m_arena.CellCount(rowOrdinal: rowOrdinal) < m_arena.Layout[rowOrdinal].CellCapacity);
    // Resolves one shape's candidate from the position as it stands, before any scope is opened for it: relocate
    // refuses a no-op target; drop and jump refuse an occupied landing (and jump additionally requires an occupied
    // intermediate cell); paired resolves the companion's destination by the same grid offset the walked token
    // takes and refuses an occupied one. Every refusal here means "not a candidate".
    private bool TryResolveCandidate(Job job, int shapeIndex, int token, long from, int candidateIndex, out int target, out int mid, out int companionIndex, out int companionTarget, out long code) {
        var plan = job.Plan;
        var shape = plan.Shapes[shapeIndex];

        code = 0L;
        companionIndex = -1;
        companionTarget = -1;
        mid = -1;
        target = -1;

        switch (shape.Kind) {
            case SearchShapeKind.Transfer: {
                    // The token must stand at the selected end of its zone, and the destination must be another
                    // zone with room: pile order is the zones' own, so nothing but the end token ever moves.
                    if (
                        (plan.ZoneOrdinals.Length == 0) ||
                        (candidateIndex == from) ||
                        (((uint)from) >= ((uint)plan.ZoneOrdinals.Length)) ||
                        (((uint)candidateIndex) >= ((uint)plan.ZoneOrdinals.Length))
                    ) {
                        return false;
                    }

                    var source = plan.ZoneOrdinals[((int)from)];
                    var members = m_arena.CellCount(rowOrdinal: source);
                    var end = ((shape.Selector == ZoneSelector.First)
                        ? 0
                        : (members - 1)
                    );

                    if (
                        (members == 0) ||
                        !m_arena.TryKeyAt(
                        key: out var endKey,
                        position: end,
                        rowOrdinal: source
                    ) ||
                        (endKey != job.TokenKeys[token]) ||
                        !ZoneHasRoom(rowOrdinal: plan.ZoneOrdinals[candidateIndex])
                    ) {
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

                    code = shape.PromoteTo[(candidateIndex % offered)];
                    target = cell;

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
                    if (AnyTokenAt(
                        cell: candidateIndex,
                        excludeA: -1,
                        excludeB: -1,
                        job: job
                    )) {
                        return false;
                    }

                    target = candidateIndex;

                    return true;
                }
            case SearchShapeKind.Jump when (shape.MaxHops <= 1): {
                    var direction = shape.Directions[candidateIndex];
                    var midCell = plan.Topology!.Neighbour(
                        cell: ((int)from),
                        direction: direction
                    );

                    if (midCell < 0) {
                        return false;
                    }

                    var targetCell = plan.Topology.Neighbour(
                        cell: midCell,
                        direction: direction
                    );

                    if (targetCell < 0) {
                        return false;
                    }
                    if (!AnyTokenAt(
                        cell: midCell,
                        excludeA: token,
                        excludeB: -1,
                        job: job
                    )) {
                        return false;
                    }
                    if (AnyTokenAt(
                        cell: targetCell,
                        excludeA: token,
                        excludeB: -1,
                        job: job
                    )) {
                        return false;
                    }

                    mid = midCell;
                    target = targetCell;

                    return true;
                }
            case SearchShapeKind.Jump:
                return TryResolveJumpChain(
                    candidateIndex: candidateIndex,
                    from: from,
                    job: job,
                    shape: shape,
                    target: out target,
                    token: token
                );
            case SearchShapeKind.Pair: {
                    if (candidateIndex == from) {
                        return false;
                    }

                    var companion = shape.PairWithIndex;

                    if (companion == token) {
                        return false;
                    }

                    var companionFrom = (TryNumberAt(
                        position: companion,
                        rowOrdinal: plan.TokensOrdinal,
                        value: out var stored
                    )
                        ? stored
                        : plan.Off
                    );

                    if (
                        (companionFrom < 0L) ||
                        (companionFrom >= plan.CellCount)
                    ) {
                        return false;
                    }

                    var topology = plan.Topology!;

                    if (
                        !topology.TryTranslation(
                        dx: out var dx,
                        dy: out var dy,
                        dz: out var dz,
                        from: ((int)from),
                        to: candidateIndex
                    ) ||
                        !topology.TryOffset(
                        cell: ((int)companionFrom),
                        dx: dx,
                        dy: dy,
                        dz: dz,
                        result: out var companionCell
                    ) ||
                        (companionCell == candidateIndex)
                    ) {
                        return false;
                    }
                    if (AnyTokenAt(
                        cell: companionCell,
                        excludeA: token,
                        excludeB: companion,
                        job: job
                    )) {
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
    // A jump chain (SearchShapePlan.MaxHops > 1): a candidate is a whole sequence of 1..MaxHops hops, each over an
    // occupied cell onto an empty one, never revisiting a cell of the chain (the token's own starting cell
    // included), with no eviction along the way. The index is read as a base-(directions + 1) digit string, one
    // digit per hop slot: 0 stops the chain there, and a digit past the first 0 must also be 0, the only canonical
    // spelling of a chain shorter than MaxHops.
    private bool TryResolveJumpChain(Job job, SearchShapePlan shape, int token, long from, int candidateIndex, out int target) {
        var radix = (shape.Directions.Length + 1);
        var topology = job.Plan.Topology!;
        var visited = (stackalloc int[(shape.MaxHops + 1)]);
        var visitedCount = 1;
        var current = ((int)from);
        var index = candidateIndex;
        var stopped = false;
        var hops = 0;

        target = -1;
        visited[0] = current;

        for (var slot = 0; (slot < shape.MaxHops); slot++) {
            var digit = (index % radix);

            index /= radix;

            if (digit == 0) {
                stopped = true;

                continue;
            }
            if (stopped) {
                return false;
            }

            var direction = shape.Directions[(digit - 1)];
            var midCell = topology.Neighbour(
                cell: current,
                direction: direction
            );

            if (midCell < 0) {
                return false;
            }

            var targetCell = topology.Neighbour(
                cell: midCell,
                direction: direction
            );

            if (targetCell < 0) {
                return false;
            }
            if (!AnyTokenAt(
                cell: midCell,
                excludeA: token,
                excludeB: -1,
                job: job
            )) {
                return false;
            }
            if (AnyTokenAt(
                cell: targetCell,
                excludeA: token,
                excludeB: -1,
                job: job
            )) {
                return false;
            }

            var revisited = false;

            for (var visit = 0; (visit < visitedCount); visit++) {
                revisited |= (visited[visit] == targetCell);
            }
            if (revisited) {
                return false;
            }

            visited[visitedCount++] = targetCell;
            current = targetCell;
            hops++;
        }
        if (hops == 0) {
            return false;
        }

        target = current;

        return true;
    }
    // Writes one resolved candidate into the open scope: the walked token's move and every consequence the shape
    // carries (an eviction, the companion's move, the new code, the zone transfer). The walk and the tree search
    // apply candidates through this one door, so they cannot disagree about what a shape does.
    private void ApplyCandidate(Job job, int shapeIndex, int token, int target, int mid, int companionIndex, int companionTarget, long code) {
        var plan = job.Plan;
        var shape = plan.Shapes[shapeIndex];

        if (shape.Kind == SearchShapeKind.Transfer) {
            _ = m_arena.TryTransfer(
                fromOrdinal: plan.ZoneOrdinals[((int)TokenCell(
                    job: job,
                    token: token
                ))],
                insertFirst: shape.InsertFirst,
                key: job.TokenKeys[token],
                reason: out _,
                toOrdinal: plan.ZoneOrdinals[target]
            );

            return;
        }

        _ = m_arena.TryWrite(
            key: job.TokenKeys[token],
            operand: target,
            reason: out _,
            rowOrdinal: plan.TokensOrdinal,
            write: StateWriteKind.Set
        );

        switch (shape.Kind) {
            case SearchShapeKind.Relocate when shape.Displace:
                EvictAt(
                    cell: target,
                    exclude: token,
                    job: job
                );

                break;
            case SearchShapeKind.Jump when (mid >= 0):
                // A chain candidate resolves with mid at its -1 default: nothing it hops over leaves the board.
                EvictAt(
                    cell: mid,
                    exclude: token,
                    job: job
                );

                break;
            case SearchShapeKind.Pair:
                _ = m_arena.TryWrite(
                    key: job.TokenKeys[companionIndex],
                    operand: companionTarget,
                    reason: out _,
                    rowOrdinal: plan.TokensOrdinal,
                    write: StateWriteKind.Set
                );

                break;
            case SearchShapeKind.Promote:
                EvictAt(
                    cell: target,
                    exclude: token,
                    job: job
                );

                if (plan.CodeOrdinals[shapeIndex] is >= 0 and var codes) {
                    _ = m_arena.TryWrite(
                        key: job.TokenKeys[token],
                        operand: code,
                        reason: out _,
                        rowOrdinal: codes,
                        write: StateWriteKind.Set
                    );
                }

                break;
            default:
                break;
        }
    }
}
