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
            hash.Add(value: job.Count);
            hash.Add(value: job.Nodes);
            hash.Add(value: job.Work);
            hash.Add(value: ((uint)job.PassDepth));
            hash.Add(value: ((uint)job.Active));
            hash.Add(value: ((uint)job.TokenCount));
            AddLevelTo(
                hash: ref hash,
                level: job.Root
            );

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
                AddLevelTo(
                    hash: ref hash,
                    level: level
                );
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
                hash.Add(value: ((uint)job.UScan));
                hash.Add(value: ((uint)job.UStart));
                hash.Add(value: ((uint)job.PlayoutPlies));
                hash.Add(value: ((uint)job.PlayCount));
                hash.Add(value: ((uint)job.PathLength));

                for (var node = 0; (node < job.TreeCount); node++) {
                    hash.Add(value: ((uint)parents[node]));
                    hash.Add(value: ((uint)job.TreeFirstChild![node]));
                    hash.Add(value: ((uint)job.TreeNextSibling![node]));
                    hash.Add(value: ((uint)job.TreeChildCount![node]));
                    hash.Add(value: job.TreeVisits![node]);
                    hash.Add(value: job.TreeTotal![node]);
                    hash.Add(value: ((uint)job.TreeShape![node]));
                    hash.Add(value: ((uint)job.TreeToken![node]));
                    hash.Add(value: ((uint)job.TreeTarget![node]));
                    hash.Add(value: ((uint)job.TreeScanned![node]));
                    hash.Add(value: ((uint)job.TreeScanStart![node]));
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
                levels[level] = CaptureLevel(level: job.Levels[level]);
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
                Root: CaptureLevel(level: job.Root),
                Count: job.Count,
                Legal: job.Legal.ToArray(),
                Counts: job.Counts.ToArray(),
                Wide: (job.Wide?.ToArray() ?? []),
                Nodes: job.Nodes,
                Work: job.Work,
                PeakStepWork: job.PeakStepWork,
                TokenCount: job.TokenCount,
                PassDepth: job.PassDepth,
                Active: job.Active,
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
            job.Count = entry.Count;
            job.Done = entry.Done;
            job.Nodes = entry.Nodes;
            job.PassDepth = entry.PassDepth;
            job.Running = entry.Running;
            job.Stamp = entry.Stamp;
            job.PeakStepWork = entry.PeakStepWork;
            job.TokenCount = entry.TokenCount;
            job.Work = entry.Work;
            RestoreLevel(
                carried: entry.Root,
                level: job.Root
            );

            for (var level = 0; (level < entry.Levels.Length); level++) {
                RestoreLevel(
                    carried: entry.Levels[level],
                    level: job.Levels[level]
                );
            }

            var chanceScopes = 0;

            for (var scope = 0; (scope < entry.Scopes.Length); scope++) {
                job.ScopeShape[scope] = entry.Scopes[scope].Shape;
                job.ScopeTarget[scope] = entry.Scopes[scope].Target;
                job.ScopeToken[scope] = entry.Scopes[scope].Token;
                if (entry.Scopes[scope].Shape == ChanceScope) {
                    chanceScopes++;
                }
            }

            job.ChanceScopes = chanceScopes;
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

    private static void AddLevelTo(ref Fnv1aHash hash, Level level) {
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
        hash.Add(value: ((uint)level.EntryTarget));
        hash.Add(value: ((ulong)(level.ChanceSum >> 64)));
        hash.Add(value: ((ulong)level.ChanceSum));
        hash.Add(value: level.ChanceWeight);

        foreach (var seat in level.Seats) {
            hash.Add(value: seat);
        }
    }
    private static ArenaSearchLevelCheckpoint CaptureLevel(Level level) => new(
        Alpha: level.Alpha,
        AlphaEntry: level.AlphaEntry,
        BaseTurn: level.BaseTurn,
        Best: level.Best,
        BestTarget: level.BestTarget,
        BestToken: level.BestToken,
        Beta: level.Beta,
        ChanceSumHigh: ((long)(level.ChanceSum >> 64)),
        ChanceSumLow: ((ulong)level.ChanceSum),
        ChanceWeight: level.ChanceWeight,
        EntryTarget: level.EntryTarget,
        Key: level.Key,
        Seats: level.Seats.ToArray(),
        Shape: level.Shape,
        Target: level.Target,
        Token: level.Token
    );
    private static void RestoreLevel(Level level, ArenaSearchLevelCheckpoint carried) {
        level.Alpha = carried.Alpha;
        level.AlphaEntry = carried.AlphaEntry;
        level.BaseTurn = carried.BaseTurn;
        level.Best = carried.Best;
        level.BestTarget = carried.BestTarget;
        level.BestToken = carried.BestToken;
        level.Beta = carried.Beta;
        level.ChanceSum = (((Int128)carried.ChanceSumHigh) << 64) | carried.ChanceSumLow;
        level.ChanceWeight = carried.ChanceWeight;
        level.EntryTarget = carried.EntryTarget;
        level.Key = carried.Key;

        (carried.Seats ?? []).AsSpan().CopyTo(destination: level.Seats);

        level.Shape = carried.Shape;
        level.Target = carried.Target;
        level.Token = carried.Token;
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
            (entry.Root is null) ||
            !Cursor(shape: entry.Root.Shape, token: entry.Root.Token, target: entry.Root.Target) ||
            (entry.Root.Seats is { Length: > 0 }) ||
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
            !Same(carried: tree.FirstChild, held: job.TreeFirstChild) ||
            !Same(carried: tree.NextSibling, held: job.TreeNextSibling) ||
            !Same(carried: tree.Parent, held: job.TreeParent) ||
            !Same(carried: tree.Path, held: job.Path) ||
            !Same(carried: tree.ScanStart, held: job.TreeScanStart) ||
            !Same(carried: tree.Scanned, held: job.TreeScanned) ||
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
            ((((long)tree.Scan) + tree.Start) > int.MaxValue)
        ) {
            return false;
        }

        for (var index = 0; (index < tree.PathLength); index++) {
            if (((uint)tree.Path[index]) >= ((uint)tree.Count)) {
                return false;
            }
        }

        // A node's children are a list of nodes naming it their parent, as long as its count says; its scan stays
        // inside the candidates it scans; and every node but the root names the candidate that reaches it. A list is
        // walked no further than the pool holds, so a cycle is a mismatch rather than a hang.
        var candidates = TotalCandidates(
            cellCount: cells,
            shapes: shapes,
            tokenCount: entry.TokenCount
        );

        for (var node = 0; (node < tree.Count); node++) {
            if (
                (tree.ChildCount[node] < 0) ||
                (((uint)tree.Scanned[node]) > ((uint)candidates)) ||
                (((uint)tree.ScanStart[node]) >= ((uint)Math.Max(val1: 1, val2: candidates))) ||
                (((uint)(tree.NextSibling[node] + 1)) > ((uint)tree.Count)) ||
                ((node > 0) && !Candidate(shape: tree.Shape[node], token: tree.Token[node], target: tree.Target[node]))
            ) {
                return false;
            }

            var walked = 0;

            for (var child = tree.FirstChild[node]; (child != -1); child = tree.NextSibling[child]) {
                if (
                    (((uint)child) >= ((uint)tree.Count)) ||
                    (tree.Parent[child] != node) ||
                    (++walked > tree.ChildCount[node])
                ) {
                    return false;
                }
            }
            if (walked != tree.ChildCount[node]) {
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
            Scan: job.UScan,
            Start: job.UStart,
            PlayoutPlies: job.PlayoutPlies,
            PlayCount: job.PlayCount,
            Parent: job.TreeParent!.ToArray(),
            FirstChild: job.TreeFirstChild!.ToArray(),
            NextSibling: job.TreeNextSibling!.ToArray(),
            ChildCount: job.TreeChildCount!.ToArray(),
            Visits: job.TreeVisits!.ToArray(),
            Total: job.TreeTotal!.ToArray(),
            Shape: job.TreeShape!.ToArray(),
            Token: job.TreeToken!.ToArray(),
            Target: job.TreeTarget!.ToArray(),
            Scanned: job.TreeScanned!.ToArray(),
            ScanStart: job.TreeScanStart!.ToArray(),
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
        carried.FirstChild.AsSpan().CopyTo(destination: job.TreeFirstChild!);
        carried.NextSibling.AsSpan().CopyTo(destination: job.TreeNextSibling!);
        carried.Parent.AsSpan().CopyTo(destination: job.TreeParent!);
        carried.Path.AsSpan().CopyTo(destination: job.Path);
        carried.ScanStart.AsSpan().CopyTo(destination: job.TreeScanStart!);
        carried.Scanned.AsSpan().CopyTo(destination: job.TreeScanned!);
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
        job.UStart = carried.Start;
    }
}
