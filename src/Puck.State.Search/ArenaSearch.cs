using Puck.Maths;

namespace Puck.State;

/// <summary>One job's progress as a read-back reads it.</summary>
/// <param name="Name">The job name.</param>
/// <param name="Running">Whether the job is still walking.</param>
/// <param name="Done">Whether the job has landed.</param>
/// <param name="Token">The token cursor.</param>
/// <param name="Tokens">How many tokens the job walks.</param>
/// <param name="Target">The candidate cursor.</param>
/// <param name="Cells">How many cells the job has.</param>
/// <param name="Count">How many candidates the root accepted.</param>
/// <param name="Nodes">How many candidates the job has judged.</param>
/// <param name="NodesPerStep">The most candidates one step judges.</param>
/// <param name="JudgeCost">The work units one judge run costs.</param>
/// <param name="Allowance">The work units one step may spend on the job.</param>
/// <param name="PeakStepWork">The most work units any one step has spent on the job since it last restarted.</param>
/// <param name="Work">The work units the job has spent since it last restarted.</param>
/// <param name="HasScore">Whether the job compares plies by a score.</param>
/// <param name="Depth">The authored depth cap.</param>
/// <param name="PassDepth">The depth the current iterative-deepening pass searches to.</param>
/// <param name="BestScore">The best score the deepest completed pass found.</param>
/// <param name="BestToken">The token of the best candidate, or <c>-1</c>.</param>
/// <param name="BestTarget">The target of the best candidate, or <c>-1</c>.</param>
/// <param name="Iteration">How many tree iterations have run.</param>
/// <param name="Iterations">How many tree iterations the job runs.</param>
/// <param name="JudgeRules">How many rules a verdict evaluates.</param>
/// <param name="HasOutcome">Whether the job's method backpropagates an outcome rather than a score.</param>
public readonly record struct ArenaSearchStatus(
    string Name, bool Running, bool Done, int Token, int Tokens, int Target, int Cells, long Count, long Nodes, int NodesPerStep, long JudgeCost,
    long Allowance, long PeakStepWork, long Work,
    bool HasScore, int Depth, int PassDepth, long BestScore, int BestToken, int BestTarget, int Iteration, int Iterations,
    int JudgeRules = 0, bool HasOutcome = false
);
/// <summary>
/// Runs a set of search jobs over one <see cref="StateArena"/>: each candidate is a journal scope holding the
/// candidate's own writes and whatever the supplied <see cref="IArenaSearchJudge"/> writes on top of them, and each
/// ply of the search is one more scope open on the same arena. A job restarts whenever the arena's input rows
/// change and lands its answer as a set of <see cref="ArenaSearchWrite"/>s once the walk completes.
/// </summary>
/// <remarks>
/// <para>Every scope the search opens is rewound before <see cref="Step"/> returns, so the arena a caller reads
/// between steps is byte-for-byte what it was before the step; a suspended descent is replayed from its recorded
/// candidates at the start of the next step. That holds when a judge, a score, or the arena throws out of a step
/// too: the scopes the step had open are rewound before the exception leaves, and the job it interrupted restarts
/// on the next step, since its cursors stopped partway through a candidate. A landing that throws is different:
/// the walk is finished and its answer is whole, so the job stays unlanded and the next step hands the same answer
/// to the door again, unless the inputs moved and restarted it first.</para>
/// <para>A job's verdict and score come from its own <see cref="IArenaSearchJudge"/> over a reader-shaped view of
/// the scoped arena, so the search carries no document or wire concept: a caller resolves its own plans, supplies
/// its own judge, and translates a landed job's writes into whatever mutation vocabulary it owns.</para>
/// </remarks>
public sealed partial class ArenaSearch {
    // One ply of the walk: the root, a move ply, or a chance ply. A chance ply enumerates outcomes through its
    // candidate cursor and folds a weighted sum instead of a maximum; the rest of its window goes unused.
    private sealed class Level {
        public long Alpha = -SearchCapacity.MateScore;
        public long AlphaEntry;
        public long BaseTurn;
        public long Best = -SearchCapacity.MateScore;
        public int BestTarget = -1;
        public int BestToken = -1;
        public long Beta = SearchCapacity.MateScore;
        // The outcomes folded so far, each value times its weight, and the weight they carried. The sum is exact:
        // a baked table's whole weight fits one word, so values of MateScore magnitude under it stay inside 128 bits
        // however many outcomes share it.
        public Int128 ChanceSum;
        public ulong ChanceWeight;
        // The cell the candidate that opened this ply lands on, which is what the parent folds as its best move.
        public int EntryTarget = -1;
        // The position's key and the window's lower edge at entry, for the transposition table's store.
        public ulong Key;
        // The seat vector of the best line found at this ply. A scope rewind discards what the position carried, so
        // a max-n fold remembers the vector here instead of in the position it came from.
        public long[] Seats = [];
        public int Shape;
        public int Target;
        public int Token;
    }
    private sealed class Job {
        public Job(ArenaSearchPlan plan, int tokenCapacity, int cellCount, int seatCount) {
            var scopes = ((2 * plan.Depth) + 4);

            Counts = new long[tokenCapacity];
            Legal = new long[tokenCapacity];
            Levels = BuildLevels(
                depth: plan.Depth,
                seatCount: seatCount
            );
            Seats = new long[seatCount];
            Plan = plan;
            ScopeMark = new int[scopes];
            ScopeShape = new int[scopes];
            ScopeTarget = new int[scopes];
            ScopeToken = new int[scopes];
            TokenKeys = new CellKey[tokenCapacity];
            Wide = ((plan.ReachOrdinal >= 0)
                ? new long[(tokenCapacity * WideWordsPerToken(cellCount: cellCount))]
                : null
            );

            // A max-n job stores no transposition: a level folds a seat vector, and the table carries one value.
            if (
                plan.Scored &&
                (plan.Method == SearchMethod.Negamax)
            ) {
                TtKey = new ulong[SearchCapacity.TranspositionEntries];
                TtMeta = new long[SearchCapacity.TranspositionEntries];
                TtValue = new long[SearchCapacity.TranspositionEntries];
            }
            if (plan.Method == SearchMethod.Tree) {
                var nodes = SearchCapacity.TreeNodes;

                Path = new int[(plan.Depth + 2)];
                TreeChildCount = new int[nodes];
                TreeExpanded = new long[nodes];
                TreeFirstChild = new int[nodes];
                TreeParent = new int[nodes];
                TreeShape = new int[nodes];
                TreeTarget = new int[nodes];
                TreeToken = new int[nodes];
                TreeTotal = new long[nodes];
                TreeVisits = new long[nodes];
            }
        }

        public int Active { get; set; }
        public long Alpha { get => Root.Alpha; set => Root.Alpha = value; }
        public long BaseTurn { get => Root.BaseTurn; set => Root.BaseTurn = value; }
        public long Best { get => Root.Best; set => Root.Best = value; }
        public int BestTarget { get => Root.BestTarget; set => Root.BestTarget = value; }
        public int BestToken { get => Root.BestToken; set => Root.BestToken = value; }
        public long Beta { get => Root.Beta; set => Root.Beta = value; }
        // How many of the open scopes hold a chance draw rather than a move, so a ply counts moves alone.
        public int ChanceScopes { get; set; }

        public CellKey[] ChanceKeys { get; set; } = [];

        public long Count { get; set; }
        public long[] Counts { get; set; }
        public bool Done { get; set; }
        public int Iteration { get; set; }

        public IArenaSearchJudge Judge { get; set; } = null!;
        // Every row a position key folds: what the plan names and what the judge reads, ascending.
        public int[] KeyRows { get; set; } = [];

        public long[] Legal { get; set; }
        public Level[] Levels { get; set; }
        public long Nodes { get; set; }
        public int[]? Path { get; set; }
        public int PathLength { get; set; }
        public int Phase { get; set; }
        public ArenaSearchPlan Plan { get; set; }
        public int PlayCount { get; set; }
        public int PlayoutPlies { get; set; }

        // Ply zero. Iterative-deepening negamax is engaged only for a scored job; the root folds candidates into
        // its window but never cuts on it, so the root outputs never depend on whether a score is authored.
        public Level Root { get; } = new();

        public CellKey[] ScoreKeys { get; set; } = [];

        // The seat vector read off the arena, reused by every max-n fold.
        public long[] Seats { get; set; }
        // The open journal scopes, innermost last, beside the candidate each one applied: a step's suspension
        // rewinds them all and the next step replays them from these cursors, so no position is ever copied.
        public int[] ScopeMark { get; set; }
        public bool Running { get; set; }
        public int ScopeCount { get; set; }
        public int[] ScopeShape { get; set; }
        public int[] ScopeTarget { get; set; }
        public int[] ScopeToken { get; set; }
        public int Shape { get => Root.Shape; set => Root.Shape = value; }
        public ulong Stamp { get; set; }
        // The most any one step has spent on the job, and what the job has spent in all, since it restarted.
        public long PeakStepWork { get; set; }
        public int Target { get => Root.Target; set => Root.Target = value; }
        public int Token { get => Root.Token; set => Root.Token = value; }
        public int TokenCount { get; set; }
        public CellKey[] TokenKeys { get; set; }
        public int[]? TreeChildCount { get; set; }
        public int TreeCount { get; set; }
        public long[]? TreeExpanded { get; set; }
        public int[]? TreeFirstChild { get; set; }
        public int[]? TreeParent { get; set; }
        public int[]? TreeShape { get; set; }
        public int[]? TreeTarget { get; set; }
        public int[]? TreeToken { get; set; }
        public long[]? TreeTotal { get; set; }
        public long[]? TreeVisits { get; set; }
        // A position's value, the plies it covers in the low byte, and its bound flag above; slot 0 of TtMeta reads
        // 0 for an empty entry, which no stored entry produces.
        public ulong[]? TtKey { get; set; }
        public bool TreeActive { get; set; }
        public long[]? TtMeta { get; set; }
        public long[]? TtValue { get; set; }
        public int UScan { get; set; }
        public int UShape { get; set; }
        public int UStart { get; set; }
        public int UTarget { get; set; }
        public int UToken { get; set; }

        public CellKey[] ReachKeys { get; set; } = [];

        public long[]? Wide { get; set; }
        public long Work { get; set; }

        public ulong Seed;

        // PassDepth is the depth the current pass searches to; Active is which ply (0 = root) is being expanded.
        public int PassDepth { get; set; } = 1;

        public static Level[] BuildLevels(int depth, int seatCount) {
            var levels = new Level[Math.Max(
                val1: 0,
                val2: (depth - 1)
            )];

            for (var index = 0; (index < levels.Length); index++) {
                levels[index] = new Level { Seats = new long[seatCount] };
            }

            return levels;
        }
    }

    private readonly StateArena m_arena;
    private readonly Action<string, string>? m_narrate;
    private readonly List<ArenaSearchWrite> m_outputs = [];

    private Job[] m_jobs = [];
    // The stamp last folded and the version each input row stood at when it was. A version is this arena's own and
    // never checkpointed, so it only ever says the fold may be reused; the stamp a job compares stays the fold of
    // the rows' content, which a restored arena reproduces.
    private bool m_stampFolded;
    private ulong m_stampValue;
    private ulong[] m_stampVersions = [];
    private CellKey m_bestScoreKey;
    private CellKey m_bestTargetKey;
    private CellKey m_bestTokenKey;
    private CellKey m_slotKey;
    private ulong m_engineTick;
    private ulong m_tick;

    /// <summary>Initializes the search over one arena.</summary>
    /// <param name="arena">The arena every candidate scope opens on.</param>
    /// <param name="narrate">Delivers one narration line (channel, text), or <see langword="null"/> to leave a
    /// job's refused landing undelivered.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public ArenaSearch(StateArena arena, Action<string, string>? narrate = null) {
        ArgumentNullException.ThrowIfNull(argument: arena);
        m_arena = arena;
        m_narrate = narrate;

        ResolveCatalogKeys();
    }

    // The keys a landed job addresses its own output rows by. They are the catalog's, and a relayout replaces the
    // catalog, so they are re-resolved on every install rather than held from construction.
    private void ResolveCatalogKeys() {
        var keys = m_arena.Catalog.Keys;

        _ = keys.TryResolve(
            key: out m_bestScoreKey,
            name: CellName.Parse(candidate: "score")
        );
        _ = keys.TryResolve(
            key: out m_bestTargetKey,
            name: CellName.Parse(candidate: "to")
        );
        _ = keys.TryResolve(
            key: out m_bestTokenKey,
            name: CellName.Parse(candidate: "token")
        );
        _ = keys.TryResolve(
            key: out m_slotKey,
            name: StateRow.SlotKey
        );
    }

    /// <summary>Gets the arena every candidate scope opens on.</summary>
    public StateArena Arena => m_arena;
    /// <summary>Gets how many jobs are installed.</summary>
    public int Count => m_jobs.Length;

    /// <summary>Installs a job set, restarting every job on its next step.</summary>
    /// <param name="plans">Every job's resolved plan, in section order.</param>
    /// <param name="judges">Each plan's judge, parallel to <paramref name="plans"/>.</param>
    /// <param name="reason">Why the job set was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when every plan addresses rows this arena carries and every judge is
    /// admitted to its plan.</returns>
    /// <remarks>Admission is asked once here and never again, so a plan whose rules need a facet the judge's host
    /// does not serve is refused by name before any candidate is judged.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="plans"/> or <paramref name="judges"/> is
    /// <see langword="null"/>.</exception>
    public bool Rebuild(ArenaSearchPlan[] plans, IArenaSearchJudge[] judges, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: judges);
        ArgumentNullException.ThrowIfNull(argument: plans);

        if (judges.Length != plans.Length) {
            reason = $"the search was handed {judges.Length} judges for {plans.Length} plans";

            return false;
        }

        ResolveCatalogKeys();

        var jobs = new Job[plans.Length];
        var rows = m_arena.Layout.RowCount;

        for (var index = 0; (index < plans.Length); index++) {
            var plan = plans[index];
            var judge = judges[index];

            if (judge is null) {
                reason = $"search '{plan.Name}' was handed no judge";

                return false;
            }
            if (!TryCheckRows(
                plan: plan,
                reason: out reason,
                rows: rows
            )) {
                return false;
            }
            if (!ReferenceEquals(
                objA: judge.Arena,
                objB: m_arena
            )) {
                reason = $"search '{plan.Name}' was handed a judge over another arena";

                return false;
            }
            if (
                plan.Scored &&
                !judge.Scores
            ) {
                reason = $"search '{plan.Name}' is scored but its judge reads no score";

                return false;
            }
            if (
                (plan.BestOrdinal >= 0) &&
                (!m_bestScoreKey.IsValid || !m_bestTargetKey.IsValid || !m_bestTokenKey.IsValid)
            ) {
                reason = $"search '{plan.Name}' writes a best-move row, and this catalog interns no 'token', 'to', and 'score' keys to address it by";

                return false;
            }
            if (!judge.TryAdmit(
                plan: plan,
                refusal: out reason
            )) {
                return false;
            }
            if (!TryCheckWork(
                plan: plan,
                reason: out reason
            )) {
                return false;
            }

            var job = new Job(
                cellCount: plan.CellCount,
                plan: plan,
                seatCount: ((plan.ScoresOrdinal >= 0)
                    ? m_arena.CellCount(rowOrdinal: plan.ScoresOrdinal)
                    : 0),
                tokenCapacity: m_arena.CellCount(rowOrdinal: plan.TokensOrdinal)
            ) {
                Judge = judge,
                ReachKeys = ResolveReachKeys(plan: plan),
            };

            job.ChanceKeys = ResolveRowKeys(
                count: (plan.Chance?.CellCount ?? 0),
                rowOrdinal: (plan.Chance?.RowOrdinal ?? -1)
            );
            job.KeyRows = ResolveKeyRows(
                judge: judge,
                plan: plan
            );
            job.ScoreKeys = ResolveRowKeys(
                count: job.Seats.Length,
                rowOrdinal: plan.ScoresOrdinal
            );
            jobs[index] = job;
        }

        m_jobs = jobs;
        m_stampFolded = false;
        reason = string.Empty;

        return true;
    }
    /// <summary>Advances every job by what its allowance and its judged-candidate quota admit, landing a finished
    /// job's outputs through <paramref name="apply"/>.</summary>
    /// <param name="tick">The simulation tick.</param>
    /// <param name="engineTick">The engine tick a value-over-time read answers as of.</param>
    /// <param name="apply">Installs one job's writes through the caller's own door.</param>
    /// <returns><see langword="true"/> when an output installed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="apply"/> is <see langword="null"/>.</exception>
    public bool Step(ulong tick, ulong engineTick, Func<IReadOnlyList<ArenaSearchWrite>, bool> apply) {
        ArgumentNullException.ThrowIfNull(argument: apply);
        m_engineTick = engineTick;
        m_tick = tick;

        if (m_jobs.Length == 0) {
            return false;
        }

        var installed = false;
        var stamp = Stamp();

        foreach (var job in m_jobs) {
            var work = job.Plan.Work;
            var spent = 0L;

            // A restart and a replay are work the step does before the walk moves, so both come out of the step's
            // allowance; SearchWork.Minimum is what leaves a unit after the costliest pair.
            if (job.Stamp != stamp) {
                Restart(
                    job: job,
                    stamp: stamp
                );
                spent += work.Restart;
            }
            if (job.Running) {
                try {
                    spent += work.Replay(scopes: job.ScopeCount);
                    if (!Replay(job: job)) {
                        Restart(
                            job: job,
                            stamp: stamp
                        );
                        spent += work.Restart;
                    }

                    spent = Walk(
                        job: job,
                        spent: spent
                    );
                } catch {
                    // Rewinding first puts the arena back to what the stamp was taken over, so the restart reads
                    // the position the step started from.
                    Suspend(job: job);
                    Restart(
                        job: job,
                        stamp: stamp
                    );

                    throw;
                }

                Suspend(job: job);
            }

            job.PeakStepWork = Math.Max(
                val1: job.PeakStepWork,
                val2: spent
            );
            job.Work += spent;
            if (
                job.Running ||
                job.Done
            ) {
                continue;
            }

            // Done is set only once the door has answered, so a door that throws leaves the job to land again.
            installed |= Land(
                apply: apply,
                job: job
            );
            job.Done = true;
        }

        return installed;
    }
    /// <summary>Returns the transposition key of the position the arena holds for one job.</summary>
    /// <param name="index">The job's index in the installed set.</param>
    /// <returns>The key.</returns>
    /// <remarks>The key folds the rows the job's plan addresses and the rows its judge reads, so a row outside both
    /// moves the arena's own hash and never this.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> addresses no installed job.</exception>
    public ulong PositionKey(int index) => PositionKey(job: JobAt(index: index));
    /// <summary>Returns one job's progress.</summary>
    /// <param name="index">The job's index in the installed set.</param>
    /// <returns>The status.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> addresses no installed job.</exception>
    public ArenaSearchStatus Status(int index) {
        var job = JobAt(index: index);
        var plan = job.Plan;

        return new ArenaSearchStatus(
            Name: plan.Name,
            Running: job.Running,
            Done: job.Done,
            Token: job.Token,
            Tokens: job.TokenCount,
            Target: job.Target,
            Cells: plan.CellCount,
            Count: job.Count,
            Nodes: job.Nodes,
            NodesPerStep: plan.Nodes,
            JudgeCost: plan.Work.Judge,
            Allowance: plan.Work.Allowance,
            PeakStepWork: job.PeakStepWork,
            Work: job.Work,
            HasScore: (plan.Scored || (plan.ScoresOrdinal >= 0)),
            Depth: plan.Depth,
            PassDepth: job.PassDepth,
            BestScore: job.Best,
            BestToken: job.BestToken,
            BestTarget: job.BestTarget,
            Iteration: job.Iteration,
            Iterations: plan.Iterations,
            JudgeRules: job.Judge.RuleCount,
            HasOutcome: (plan.Method == SearchMethod.Tree)
        );
    }

    private Job JobAt(int index) => ((((uint)index) < ((uint)m_jobs.Length))
        ? m_jobs[index]
        : throw new ArgumentOutOfRangeException(
            actualValue: index,
            message: "The search carries no job at that index.",
            paramName: nameof(index)
        )
    );
    private static int WideWordsPerToken(int cellCount) => ((cellCount + 63) >> 6);
    private static bool WideBit(long[] wide, int token, int cell, int words) {
        var index = ((token * words) + (cell >> 6));

        return (
            (((uint)index) < ((uint)wide.Length)) &&
            ((wide[index] & (1L << (cell & 63))) != 0L)
        );
    }
    private static void SetWideBit(long[] wide, int token, int cell, int words) {
        var index = ((token * words) + (cell >> 6));

        if (((uint)index) < ((uint)wide.Length)) {
            wide[index] |= (1L << (cell & 63));
        }
    }
    private static bool TryCheckRows(ArenaSearchPlan plan, int rows, out string reason) {
        Span<int> required = [
            plan.TokensOrdinal,
            plan.TurnOrdinal,
            plan.VerdictOrdinal,
            ((plan.ScoresOrdinal >= 0)
                ? plan.ScoresOrdinal
                : plan.TurnOrdinal),
            ((plan.Chance is { } chance)
                ? chance.RowOrdinal
                : plan.TurnOrdinal),
        ];

        foreach (var ordinal in required) {
            if (((uint)ordinal) >= ((uint)rows)) {
                reason = $"search '{plan.Name}' addresses row ordinal {ordinal}, which this arena does not carry";

                return false;
            }
        }

        foreach (var ordinal in plan.ZoneOrdinals) {
            if (((uint)ordinal) >= ((uint)rows)) {
                reason = $"search '{plan.Name}' addresses zone row ordinal {ordinal}, which this arena does not carry";

                return false;
            }
        }

        return TryCheckChance(
            plan: plan,
            reason: out reason
        );
    }
    // A chance node's table holds one run of cell values per weight, and the weights sum inside the word the
    // expectation divides by: a table of any other shape would be indexed past its end, and a sum that wrapped would
    // average against the wrong total.
    private static bool TryCheckChance(ArenaSearchPlan plan, out string reason) {
        reason = string.Empty;

        if (plan.Chance is not { } node) {
            return true;
        }
        if (
            (node.Weights is not { Length: > 0 } weights) ||
            (node.Outcomes is not { } outcomes) ||
            (node.CellCount < 1) ||
            (((long)outcomes.Length) != (((long)weights.Length) * node.CellCount))
        ) {
            reason = $"search '{plan.Name}' declares a chance node whose outcome table does not hold {node.CellCount} cell value(s) for each of its {(node.Weights?.Length ?? 0)} weight(s)";

            return false;
        }

        if (
            (node.AtDepth < 0) ||
            (node.AtDepth >= plan.Depth)
        ) {
            reason = $"search '{plan.Name}' declares a chance node at ply {node.AtDepth}, outside the {plan.Depth} it searches";

            return false;
        }
        if (plan.ScoresOrdinal >= 0) {
            reason = $"search '{plan.Name}' declares a chance node, which averages one value, over per-seat scores, which fold a vector";

            return false;
        }

        var total = 0UL;

        foreach (var weight in weights) {
            if (weight > (ulong.MaxValue - total)) {
                reason = $"search '{plan.Name}' declares a chance node whose weights sum past what one word holds";

                return false;
            }

            total += weight;
        }

        return true;
    }
    // A job whose allowance cannot cover a restart, a full replay, and one unit would stop making progress the
    // first step its scopes ran that deep, so it is refused with the sum it needs rather than left to starve.
    private static bool TryCheckWork(ArenaSearchPlan plan, out string reason) {
        var work = plan.Work;

        if (plan.Nodes < 1) {
            reason = $"search '{plan.Name}' judges {plan.Nodes} candidates a step, and a job that judges none never lands";

            return false;
        }
        if (
            (work.Minimum == long.MaxValue) ||
            (work.Allowance < work.Minimum)
        ) {
            reason = $"search '{plan.Name}' may spend {work.Allowance} work units a step and needs {((work.Minimum == long.MaxValue)
                ? "more than any allowance holds"
                : work.Minimum.ToString(provider: System.Globalization.CultureInfo.InvariantCulture))} to make progress: a restart at {work.Restart}, a replay of {work.Scopes} scopes at {work.Candidate} each, and one unit at {work.Unit}";

            return false;
        }

        reason = string.Empty;

        return true;
    }
    private CellKey[] ResolveReachKeys(ArenaSearchPlan plan) {
        if (
            (plan.ReachOrdinal < 0) ||
            (plan.Topology is not { } topology)
        ) {
            return [];
        }

        var keys = new CellKey[topology.CellCount];

        // A reach board is an output: its cells are painted, never authored, so the catalog has no key for one
        // until the job lands it. Interning here, in the topology's own cell order, is what gives a landed write an
        // address at all.
        for (var cell = 0; (cell < keys.Length); cell++) {
            keys[cell] = m_arena.Catalog.Keys.Intern(name: topology.NameOf(cell: cell));
        }

        return keys;
    }
    // One key per cell position of a row, for a table a walk addresses by position rather than by name.
    private CellKey[] ResolveRowKeys(int rowOrdinal, int count) {
        if (
            (rowOrdinal < 0) ||
            (count <= 0)
        ) {
            return [];
        }

        var keys = new CellKey[count];

        for (var position = 0; (position < count); position++) {
            if (!m_arena.TryKeyAt(
                key: out keys[position],
                position: position,
                rowOrdinal: rowOrdinal
            )) {
                keys[position] = default;
            }
        }

        return keys;
    }
    // The rows a position key folds: what the plan itself addresses, plus what the judge's own rules and score
    // read. A row outside this set cannot change a candidate's verdict, so two positions differing only there are
    // the same position to the job.
    private static int[] ResolveKeyRows(ArenaSearchPlan plan, IArenaSearchJudge judge) {
        var rows = new SortedSet<int> {
            plan.TokensOrdinal,
            plan.TurnOrdinal,
            plan.VerdictOrdinal,
        };

        foreach (var ordinal in plan.ZoneOrdinals) {
            _ = rows.Add(item: ordinal);
        }
        foreach (var ordinal in plan.CodeOrdinals) {
            if (ordinal >= 0) {
                _ = rows.Add(item: ordinal);
            }
        }
        foreach (var ordinal in judge.KeyRows) {
            if (ordinal >= 0) {
                _ = rows.Add(item: ordinal);
            }
        }

        if (plan.ScoresOrdinal >= 0) {
            _ = rows.Add(item: plan.ScoresOrdinal);
        }
        if (plan.Chance is { } chance) {
            _ = rows.Add(item: chance.RowOrdinal);
        }

        return [.. rows];
    }
    // Every input row folded in ordinal order, skipping the rows the installed jobs write: a job's own landing must
    // not look like an input change and restart it.
    //
    // A row whose version has not moved holds the bytes it held, so the fold is taken again only when some input
    // row's version has: an idle tick compares one counter per row rather than folding every cell of the arena.
    private ulong Stamp() {
        var rows = m_arena.Layout.RowCount;

        if (m_stampVersions.Length != rows) {
            m_stampFolded = false;
            m_stampVersions = new ulong[rows];
        }

        var moved = !m_stampFolded;

        for (var ordinal = 0; (ordinal < rows); ordinal++) {
            var version = m_arena.RowVersion(rowOrdinal: ordinal);

            if (m_stampVersions[ordinal] != version) {
                m_stampVersions[ordinal] = version;
                moved |= !IsOutput(rowOrdinal: ordinal);
            }
        }

        if (!moved) {
            return m_stampValue;
        }

        var hash = Fnv1aHash.Create();

        for (var ordinal = 0; (ordinal < rows); ordinal++) {
            if (IsOutput(rowOrdinal: ordinal)) {
                continue;
            }

            m_arena.AddRowTo(
                hash: ref hash,
                rowOrdinal: ordinal
            );
        }

        m_stampFolded = true;
        m_stampValue = hash.Value;

        return m_stampValue;
    }
    // The transposition key of the position the arena holds for one job: its own rows alone, folded in ordinal
    // order.
    private ulong PositionKey(Job job) {
        var hash = Fnv1aHash.Create();

        var timed = false;

        foreach (var ordinal in job.KeyRows) {
            m_arena.AddRowTo(
                hash: ref hash,
                rowOrdinal: ordinal
            );
            timed |= m_arena.Layout[ordinal].HasTraits;
        }

        // A row carrying a value-over-time trait answers a live read from its stored epoch and the tick it is read
        // at, so a position over such a row is the same position only at the same tick pair.
        if (timed) {
            hash.Add(value: m_tick);
            hash.Add(value: m_engineTick);
        }

        return hash.Value;
    }
    private bool IsOutput(int rowOrdinal) {
        foreach (var job in m_jobs) {
            var plan = job.Plan;

            if (
                (plan.BestOrdinal == rowOrdinal) ||
                (plan.CountsOrdinal == rowOrdinal) ||
                (plan.LegalOrdinal == rowOrdinal) ||
                (plan.ReachOrdinal == rowOrdinal)
            ) {
                return true;
            }
        }

        return false;
    }
    private void Restart(Job job, ulong stamp) {
        PopAllScopes(job: job);
        job.BaseTurn = Slot(rowOrdinal: job.Plan.TurnOrdinal);
        job.Done = false;
        job.Iteration = 0;
        job.Nodes = 0L;
        // A chance node at the root has no move to choose before the draw, so its one pass searches the plan's
        // whole depth rather than deepening toward it.
        job.PassDepth = ((job.Plan.Chance is { AtDepth: 0 })
            ? job.Plan.Depth
            : 1
        );
        job.Running = true;
        job.Stamp = stamp;
        job.TreeActive = false;
        job.PeakStepWork = 0L;
        job.TreeCount = 0;
        job.Work = 0L;
        job.TtKey?.AsSpan().Clear();
        job.TtMeta?.AsSpan().Clear();
        job.TtValue?.AsSpan().Clear();
        job.TokenCount = m_arena.CellCount(rowOrdinal: job.Plan.TokensOrdinal);

        for (var index = 0; (index < job.TokenKeys.Length); index++) {
            if (!m_arena.TryKeyAt(
                key: out job.TokenKeys[index],
                position: index,
                rowOrdinal: job.Plan.TokensOrdinal
            )) {
                job.TokenKeys[index] = default;
            }
        }

        job.ChanceKeys = ResolveRowKeys(
            count: (job.Plan.Chance?.CellCount ?? 0),
            rowOrdinal: (job.Plan.Chance?.RowOrdinal ?? -1)
        );
        job.ScoreKeys = ResolveRowKeys(
            count: job.Seats.Length,
            rowOrdinal: job.Plan.ScoresOrdinal
        );

        ResetPass(job: job);
    }
    // A fresh iterative-deepening pass over the same root position: the root outputs are recomputed identically
    // every pass, and the running negamax result starts over at the terminal sentinel. The caller owns PassDepth.
    private static void ResetPass(Job job) {
        job.Active = 0;
        job.Alpha = -SearchCapacity.MateScore;
        job.Best = -SearchCapacity.MateScore;
        job.BestTarget = -1;
        job.BestToken = -1;
        job.Beta = SearchCapacity.MateScore;
        job.Root.ChanceSum = Int128.Zero;
        job.Root.ChanceWeight = 0UL;
        job.Count = 0L;
        job.Counts.AsSpan().Clear();
        job.Legal.AsSpan().Clear();
        job.Shape = 0;
        job.Target = 0;
        job.Token = 0;
        job.Wide?.AsSpan().Clear();
    }
    private static long Raw(in CellValue value) => (value.Kind switch {
        CellKind.Bool => (value.AsBool
        ? 1L
        : 0L),
        CellKind.Fixed => value.AsFixed,
        CellKind.Int => value.AsInt,
        _ => 0L,
    });
    private long Slot(int rowOrdinal) => ((m_arena.TryRead(
        key: m_slotKey,
        rowOrdinal: rowOrdinal,
        value: out var value
    ))
        ? Raw(value: value)
        : 0L
    );
    private bool TryNumberAt(int rowOrdinal, int position, out long value) {
        if (m_arena.TryReadAt(
            position: position,
            rowOrdinal: rowOrdinal,
            value: out var carried
        )) {
            value = Raw(value: carried);

            return true;
        }

        value = 0L;

        return false;
    }
    private ArenaSearchView View(int ply) => new(
        Arena: m_arena,
        EngineTick: m_engineTick,
        Ply: ply,
        Tick: m_tick
    );
    private bool Land(Job job, Func<IReadOnlyList<ArenaSearchWrite>, bool> apply) {
        var plan = job.Plan;

        m_outputs.Clear();

        if (plan.LegalOrdinal >= 0) {
            for (var index = 0; ((index < job.TokenCount) && (index < job.Legal.Length)); index++) {
                m_outputs.Add(item: new ArenaSearchWrite.Cell(
                    Key: job.TokenKeys[index],
                    RowOrdinal: plan.LegalOrdinal,
                    Value: job.Legal[index]
                ));
            }
        }
        if (plan.CountsOrdinal >= 0) {
            for (var index = 0; ((index < job.TokenCount) && (index < job.Counts.Length)); index++) {
                m_outputs.Add(item: new ArenaSearchWrite.Cell(
                    Key: job.TokenKeys[index],
                    RowOrdinal: plan.CountsOrdinal,
                    Value: job.Counts[index]
                ));
            }
        }
        if (plan.BestOrdinal >= 0) {
            m_outputs.Add(item: new ArenaSearchWrite.Cell(
                Key: m_bestTokenKey,
                RowOrdinal: plan.BestOrdinal,
                Value: job.BestToken
            ));
            m_outputs.Add(item: new ArenaSearchWrite.Cell(
                Key: m_bestTargetKey,
                RowOrdinal: plan.BestOrdinal,
                Value: job.BestTarget
            ));
            m_outputs.Add(item: new ArenaSearchWrite.Cell(
                Key: m_bestScoreKey,
                RowOrdinal: plan.BestOrdinal,
                Value: job.Best
            ));
        }
        if (
            (plan.ReachOrdinal >= 0) &&
            (job.Wide is { } wide) &&
            (plan.Topology is not null)
        ) {
            // Every reach cell resets through the same clear-then-paint door a caller's own board combination
            // authors, so a wide board needs no dedicated write kind.
            m_outputs.Add(item: new ArenaSearchWrite.ClearBoard(RowOrdinal: plan.ReachOrdinal));

            var held = ((plan.HeldOrdinal >= 0)
                ? Slot(rowOrdinal: plan.HeldOrdinal)
                : -1L
            );

            if (
                (held >= 0L) &&
                (held < job.Legal.Length)
            ) {
                var token = ((int)held);
                var words = WideWordsPerToken(cellCount: plan.CellCount);

                for (var cell = 0; ((cell < plan.CellCount) && (cell < job.ReachKeys.Length)); cell++) {
                    if (WideBit(
                        cell: cell,
                        token: token,
                        wide: wide,
                        words: words
                    )) {
                        m_outputs.Add(item: new ArenaSearchWrite.Cell(
                            Key: job.ReachKeys[cell],
                            RowOrdinal: plan.ReachOrdinal,
                            Value: 1L
                        ));
                    }
                }
            }
        }
        if (m_outputs.Count == 0) {
            return false;
        }
        if (apply(m_outputs)) {
            return true;
        }

        m_narrate?.Invoke(
            "state.search",
            $"[state.search: job '{plan.Name}' finished but its outputs were refused by the mutation door]"
        );

        return false;
    }
}
