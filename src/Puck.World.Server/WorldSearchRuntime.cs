using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One recursive ply's checkpointed progress: the position it enumerates moves from (<see cref="Values"/>,
/// a frame snapshot in the layout's own order), its own (token, target) cursor, its negamax window, and the best
/// candidate found so far.</summary>
public sealed record WorldSearchLevelCheckpoint(int Token, int Target, long Alpha, long Beta, long Best, int BestToken, int BestTarget, long BaseTurn, long[] Values);

/// <summary>One job's checkpointed progress. <see cref="Legal"/> holds one mask per token in the token row's cell
/// order, filled as the job walks. <see cref="Levels"/> holds one entry per ply beyond the root — empty for a
/// depth-one job — populated only while the negamax search has descended into it.</summary>
public sealed record WorldSearchJobCheckpoint(
    string Name, ulong Stamp, bool Running, bool Done, int Token, int Target, long Count, long[] Legal, long Nodes, long BaseTurn,
    int PassDepth, int Active, long Best, int BestToken, int BestTarget, long Alpha, long Beta, WorldSearchLevelCheckpoint[] Levels
);

/// <summary>The search runtime's checkpointed state, in section order.</summary>
public sealed record WorldSearchCheckpoint(WorldSearchJobCheckpoint[] Jobs) {
    /// <summary>Gets the checkpoint of a runtime with no jobs.</summary>
    public static WorldSearchCheckpoint Empty { get; } = new(Jobs: []);
}

/// <summary>One job's progress as the console reads it.</summary>
public readonly record struct WorldSearchStatus(
    string Name, bool Running, bool Done, int Token, int Tokens, int Target, int Cells, long Count, long Nodes, int NodesPerTick, long JudgeCost, int JudgeRules,
    bool HasScore, int Depth, int PassDepth, long BestScore, int BestToken, int BestTarget
);

/// <summary>Runs the document's search jobs: a frame over the installed section, the rules a frame can evaluate, and
/// per job a walk over every (token, target cell) relocation judged by those rules under a per-tick node quota. A
/// job restarts whenever the frame's inputs change and lands its answer through the ordinary mutation door when the
/// walk completes. Progress is simulation state: it hashes and checkpoints.
///
/// A job with an authored score iterative-deepens: for each authored depth in turn, the same root walk that always
/// populates <c>legal</c>/<c>count</c> also negamaxes every accepted relocation to that depth and keeps the best.
/// The recursion below the root runs on an explicit stack (<see cref="Job.Levels"/>, one <see cref="StateFrame"/>
/// per ply beyond the root) rather than the call stack, so a tick boundary can suspend it anywhere and a checkpoint
/// carries it byte-for-byte. The root ply never prunes and never skips a candidate, so a depth-one job's <c>legal</c>
/// and <c>count</c> are unchanged by whether a score is authored.</summary>
internal sealed class WorldSearchRuntime {
    private sealed class Level {
        public StateFrame Frame = null!;
        public int Token;
        public int Target;
        public long Alpha;
        public long Beta;
        public long Best;
        public int BestToken = -1;
        public int BestTarget = -1;
        public long BaseTurn;
    }

    private sealed class Job {
        public Job(WorldSearchPlan plan, int tokenCapacity, FrameLayout layout, IReadOnlyList<StateRow> rows) {
            Plan = plan;
            Legal = new long[tokenCapacity];
            Levels = BuildLevels(depth: plan.Depth, layout: layout, rows: rows);
        }

        public WorldSearchPlan Plan { get; set; }
        public ulong Stamp { get; set; }
        public bool Running { get; set; }
        public bool Done { get; set; }
        public int Token { get; set; }
        public int Target { get; set; }
        public long Count { get; set; }
        public long[] Legal { get; set; }
        public long Nodes { get; set; }
        public long BaseTurn { get; set; }

        // Iterative-deepening negamax, engaged only when Plan.Score is not null. PassDepth is the depth the current
        // pass searches to; Active is which ply (0 = root) is being expanded. Best/BestToken/BestTarget are the root's
        // own running negamax result, overwritten every pass, so whatever they hold when the job finishes is the
        // deepest completed pass's answer. Alpha/Beta are the root's own window: it updates Alpha as candidates fold
        // in (so a child gets a tighter bound), but never breaks its own loop on it — every root candidate is always
        // tried, so legal/count never depend on whether a score is authored.
        public int PassDepth { get; set; } = 1;
        public int Active { get; set; }
        public long Best { get; set; } = -WorldSearchCapacity.MateScore;
        public int BestToken { get; set; } = -1;
        public int BestTarget { get; set; } = -1;
        public long Alpha { get; set; } = -WorldSearchCapacity.MateScore;
        public long Beta { get; set; } = WorldSearchCapacity.MateScore;
        public Level[] Levels { get; set; }

        public static Level[] BuildLevels(int depth, FrameLayout layout, IReadOnlyList<StateRow> rows) {
            var levels = new Level[Math.Max(val1: 0, val2: (depth - 1))];

            for (var index = 0; index < levels.Length; index++) {
                levels[index] = new Level { Frame = new StateFrame(layout: layout, rows: rows) };
            }

            return levels;
        }
    }

    private readonly Func<IReadOnlyList<StateRow>> m_live;
    private readonly RowStore m_store;
    private readonly List<WorldMutation> m_outputs = [];
    private WorldDefinition m_definition = null!;
    private Job[] m_jobs = [];
    private CompiledWorldRule[] m_judge = [];
    private FrameLayout? m_layout;
    private FrameHost? m_host;
    private StateFrame? m_base;
    private StateCatalog? m_catalog;

    /// <summary>Initializes the runtime over a live row source.</summary>
    /// <param name="live">Returns the installed section's rows.</param>
    public WorldSearchRuntime(Func<IReadOnlyList<StateRow>> live) {
        ArgumentNullException.ThrowIfNull(argument: live);
        m_live = live;
        m_store = new RowStore(rows: live);
    }

    /// <summary>Gets how many jobs the installed document declares.</summary>
    public int Count => m_jobs.Length;

    /// <summary>Rebuilds plans, judge rules, and the frame against an installed document; a frame whose layout still
    /// fits is rebound and every job keeps its progress, otherwise every job restarts on its next step.</summary>
    /// <param name="definition">The installed document.</param>
    /// <param name="rules">Its compiled rules.</param>
    /// <param name="patterns">Its compiled patterns.</param>
    /// <param name="tables">Its compiled tables.</param>
    public void Rebuild(WorldDefinition definition, CompiledWorldRule[] rules, CompiledPatterns patterns, IReadOnlyList<CompiledTable> tables) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        m_definition = definition;

        if (!WorldSearchCompilation.TryPlanAll(definition: definition, rules: rules, plans: out var plans, judge: out m_judge, reason: out var reason)) {
            throw new InvalidOperationException(message: $"search failed to plan after validation: {reason}");
        }

        if (plans.Length == 0) {
            m_jobs = [];
            m_layout = null;
            m_host = null;
            m_base = null;

            return;
        }

        var rows = definition.State;
        var catalog = definition.StateCatalog;
        var layoutRebuilt = false;

        // A FrameHost's own Catalog is fixed at construction, but a document swap mints a fresh WorldDefinition (and
        // so a fresh StateCatalog instance) even when the row structure is byte-for-byte unchanged — a checkpoint
        // restore is exactly this case. Rebinding rows alone would leave the host answering reads against the OLD
        // catalog while a freshly compiled judge rule's operand carries a StateHandle minted against the NEW one, so
        // a catalog swap forces the same full rebuild a layout mismatch does.
        if ((m_layout is null) || !m_layout.Fits(rows: rows) || !ReferenceEquals(objA: m_catalog, objB: catalog)) {
            m_layout = new FrameLayout(rows: rows, topology: name => WorldTopologyCompilation.Find(definition, name));
            m_host = new FrameHost(layout: m_layout, rows: rows, catalog: catalog, patterns: patterns, tables: tables);
            m_base = new StateFrame(layout: m_layout, rows: rows);
            m_catalog = catalog;
            layoutRebuilt = true;

            foreach (var job in m_jobs) {
                job.Stamp = 0UL;
            }
        } else {
            m_host!.Rebind(rows: rows);
            m_base!.Rebind(rows: rows);
        }

        var jobs = new Job[plans.Length];

        for (var index = 0; index < plans.Length; index++) {
            var plan = plans[index];
            var tokens = (StateRows.FindStateRow(rows: rows, name: plan.Row.Tokens)?.Cells?.Count ?? 0);
            var levelCount = Math.Max(val1: 0, val2: (plan.Depth - 1));
            var kept = Array.Find(array: m_jobs, match: job => string.Equals(a: job.Plan.Row.Name, b: plan.Row.Name, comparisonType: StringComparison.Ordinal));

            if ((kept is not null) && (kept.Legal.Length == tokens)) {
                kept.Plan = plan;

                if (layoutRebuilt || (kept.Levels.Length != levelCount)) {
                    kept.Levels = Job.BuildLevels(depth: plan.Depth, layout: m_layout, rows: rows);
                    kept.Stamp = 0UL;
                } else {
                    foreach (var level in kept.Levels) {
                        level.Frame.Rebind(rows: rows);
                    }
                }

                jobs[index] = kept;
            } else {
                jobs[index] = new Job(plan: plan, tokenCapacity: tokens, layout: m_layout, rows: rows);
            }
        }

        m_jobs = jobs;
    }

    /// <summary>Advances every job by its node quota, landing a finished job's outputs through <paramref name="apply"/>.</summary>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="apply">Installs one mutation through the ordinary door.</param>
    /// <returns><see langword="true"/> when an output installed.</returns>
    public bool Step(ulong tick, Func<WorldMutation, bool> apply) {
        ArgumentNullException.ThrowIfNull(argument: apply);

        if ((m_jobs.Length == 0) || (m_host is null) || (m_base is null) || (m_layout is null)) {
            return false;
        }

        m_base.Load(source: m_store);
        var stamp = Stamp();
        var installed = false;

        foreach (var job in m_jobs) {
            if (job.Stamp != stamp) {
                Restart(job: job, stamp: stamp);
            }
            if (!job.Running) {
                continue;
            }

            Walk(job: job, tick: tick);

            if (job.Running) {
                continue;
            }

            job.Done = true;
            installed |= Land(job: job, apply: apply);
        }

        return installed;
    }

    /// <summary>Captures every job's progress.</summary>
    public WorldSearchCheckpoint Capture() {
        var jobs = new WorldSearchJobCheckpoint[m_jobs.Length];

        for (var index = 0; index < m_jobs.Length; index++) {
            var job = m_jobs[index];
            var levels = ((job.Levels.Length == 0) ? [] : new WorldSearchLevelCheckpoint[job.Levels.Length]);

            for (var level = 0; level < levels.Length; level++) {
                var entry = job.Levels[level];

                levels[level] = new WorldSearchLevelCheckpoint(
                    Token: entry.Token, Target: entry.Target, Alpha: entry.Alpha, Beta: entry.Beta,
                    Best: entry.Best, BestToken: entry.BestToken, BestTarget: entry.BestTarget, BaseTurn: entry.BaseTurn,
                    Values: entry.Frame.Values.ToArray()
                );
            }

            jobs[index] = new WorldSearchJobCheckpoint(
                Name: job.Plan.Row.Name, Stamp: job.Stamp, Running: job.Running, Done: job.Done, Token: job.Token, Target: job.Target,
                Count: job.Count, Legal: [.. job.Legal], Nodes: job.Nodes, BaseTurn: job.BaseTurn,
                PassDepth: job.PassDepth, Active: job.Active, Best: job.Best, BestToken: job.BestToken, BestTarget: job.BestTarget,
                Alpha: job.Alpha, Beta: job.Beta, Levels: levels
            );
        }

        return new WorldSearchCheckpoint(Jobs: jobs);
    }
    /// <summary>Restores every job's progress by name; a job the checkpoint lacks, or whose shape the checkpoint no
    /// longer matches, restarts on its next step.</summary>
    /// <param name="checkpoint">The checkpoint.</param>
    public void Restore(WorldSearchCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        foreach (var job in m_jobs) {
            var saved = Array.Find(array: checkpoint.Jobs, match: entry => string.Equals(a: entry.Name, b: job.Plan.Row.Name, comparisonType: StringComparison.Ordinal));

            if ((saved is null) || (saved.Legal.Length != job.Legal.Length) || (saved.Levels.Length != job.Levels.Length)) {
                job.Stamp = 0UL;
                job.Running = false;
                job.Done = false;

                continue;
            }

            var shapeMatches = true;

            for (var level = 0; level < job.Levels.Length; level++) {
                if (saved.Levels[level].Values.Length != job.Levels[level].Frame.Values.Length) {
                    shapeMatches = false;

                    break;
                }
            }
            if (!shapeMatches) {
                job.Stamp = 0UL;
                job.Running = false;
                job.Done = false;

                continue;
            }

            job.Stamp = saved.Stamp;
            job.Running = saved.Running;
            job.Done = saved.Done;
            job.Token = saved.Token;
            job.Target = saved.Target;
            job.Count = saved.Count;
            saved.Legal.AsSpan().CopyTo(destination: job.Legal);
            job.Nodes = saved.Nodes;
            job.BaseTurn = saved.BaseTurn;
            job.PassDepth = saved.PassDepth;
            job.Active = saved.Active;
            job.Best = saved.Best;
            job.BestToken = saved.BestToken;
            job.BestTarget = saved.BestTarget;
            job.Alpha = saved.Alpha;
            job.Beta = saved.Beta;

            for (var level = 0; level < job.Levels.Length; level++) {
                var entry = job.Levels[level];
                var restored = saved.Levels[level];

                entry.Token = restored.Token;
                entry.Target = restored.Target;
                entry.Alpha = restored.Alpha;
                entry.Beta = restored.Beta;
                entry.Best = restored.Best;
                entry.BestToken = restored.BestToken;
                entry.BestTarget = restored.BestTarget;
                entry.BaseTurn = restored.BaseTurn;
                restored.Values.AsSpan().CopyTo(destination: entry.Frame.Values);
            }
        }
    }
    /// <summary>Folds every job's progress into a state hash.</summary>
    /// <param name="hash">The running hash.</param>
    public void AppendHash(ref Fnv1aHash hash) {
        hash.Add(value: ((uint)m_jobs.Length));

        foreach (var job in m_jobs) {
            hash.Add(value: Fnv1aHash.Compute(values: job.Plan.Row.Name.AsSpan()));
            hash.Add(value: job.Stamp);
            hash.Add(value: ((byte)(job.Running ? 1 : 0)));
            hash.Add(value: ((byte)(job.Done ? 1 : 0)));
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

            foreach (var mask in job.Legal) {
                hash.Add(value: mask);
            }
            foreach (var level in job.Levels) {
                hash.Add(value: ((uint)level.Token));
                hash.Add(value: ((uint)level.Target));
                hash.Add(value: level.Alpha);
                hash.Add(value: level.Beta);
                hash.Add(value: level.Best);
                hash.Add(value: ((uint)level.BestToken));
                hash.Add(value: ((uint)level.BestTarget));
                hash.Add(value: level.BaseTurn);

                foreach (var value in level.Frame.Values) {
                    hash.Add(value: value);
                }
            }
        }
    }
    /// <summary>Lists every job's progress.</summary>
    public IReadOnlyList<WorldSearchStatus> Status() {
        var status = new WorldSearchStatus[m_jobs.Length];

        for (var index = 0; index < m_jobs.Length; index++) {
            var job = m_jobs[index];

            status[index] = new WorldSearchStatus(
                Name: job.Plan.Row.Name, Running: job.Running, Done: job.Done, Token: job.Token, Tokens: job.Legal.Length, Target: job.Target,
                Cells: job.Plan.Topology.CellCount, Count: job.Count, Nodes: job.Nodes, NodesPerTick: job.Plan.Nodes, JudgeCost: job.Plan.JudgeCost, JudgeRules: m_judge.Length,
                HasScore: (job.Plan.Score is not null), Depth: job.Plan.Depth, PassDepth: job.PassDepth, BestScore: job.Best, BestToken: job.BestToken, BestTarget: job.BestTarget
            );
        }

        return status;
    }

    // Every framed value except the jobs' own output rows: an output landing never restarts the job that wrote it.
    private ulong Stamp() {
        var hash = new Fnv1aHash();
        var values = m_base!.Values;

        for (var ordinal = 0; ordinal < m_layout!.RowCount; ordinal++) {
            var layout = m_layout[ordinal];

            if ((layout.Kind == FrameRowKind.Unframed) || IsOutput(name: m_base.Rows[ordinal].Name.Value)) {
                continue;
            }

            foreach (var value in values.Slice(start: layout.Offset, length: layout.Length)) {
                hash.Add(value: value);
            }
        }

        return hash.Value;
    }

    private bool IsOutput(string name) {
        foreach (var job in m_jobs) {
            if (
                string.Equals(a: job.Plan.Row.Legal, b: name, comparisonType: StringComparison.Ordinal) ||
                string.Equals(a: job.Plan.Row.Count, b: name, comparisonType: StringComparison.Ordinal) ||
                string.Equals(a: job.Plan.Best, b: name, comparisonType: StringComparison.Ordinal)
            ) {
                return true;
            }
        }

        return false;
    }

    private void Restart(Job job, ulong stamp) {
        job.Stamp = stamp;
        job.Running = true;
        job.Done = false;
        job.Nodes = 0L;
        job.BaseTurn = Slot(store: m_base!, name: job.Plan.Turn);
        job.PassDepth = 1;
        ResetPass(job: job);
    }

    // A fresh iterative-deepening pass over the same (unchanged) root position: legal/count are recomputed
    // identically every pass, and the running negamax result starts over at the terminal sentinel. The caller owns
    // PassDepth — this never touches it, so a mid-search pass transition (already incremented) and a full restart
    // (set to 1) share the same reset.
    private static void ResetPass(Job job) {
        job.Active = 0;
        job.Token = 0;
        job.Target = 0;
        job.Count = 0L;
        job.Legal.AsSpan().Clear();
        job.Best = -WorldSearchCapacity.MateScore;
        job.BestToken = -1;
        job.BestTarget = -1;
        job.Alpha = -WorldSearchCapacity.MateScore;
        job.Beta = WorldSearchCapacity.MateScore;
    }

    // One relocation per node: the token moves to the target cell, whatever stood there leaves the board, the judge
    // runs, and the verdict at its accept value with the turn changed is an accepted relocation. Ply 0 is the root —
    // its own (token, target) cursor lives on the job and is always exhausted before the job finishes, so legal/count
    // never depend on whether a score is authored or how deep the search goes. A ply past 0 lives on job.Levels and
    // exists only long enough to negamax one accepted root candidate (or a descendant of one) to the pass's depth.
    private void Walk(Job job, ulong tick) {
        var host = m_host!;
        var scratch = host.Frame;
        var plan = job.Plan;
        var rows = m_base!.Rows;
        var tokens = StateRows.FindStateRow(rows: rows, name: plan.Row.Tokens);
        var turn = StateRows.FindStateRow(rows: rows, name: plan.Turn);
        var verdict = StateRows.FindStateRow(rows: rows, name: plan.Verdict);
        var cells = plan.Topology.CellCount;

        if ((tokens?.Cells is not { } tokenCells) || (turn is null) || (verdict is null) || (tokenCells.Count != job.Legal.Length)) {
            job.Running = false;

            return;
        }

        var hasScore = (plan.Score is not null);
        var budget = plan.Nodes;

        while ((budget > 0) && job.Running) {
            var p = job.Active;
            var frame = Position(job: job, p: p);
            var token = CursorToken(job: job, p: p);

            if (token >= tokenCells.Count) {
                if (p == 0) {
                    if (!hasScore || (job.PassDepth >= plan.Depth)) {
                        job.Running = false;
                    } else {
                        job.PassDepth++;
                        ResetPass(job: job);
                    }
                } else {
                    var value = -CursorBest(job: job, p: p);
                    var parent = (p - 1);

                    Fold(job: job, p: parent, value: value, token: CursorToken(job: job, p: parent), target: CursorTarget(job: job, p: parent));
                    Advance(job: job, p: parent, cells: cells, tokenCount: tokenCells.Count);
                    job.Active = parent;
                }

                continue;
            }

            var target = CursorTarget(job: job, p: p);
            var from = (frame.TryStoredAt(row: tokens, index: token, value: out var stored) ? stored : plan.Off);

            if ((from < 0L) || (from >= cells) || (target >= cells)) {
                SetCursorToken(job: job, p: p, value: (token + 1));
                SetCursorTarget(job: job, p: p, value: 0);

                continue;
            }
            if (target == from) {
                SetCursorTarget(job: job, p: p, value: (target + 1));

                continue;
            }

            scratch.CopyFrom(other: frame);
            _ = scratch.TryWrite(row: tokens, key: tokenCells[token].Key, value: target, write: StateWriteKind.Set, reason: out _);

            for (var other = 0; other < tokenCells.Count; other++) {
                if ((other != token) && frame.TryStoredAt(row: tokens, index: other, value: out var standing) && (standing == target)) {
                    _ = scratch.TryWrite(row: tokens, key: tokenCells[other].Key, value: plan.Off, write: StateWriteKind.Set, reason: out _);
                }
            }

            _ = host.Judge(rules: m_judge, tick: tick);

            var mover = CursorBaseTurn(job: job, p: p);
            var accepted = ((Slot(store: scratch, name: plan.Verdict) == plan.Row.Accept) && (Slot(store: scratch, name: plan.Turn) != mover));

            if ((p == 0) && accepted) {
                job.Count++;

                if (target < BoardMask.MaxCells) {
                    job.Legal[token] |= (1L << target);
                }
            }
            if (hasScore && accepted && (p < (job.PassDepth - 1))) {
                var next = (p + 1);
                var levelFrame = job.Levels[next - 1].Frame;

                levelFrame.CopyFrom(other: scratch);
                SetCursorToken(job: job, p: next, value: 0);
                SetCursorTarget(job: job, p: next, value: 0);
                SetCursorBest(job: job, p: next, value: -WorldSearchCapacity.MateScore);
                SetCursorBestMove(job: job, p: next, token: -1, target: -1);
                SetCursorAlpha(job: job, p: next, value: -CursorBeta(job: job, p: p));
                SetCursorBeta(job: job, p: next, value: -CursorAlpha(job: job, p: p));
                SetCursorBaseTurn(job: job, p: next, value: Slot(store: scratch, name: plan.Turn));
                job.Active = next;
            } else if (hasScore && accepted) {
                var value = EvaluateScore(plan: plan, tick: tick);

                Fold(job: job, p: p, value: value, token: token, target: target);
                Advance(job: job, p: p, cells: cells, tokenCount: tokenCells.Count);
            } else {
                Advance(job: job, p: p, cells: cells, tokenCount: tokenCells.Count);
            }

            job.Nodes++;
            budget--;
        }
    }

    private long EvaluateScore(WorldSearchPlan plan, ulong tick) =>
        (m_host!.Evaluator.TryEvaluateExpression(program: plan.Score!, kind: CellKind.Int, tick: tick, value: out var value) ? value : 0L);

    private static void Advance(Job job, int p, int cells, int tokenCount) {
        var target = (CursorTarget(job: job, p: p) + 1);

        if (target >= cells) {
            SetCursorToken(job: job, p: p, value: (CursorToken(job: job, p: p) + 1));
            target = 0;
        }

        SetCursorTarget(job: job, p: p, value: target);

        // The root ply never prunes: every candidate is tried, so legal/count are exhaustive whether or not a score
        // is authored. A ply past the root may cut once its window has closed.
        if ((p > 0) && (CursorAlpha(job: job, p: p) >= CursorBeta(job: job, p: p))) {
            SetCursorToken(job: job, p: p, value: tokenCount);
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

    private bool Land(Job job, Func<WorldMutation, bool> apply) {
        var plan = job.Plan;
        var rows = m_live();

        m_outputs.Clear();

        if ((plan.Row.Legal is { } legal) && (StateRows.FindStateRow(rows: rows, name: plan.Row.Tokens)?.Cells is { } tokenCells)) {
            for (var index = 0; (index < tokenCells.Count) && (index < job.Legal.Length); index++) {
                m_outputs.Add(item: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: legal, Key: tokenCells[index].Key.Value, Value: job.Legal[index], Kind: WorldDocumentWriteKind.Set));
            }
        }
        if (plan.Row.Count is { } count) {
            m_outputs.Add(item: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: count, Key: StateRow.SlotKey.Value, Value: job.Count, Kind: WorldDocumentWriteKind.Set));
        }
        if (plan.Best is { } best) {
            m_outputs.Add(item: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: best, Key: "token", Value: job.BestToken, Kind: WorldDocumentWriteKind.Set));
            m_outputs.Add(item: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: best, Key: "to", Value: job.BestTarget, Kind: WorldDocumentWriteKind.Set));
            m_outputs.Add(item: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: best, Key: "score", Value: job.Best, Kind: WorldDocumentWriteKind.Set));
        }
        if (m_outputs.Count == 0) {
            return false;
        }

        var mutation = ((m_outputs.Count == 1) ? m_outputs[0] : new WorldMutation.Batch(Principal: WorldPrincipal.World, Mutations: [.. m_outputs]));

        if (apply(mutation)) {
            return true;
        }

        Console.Error.WriteLine(value: $"[world.search: job '{plan.Row.Name}' finished but its outputs were refused by the mutation door; world.search shows the count it found]");

        return false;
    }

    private static long Slot(StateStore store, string name) =>
        (((store.Find(name: name) is { } row) && store.TryStored(row: row, key: StateRow.SlotKey, value: out var value, text: out _)) ? value : 0L);
}
