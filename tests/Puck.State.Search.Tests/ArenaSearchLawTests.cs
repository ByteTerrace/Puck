using Puck.Maths;

using Xunit;

using static Puck.State.Search.Tests.SearchFixture;

namespace Puck.State.Search.Tests;

/// <summary>CONTRACT UNDER TEST: a candidate is a journal scope on the arena rather than a copied position, so a
/// rewound candidate leaves the arena byte-identical and a committed one does not; a job suspended mid-descent by
/// its node quota replays its scopes and lands the answer its unsuspended twin lands; a checkpoint round-trips a
/// suspended job; a throwing score leaves the arena untouched; and a step's candidates allocate nothing once the
/// job's arrays have been built.</summary>
public sealed class ArenaSearchLawTests {
    // The same writes the judge rules make, as a delegate over the scoped arena.
    private static DelegateJudge AcceptEveryCandidate(Position position, ScoreStep? score = null) {
        var slot = position.SlotKey;
        var turn = position.Ordinal(name: "turn");
        var verdict = position.Ordinal(name: "verdict");

        return new DelegateJudge(
            arena: position.Arena,
            judge: (in ArenaSearchView view) => {
                var arena = view.Arena;

                _ = arena.TryWrite(
                    key: slot,
                    operand: 1L,
                    reason: out _,
                    rowOrdinal: verdict,
                    write: StateWriteKind.Set
                );
                _ = arena.TryWrite(
                    key: slot,
                    operand: 1L,
                    reason: out _,
                    rowOrdinal: turn,
                    write: StateWriteKind.Add
                );

                return true;
            },
            score: score
        );
    }
    private static ArenaSearch Build(Position position, SearchPlan plan, ScoreStep? score) => SearchFixture.Build(
        judge: AcceptEveryCandidate(
            position: position,
            score: score
        ),
        plan: plan,
        position: position
    );
    private static List<(string Row, string Key, long Value)> RunArena(Position position, SearchPlan plan, ScoreStep? score) => SearchFixture.RunArena(
        catalog: position.Catalog,
        search: Build(
            plan: plan,
            position: position,
            score: score
        )
    );
    private static SearchPlan Relocate(int cells, int depth, bool scored = false, int nodes = 256, SearchMethod method = SearchMethod.Negamax, int iterations = 0) =>
        new(
            Name: "moves",
            Tokens: "piece",
            Topology: null,
            Zones: [],
            CellCount: cells,
            Turn: "turn",
            Verdict: "verdict",
            Off: -1L,
            Nodes: nodes,
            JudgeCost: 1L,
            Depth: depth,
            Best: "best",
            Shapes: [new SearchShapePlan(
                    Kind: SearchShapeKind.Relocate,
                    Displace: false,
                    Directions: [],
                    PairWithIndex: -1
                )],
            Counts: "counts",
            Iterations: iterations,
            Legal: "legal",
            Method: method
        ) {
            Scored = scored,
        };

    [Fact]
    public void AJobSuspendedByItsNodeQuotaLandsTheSameAnswer() {
        var rows = Board(
            cells: 4,
            tokens: 2
        );
        var wide = new Position(rows: rows);
        var narrow = new Position(rows: rows);
        var text = "(piece[t0] * 4) - piece[t1]";

        Assert.Equal(
            expected: RunArena(
                plan: Relocate(
                    cells: 4,
                    depth: 2,
                    scored: true
                ),
                position: wide,
                score: Score(
                    position: wide,
                    text: text
                )
            ),
            actual: RunArena(
                plan: Relocate(
                    cells: 4,
                    depth: 2,
                    nodes: 3,
                    scored: true
                ),
                position: narrow,
                score: Score(
                    position: narrow,
                    text: text
                )
            )
        );
    }
    [Fact]
    public void ARewoundCandidateLeavesTheArenaByteIdenticalAndACommittedOneDoesNot() {
        var position = new Position(rows: Board(
            cells: 4,
            tokens: 2
        ));
        var arena = position.Arena;
        var piece = position.Ordinal(name: "piece");
        var token = position.Key(name: "t0");
        var before = arena.ComputeHash();
        var rewound = ArenaSearchCandidate.Begin(arena: arena);

        Assert.True(condition: arena.TryWrite(
            key: token,
            operand: 3L,
            reason: out var reason,
            rowOrdinal: piece,
            write: StateWriteKind.Set
        ), userMessage: reason);

        rewound.Rewind();

        Assert.Equal(
            expected: before,
            actual: arena.ComputeHash()
        );

        var committed = ArenaSearchCandidate.Begin(arena: arena);

        Assert.True(condition: arena.TryWrite(
            key: token,
            operand: 3L,
            reason: out reason,
            rowOrdinal: piece,
            write: StateWriteKind.Set
        ), userMessage: reason);

        committed.Commit();

        Assert.NotEqual(
            expected: before,
            actual: arena.ComputeHash()
        );
    }
    [Fact]
    public void AStepLeavesTheArenaByteIdenticalWhileTheJobIsStillDescending() {
        var position = new Position(rows: Board(
            cells: 4,
            tokens: 2
        ));
        var text = "(piece[t0] * 4) - piece[t1]";
        var search = Build(
            plan: Relocate(
                cells: 4,
                depth: 2,
                nodes: 2,
                scored: true
            ),
            position: position,
            score: Score(
                position: position,
                text: text
            )
        );
        var before = position.Arena.ComputeHash();
        var descended = false;

        for (var step = 0; (step < 8); step++) {
            _ = search.Step(
                apply: static _ => true,
                engineTick: 0UL,
                tick: 1UL
            );

            Assert.Equal(
                expected: before,
                actual: position.Arena.ComputeHash()
            );

            descended |= search.Status(index: 0).Running;
        }

        Assert.True(condition: descended);
    }
    [Fact]
    public void ACheckpointRoundTripsAJobSuspendedMidDescent() {
        var rows = Board(
            cells: 4,
            tokens: 2
        );
        var text = "(piece[t0] * 4) - piece[t1]";
        var carried = new Position(rows: rows);
        var whole = new Position(rows: rows);
        var partial = Build(
            plan: Relocate(
                cells: 4,
                depth: 2,
                nodes: 2,
                scored: true
            ),
            position: carried,
            score: Score(
                position: carried,
                text: text
            )
        );

        _ = partial.Step(
            apply: static _ => true,
            engineTick: 0UL,
            tick: 1UL
        );

        var restored = Build(
            plan: Relocate(
                cells: 4,
                depth: 2,
                nodes: 2,
                scored: true
            ),
            position: carried,
            score: Score(
                position: carried,
                text: text
            )
        );

        Assert.True(
            condition: restored.TryRestore(
                checkpoint: partial.Capture(),
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            expected: RunArena(
                plan: Relocate(
                    cells: 4,
                    depth: 2,
                    scored: true
                ),
                position: whole,
                score: Score(
                    position: whole,
                    text: text
                )
            ),
            actual: SearchFixture.RunArena(
                catalog: carried.Catalog,
                search: restored
            )
        );
    }
    // A score that throws mid-descent throws with candidate scopes open. The step rewinds them before the exception
    // leaves, and the interrupted job restarts, so the arena is untouched and the next steps land the whole answer.
    [Fact]
    public void AThrowingScoreLeavesTheArenaUntouchedAndTheJobRestarts() {
        var rows = Board(
            cells: 4,
            tokens: 2
        );
        var text = "(piece[t0] * 4) - piece[t1]";
        var position = new Position(rows: rows);
        var whole = new Position(rows: rows);
        var inner = Score(
            position: position,
            text: text
        );
        var calls = 0;
        var search = Build(
            plan: Relocate(
                cells: 4,
                depth: 2,
                scored: true
            ),
            position: position,
            score: (in ArenaSearchView view) => ((++calls == 3)
                ? throw new InvalidOperationException(message: "score failure")
                : inner(view: in view)
            )
        );
        var before = position.Arena.ComputeHash();

        _ = Assert.Throws<InvalidOperationException>(testCode: () => search.Step(
            apply: static _ => true,
            engineTick: 0UL,
            tick: 1UL
        ));
        Assert.Equal(
            actual: position.Arena.ComputeHash(),
            expected: before
        );

        var status = search.Status(index: 0);

        Assert.True(condition: status.Running);
        Assert.Equal(
            actual: status.Nodes,
            expected: 0L
        );
        Assert.Equal(
            actual: SearchFixture.RunArena(
                catalog: position.Catalog,
                search: search
            ),
            expected: RunArena(
                plan: Relocate(
                    cells: 4,
                    depth: 2,
                    scored: true
                ),
                position: whole,
                score: Score(
                    position: whole,
                    text: text
                )
            )
        );
    }
    // A checkpoint whose arrays do not fit the installed job is refused with a reason before any job is touched,
    // whichever array is the wrong length.
    [Fact]
    public void ACheckpointOfAnotherShapeIsRefusedWhole() {
        var rows = Board(
            cells: 4,
            tokens: 2
        );
        var text = "(piece[t0] * 4) - piece[t1]";
        var position = new Position(rows: rows);

        ArenaSearch Make() => Build(
            plan: Relocate(
                cells: 4,
                depth: 2,
                nodes: 2,
                scored: true
            ),
            position: position,
            score: Score(
                position: position,
                text: text
            )
        );

        var source = Make();
        var target = Make();

        _ = source.Step(
            apply: static _ => true,
            engineTick: 0UL,
            tick: 1UL
        );

        var captured = source.Capture();
        var job = captured.Jobs[0];
        var before = target.Status(index: 0);

        foreach (var bent in new[] {
            (job with { Counts = new long[(job.Counts.Length + 1)] }),
            (job with { TtMeta = new long[(job.TtMeta.Length + 1)] }),
            (job with { TtValue = [] }),
            (job with { Wide = new long[1] }),
            (job with { Active = (job.Levels.Length + 1) }),
            (job with { PassDepth = 0 }),
            (job with { PassDepth = (job.Levels.Length + 2) }),
            (job with { Shape = -1 }),
            (job with { Token = -1 }),
            (job with { Target = -1 }),
            (job with { Levels = [(job.Levels[0] with { Shape = -1 })] }),
            (job with { Levels = [(job.Levels[0] with { Seats = new long[1] })] }),
            (job with { Levels = [(job.Levels[0] with { Seats = null })] }),
            (job with { Levels = [null!] }),
            (job with { Scopes = [new ArenaSearchScopeCheckpoint(
                Shape: -1,
                Target: 0,
                Token: 0
            )] }),
            (job with { Scopes = [new ArenaSearchScopeCheckpoint(
                Shape: 0,
                Target: 4,
                Token: 0
            )] }),
            (job with { Scopes = [new ArenaSearchScopeCheckpoint(
                Shape: 0,
                Target: 0,
                Token: 2
            )] }),
            (job with { Counts = null! }),
            (job with { Levels = null! }),
            (job with { Scopes = null! }),
            (job with { TtKey = null! }),
            (job with { Wide = null! }),
            (job with { Tree = new ArenaSearchTreeCheckpoint(
                Active: false,
                ChildCount: [],
                Count: 0,
                ExpandShape: 0,
                ExpandTarget: 0,
                ExpandToken: 0,
                Expanded: [],
                FirstChild: [],
                Iteration: 0,
                Parent: [],
                Path: [],
                PathLength: 0,
                Phase: 0,
                PlayCount: 0,
                PlayoutPlies: 0,
                Scan: 0,
                Seed: 0UL,
                Shape: [],
                Start: 0,
                Target: [],
                Token: [],
                Total: [],
                Visits: []
            ) }),
        }) {
            Assert.False(condition: target.TryRestore(
                checkpoint: new ArenaSearchCheckpoint(Jobs: [bent]),
                reason: out var reason
            ));
            Assert.Contains(
                actualString: reason,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "different job shape"
            );
            Assert.Equal(
                actual: target.Status(index: 0),
                expected: before
            );
        }

        Assert.True(
            condition: target.TryRestore(
                checkpoint: captured,
                reason: out var accepted
            ),
            userMessage: accepted
        );
    }
    // A checkpoint naming no jobs, or no progress for a job, is refused rather than dereferenced.
    [Fact]
    public void ACheckpointMissingItsJobsIsRefused() {
        var position = new Position(rows: Board(
            cells: 3,
            tokens: 1
        ));
        var search = Build(
            plan: Relocate(
                cells: 3,
                depth: 1
            ),
            position: position,
            score: null
        );

        Assert.False(condition: search.TryRestore(
            checkpoint: new ArenaSearchCheckpoint(Jobs: null!),
            reason: out _
        ));
        Assert.False(condition: search.TryRestore(
            checkpoint: new ArenaSearchCheckpoint(Jobs: [null!]),
            reason: out _
        ));
    }
    // A tree checkpoint's path and node pool are indexed by the walk, so a node outside the pool, a child run past
    // it, or a node naming no candidate is refused.
    [Fact]
    public void ATreeCheckpointWhoseNodesPointOutsideThePoolIsRefused() {
        var position = new Position(rows: Board(
            cells: 3,
            tokens: 1
        ));

        ArenaSearch Make() => Build(
            plan: Relocate(
                cells: 3,
                depth: 2,
                iterations: 64,
                method: SearchMethod.Tree,
                nodes: 8,
                scored: true
            ),
            position: position,
            score: Score(
                position: position,
                text: "piece[t0]"
            )
        );

        var source = Make();
        var target = Make();

        for (var step = 0; (step < 3); step++) {
            _ = source.Step(
                apply: static _ => true,
                engineTick: 0UL,
                tick: 1UL
            );
        }

        var captured = source.Capture();
        var job = captured.Jobs[0];
        var tree = job.Tree!;

        Assert.True(condition: tree.Active);
        Assert.True(condition: (tree.Count > 1));

        int[] Bent(int[] from, int index, int value) {
            var copy = from.ToArray();

            copy[index] = value;

            return copy;
        }

        foreach (var bent in new[] {
            (tree with { Count = (tree.Parent.Length + 1) }),
            (tree with { PathLength = (tree.Path.Length + 1) }),
            (tree with { PathLength = 0 }),
            (tree with { Path = Bent(from: tree.Path, index: 0, value: tree.Count) }),
            (tree with { Path = Bent(from: tree.Path, index: 0, value: -1) }),
            (tree with { ChildCount = Bent(from: tree.ChildCount, index: 0, value: (tree.Count + 1)) }),
            (tree with { FirstChild = Bent(from: tree.FirstChild, index: 0, value: -1) }),
            (tree with { Shape = Bent(from: tree.Shape, index: 1, value: -1) }),
            (tree with { Token = Bent(from: tree.Token, index: 1, value: 1) }),
            (tree with { Target = Bent(from: tree.Target, index: 1, value: 3) }),
            (tree with { ExpandShape = -1 }),
            (tree with { Scan = -1 }),
            (tree with { Visits = null! }),
        }) {
            Assert.False(condition: target.TryRestore(
                checkpoint: new ArenaSearchCheckpoint(Jobs: [(job with { Tree = bent })]),
                reason: out _
            ));
        }

        Assert.True(
            condition: target.TryRestore(
                checkpoint: captured,
                reason: out var accepted
            ),
            userMessage: accepted
        );
    }
    // A finished walk's answer is whole, so a door that throws leaves the job unlanded and the next step hands the
    // same answer to the door again.
    [Fact]
    public void ALandingThatThrowsLandsAgainOnTheNextStep() {
        var rows = Board(
            cells: 3,
            tokens: 1
        );
        var position = new Position(rows: rows);
        var whole = new Position(rows: rows);
        var search = Build(
            plan: Relocate(
                cells: 3,
                depth: 1
            ),
            position: position,
            score: null
        );

        _ = Assert.Throws<InvalidOperationException>(testCode: () => search.Step(
            apply: static _ => throw new InvalidOperationException(message: "door failure"),
            engineTick: 0UL,
            tick: 1UL
        ));
        Assert.False(condition: search.Status(index: 0).Done);

        IReadOnlyList<ArenaSearchWrite>? landed = null;

        Assert.True(condition: search.Step(
            apply: writes => {
                landed = writes;

                return true;
            },
            engineTick: 0UL,
            tick: 1UL
        ));
        Assert.True(condition: search.Status(index: 0).Done);
        Assert.NotNull(@object: landed);
        Assert.Equal(
            actual: Normalize(
                catalog: position.Catalog,
                writes: landed
            ),
            expected: RunArena(
                plan: Relocate(
                    cells: 3,
                    depth: 1
                ),
                position: whole,
                score: null
            )
        );

        // A landed job is not handed to the door a second time.
        Assert.False(condition: search.Step(
            apply: static _ => throw new InvalidOperationException(message: "door failure"),
            engineTick: 0UL,
            tick: 1UL
        ));
    }
    [Fact]
    public void AStepsCandidatesAllocateNothingAfterWarmUp() {
        var position = new Position(rows: [
            .. Board(
                cells: 16,
                tokens: 4
            ),
            Slot(
                name: "nudge",
                value: 0L
            ),
        ]);
        var nudge = position.Ordinal(name: "nudge");
        // The one delegate every step below is handed: a fresh lambda site allocates its delegate on first use,
        // which would land inside the measured window.
        var apply = static (IReadOnlyList<ArenaSearchWrite> _) => true;
        var search = Build(
            plan: Relocate(
                cells: 16,
                depth: 1,
                nodes: 4
            ),
            position: position,
            score: null
        );

        // Two whole walks grow every buffer the third one reuses; each one is restarted by an input row the judge
        // never reads, so the third walk starts from the same position the first two did.
        for (var pass = 1L; (pass <= 2L); pass++) {
            for (var step = 0; (step < 64); step++) {
                _ = search.Step(
                    apply: apply,
                    engineTick: 0UL,
                    tick: 1UL
                );
            }

            Assert.True(condition: position.Arena.TryWrite(
                key: position.SlotKey,
                operand: pass,
                reason: out var reason,
                rowOrdinal: nudge,
                write: StateWriteKind.Set
            ), userMessage: reason);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var step = 0; (step < 8); step++) {
            _ = search.Step(
                apply: apply,
                engineTick: 0UL,
                tick: 1UL
            );
        }

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.True(condition: search.Status(index: 0).Running);
        Assert.Equal(
            actual: allocated,
            expected: 0L
        );
    }
    // The same walk judged by compiled rules through the evaluator: the judge starts each candidate from a clean
    // latch without dropping the storage the latch has grown.
    [Fact]
    public void AStepsCandidatesJudgedByCompiledRulesAllocateNothingAfterWarmUp() {
        var position = new Position(rows: [
            .. Board(
                cells: 16,
                tokens: 4
            ),
            Slot(
                name: "nudge",
                value: 0L
            ),
        ]);
        var nudge = position.Ordinal(name: "nudge");
        // The one delegate every step below is handed: a fresh lambda site allocates its delegate on first use,
        // which would land inside the measured window.
        var apply = static (IReadOnlyList<ArenaSearchWrite> _) => true;
        var search = SearchFixture.Build(
            judge: SearchFixture.RuleJudge(
                position: position,
                rules: [SearchFixture.AcceptEveryCandidate()]
            ),
            plan: Relocate(
                cells: 16,
                depth: 1,
                nodes: 4
            ),
            position: position
        );

        // Two whole walks grow every buffer the third one reuses; each one is restarted by an input row the judge
        // never reads, so the third walk starts from the same position the first two did.
        for (var pass = 1L; (pass <= 2L); pass++) {
            for (var step = 0; (step < 64); step++) {
                _ = search.Step(
                    apply: apply,
                    engineTick: 0UL,
                    tick: 1UL
                );
            }

            Assert.True(condition: position.Arena.TryWrite(
                key: position.SlotKey,
                operand: pass,
                reason: out var reason,
                rowOrdinal: nudge,
                write: StateWriteKind.Set
            ), userMessage: reason);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var step = 0; (step < 8); step++) {
            _ = search.Step(
                apply: apply,
                engineTick: 0UL,
                tick: 1UL
            );
        }

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.True(condition: search.Status(index: 0).Running);
        Assert.Equal(
            actual: allocated,
            expected: 0L
        );
    }
    [Fact]
    public void APlanNamingARowTheCatalogDoesNotDeclareIsRefusedByName() {
        var position = new Position(rows: Board(
            cells: 4,
            tokens: 2
        ));

        Assert.False(condition: ArenaSearchPlan.TryResolve(
            catalog: position.Catalog,
            plan: Relocate(
                cells: 4,
                depth: 1
            ) with {
                Verdict = "missing",
            },
            reason: out var reason,
            resolved: out _
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "missing"
        );
    }
    // A job's progress hashes beside the arena: it moves as the walk advances, and a checkpoint restored into a
    // fresh search reproduces it exactly.
    [Fact]
    public void AJobsProgressHashesAndRoundTripsThroughItsCheckpoint() {
        var rows = Board(
            cells: 4,
            tokens: 2
        );
        var text = "(piece[t0] * 4) - piece[t1]";
        var position = new Position(rows: rows);

        ArenaSearch Make() => Build(
            plan: Relocate(
                cells: 4,
                depth: 2,
                nodes: 2,
                scored: true
            ),
            position: position,
            score: Score(
                position: position,
                text: text
            )
        );

        ulong Hash(ArenaSearch search) {
            var hash = Fnv1aHash.Create();

            search.AppendStateHash(hash: ref hash);

            return hash.Value;
        }

        var source = Make();
        var fresh = Hash(search: source);

        _ = source.Step(
            apply: static _ => true,
            engineTick: 0UL,
            tick: 1UL
        );

        var advanced = Hash(search: source);

        Assert.NotEqual(
            actual: advanced,
            expected: fresh
        );

        var target = Make();

        Assert.True(
            condition: target.TryRestore(
                checkpoint: source.Capture(),
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: Hash(search: target),
            expected: advanced
        );
    }

    private static ScoreStep Score(Position position, string text) {
        var piece = position.Ordinal(name: "piece");
        var first = position.Key(name: "t0");
        var second = position.Key(name: "t1");

        return ((text == "piece[t0]")
            ? ((in ArenaSearchView view) => Cell(
                arena: view.Arena,
                key: first,
                rowOrdinal: piece
            ))
            : ((in ArenaSearchView view) => ((Cell(
                arena: view.Arena,
                key: first,
                rowOrdinal: piece
            ) * 4L) - Cell(
                arena: view.Arena,
                key: second,
                rowOrdinal: piece
            )))
        );
    }
    private static long Cell(StateArena arena, int rowOrdinal, CellKey key) => (arena.TryRead(
        key: key,
        rowOrdinal: rowOrdinal,
        value: out var value
    )
        ? value.AsInt
        : 0L
    );
    // A jump walks a board's neighbours, so a job that names no board cannot hold one: the plan refuses it by name
    // when it is resolved rather than dereferencing a topology it does not have mid-search.
    [Fact]
    public void AShapeThatWalksABoardIsRefusedOverAJobThatNamesNone() {
        var position = new Position(rows: Board(
            cells: 4,
            tokens: 2
        ));
        var plan = Relocate(
            cells: 4,
            depth: 1
        );

        Assert.False(condition: ArenaSearchPlan.TryResolve(
            catalog: position.Catalog,
            plan: plan with {
                Shapes = [.. plan.Shapes.Select(selector: static shape => shape with { Kind = SearchShapeKind.Jump })],
                Topology = null,
            },
            reason: out var reason,
            resolved: out _
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "Jump shape"
        );
    }
}
