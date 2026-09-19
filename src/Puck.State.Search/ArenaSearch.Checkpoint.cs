using Puck.Maths;

namespace Puck.State;

public sealed partial class ArenaSearch {
    /// <summary>Folds every job's progress into a hash.</summary>
    /// <param name="hash">The fold in flight.</param>
    /// <remarks>A job's progress is simulation state: it advances one step at a time and a caller carries it across
    /// a restore, so it hashes beside the arena rather than beside the walk's scratch. The position is not folded
    /// here — the arena is the position, and a suspended job's open scopes are named by the candidates that opened
    /// them.</remarks>
    public void AppendStateHash(ref Fnv1aHash hash) {
        hash.Add(value: ((uint)m_jobs.Length));

        foreach (var job in m_jobs) {
            hash.Add(value: Fnv1aHash.Compute(values: job.Plan.Name.AsSpan()));
            hash.Add(value: job.Stamp);
            hash.Add(value: ((byte)(job.Running
                ? 1
                : 0)));
            hash.Add(value: ((byte)(job.Done
                ? 1
                : 0)));
            hash.Add(value: ((uint)job.Shape));
            hash.Add(value: ((uint)job.Token));
            hash.Add(value: ((uint)job.Target));
            hash.Add(value: job.Count);
            hash.Add(value: job.Nodes);
            hash.Add(value: job.BaseTurn);
            hash.Add(value: ((uint)job.PassDepth));
            hash.Add(value: ((uint)job.Active));
            hash.Add(value: job.Best);
            hash.Add(value: ((uint)job.BestToken));
            hash.Add(value: ((uint)job.BestTarget));
            hash.Add(value: job.Alpha);
            hash.Add(value: job.Beta);
            hash.Add(value: ((uint)job.TokenCount));

            foreach (var mask in job.Legal) {
                hash.Add(value: mask);
            }
            foreach (var count in job.Counts) {
                hash.Add(value: count);
            }
            if (job.Wide is { } wide) {
                foreach (var word in wide) {
                    hash.Add(value: word);
                }
            }
            foreach (var level in job.Levels) {
                hash.Add(value: ((uint)level.Shape));
                hash.Add(value: ((uint)level.Token));
                hash.Add(value: ((uint)level.Target));
                hash.Add(value: level.Alpha);
                hash.Add(value: level.Beta);
                hash.Add(value: level.Best);
                hash.Add(value: ((uint)level.BestToken));
                hash.Add(value: ((uint)level.BestTarget));
                hash.Add(value: level.BaseTurn);
                hash.Add(value: level.Key);
                hash.Add(value: level.AlphaEntry);

                foreach (var seat in level.Seats) {
                    hash.Add(value: seat);
                }
            }

            hash.Add(value: ((uint)job.ScopeCount));

            for (var scope = 0; (scope < job.ScopeCount); scope++) {
                hash.Add(value: ((uint)job.ScopeShape[scope]));
                hash.Add(value: ((uint)job.ScopeToken[scope]));
                hash.Add(value: ((uint)job.ScopeTarget[scope]));
            }

            if (job.TtKey is { } ttKey) {
                for (var slot = 0; (slot < ttKey.Length); slot++) {
                    hash.Add(value: ttKey[slot]);
                    hash.Add(value: job.TtValue![slot]);
                    hash.Add(value: job.TtMeta![slot]);
                }
            }
            if (job.TreeParent is { } parents) {
                hash.Add(value: ((byte)(job.TreeActive
                    ? 1
                    : 0)));
                hash.Add(value: ((uint)job.Phase));
                hash.Add(value: ((uint)job.TreeCount));
                hash.Add(value: ((uint)job.Iteration));
                hash.Add(value: job.Seed);
                hash.Add(value: ((uint)job.UShape));
                hash.Add(value: ((uint)job.UToken));
                hash.Add(value: ((uint)job.UTarget));
                hash.Add(value: ((uint)job.UScan));
                hash.Add(value: ((uint)job.UStart));
                hash.Add(value: ((uint)job.PlayoutPlies));
                hash.Add(value: ((uint)job.PlayCount));
                hash.Add(value: ((uint)job.PathLength));

                for (var node = 0; (node < job.TreeCount); node++) {
                    hash.Add(value: ((uint)parents[node]));
                    hash.Add(value: ((uint)job.TreeFirstChild![node]));
                    hash.Add(value: ((uint)job.TreeChildCount![node]));
                    hash.Add(value: job.TreeVisits![node]);
                    hash.Add(value: job.TreeTotal![node]);
                    hash.Add(value: ((uint)job.TreeShape![node]));
                    hash.Add(value: ((uint)job.TreeToken![node]));
                    hash.Add(value: ((uint)job.TreeTarget![node]));
                    hash.Add(value: job.TreeExpanded![node]);
                }
                for (var index = 0; (index < job.PathLength); index++) {
                    hash.Add(value: ((uint)job.Path![index]));
                }
            }
        }
    }
    /// <summary>Captures every job's progress.</summary>
    /// <returns>The checkpoint.</returns>
    /// <remarks>A job's position is not captured: the arena is the position, and the candidate scopes a suspended
    /// job holds are named by the candidates that opened them.</remarks>
    public ArenaSearchCheckpoint Capture() {
        var jobs = new ArenaSearchJobCheckpoint[m_jobs.Length];

        for (var index = 0; (index < m_jobs.Length); index++) {
            var job = m_jobs[index];
            var levels = new ArenaSearchLevelCheckpoint[job.Levels.Length];
            var scopes = new ArenaSearchScopeCheckpoint[job.ScopeCount];

            for (var level = 0; (level < levels.Length); level++) {
                var entry = job.Levels[level];

                levels[level] = new ArenaSearchLevelCheckpoint(
                    Alpha: entry.Alpha,
                    AlphaEntry: entry.AlphaEntry,
                    BaseTurn: entry.BaseTurn,
                    Best: entry.Best,
                    BestTarget: entry.BestTarget,
                    BestToken: entry.BestToken,
                    Beta: entry.Beta,
                    Key: entry.Key,
                    Seats: entry.Seats.ToArray(),
                    Shape: entry.Shape,
                    Target: entry.Target,
                    Token: entry.Token
                );
            }
            for (var scope = 0; (scope < scopes.Length); scope++) {
                scopes[scope] = new ArenaSearchScopeCheckpoint(
                    Shape: job.ScopeShape[scope],
                    Target: job.ScopeTarget[scope],
                    Token: job.ScopeToken[scope]
                );
            }

            jobs[index] = new ArenaSearchJobCheckpoint(
                Name: job.Plan.Name,
                Stamp: job.Stamp,
                Running: job.Running,
                Done: job.Done,
                Shape: job.Shape,
                Token: job.Token,
                Target: job.Target,
                Count: job.Count,
                Legal: job.Legal.ToArray(),
                Counts: job.Counts.ToArray(),
                Wide: (job.Wide?.ToArray() ?? []),
                Nodes: job.Nodes,
                BaseTurn: job.BaseTurn,
                TokenCount: job.TokenCount,
                PassDepth: job.PassDepth,
                Active: job.Active,
                Best: job.Best,
                BestToken: job.BestToken,
                BestTarget: job.BestTarget,
                Alpha: job.Alpha,
                Beta: job.Beta,
                Levels: levels,
                Scopes: scopes,
                TtKey: (job.TtKey?.ToArray() ?? []),
                TtValue: (job.TtValue?.ToArray() ?? []),
                TtMeta: (job.TtMeta?.ToArray() ?? []),
                Tree: CaptureTree(job: job)
            );
        }

        return new ArenaSearchCheckpoint(Jobs: jobs);
    }
    /// <summary>Restores every job's progress, refusing a checkpoint that does not describe the installed job
    /// set.</summary>
    /// <param name="checkpoint">The captured progress.</param>
    /// <param name="reason">Why the checkpoint was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when every job was restored.</returns>
    /// <remarks>The whole checkpoint is checked against the installed job set before any job is touched, so a
    /// refusal leaves every job's progress exactly as it was.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    public bool TryRestore(ArenaSearchCheckpoint checkpoint, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        if ((checkpoint.Jobs?.Length ?? -1) != m_jobs.Length) {
            reason = $"the checkpoint carries {(checkpoint.Jobs?.Length ?? 0)} jobs and the search holds {m_jobs.Length}";

            return false;
        }

        for (var index = 0; (index < m_jobs.Length); index++) {
            var job = m_jobs[index];
            var entry = checkpoint.Jobs![index];

            if (entry is null) {
                reason = $"the checkpoint carries no progress for the installed job '{job.Plan.Name}'";

                return false;
            }
            if (!string.Equals(
                a: entry.Name,
                b: job.Plan.Name,
                comparisonType: StringComparison.Ordinal
            )) {
                reason = $"the checkpoint's job '{entry.Name}' does not match the installed job '{job.Plan.Name}'";

                return false;
            }
            if (!ShapesAgree(
                entry: entry,
                job: job
            )) {
                reason = $"the checkpoint's job '{entry.Name}' was captured against a different job shape";

                return false;
            }
        }

        for (var index = 0; (index < m_jobs.Length); index++) {
            var job = m_jobs[index];
            var entry = checkpoint.Jobs![index];

            PopAllScopes(job: job);
            entry.Counts.AsSpan().CopyTo(destination: job.Counts);
            entry.Legal.AsSpan().CopyTo(destination: job.Legal);
            entry.TtKey.AsSpan().CopyTo(destination: (job.TtKey ?? []));
            entry.TtMeta.AsSpan().CopyTo(destination: (job.TtMeta ?? []));
            entry.TtValue.AsSpan().CopyTo(destination: (job.TtValue ?? []));
            entry.Wide.AsSpan().CopyTo(destination: (job.Wide ?? []));
            job.Active = entry.Active;
            job.Alpha = entry.Alpha;
            job.BaseTurn = entry.BaseTurn;
            job.Best = entry.Best;
            job.BestTarget = entry.BestTarget;
            job.BestToken = entry.BestToken;
            job.Beta = entry.Beta;
            job.Count = entry.Count;
            job.Done = entry.Done;
            job.Nodes = entry.Nodes;
            job.PassDepth = entry.PassDepth;
            job.Running = entry.Running;
            job.Shape = entry.Shape;
            job.Stamp = entry.Stamp;
            job.Target = entry.Target;
            job.Token = entry.Token;
            job.TokenCount = entry.TokenCount;

            for (var level = 0; (level < entry.Levels.Length); level++) {
                var carried = entry.Levels[level];
                var target = job.Levels[level];

                target.Alpha = carried.Alpha;
                target.AlphaEntry = carried.AlphaEntry;
                target.BaseTurn = carried.BaseTurn;
                target.Best = carried.Best;
                target.BestTarget = carried.BestTarget;
                target.BestToken = carried.BestToken;
                target.Beta = carried.Beta;
                target.Key = carried.Key;

                (carried.Seats ?? []).AsSpan().CopyTo(destination: target.Seats);

                target.Shape = carried.Shape;
                target.Target = carried.Target;
                target.Token = carried.Token;
            }
            for (var scope = 0; (scope < entry.Scopes.Length); scope++) {
                job.ScopeShape[scope] = entry.Scopes[scope].Shape;
                job.ScopeTarget[scope] = entry.Scopes[scope].Target;
                job.ScopeToken[scope] = entry.Scopes[scope].Token;
            }

            job.ScopeCount = entry.Scopes.Length;

            RestoreTree(
                carried: entry.Tree,
                job: job
            );

            for (var token = 0; (token < job.TokenKeys.Length); token++) {
                if (!m_arena.TryKeyAt(
                    key: out job.TokenKeys[token],
                    position: token,
                    rowOrdinal: job.Plan.TokensOrdinal
                )) {
                    job.TokenKeys[token] = default;
                }
            }
        }

        reason = string.Empty;

        return true;
    }

    // Whether a checkpoint describes this job: every array a restore copies is present at the length the job
    // allocated, and every value a replay or a walk indexes by lands inside what it indexes. A job that allocated
    // no table or tree is matched by a checkpoint carrying an empty one or none. Token cursors are held to the
    // job's token capacity rather than its live count, which is what they index and what a genuine capture never
    // exceeds.
    private static bool ShapesAgree(Job job, ArenaSearchJobCheckpoint entry) {
        static bool Same(Array? carried, Array? held) => ((carried is not null) && (carried.Length == (held?.Length ?? 0)));

        var cells = job.Plan.CellCount;
        var shapes = job.Plan.Shapes;
        var tokens = job.Legal.Length;

        // A cursor may rest on the one-past-the-end sentinel of its shape and token ranges.
        bool Cursor(int shape, int token, int target) => (
            (((uint)shape) <= ((uint)shapes.Length)) &&
            (((uint)token) <= ((uint)tokens)) &&
            (target >= 0)
        );
        // A candidate is named exactly: a shape of the plan, a token of the job, an index the shape offers.
        bool Candidate(int shape, int token, int target) => (
            (((uint)shape) < ((uint)shapes.Length)) &&
            (((uint)token) < ((uint)tokens)) &&
            (((uint)target) < ((uint)shapes[shape].CandidateCount(cellCount: cells)))
        );
        // A chance-draw scope names an outcome of the baked table instead of a candidate.
        bool Scope(int shape, int token, int target) => ((shape == ChanceScope)
            ? ((job.Plan.Chance is { } chance) && (target == 0) && (((uint)token) < ((uint)chance.Weights.Length)))
            : Candidate(
                shape: shape,
                target: target,
                token: token
            )
        );

        if (
            !Same(carried: entry.Counts, held: job.Counts) ||
            !Same(carried: entry.Legal, held: job.Legal) ||
            !Same(carried: entry.Levels, held: job.Levels) ||
            !Same(carried: entry.TtKey, held: job.TtKey) ||
            !Same(carried: entry.TtMeta, held: job.TtMeta) ||
            !Same(carried: entry.TtValue, held: job.TtValue) ||
            !Same(carried: entry.Wide, held: job.Wide) ||
            (entry.Scopes is null) ||
            (entry.Scopes.Length > job.ScopeMark.Length) ||
            (((uint)entry.Active) > ((uint)job.Levels.Length)) ||
            (((uint)(entry.PassDepth - 1)) > ((uint)job.Levels.Length)) ||
            (((uint)entry.TokenCount) > ((uint)tokens)) ||
            !Cursor(shape: entry.Shape, token: entry.Token, target: entry.Target) ||
            ((entry.Tree is null) != (job.Path is null))
        ) {
            return false;
        }

        foreach (var level in entry.Levels) {
            if (
                (level is null) ||
                !Same(carried: level.Seats, held: job.Seats) ||
                !Cursor(shape: level.Shape, token: level.Token, target: level.Target)
            ) {
                return false;
            }
        }
        foreach (var scope in entry.Scopes) {
            if (!Scope(shape: scope.Shape, token: scope.Token, target: scope.Target)) {
                return false;
            }
        }

        if (entry.Tree is not { } tree) {
            return true;
        }
        if (
            !Same(carried: tree.ChildCount, held: job.TreeChildCount) ||
            !Same(carried: tree.Expanded, held: job.TreeExpanded) ||
            !Same(carried: tree.FirstChild, held: job.TreeFirstChild) ||
            !Same(carried: tree.Parent, held: job.TreeParent) ||
            !Same(carried: tree.Path, held: job.Path) ||
            !Same(carried: tree.Shape, held: job.TreeShape) ||
            !Same(carried: tree.Target, held: job.TreeTarget) ||
            !Same(carried: tree.Token, held: job.TreeToken) ||
            !Same(carried: tree.Total, held: job.TreeTotal) ||
            !Same(carried: tree.Visits, held: job.TreeVisits) ||
            (((uint)tree.Count) > ((uint)tree.Parent.Length)) ||
            (((uint)tree.PathLength) > ((uint)tree.Path.Length)) ||
            (tree.Active && ((tree.Count < 1) || (tree.PathLength < 1))) ||
            (tree.Scan < 0) ||
            (tree.Start < 0) ||
            // The playout scans from Start for Scan candidates, so their sum must stay a position.
            ((((long)tree.Scan) + tree.Start) > int.MaxValue) ||
            !Cursor(shape: tree.ExpandShape, token: tree.ExpandToken, target: tree.ExpandTarget)
        ) {
            return false;
        }

        for (var index = 0; (index < tree.PathLength); index++) {
            if (((uint)tree.Path[index]) >= ((uint)tree.Count)) {
                return false;
            }
        }
        // A node's children are one run inside the pool, and every node but the root names the candidate that
        // reaches it.
        for (var node = 0; (node < tree.Count); node++) {
            var children = tree.ChildCount[node];

            if (
                (children < 0) ||
                ((children > 0) && ((tree.FirstChild[node] < 0) || ((((long)tree.FirstChild[node]) + children) > tree.Count))) ||
                ((node > 0) && !Candidate(shape: tree.Shape[node], token: tree.Token[node], target: tree.Target[node]))
            ) {
                return false;
            }
        }

        return true;
    }
    private static ArenaSearchTreeCheckpoint? CaptureTree(Job job) => ((job.Path is null)
        ? null
        : new ArenaSearchTreeCheckpoint(
            Active: job.TreeActive,
            Phase: job.Phase,
            Count: job.TreeCount,
            Iteration: job.Iteration,
            Seed: job.Seed,
            ExpandShape: job.UShape,
            ExpandToken: job.UToken,
            ExpandTarget: job.UTarget,
            Scan: job.UScan,
            Start: job.UStart,
            PlayoutPlies: job.PlayoutPlies,
            PlayCount: job.PlayCount,
            Parent: job.TreeParent!.ToArray(),
            FirstChild: job.TreeFirstChild!.ToArray(),
            ChildCount: job.TreeChildCount!.ToArray(),
            Visits: job.TreeVisits!.ToArray(),
            Total: job.TreeTotal!.ToArray(),
            Shape: job.TreeShape!.ToArray(),
            Token: job.TreeToken!.ToArray(),
            Target: job.TreeTarget!.ToArray(),
            Expanded: job.TreeExpanded!.ToArray(),
            Path: job.Path.ToArray(),
            PathLength: job.PathLength
        )
    );
    private static void RestoreTree(Job job, ArenaSearchTreeCheckpoint? carried) {
        if (
            (carried is null) ||
            (job.Path is null)
        ) {
            return;
        }

        carried.ChildCount.AsSpan().CopyTo(destination: job.TreeChildCount!);
        carried.Expanded.AsSpan().CopyTo(destination: job.TreeExpanded!);
        carried.FirstChild.AsSpan().CopyTo(destination: job.TreeFirstChild!);
        carried.Parent.AsSpan().CopyTo(destination: job.TreeParent!);
        carried.Path.AsSpan().CopyTo(destination: job.Path);
        carried.Shape.AsSpan().CopyTo(destination: job.TreeShape!);
        carried.Target.AsSpan().CopyTo(destination: job.TreeTarget!);
        carried.Token.AsSpan().CopyTo(destination: job.TreeToken!);
        carried.Total.AsSpan().CopyTo(destination: job.TreeTotal!);
        carried.Visits.AsSpan().CopyTo(destination: job.TreeVisits!);
        job.Iteration = carried.Iteration;
        job.PathLength = carried.PathLength;
        job.Phase = carried.Phase;
        job.PlayCount = carried.PlayCount;
        job.PlayoutPlies = carried.PlayoutPlies;
        job.Seed = carried.Seed;
        job.TreeActive = carried.Active;
        job.TreeCount = carried.Count;
        job.UScan = carried.Scan;
        job.UShape = carried.ExpandShape;
        job.UStart = carried.Start;
        job.UTarget = carried.ExpandTarget;
        job.UToken = carried.ExpandToken;
    }
}
