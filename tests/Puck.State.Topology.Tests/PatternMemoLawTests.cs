using Xunit;

namespace Puck.State.Topology.Tests;

/// <summary>CONTRACT UNDER TEST: an incremental pattern match answers exactly what a full walk answers, whatever
/// the memo remembered. A push at an ordered row's tail is the one mutation that lets a walk resume from the
/// memoized derivative state; a prefix replacement, a removal, a reorder, a clear, a relayout, and a rewound
/// speculative write all restart it from the first symbol. Two reads over one unchanged row — a later start, a
/// ray off another origin or direction — are two words, and neither answers from the other's state. A
/// complement's machine accepts exactly the words its item rejects.</summary>
public sealed class PatternMemoLawTests {
    private static readonly PatternSymbol[] Alphabet = [
        new(
            Max: 0m,
            Min: 0m,
            Name: TopologyArenaFixture.Name(value: "low")
        ),
        new(
            Max: 3m,
            Min: 1m,
            Name: TopologyArenaFixture.Name(value: "high")
        ),
    ];

    private static CompiledPattern Compile(PatternNode node, string name = "run") {
        Assert.True(
            condition: CompiledPattern.TryCompile(
                compiled: out var compiled,
                reason: out var reason,
                row: new PatternRow(
                    Kind: CellKind.Int,
                    Name: TopologyArenaFixture.Name(value: name),
                    Pattern: node,
                    Symbols: Alphabet
                )
            ),
            userMessage: reason
        );

        return compiled!;
    }
    private static PatternNode Run() => new PatternNode.Sequence(Items: [
        new PatternNode.Star(Item: new PatternNode.Symbol(Name: "low")),
        new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "high")),
    ]);
    private static PatternWalk Fresh(StateArena arena, CompiledPattern pattern, int start = 0) => new PatternMemo().Walk(
        pattern: pattern,
        word: Word(
            arena: arena,
            buffer: new long[16],
            start: start
        )
    );
    private static ArenaWord Word(StateArena arena, long[] buffer, int start = 0) => arena.ReadWord(
        rowOrdinal: TopologyArenaFixture.Pile,
        start: start,
        word: buffer
    );
    private static void Mint(StateArena arena, string key, long value, int position = -1) => Assert.True(
        condition: arena.TryInsert(
            key: out _,
            name: TopologyArenaFixture.Name(value: key),
            position: position,
            reason: out var reason,
            rowOrdinal: TopologyArenaFixture.Pile,
            value: CellValue.Int(value: value)
        ),
        userMessage: reason
    );
    private static PatternWalk Memoized(PatternMemo memo, StateArena arena, CompiledPattern pattern, int start = 0) => memo.Walk(
        pattern: pattern,
        word: Word(
            arena: arena,
            buffer: new long[16],
            start: start
        )
    );
    private static void Agrees(PatternMemo memo, StateArena arena, CompiledPattern pattern, bool resumed, int start = 0) {
        var cached = Memoized(
            arena: arena,
            memo: memo,
            pattern: pattern,
            start: start
        );
        var walked = Fresh(
            arena: arena,
            pattern: pattern,
            start: start
        );

        Assert.Equal(
            actual: cached.Match,
            expected: walked.Match
        );
        Assert.Equal(
            actual: cached.LongestAcceptedPrefix,
            expected: walked.LongestAcceptedPrefix
        );
        Assert.Equal(
            actual: cached.State,
            expected: walked.State
        );
        Assert.Equal(
            actual: cached.Resumed,
            expected: resumed
        );
    }

    // A word's letters are a copy, so a word kept across a mutation still walks as what it read. It memoizes against
    // the counter it was read under, and the current word restarts instead of resuming from letters the row lost.
    [Fact]
    public void AWordKeptAcrossAMutationNeverCertifiesItsLettersAsCurrent() {
        var (catalog, arena) = TopologyArenaFixture.Build();
        var memo = new PatternMemo();
        var pattern = Compile(node: Run());

        Mint(
            arena: arena,
            key: "t0",
            value: 2L
        );

        var kept = Word(
            arena: arena,
            buffer: new long[16]
        );

        Assert.True(condition: arena.TryWrite(
            key: catalog.Keys.Intern(name: TopologyArenaFixture.Name(value: "t0")),
            operand: 0L,
            reason: out _,
            rowOrdinal: TopologyArenaFixture.Pile,
            write: StateWriteKind.Set
        ));
        Assert.Equal(
            actual: memo.Walk(
                pattern: pattern,
                word: kept
            ).Match,
            expected: 1L
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: false
        );
        Assert.Equal(
            actual: Memoized(
                arena: arena,
                memo: memo,
                pattern: pattern
            ).Match,
            expected: 0L
        );

        // The kept word walked again after the current one restarts too, and still answers on its own letters.
        var again = memo.Walk(
            pattern: pattern,
            word: kept
        );

        Assert.False(condition: again.Resumed);
        Assert.Equal(
            actual: again.Match,
            expected: 1L
        );
    }
    // A word no read minted names no arena, so it walks from the first symbol and memoizes nothing.
    [Fact]
    public void AWordNoReadMintedMemoizesNothing() {
        var memo = new PatternMemo();
        var pattern = Compile(node: Run());

        Assert.False(condition: memo.Walk(
            pattern: pattern,
            word: default
        ).Resumed);
        Assert.False(condition: memo.Walk(
            pattern: pattern,
            word: default
        ).Resumed);
    }
    [Fact]
    public void ATailAppendResumesTheMemoAndEveryOtherMutationRestartsIt() {
        var (catalog, arena) = TopologyArenaFixture.Build();
        var memo = new PatternMemo();
        var pattern = Compile(node: Run());

        Mint(
            arena: arena,
            key: "t0",
            value: 0L
        );
        Mint(
            arena: arena,
            key: "t1",
            value: 0L
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: false
        );

        // A push at the tail leaves the prefix proof standing, so the walk reads only the appended symbol.
        Mint(
            arena: arena,
            key: "t2",
            value: 2L
        );

        var steps = memo.Steps;

        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: true
        );
        Assert.Equal(
            actual: (memo.Steps - steps),
            expected: 1L
        );

        // A prefix replacement.
        Assert.True(condition: arena.TryWrite(
            key: catalog.Keys.Intern(name: TopologyArenaFixture.Name(value: "t0")),
            operand: 3L,
            reason: out _,
            rowOrdinal: TopologyArenaFixture.Pile,
            write: StateWriteKind.Set
        ));
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: false
        );

        // A non-tail insert, which reorders everything after it.
        Mint(
            arena: arena,
            key: "t3",
            position: 0,
            value: 0L
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: false
        );

        // A removal.
        Assert.True(condition: arena.TryRemove(
            key: catalog.Keys.Intern(name: TopologyArenaFixture.Name(value: "t1")),
            reason: out _,
            rowOrdinal: TopologyArenaFixture.Pile
        ));
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: false
        );

        // A clear.
        while (arena.CellCount(rowOrdinal: TopologyArenaFixture.Pile) != 0) {
            Assert.True(condition: arena.TryKeyAt(
                key: out var key,
                position: 0,
                rowOrdinal: TopologyArenaFixture.Pile
            ));
            Assert.True(condition: arena.TryRemove(
                key: key,
                reason: out _,
                rowOrdinal: TopologyArenaFixture.Pile
            ));
        }
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: false
        );
    }
    [Fact]
    public void ARewoundSpeculationAndARelayoutBothRestartTheMemo() {
        var (catalog, arena) = TopologyArenaFixture.Build();
        var memo = new PatternMemo();
        var pattern = Compile(node: Run());

        Mint(
            arena: arena,
            key: "t0",
            value: 0L
        );
        Mint(
            arena: arena,
            key: "t1",
            value: 1L
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: false
        );

        var mark = arena.BeginScope();

        Mint(
            arena: arena,
            key: "t2",
            value: 1L
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: true
        );
        arena.Rewind(mark: mark);
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: false
        );

        // The rewound prefix is a prefix like any other, so a different suffix appended onto it resumes from it and
        // answers on the new word's own terms.
        Mint(
            arena: arena,
            key: "t4",
            value: 0L
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: true
        );
        Assert.Equal(
            actual: Fresh(
                arena: arena,
                pattern: pattern
            ).Match,
            expected: 0L
        );
        Assert.True(condition: arena.TryRelayout(
            catalog: catalog,
            reason: out var reason,
            section: TopologyArenaFixture.Section(),
            time: ArenaTime.Origin
        ), userMessage: reason);
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: false
        );
    }
    [Fact]
    public void AComplementAcceptsExactlyTheWordsItsItemRejects() {
        var run = Compile(node: Run());
        var complement = Compile(
            name: "notRun",
            node: new PatternNode.Complement(Item: Run())
        );
        long[][] words = [
            [],
            [0L],
            [1L],
            [0L, 1L],
            [1L, 0L],
            [0L, 0L, 2L, 3L],
            [3L, 0L],
            [0L, 0L, 0L],
        ];

        foreach (var word in words) {
            Assert.Equal(
                actual: complement.Match(values: word),
                expected: (1L - run.Match(values: word))
            );
        }
    }
    [Fact]
    public void TheMemoKeepsOnePrefixPerRowPatternAndAttribute() {
        var (_, arena) = TopologyArenaFixture.Build();
        var memo = new PatternMemo();
        var run = Compile(node: Run());
        var lows = Compile(
            name: "lows",
            node: new PatternNode.Plus(Item: new PatternNode.Symbol(Name: "low"))
        );

        Mint(
            arena: arena,
            key: "t0",
            value: 0L
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: run,
            resumed: false
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: lows,
            resumed: false
        );
        Mint(
            arena: arena,
            key: "t1",
            value: 0L
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: run,
            resumed: true
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: lows,
            resumed: true
        );

        Assert.False(condition: memo.Walk(
            pattern: run,
            word: arena.ReadWord(
                attributeOrdinal: TopologyArenaFixture.Attribute,
                rowOrdinal: TopologyArenaFixture.Pile,
                word: new long[16]
            )
        ).Resumed);
    }
    [Fact]
    public void ALaterStartOverOneUnchangedRowAnswersOnItsOwnLetters() {
        var (_, arena) = TopologyArenaFixture.Build();
        var memo = new PatternMemo();
        var pattern = Compile(node: Run());
        long[] values = [1L, 0L, 1L, 1L];

        for (var index = 0; (index < values.Length); index++) {
            Mint(
                arena: arena,
                key: $"t{index}",
                value: values[index]
            );
        }

        // The shorter read runs first, so the whole row's word could only resume from a state that is not its own.
        Assert.Equal(
            actual: Fresh(
                arena: arena,
                pattern: pattern,
                start: 2
            ).Match,
            expected: 1L
        );
        Assert.Equal(
            actual: Fresh(
                arena: arena,
                pattern: pattern
            ).Match,
            expected: 0L
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: false,
            start: 2
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: false
        );

        // Each read resumes from the state it left off in, and from no other read's.
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: true,
            start: 2
        );
        Agrees(
            arena: arena,
            memo: memo,
            pattern: pattern,
            resumed: true
        );
    }
    [Fact]
    public void EveryRayOffOneBoardAnswersOnItsOwnLetters() {
        var (_, arena) = TopologyArenaFixture.Build();
        var memo = new PatternMemo();
        var pattern = Compile(node: Run());

        for (var cell = 0; (cell < TopologyArenaFixture.BoardCells); cell++) {
            Assert.True(
                condition: arena.TryWriteBoardCell(
                    cell: cell,
                    reason: out var reason,
                    rowOrdinal: TopologyArenaFixture.Board,
                    value: (((cell >= 4) && ((cell % 4) == 1))
                        ? 1L
                        : 0L
                    ),
                    write: StateWriteKind.Set
                ),
                userMessage: reason
            );
        }

        var topology = arena.Layout[TopologyArenaFixture.Board].Topology!;
        var scratch = new long[TopologyArenaFixture.BoardCells];

        for (var pass = 0; (pass < 2); pass++) {
            for (var origin = 0; (origin < TopologyArenaFixture.BoardCells); origin++) {
                for (var direction = 0; (direction < topology.DirectionCount); direction++) {
                    Assert.True(
                        condition: ArenaBoards.TryReadRay(
                            arena: arena,
                            direction: direction,
                            origin: origin,
                            ray: out var ray,
                            reason: out var reason,
                            rowOrdinal: TopologyArenaFixture.Board,
                            values: scratch,
                            word: new long[TopologyArenaFixture.BoardCells]
                        ),
                        userMessage: reason
                    );

                    var cached = memo.Walk(
                        pattern: pattern,
                        word: ray
                    );
                    var walked = new PatternMemo().Walk(
                        pattern: pattern,
                        word: ray
                    );

                    Assert.Equal(
                        actual: cached.Match,
                        expected: walked.Match
                    );
                    Assert.Equal(
                        actual: cached.LongestAcceptedPrefix,
                        expected: walked.LongestAcceptedPrefix
                    );
                    Assert.Equal(
                        actual: cached.State,
                        expected: walked.State
                    );

                    // The board has not moved between the passes, so the second reading of a ray resumes where the
                    // first left off and reads nothing.
                    Assert.Equal(
                        actual: cached.Resumed,
                        expected: ((pass == 1) && (ray.Length != 0))
                    );
                }
            }
        }
    }
}
