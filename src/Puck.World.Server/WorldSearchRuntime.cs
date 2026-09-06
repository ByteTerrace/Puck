using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One job's checkpointed progress. <see cref="Legal"/> holds one mask per token in the token row's cell
/// order, filled as the job walks.</summary>
public sealed record WorldSearchJobCheckpoint(string Name, ulong Stamp, bool Running, bool Done, int Token, int Target, long Count, long[] Legal, long Nodes, long BaseTurn);

/// <summary>The search runtime's checkpointed state, in section order.</summary>
public sealed record WorldSearchCheckpoint(WorldSearchJobCheckpoint[] Jobs) {
    /// <summary>Gets the checkpoint of a runtime with no jobs.</summary>
    public static WorldSearchCheckpoint Empty { get; } = new(Jobs: []);
}

/// <summary>One job's progress as the console reads it.</summary>
public readonly record struct WorldSearchStatus(string Name, bool Running, bool Done, int Token, int Tokens, int Target, int Cells, long Count, long Nodes, int NodesPerTick, long JudgeCost, int JudgeRules);

/// <summary>Runs the document's search jobs: a frame over the installed section, the rules a frame can evaluate, and
/// per job a walk over every (token, target cell) relocation judged by those rules under a per-tick node quota. A
/// job restarts whenever the frame's inputs change and lands its answer through the ordinary mutation door when the
/// walk completes. Progress is simulation state: it hashes and checkpoints.</summary>
internal sealed class WorldSearchRuntime {
    private sealed class Job {
        public Job(WorldSearchPlan plan, int tokenCapacity) {
            Plan = plan;
            Legal = new long[tokenCapacity];
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

        if ((m_layout is null) || !m_layout.Fits(rows: rows)) {
            m_layout = new FrameLayout(rows: rows, topology: name => WorldTopologyCompilation.Find(definition, name));
            m_host = new FrameHost(layout: m_layout, rows: rows, catalog: definition.StateCatalog, patterns: patterns, tables: tables);
            m_base = new StateFrame(layout: m_layout, rows: rows);

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
            var kept = Array.Find(array: m_jobs, match: job => string.Equals(a: job.Plan.Row.Name, b: plan.Row.Name, comparisonType: StringComparison.Ordinal));

            if ((kept is not null) && (kept.Legal.Length == tokens)) {
                kept.Plan = plan;
                jobs[index] = kept;
            } else {
                jobs[index] = new Job(plan: plan, tokenCapacity: tokens);
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

            jobs[index] = new WorldSearchJobCheckpoint(Name: job.Plan.Row.Name, Stamp: job.Stamp, Running: job.Running, Done: job.Done, Token: job.Token, Target: job.Target, Count: job.Count, Legal: [.. job.Legal], Nodes: job.Nodes, BaseTurn: job.BaseTurn);
        }

        return new WorldSearchCheckpoint(Jobs: jobs);
    }
    /// <summary>Restores every job's progress by name; a job the checkpoint lacks restarts on its next step.</summary>
    /// <param name="checkpoint">The checkpoint.</param>
    public void Restore(WorldSearchCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        foreach (var job in m_jobs) {
            var saved = Array.Find(array: checkpoint.Jobs, match: entry => string.Equals(a: entry.Name, b: job.Plan.Row.Name, comparisonType: StringComparison.Ordinal));

            if ((saved is null) || (saved.Legal.Length != job.Legal.Length)) {
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

            foreach (var mask in job.Legal) {
                hash.Add(value: mask);
            }
        }
    }
    /// <summary>Lists every job's progress.</summary>
    public IReadOnlyList<WorldSearchStatus> Status() {
        var status = new WorldSearchStatus[m_jobs.Length];

        for (var index = 0; index < m_jobs.Length; index++) {
            var job = m_jobs[index];

            status[index] = new WorldSearchStatus(Name: job.Plan.Row.Name, Running: job.Running, Done: job.Done, Token: job.Token, Tokens: job.Legal.Length, Target: job.Target, Cells: job.Plan.Topology.CellCount, Count: job.Count, Nodes: job.Nodes, NodesPerTick: job.Plan.Nodes, JudgeCost: job.Plan.JudgeCost, JudgeRules: m_judge.Length);
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
            if (string.Equals(a: job.Plan.Row.Legal, b: name, comparisonType: StringComparison.Ordinal) || string.Equals(a: job.Plan.Row.Count, b: name, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    private void Restart(Job job, ulong stamp) {
        job.Stamp = stamp;
        job.Running = true;
        job.Done = false;
        job.Token = 0;
        job.Target = 0;
        job.Count = 0L;
        job.Nodes = 0L;
        job.Legal.AsSpan().Clear();
        job.BaseTurn = Slot(store: m_base!, name: job.Plan.Turn);
    }

    // One relocation per node: the token moves to the target cell, whatever stood there leaves the board, the judge
    // runs, and the verdict at its accept value with the turn changed is an accepted relocation.
    private void Walk(Job job, ulong tick) {
        var host = m_host!;
        var frame = host.Frame;
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

        var budget = plan.Nodes;

        while ((budget > 0) && (job.Token < tokenCells.Count)) {
            var from = (m_base.TryStoredAt(row: tokens, index: job.Token, value: out var stored) ? stored : plan.Off);

            if ((from < 0L) || (from >= cells) || (job.Target >= cells)) {
                job.Token++;
                job.Target = 0;

                continue;
            }
            if (job.Target == from) {
                job.Target++;

                continue;
            }

            frame.CopyFrom(other: m_base);
            _ = frame.TryWrite(row: tokens, key: tokenCells[job.Token].Key, value: job.Target, write: StateWriteKind.Set, reason: out _);

            for (var other = 0; other < tokenCells.Count; other++) {
                if ((other != job.Token) && m_base.TryStoredAt(row: tokens, index: other, value: out var standing) && (standing == job.Target)) {
                    _ = frame.TryWrite(row: tokens, key: tokenCells[other].Key, value: plan.Off, write: StateWriteKind.Set, reason: out _);
                }
            }

            _ = host.Judge(rules: m_judge, tick: tick);

            if ((Slot(store: frame, name: plan.Verdict) == plan.Row.Accept) && (Slot(store: frame, name: plan.Turn) != job.BaseTurn)) {
                job.Count++;

                if (job.Target < BoardMask.MaxCells) {
                    job.Legal[job.Token] |= (1L << job.Target);
                }
            }

            job.Nodes++;
            budget--;
            job.Target++;

            if (job.Target >= cells) {
                job.Token++;
                job.Target = 0;
            }
        }

        if (job.Token >= tokenCells.Count) {
            job.Running = false;
        }
    }

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
