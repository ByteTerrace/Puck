using Puck.Maths;

using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: a job spends no more than its allowance in any step, whatever it is doing — walking
/// moves, averaging a chance ply at the root or inside the walk, running tree playouts, replaying, or restarting —
/// and still lands the answer an unbounded job lands. An allowance one unit short of guaranteed progress is refused
/// with the sum it needs. A candidate that resolves to nothing is charged, and a judge counted from outside the
/// search runs no more often than the allowance bought.</summary>
public sealed class ArenaSearchAllowanceLawTests {
    // A judge that counts its own runs, so the bound is checked against a count the search does not keep.
    private sealed class CountingJudge(IArenaSearchJudge inner) : IArenaSearchJudge {
        public StateArena Arena => inner.Arena;
        public long Judged { get; private set; }
        public IReadOnlyList<int> KeyRows => inner.KeyRows;
        public int RuleCount => inner.RuleCount;
        public long Scored { get; private set; }
        public bool Scores => inner.Scores;

        public bool Judge(in ArenaSearchView view) {
            Judged++;

            return inner.Judge(view: in view);
        }
        public long Score(in ArenaSearchView view) {
            Scored++;

            return inner.Score(view: in view);
        }
        public bool TryAdmit(ArenaSearchPlan plan, out string refusal) => inner.TryAdmit(
            plan: plan,
            refusal: out refusal
        );
    }

    private const long JudgeWork = 7L;
    private const long ScoreWork = 3L;

    private static StateRow[] PieceAndLuck(long piece = 0L) => [
        Keyed(
            name: "piece",
            ("t0", piece)
        ),
        Keyed(
            name: "luck",
            ("only", 0L)
        ),
        Slot(
            name: "turn",
            value: 0L
        ),
        Slot(
            name: "verdict",
            value: 0L
        ),
        Keyed(
            name: "best",
            ("token", -1L),
            ("to", -1L),
            ("score", 0L)
        ),
    ];
    private static SearchShapePlan[] Relocate() => [new SearchShapePlan(
            Kind: SearchShapeKind.Relocate,
            Displace: false,
            Directions: [],
            PairWithIndex: -1
        )];
    private static SearchWork Priced(int depth, SearchMethod method, int cells, bool chance) => SearchWork.Price(
        allowance: long.MaxValue,
        cellCount: cells,
        chanceCells: (chance
            ? 1
            : 0),
        depth: depth,
        judge: JudgeWork,
        keyed: (method == SearchMethod.Negamax),
        method: method,
        position: 8L,
        score: ScoreWork,
        seats: 0,
        shapes: Relocate(),
        tokens: 1
    );
    private static SearchPlan Plan(SearchWork work, int depth, int? atDepth, SearchMethod method = SearchMethod.Negamax, int cells = 4, int nodes = 256) =>
        new(
            Name: "bounded",
            Tokens: "piece",
            Topology: null,
            Zones: [],
            CellCount: cells,
            Turn: "turn",
            Verdict: "verdict",
            Off: -1L,
            Nodes: nodes,
            Work: work,
            Depth: depth,
            Best: "best",
            Shapes: Relocate(),
            Chance: ((atDepth is { } ply)
                ? new SearchChancePlan(
                    AtDepth: ply,
                    CellCount: 1,
                    Outcomes: [-4L, 4L, 9L],
                    Row: "luck",
                    Weights: [1UL, 2UL, 1UL]
                )
                : null),
            Iterations: ((method == SearchMethod.Tree)
                ? 48
                : 0),
            Method: method
        ) {
            Scored = true,
        };
    // Runs one job to its landing, holding every step to the allowance and the judge to what the allowance bought.
    private static (List<(string Row, string Key, long Value)> Landed, int Steps, ArenaSearchStatus Status) Run(SearchPlan plan, long piece = 0L) {
        var position = new Position(rows: PieceAndLuck(piece: piece));
        var judge = new CountingJudge(inner: RuleJudge(
            position: position,
            rules: [AcceptEveryCandidate()],
            score: "(piece[t0] * 10) + luck[only]"
        ));
        var search = Build(
            drawSeed: 11UL,
            judge: judge,
            plan: plan,
            position: position
        );
        IReadOnlyList<ArenaSearchWrite>? landed = null;
        var steps = 0;

        while (
            (landed is null) &&
            (steps < 100_000)
        ) {
            var judgedBefore = judge.Judged;

            _ = search.Step(
                apply: writes => {
                    landed = writes;

                    return true;
                },
                engineTick: 0UL,
                tick: 1UL
            );
            steps++;

            var status = search.Status(index: 0);

            Assert.True(
                condition: (status.PeakStepWork <= plan.Work.Allowance),
                userMessage: $"step {steps} spent {status.PeakStepWork} of {plan.Work.Allowance}"
            );
            // Every judge run the step made, the replay's included, was bought at the judge's price.
            Assert.True(
                condition: (plan.Work.Allowance == long.MaxValue) || (((judge.Judged - judgedBefore) * JudgeWork) <= plan.Work.Allowance),
                userMessage: $"step {steps} judged {(judge.Judged - judgedBefore)} times under {plan.Work.Allowance}"
            );
        }

        Assert.NotNull(@object: landed);

        return (Normalize(
            catalog: position.Catalog,
            writes: landed
        ), steps, search.Status(index: 0));
    }

    [Theory]
    [InlineData(null, 3, SearchMethod.Negamax)]
    [InlineData(0, 3, SearchMethod.Negamax)]
    [InlineData(1, 3, SearchMethod.Negamax)]
    [InlineData(2, 4, SearchMethod.Negamax)]
    [InlineData(null, 3, SearchMethod.Tree)]
    [InlineData(2, 3, SearchMethod.Tree)]
    public void TheLeastAdmissibleAllowanceLandsWhatAnUnboundedJobLands(int? atDepth, int depth, SearchMethod method) {
        var priced = Priced(
            cells: 4,
            chance: (atDepth is not null),
            depth: depth,
            method: method
        );
        var unbounded = Run(plan: Plan(
            atDepth: atDepth,
            depth: depth,
            method: method,
            work: priced
        ));
        var bounded = Run(plan: Plan(
            atDepth: atDepth,
            depth: depth,
            method: method,
            work: (priced with { Allowance = priced.Minimum })
        ));

        Assert.Equal(
            unbounded.Landed,
            bounded.Landed
        );
        // The bound bit: the bounded job had to yield where the unbounded one ran on.
        Assert.True(
            condition: (bounded.Steps > unbounded.Steps),
            userMessage: $"bounded {bounded.Steps} steps, unbounded {unbounded.Steps}"
        );
        Assert.Equal(
            unbounded.Status.Nodes,
            bounded.Status.Nodes
        );
    }
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void AnAllowanceShortOfGuaranteedProgressIsRefusedWithTheSumItNeeds(long shortBy) {
        var priced = Priced(
            cells: 4,
            chance: true,
            depth: 3,
            method: SearchMethod.Negamax
        );
        var position = new Position(rows: PieceAndLuck());

        Assert.True(condition: ArenaSearchPlan.TryResolve(
            catalog: position.Catalog,
            drawSeed: 0UL,
            plan: Plan(
                atDepth: 1,
                depth: 3,
                work: (priced with {
                    Allowance = ((shortBy == 0L)
                        ? 0L
                        : (priced.Minimum + shortBy)),
                })
            ),
            reason: out _,
            resolved: out var resolved
        ));
        Assert.False(condition: new ArenaSearch(arena: position.Arena).Rebuild(
            judges: [RuleJudge(
                position: position,
                rules: [AcceptEveryCandidate()],
                score: "piece[t0]"
            )],
            plans: [resolved],
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"needs {priced.Minimum} to make progress"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "'bounded'"
        );
    }
    // The one cell a lone token already stands on is the only target, and relocating onto it is no move: every
    // candidate resolves to nothing, no judge ever runs, and the inspection is still paid for.
    [Fact]
    public void ACandidateThatResolvesToNothingIsChargedThoughNoJudgeRuns() {
        var priced = Priced(
            cells: 1,
            chance: false,
            depth: 1,
            method: SearchMethod.Negamax
        );
        var (_, _, status) = Run(plan: Plan(
            atDepth: null,
            cells: 1,
            depth: 1,
            work: (priced with { Allowance = priced.Minimum })
        ));

        Assert.Equal(
            0L,
            status.Nodes
        );
        Assert.True(
            condition: (status.Work >= priced.Inspect),
            userMessage: $"spent {status.Work}, one inspection is {priced.Inspect}"
        );
    }
    [Fact]
    public void TheJudgedCandidateQuotaStillBoundsAStepTheAllowanceWouldNot() {
        var priced = Priced(
            cells: 4,
            chance: false,
            depth: 3,
            method: SearchMethod.Negamax
        );
        var one = Run(plan: Plan(
            atDepth: null,
            depth: 3,
            nodes: 1,
            work: priced
        ));
        var many = Run(plan: Plan(
            atDepth: null,
            depth: 3,
            work: priced
        ));

        Assert.Equal(
            many.Landed,
            one.Landed
        );
        Assert.True(condition: (one.Steps >= one.Status.Nodes));
        Assert.True(condition: (many.Steps < one.Steps));
    }
    // An input that moves every step restarts the job every step. Each restart is charged to the step that made
    // it, the step still fits its allowance, and the job lands once the input holds still.
    [Fact]
    public void ARestartEveryStepIsChargedAndTheJobLandsOnceItsInputsHold() {
        var priced = Priced(
            cells: 4,
            chance: true,
            depth: 3,
            method: SearchMethod.Negamax
        );
        var plan = Plan(
            atDepth: 1,
            depth: 3,
            work: (priced with { Allowance = priced.Minimum })
        );
        var position = new Position(rows: PieceAndLuck());
        var search = Build(
            judge: RuleJudge(
                position: position,
                rules: [AcceptEveryCandidate()],
                score: "(piece[t0] * 10) + luck[only]"
            ),
            plan: plan,
            position: position
        );
        var luck = position.Ordinal(name: "luck");
        var only = position.Catalog.Keys.Intern(name: Name(value: "only"));
        var landed = false;

        for (var step = 0; (step < 32); step++) {
            Assert.True(condition: position.Arena.TryWrite(
                key: only,
                operand: step,
                reason: out _,
                rowOrdinal: luck,
                write: StateWriteKind.Set
            ));
            _ = search.Step(
                apply: _ => {
                    landed = true;

                    return true;
                },
                engineTick: 0UL,
                tick: 1UL
            );

            var status = search.Status(index: 0);

            Assert.False(condition: landed);
            Assert.True(condition: (status.PeakStepWork >= priced.Restart));
            Assert.True(condition: (status.PeakStepWork <= plan.Work.Allowance));
            // A restart forgets what the job spent before it, so the total never outgrows one step's spending.
            Assert.Equal(
                status.PeakStepWork,
                status.Work
            );
        }
        for (var step = 0; ((step < 100_000) && !landed); step++) {
            _ = search.Step(
                apply: _ => {
                    landed = true;

                    return true;
                },
                engineTick: 0UL,
                tick: 1UL
            );
        }

        Assert.True(condition: landed);
    }
    // A job suspended inside its chance ply carries the outcomes it has folded across a capture: a twin restored
    // from it hashes the same at every later step and lands the same answer.
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    public void AJobSuspendedInsideAChancePlyResumesFromACheckpoint(int atDepth, int depth) {
        var priced = Priced(
            cells: 4,
            chance: true,
            depth: depth,
            method: SearchMethod.Negamax
        );
        // The node ceiling is what suspends the walk here: the minimum allowance covers a restart, which clears the
        // whole transposition table, so every other step has room to fold a chance ply in one go.
        var plan = Plan(
            atDepth: atDepth,
            depth: depth,
            nodes: 2,
            work: (priced with { Allowance = priced.Minimum })
        );

        static (Position Position, ArenaSearch Search) Make(SearchPlan plan) {
            var position = new Position(rows: PieceAndLuck());

            return (position, Build(
                judge: RuleJudge(
                    position: position,
                    rules: [AcceptEveryCandidate()],
                    score: "(piece[t0] * 10) + luck[only]"
                ),
                plan: plan,
                position: position
            ));
        }
        static ulong Hash(ArenaSearch search) {
            var hash = Fnv1aHash.Create();

            search.AppendStateHash(hash: ref hash);

            return hash.Value;
        }

        var source = Make(plan: plan);
        var sawChanceSum = false;

        // Step until a capture holds a partly folded chance ply, which is the state a restore has to carry.
        for (var step = 0; ((step < 10_000) && !sawChanceSum); step++) {
            _ = source.Search.Step(
                apply: static _ => true,
                engineTick: 0UL,
                tick: 1UL
            );

            var job = source.Search.Capture().Jobs[0];

            sawChanceSum = (job.Running && ((job.Root.ChanceWeight > 0UL) || job.Levels.Any(predicate: static level => (level.ChanceWeight > 0UL))));
        }

        Assert.True(condition: sawChanceSum);

        var twin = Make(plan: plan);

        Assert.True(
            condition: twin.Search.TryRestore(
                checkpoint: source.Search.Capture(),
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            Hash(search: source.Search),
            Hash(search: twin.Search)
        );

        IReadOnlyList<ArenaSearchWrite>? fromSource = null;
        IReadOnlyList<ArenaSearchWrite>? fromTwin = null;

        for (var step = 0; ((step < 100_000) && ((fromSource is null) || (fromTwin is null))); step++) {
            _ = source.Search.Step(
                apply: writes => {
                    fromSource = writes;

                    return true;
                },
                engineTick: 0UL,
                tick: 1UL
            );
            _ = twin.Search.Step(
                apply: writes => {
                    fromTwin = writes;

                    return true;
                },
                engineTick: 0UL,
                tick: 1UL
            );
            Assert.Equal(
                Hash(search: source.Search),
                Hash(search: twin.Search)
            );
        }

        Assert.NotNull(@object: fromSource);
        Assert.NotNull(@object: fromTwin);
        Assert.Equal(
            Normalize(
                catalog: source.Position.Catalog,
                writes: fromSource
            ),
            Normalize(
                catalog: twin.Position.Catalog,
                writes: fromTwin
            )
        );
    }
}
