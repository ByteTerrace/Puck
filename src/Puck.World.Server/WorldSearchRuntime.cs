using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One recursive ply's checkpointed progress: the position it enumerates candidates from (<see cref="Values"/>,
/// a frame snapshot in the layout's own order), its own (shape, token, candidate) cursor, its negamax window, and the
/// best candidate found so far.</summary>
public sealed record WorldSearchLevelCheckpoint(int Shape, int Token, int Target, long Alpha, long Beta, long Best, int BestToken, int BestTarget, long BaseTurn, long[] Values, ulong Key = 0UL, long AlphaEntry = 0L);

/// <summary>One job's checkpointed progress. <see cref="Legal"/> holds one mask per token in the token row's cell
/// order, filled as the job walks — populated only for a board of at most <c>BoardMask.MaxCells</c> cells.
/// <see cref="Counts"/> holds one accepted-candidate count per token, size-agnostic. <see cref="Wide"/> holds one
/// bit-packed accepted-destination set per token (empty when the job authors no <c>reach</c> output), size-agnostic.
/// <see cref="Levels"/> holds one entry per ply beyond the root — empty for a depth-one job — populated only while
/// the negamax search has descended into it.</summary>
public sealed record WorldSearchJobCheckpoint(
    string Name, ulong Stamp, bool Running, bool Done, int Shape, int Token, int Target, long Count, long[] Legal, long[] Counts, long[] Wide, long Nodes, long BaseTurn,
    int PassDepth, int Active, long Best, int BestToken, int BestTarget, long Alpha, long Beta, WorldSearchLevelCheckpoint[] Levels,
    ulong[] TtKey, long[] TtValue, long[] TtMeta, WorldSearchTreeCheckpoint? Tree = null
);

/// <summary>A job's tree search in flight: the node pool (parallel arrays, <see cref="Count"/> nodes used), the
/// path from the root, the phase and its cursors, the playout and path frames, and the draw seed.</summary>
public sealed record WorldSearchTreeCheckpoint(
    bool Active, int Phase, int Count, int Iteration, ulong Seed, int UShape, int UToken, int UTarget, int UScan, int UStart, int PlayoutPlies,
    long[] Parent, long[] FirstChild, long[] ChildCount, long[] Visits, long[] Total, long[] Shape, long[] Token, long[] Target, long[] Expanded,
    long[] Path, int PathLength, long[] UctValues, long[] PlayValues
);

/// <summary>The search runtime's checkpointed state, in section order.</summary>
public sealed record WorldSearchCheckpoint(WorldSearchJobCheckpoint[] Jobs) {
    /// <summary>Gets the checkpoint of a runtime with no jobs.</summary>
    public static WorldSearchCheckpoint Empty { get; } = new(Jobs: []);
}

/// <summary>One job's progress as the console reads it.</summary>
public readonly record struct WorldSearchStatus(
    string Name, bool Running, bool Done, int Token, int Tokens, int Target, int Cells, long Count, long Nodes, int NodesPerTick, long JudgeCost, int JudgeRules,
    bool HasScore, int Depth, int PassDepth, long BestScore, int BestToken, int BestTarget, bool HasOutcome = false, int Iteration = 0, int Iterations = 0
);

/// <summary>Runs the document's search jobs: a frame over the installed section, the rules a frame can evaluate, and
/// per job a walk over every (shape, token, target cell or direction) candidate judged by those rules under a
/// per-tick node quota. A job restarts whenever the frame's inputs change and lands its answer through the ordinary
/// mutation door when the walk completes. Progress is simulation state: it hashes and checkpoints.
///
/// A job with an authored score iterative-deepens: for each authored depth in turn, the same root walk that always
/// populates <c>legal</c>/<c>count</c>/<c>reach</c>/<c>counts</c> also negamaxes every accepted candidate to that
/// depth and keeps the best. The recursion below the root runs on an explicit stack (<see cref="Job.Levels"/>, one
/// <see cref="StateFrame"/> per ply beyond the root) rather than the call stack, so a tick boundary can suspend it
/// anywhere and a checkpoint carries it byte-for-byte. The root ply never prunes and never skips a candidate, so a
/// depth-one job's root outputs are unchanged by whether a score is authored.</summary>
internal sealed partial class WorldSearchRuntime {
    private sealed class Level {
        public StateFrame Frame = null!;
        public int Shape;
        public int Token;
        public int Target;
        public long Alpha;
        public long Beta;
        public long Best;
        public int BestToken = -1;
        public int BestTarget = -1;
        public long BaseTurn;
        // The position's frame hash and the window's lower edge at entry, for the transposition table's store.
        public ulong Key;
        public long AlphaEntry;
    }

    private sealed class Job {
        public Job(WorldSearchPlan plan, int tokenCapacity, FrameLayout layout, IReadOnlyList<StateRow> rows) {
            Plan = plan;
            Legal = new long[tokenCapacity];
            Counts = new long[tokenCapacity];
            Wide = ((plan.Row.Reach is not null) ? new long[tokenCapacity * WideWordsPerToken(cellCount: plan.CellCount)] : null);
            Levels = BuildLevels(depth: plan.Depth, layout: layout, rows: rows);
            ZoneRows = ResolveZones(plan: plan, rows: rows);

            if ((plan.Score is not null) && (plan.Method == WorldSearchMethod.Negamax)) {
                TtKey = new ulong[WorldSearchCapacity.TranspositionEntries];
                TtValue = new long[WorldSearchCapacity.TranspositionEntries];
                TtMeta = new long[WorldSearchCapacity.TranspositionEntries];
            }
            if (plan.Method == WorldSearchMethod.Tree) {
                var nodes = WorldSearchCapacity.TreeNodes;

                TreeParent = new int[nodes];
                TreeFirstChild = new int[nodes];
                TreeChildCount = new int[nodes];
                TreeVisits = new long[nodes];
                TreeTotal = new long[nodes];
                TreeShape = new int[nodes];
                TreeToken = new int[nodes];
                TreeTarget = new int[nodes];
                TreeExpanded = new long[nodes];
                Path = new int[plan.Depth + 2];
                UctFrame = new StateFrame(layout: layout, rows: rows);
                PlayFrame = new StateFrame(layout: layout, rows: rows);
            }
        }

        // The tree search (WorldSearchRuntime.Uct.cs), allocated only for a job with an outcome.
        public bool UctActive { get; set; }
        public int Phase { get; set; }
        public int TreeCount { get; set; }
        public int Iteration { get; set; }
        public ulong Seed;
        public int UShape { get; set; }
        public int UToken { get; set; }
        public int UTarget { get; set; }
        public int UScan { get; set; }
        public int UStart { get; set; }
        public int PlayoutPlies { get; set; }
        public int PathLength { get; set; }
        public int[]? TreeParent { get; set; }
        public int[]? TreeFirstChild { get; set; }
        public int[]? TreeChildCount { get; set; }
        public long[]? TreeVisits { get; set; }
        public long[]? TreeTotal { get; set; }
        public int[]? TreeShape { get; set; }
        public int[]? TreeToken { get; set; }
        public int[]? TreeTarget { get; set; }
        public long[]? TreeExpanded { get; set; }
        public int[]? Path { get; set; }
        public StateFrame? UctFrame { get; set; }
        public StateFrame? PlayFrame { get; set; }

        // The transposition table: a position's frame hash, the value negamax found there, and (in TtMeta) how many
        // plies that value covers in the low byte with its bound flag above — 1 exact, 2 lower, 3 upper. Slot 0 of
        // TtMeta reads 0 for an empty entry, which no stored entry produces. Cleared on restart, kept across passes.
        public ulong[]? TtKey { get; set; }
        public long[]? TtValue { get; set; }
        public long[]? TtMeta { get; set; }

        public WorldSearchPlan Plan { get; set; }
        // A zone job's zones as rows of the bound section, in plan order — the cells a token's value is the index of.
        // Null for a board job. Rebound with the rows.
        public StateRow[]? ZoneRows { get; set; }
        public ulong Stamp { get; set; }
        public bool Running { get; set; }
        public bool Done { get; set; }
        public int Shape { get; set; }
        public int Token { get; set; }
        public int Target { get; set; }
        public long Count { get; set; }
        public long[] Legal { get; set; }
        public long[] Counts { get; set; }
        public long[]? Wide { get; set; }
        public long Nodes { get; set; }
        public long BaseTurn { get; set; }

        // Iterative-deepening negamax, engaged only when Plan.Score is not null. PassDepth is the depth the current
        // pass searches to; Active is which ply (0 = root) is being expanded. Best/BestToken/BestTarget are the root's
        // own running negamax result, overwritten every pass, so whatever they hold when the job finishes is the
        // deepest completed pass's answer. Alpha/Beta are the root's own window: it updates Alpha as candidates fold
        // in (so a child gets a tighter bound), but never breaks its own loop on it — every root candidate is always
        // tried, so the root outputs never depend on whether a score is authored.
        public int PassDepth { get; set; } = 1;
        public int Active { get; set; }
        public long Best { get; set; } = -WorldSearchCapacity.MateScore;
        public int BestToken { get; set; } = -1;
        public int BestTarget { get; set; } = -1;
        public long Alpha { get; set; } = -WorldSearchCapacity.MateScore;
        public long Beta { get; set; } = WorldSearchCapacity.MateScore;
        public Level[] Levels { get; set; }

        public static StateRow[]? ResolveZones(WorldSearchPlan plan, IReadOnlyList<StateRow> rows) {
            if (plan.Zones.Length == 0) {
                return null;
            }

            var zones = new StateRow[plan.Zones.Length];

            for (var index = 0; index < zones.Length; index++) {
                zones[index] = (StateRows.FindStateRow(rows: rows, name: plan.Zones[index]) ?? throw new InvalidOperationException(message: $"search '{plan.Row.Name}' zone '{plan.Zones[index]}' vanished after planning"));
            }

            return zones;
        }
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
            var expectedWide = ((plan.Row.Reach is not null) ? (tokens * WideWordsPerToken(cellCount: plan.CellCount)) : 0);
            var kept = Array.Find(array: m_jobs, match: job => string.Equals(a: job.Plan.Row.Name, b: plan.Row.Name, comparisonType: StringComparison.Ordinal));

            if ((kept is not null) && (kept.Legal.Length == tokens) && ((kept.Wide?.Length ?? 0) == expectedWide)) {
                kept.Plan = plan;
                kept.ZoneRows = Job.ResolveZones(plan: plan, rows: rows);

                if (layoutRebuilt || (kept.Levels.Length != levelCount)) {
                    kept.Levels = Job.BuildLevels(depth: plan.Depth, layout: m_layout, rows: rows);
                    kept.Stamp = 0UL;
                } else {
                    foreach (var level in kept.Levels) {
                        level.Frame.Rebind(rows: rows);
                    }
                }
                if (kept.TreeParent is not null) {
                    if (layoutRebuilt) {
                        kept.UctFrame = new StateFrame(layout: m_layout, rows: rows);
                        kept.PlayFrame = new StateFrame(layout: m_layout, rows: rows);
                        kept.Stamp = 0UL;
                    } else {
                        kept.UctFrame!.Rebind(rows: rows);
                        kept.PlayFrame!.Rebind(rows: rows);
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
                    Shape: entry.Shape, Token: entry.Token, Target: entry.Target, Alpha: entry.Alpha, Beta: entry.Beta,
                    Best: entry.Best, BestToken: entry.BestToken, BestTarget: entry.BestTarget, BaseTurn: entry.BaseTurn,
                    Values: entry.Frame.Values.ToArray(), Key: entry.Key, AlphaEntry: entry.AlphaEntry
                );
            }

            jobs[index] = new WorldSearchJobCheckpoint(
                Name: job.Plan.Row.Name, Stamp: job.Stamp, Running: job.Running, Done: job.Done, Shape: job.Shape, Token: job.Token, Target: job.Target,
                Count: job.Count, Legal: [.. job.Legal], Counts: [.. job.Counts], Wide: ((job.Wide is { } wide) ? [.. wide] : []), Nodes: job.Nodes, BaseTurn: job.BaseTurn,
                PassDepth: job.PassDepth, Active: job.Active, Best: job.Best, BestToken: job.BestToken, BestTarget: job.BestTarget,
                Alpha: job.Alpha, Beta: job.Beta, Levels: levels,
                TtKey: ((job.TtKey is { } ttKey) ? [.. ttKey] : []), TtValue: ((job.TtValue is { } ttValue) ? [.. ttValue] : []), TtMeta: ((job.TtMeta is { } ttMeta) ? [.. ttMeta] : []),
                Tree: CaptureTree(job: job)
            );
        }

        return new WorldSearchCheckpoint(Jobs: jobs);
    }
    private static long[] Widen(int[] values) {
        var wide = new long[values.Length];

        for (var index = 0; index < values.Length; index++) {
            wide[index] = values[index];
        }

        return wide;
    }
    private static void Narrow(long[] wide, int[] into) {
        for (var index = 0; index < into.Length; index++) {
            into[index] = (int)wide[index];
        }
    }
    private static WorldSearchTreeCheckpoint? CaptureTree(Job job) => ((job.TreeParent is null)
        ? null
        : new WorldSearchTreeCheckpoint(
            Active: job.UctActive, Phase: job.Phase, Count: job.TreeCount, Iteration: job.Iteration, Seed: job.Seed, UShape: job.UShape, UToken: job.UToken, UTarget: job.UTarget,
            UScan: job.UScan, UStart: job.UStart, PlayoutPlies: job.PlayoutPlies,
            Parent: Widen(job.TreeParent), FirstChild: Widen(job.TreeFirstChild!), ChildCount: Widen(job.TreeChildCount!), Visits: [.. job.TreeVisits!], Total: [.. job.TreeTotal!],
            Shape: Widen(job.TreeShape!), Token: Widen(job.TreeToken!), Target: Widen(job.TreeTarget!), Expanded: [.. job.TreeExpanded!],
            Path: Widen(job.Path!), PathLength: job.PathLength, UctValues: job.UctFrame!.Values.ToArray(), PlayValues: job.PlayFrame!.Values.ToArray()
        ));
    private static bool TreeFits(Job job, WorldSearchTreeCheckpoint? tree) {
        if (job.TreeParent is null) {
            return (tree is null);
        }
        if (tree is null) {
            return false;
        }

        var nodes = job.TreeParent.Length;

        return (tree.Parent.Length == nodes) && (tree.FirstChild.Length == nodes) && (tree.ChildCount.Length == nodes) && (tree.Visits.Length == nodes) && (tree.Total.Length == nodes) &&
            (tree.Shape.Length == nodes) && (tree.Token.Length == nodes) && (tree.Target.Length == nodes) && (tree.Expanded.Length == nodes) &&
            (tree.Path.Length == job.Path!.Length) && (tree.UctValues.Length == job.UctFrame!.Values.Length) && (tree.PlayValues.Length == job.PlayFrame!.Values.Length);
    }
    private static void RestoreTree(Job job, WorldSearchTreeCheckpoint? tree) {
        if ((job.TreeParent is null) || (tree is null)) {
            return;
        }

        job.UctActive = tree.Active;
        job.Phase = tree.Phase;
        job.TreeCount = tree.Count;
        job.Iteration = tree.Iteration;
        job.Seed = tree.Seed;
        job.UShape = tree.UShape;
        job.UToken = tree.UToken;
        job.UTarget = tree.UTarget;
        job.UScan = tree.UScan;
        job.UStart = tree.UStart;
        job.PlayoutPlies = tree.PlayoutPlies;
        Narrow(tree.Parent, job.TreeParent);
        Narrow(tree.FirstChild, job.TreeFirstChild!);
        Narrow(tree.ChildCount, job.TreeChildCount!);
        tree.Visits.AsSpan().CopyTo(destination: job.TreeVisits!);
        tree.Total.AsSpan().CopyTo(destination: job.TreeTotal!);
        Narrow(tree.Shape, job.TreeShape!);
        Narrow(tree.Token, job.TreeToken!);
        Narrow(tree.Target, job.TreeTarget!);
        tree.Expanded.AsSpan().CopyTo(destination: job.TreeExpanded!);
        Narrow(tree.Path, job.Path!);
        job.PathLength = tree.PathLength;
        tree.UctValues.AsSpan().CopyTo(destination: job.UctFrame!.Values);
        tree.PlayValues.AsSpan().CopyTo(destination: job.PlayFrame!.Values);
    }

    /// <summary>Restores every job's progress by name; a job the checkpoint lacks, or whose shape the checkpoint no
    /// longer matches, restarts on its next step.</summary>
    /// <param name="checkpoint">The checkpoint.</param>
    public void Restore(WorldSearchCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        foreach (var job in m_jobs) {
            var saved = Array.Find(array: checkpoint.Jobs, match: entry => string.Equals(a: entry.Name, b: job.Plan.Row.Name, comparisonType: StringComparison.Ordinal));

            if (
                (saved is null) || (saved.Legal.Length != job.Legal.Length) || (saved.Counts.Length != job.Counts.Length) ||
                (saved.Wide.Length != (job.Wide?.Length ?? 0)) || (saved.Levels.Length != job.Levels.Length) ||
                (saved.TtKey.Length != (job.TtKey?.Length ?? 0)) || (saved.TtValue.Length != (job.TtValue?.Length ?? 0)) || (saved.TtMeta.Length != (job.TtMeta?.Length ?? 0)) ||
                !TreeFits(job: job, tree: saved.Tree)
            ) {
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
            job.Shape = saved.Shape;
            job.Token = saved.Token;
            job.Target = saved.Target;
            job.Count = saved.Count;
            saved.Legal.AsSpan().CopyTo(destination: job.Legal);
            saved.Counts.AsSpan().CopyTo(destination: job.Counts);
            if (job.Wide is { } wide) {
                saved.Wide.AsSpan().CopyTo(destination: wide);
            }
            job.Nodes = saved.Nodes;
            job.BaseTurn = saved.BaseTurn;
            job.PassDepth = saved.PassDepth;
            job.Active = saved.Active;
            job.Best = saved.Best;
            job.BestToken = saved.BestToken;
            job.BestTarget = saved.BestTarget;
            job.Alpha = saved.Alpha;
            job.Beta = saved.Beta;
            if (job.TtKey is { } ttKey) {
                saved.TtKey.AsSpan().CopyTo(destination: ttKey);
                saved.TtValue.AsSpan().CopyTo(destination: job.TtValue!);
                saved.TtMeta.AsSpan().CopyTo(destination: job.TtMeta!);
            }
            RestoreTree(job: job, tree: saved.Tree);

            for (var level = 0; level < job.Levels.Length; level++) {
                var entry = job.Levels[level];
                var restored = saved.Levels[level];

                entry.Key = restored.Key;
                entry.AlphaEntry = restored.AlphaEntry;

                entry.Shape = restored.Shape;
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

                foreach (var value in level.Frame.Values) {
                    hash.Add(value: value);
                }
            }
            if (job.TtKey is { } ttKey) {
                for (var slot = 0; slot < ttKey.Length; slot++) {
                    hash.Add(value: ttKey[slot]);
                    hash.Add(value: job.TtValue![slot]);
                    hash.Add(value: job.TtMeta![slot]);
                }
            }
            if (job.TreeParent is { } parents) {
                hash.Add(value: ((byte)(job.UctActive ? 1 : 0)));
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
                hash.Add(value: ((uint)job.PathLength));

                for (var node = 0; node < job.TreeCount; node++) {
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
                for (var index = 0; index < job.PathLength; index++) {
                    hash.Add(value: ((uint)job.Path![index]));
                }
                foreach (var value in job.UctFrame!.Values) {
                    hash.Add(value: value);
                }
                foreach (var value in job.PlayFrame!.Values) {
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
                Cells: job.Plan.CellCount, Count: job.Count, Nodes: job.Nodes, NodesPerTick: job.Plan.Nodes, JudgeCost: job.Plan.JudgeCost, JudgeRules: m_judge.Length,
                HasScore: ((job.Plan.Score is not null) && (job.Plan.Method == WorldSearchMethod.Negamax)), Depth: job.Plan.Depth, PassDepth: job.PassDepth, BestScore: job.Best, BestToken: job.BestToken, BestTarget: job.BestTarget,
                HasOutcome: (job.Plan.Method == WorldSearchMethod.Tree), Iteration: job.Iteration, Iterations: job.Plan.Iterations
            );
        }

        return status;
    }

    // Every framed value except the jobs' own output rows: an output landing never restarts the job that wrote it.
    private ulong Stamp() {
        var hash = Fnv1aHash.Create();
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
                string.Equals(a: job.Plan.Row.Reach, b: name, comparisonType: StringComparison.Ordinal) ||
                string.Equals(a: job.Plan.Row.Counts, b: name, comparisonType: StringComparison.Ordinal) ||
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
        job.TtKey?.AsSpan().Clear();
        job.TtValue?.AsSpan().Clear();
        job.TtMeta?.AsSpan().Clear();
        job.UctActive = false;
        job.TreeCount = 0;
        job.Iteration = 0;
        ResetPass(job: job);
    }

    // A fresh iterative-deepening pass over the same (unchanged) root position: the root outputs are recomputed
    // identically every pass, and the running negamax result starts over at the terminal sentinel. The caller owns
    // PassDepth — this never touches it, so a mid-search pass transition (already incremented) and a full restart
    // (set to 1) share the same reset.
    private static void ResetPass(Job job) {
        job.Active = 0;
        job.Shape = 0;
        job.Token = 0;
        job.Target = 0;
        job.Count = 0L;
        job.Legal.AsSpan().Clear();
        job.Counts.AsSpan().Clear();
        job.Wide?.AsSpan().Clear();
        job.Best = -WorldSearchCapacity.MateScore;
        job.BestToken = -1;
        job.BestTarget = -1;
        job.Alpha = -WorldSearchCapacity.MateScore;
        job.Beta = WorldSearchCapacity.MateScore;
    }

    private static long Slot(StateStore store, string name) =>
        (((store.Find(name: name) is { } row) && store.TryStored(row: row, key: StateRow.SlotKey, value: out var value, text: out _)) ? value : 0L);
}
